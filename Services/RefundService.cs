using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Npgsql; // 🛡️ Fix 23505: PostgresException tipado para absorber colisión IX_FT_StripeRefundId_uq
using Stripe;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs; // 🛡️ V8: MoneyDistributionConfigDto para construir snapshot
using newApi.Common;
using System;

namespace newApi.Services
{
    public class StripeRefundService
    {
        private readonly AppDbContext _context;
        private readonly SystemStatusService _systemStatusService;
        private readonly ILoggingService _loggingService;
        // 📜 Round 9 — A2 FIX: audit log de transiciones de estado.
        private readonly ISearchHireStatusAuditService? _statusAudit;

        // 🛡️ Round 28 MUD-BD: cola de pérdidas pendientes off-Stripe.
        private readonly ClawbackQueueService? _clawbackQueue;

        // 🔔 NOTIF-FIX [SMS-dinero]: refuerzo SMS en refund/transfer exitosos (auto-gateado por
        // móvil verificado en SendImportantSmsAsync). Opcional para no romper tests existentes.
        private readonly IInAppNotificationService? _inAppNotifications;

        public StripeRefundService(AppDbContext context, SystemStatusService systemStatusService, ILoggingService loggingService, ISearchHireStatusAuditService? statusAudit = null, ClawbackQueueService? clawbackQueue = null, IInAppNotificationService? inAppNotifications = null)
        {
            _context = context;
            _systemStatusService = systemStatusService;
            _loggingService = loggingService;
            _statusAudit = statusAudit; // opcional para no romper tests existentes
            _clawbackQueue = clawbackQueue;
            _inAppNotifications = inAppNotifications;
        }


        /// <summary>
        /// Orquesta la distribuci├│n de dinero seg├║n un estado concreto: realiza refund al cliente y transferencia al experto.
        /// Respeta subestados de finalizaci├│n y granularidad (categor├¡a/tipo/global) mediante el statusValue recibido.
        /// 
        /// Estructura en 3 fases:
        /// - Fase 1: Validaciones (sin cambiar estado)
        /// - Fase 2: Cambio de estado (transacci├│n BD r├ípida, separada)
        /// - Fase 3: Procesamiento de dinero (Stripe, fuera de transacci├│n de estado)
        /// </summary>
        /// <param name="searchHireId">ID del servicio contratado</param>
        /// <param name="statusValue">Estado espec├¡fico, p.ej. "appointment_cancelled_by_expert_second"</param>
        /// <param name="reason">Raz├│n del movimiento</param>
        /// <param name="initiatedByUserId">Opcional: usuario que inicia la operaci├│n (para trazas)</param>
        /// <param name="updateState">Si true, cambia el estado de Appointment y SearchHire antes de procesar dinero (por defecto true)</param>
        /// <returns>True si refund y (si aplica) transfer se procesan correctamente</returns>
        public async Task<bool> ProcessMoneyDistributionAsync(int searchHireId, string statusValue, string reason, int? initiatedByUserId = null, bool updateState = true)
        {
            try
            {
                // 🛡️ Round 28 MUD-AM (+ MUD-AP regression fix): el `SELECT ... FOR UPDATE`
                // previo era no-op porque EF abría/cerraba una tx implícita por SELECT — el
                // row-lock se liberaba al cerrar el cursor de FirstOrDefaultAsync.
                //
                // MUD-AP: NO abrir tx propia si el caller ya tiene una. CancelService /
                // ForceFinalize / ResolveDispute admin (SubscriptionController.cs:6172/6312/6497)
                // abren tx exterior antes de llamar. EF Core lanza InvalidOperationException
                // si BeginTransactionAsync se invoca con CurrentTransaction != null. Esa
                // excepción caía en el outer catch genérico → todos los refunds desde esos
                // 3 endpoints fallaban silenciosos con "ProcessMoneyDistributionAsync failed".
                //
                // Patrón correcto: detectar CurrentTransaction (igual que L647 y L1055 ya
                // hacen para Phase 2). Si hay tx exterior, el pg_advisory_xact_lock atado
                // a esa tx persiste hasta su commit/rollback del caller (mejor lifetime
                // todavía). Si no hay, abrimos micro-tx propia que liberamos antes de
                // Stripe calls.
                SearchHire? searchHire;
                if (_context.Database.CurrentTransaction == null)
                {
                    // 🛡️ FIX TX-5 (2026-06-11): BeginTransactionAsync "pelado" es incompatible con
                    // NpgsqlRetryingExecutionStrategy (EnableRetryOnFailure, Program.cs:1267) — EF
                    // lanza InvalidOperationException "does not support user-initiated transactions"
                    // en la PRIMERA operación dentro de la tx. Caía al outer catch → return false →
                    // TODO caller SIN tx exterior fallaba: ResolveDispute inline, el propio
                    // RetryMoneyDistributionJobAsync (reintento muerto: Logs prod #4649) y las
                    // cancelaciones por timer del watchdog (Logs prod #5565, hire 16 con 100€
                    // atascados). Mismo wrap CreateExecutionStrategy().ExecuteAsync que Fase 2
                    // (L678) y Fase 3 (L2432) ya usan; el bloque es idempotente (lock + SELECT),
                    // así que el retry de la estrategia es seguro.
                    var lockStrategy = _context.Database.CreateExecutionStrategy();
                    searchHire = await lockStrategy.ExecuteAsync(async () =>
                    {
                        using var lockTx = await _context.Database.BeginTransactionAsync();
                        await _context.Database.ExecuteSqlInterpolatedAsync(
                            $"SELECT pg_advisory_xact_lock({(long)searchHireId})");

                        var sh = await _context.SearchHires
                            .Include(sh => sh.Status)
                            .Include(sh => sh.Client)
                            .Include(sh => sh.Expert)
                                .ThenInclude(e => e.ExpertProfile)
                            .Include(sh => sh.SearchService)
                                .ThenInclude(ss => ss.ServiceType)
                            .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                        await lockTx.CommitAsync();
                        return sh;
                    });
                }
                else
                {
                    // Tx exterior — reusar y atar el lock a su lifetime (mejor garantía:
                    // el lock cubre TODO el flujo del caller, no solo el SELECT inicial).
                    await _context.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock({(long)searchHireId})");

                    searchHire = await _context.SearchHires
                        .Include(sh => sh.Status)
                        .Include(sh => sh.Client)
                        .Include(sh => sh.Expert)
                            .ThenInclude(e => e.ExpertProfile)
                        .Include(sh => sh.SearchService)
                            .ThenInclude(ss => ss.ServiceType)
                        .FirstOrDefaultAsync(sh => sh.Id == searchHireId);
                }

                if (searchHire == null)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: SearchHire not found - money distribution failed",
                        details: $"SearchHire {searchHireId} not found in database. Cannot process money distribution for status {statusValue}. " +
                                $"Reason: {reason}. " +
                                $"ACTION REQUIRED: Verify SearchHire exists in database.",
                        userId: initiatedByUserId ?? 0,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { 
                            SearchHireId = searchHireId,
                            Status = statusValue,
                            Reason = reason
                        }
                    );
                    return false;
                }

                // Validar si el estado es de finalizaci├│n cuando proviene de AppointmentStatus
                try
                {
                    var statusRow = await _context.SystemStatuses
                        .FirstOrDefaultAsync(s => s.StatusValue == statusValue);
                    if (statusRow != null && statusRow.StatusType == "AppointmentStatus" && statusRow.IsFinalizationStatus == false)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Invalid AppointmentStatus for money distribution",
                            details: $"Status {statusValue} is an AppointmentStatus but is not a finalization status. " +
                                    $"Cannot process money distribution. SearchHireId: {searchHireId}, Reason: {reason}. " +
                                    $"ACTION REQUIRED: Use a finalization status or SearchHireStatus for money distribution.",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                StatusType = statusRow.StatusType,
                                IsFinalizationStatus = statusRow.IsFinalizationStatus,
                                Reason = reason
                            }
                        );
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Error validating AppointmentStatus",
                        details: $"Error validating status {statusValue}: {ex.Message}",
                        userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { 
                            Status = statusValue,
                            Error = ex.Message
                        }
                    );
                }

                // 🛡️ R26 FIX: validar que statusValue exista en SystemStatuses ANTES de buscar config.
                // Si no existe, GetMoneyDistributionConfigAsync retornará null y caemos al fallback con
                // critical genérico ("config not found") sin pista de la causa raíz (typo, enum nuevo
                // sin sembrar). Detectar acá da error claro al admin.
                var statusValueExists = await _context.SystemStatuses
                    .AsNoTracking()
                    .AnyAsync(s => s.StatusValue == statusValue);
                if (!statusValueExists)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL R26: statusValue NO existe en SystemStatuses",
                        details: $"SearchHire {searchHireId}: el statusValue '{statusValue}' no está sembrado en SystemStatuses. Imposible distribuir dinero. CAUSA típica: enum nuevo agregado sin run del SEED_ESTADOS_COMPLETO.sql, o typo en el caller. ACCIÓN: revisar seed + lista de SearchHireStatus/AppointmentStatus.",
                        userId: initiatedByUserId ?? searchHire.ClientId,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync.R26",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { StatusValue = statusValue });
                    return false;
                }

                // 🛡️ V8 FIX: usar snapshot de % capturado al crear el hire SI EXISTE. Sin
                // snapshot (hires legacy o pre-flip migración), fallback a StatusConfiguration
                // actual (comportamiento previo). Esto protege contratos vigentes contra cambios
                // retroactivos de % por admin: el experto que contrató con 95% recibe 95% aunque
                // el admin baje el porcentaje después. NULL coalescing en las 3 → si alguno
                // falta, usar config live (no mezclar snapshot parcial + live: incoherente).
                // 🛡️ F4 FIX: el snapshot protege el % CONTRATADO en el camino de éxito (completed),
                // pero NO debe aplicarse a cancelaciones: ahí mandan los tramos escalonados de la
                // política (100/0, 50/50, 0/100). Si algún día se siembra un StatusConfiguration para
                // 'pending', el snapshot se poblaría y, sin este guard, pisaría todos los % de
                // cancelación con los de 'pending'. Para estados de cancelación: SIEMPRE config live.
                // 🛡️ F4b FIX (2026-07-06): el guard por prefijo "appointment_cancelled" dejaba pasar al
                // snapshot los estados hire-level cuyo reparto NO es el contratado: 'dispute_resolved_client'
                // (90/8/2) y 'cancelled' (100/0/0, lo usan borrado de cuenta y cancelaciones de webhook).
                // Como el snapshot se captura SIEMPRE con el reparto de 'completed' (0/95/5), esos estados
                // pagaban 95% al experto y 0% al cliente (una disputa resuelta A FAVOR del cliente no le
                // devolvía nada). Invertido a LISTA BLANCA: el snapshot solo aplica a los estados cuyo
                // reparto ES el contratado (camino de éxito + reintento de transfer). Todo lo demás → live.
                var snapshotWhitelist = new[]
                {
                    "completed",
                    "appointment_completed",
                    "appointment_completed_without_client_approval",
                    "appointment_completed_auto",
                    "dispute_resolved_expert",
                    "transfer_failed" // reintento del transfer al experto de un hire completado: mismo reparto contratado
                };
                var usesContractedSplit = snapshotWhitelist.Contains(statusValue, StringComparer.OrdinalIgnoreCase);

                MoneyDistributionConfigDto? config;
                if (usesContractedSplit
                    && searchHire.ClientPercentageSnapshot.HasValue
                    && searchHire.ExpertPercentageSnapshot.HasValue
                    && searchHire.PlatformPercentageSnapshot.HasValue)
                {
                    config = new MoneyDistributionConfigDto
                    {
                        ClientPercentage = searchHire.ClientPercentageSnapshot.Value,
                        ExpertPercentage = searchHire.ExpertPercentageSnapshot.Value,
                        PlatformPercentage = searchHire.PlatformPercentageSnapshot.Value
                    };
                    await _loggingService.LogInfoAsync(
                        message: "V8: usando snapshot de porcentajes del SearchHire (no StatusConfiguration live)",
                        details: $"SearchHire {searchHire.Id}: Client={config.ClientPercentage}%, Expert={config.ExpertPercentage}%, Platform={config.PlatformPercentage}% (snapshot inmutable al momento de creación, protege contra cambios admin retroactivos).",
                        userId: null,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync.V8",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id);
                }
                else
                {
                    // Obtener configuraci├│n de distribuci├│n para el estado concreto (subestado/granularidad lo resuelve el servicio)
                    config = await _systemStatusService.GetMoneyDistributionConfigAsync(
                        statusValue,
                        searchHire.SearchService?.CategoryId,
                        searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId
                    );
                }

                if (config == null)
                {
                    // Fallback: si no hay configuraci├│n para subestado, usar estado final de SearchHire
                    // Intentar mapear statusValue (appointment_*) a SearchHireStatus mediante servicio centralizado
                    try
                    {
                        AppointmentStatus? appointmentStatus = MapAppointmentStatus(statusValue);
                        if (appointmentStatus.HasValue)
                        {
                            var targetSearchHireStatus = await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatus.Value);
                            if (targetSearchHireStatus.HasValue)
                            {
                                var finalStatusValue = SearchHireStatusExtensions.ToStringValue(targetSearchHireStatus.Value);
                                // Validar que el target sea estado de finalizaci├│n
                                try
                                {
                                    var targetRow = await _context.SystemStatuses
                                        .FirstOrDefaultAsync(s => s.StatusValue == finalStatusValue && s.StatusType == "SearchHireStatus");
                                    if (targetRow != null && targetRow.IsFinalizationStatus == false)
                                    {
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: Target SearchHireStatus is not a finalization status",
                                            details: $"Mapped status {finalStatusValue} from {statusValue} is not a finalization status. " +
                                                    $"Cannot process money distribution. SearchHireId: {searchHireId}, Reason: {reason}. " +
                                                    $"ACTION REQUIRED: Use a finalization status for money distribution.",
                                            userId: initiatedByUserId ?? searchHire.ClientId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId,
                                            additionalData: new { 
                                                OriginalStatus = statusValue,
                                                MappedStatus = finalStatusValue,
                                                IsFinalizationStatus = targetRow.IsFinalizationStatus,
                                                Reason = reason
                                            }
                                        );
                                        return false;
                                    }
                                }
                                catch (Exception ex2)
                                {
                                    // Log error but continue
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL: Error validating mapped SearchHireStatus",
                                        details: $"Error validating mapped status {finalStatusValue}: {ex2.Message}",
                                        userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new { 
                                            OriginalStatus = statusValue,
                                            MappedStatus = finalStatusValue,
                                            Error = ex2.Message
                                        }
                                    );
                                }
                                config = await _systemStatusService.GetMoneyDistributionConfigAsync(
                                    finalStatusValue,
                                    searchHire.SearchService?.CategoryId,
                                    searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId
                                );
                            }
                        }
                    }
                    catch (Exception mapEx)
                    {
                        // 🛡️ R24 FIX: antes silent catch — ahora log warning para no perder la causa.
                        // Si llegamos aquí, statusValue NO se pudo mapear a SearchHireStatus (typo,
                        // enum nuevo no agregado, valor corrupto en webhook). El config queda null y
                        // saltará el critical de la línea ~196, pero el warning ayuda a triangular.
                        await _loggingService.LogWarningAsync(
                            message: "R24: failed to map statusValue → SearchHireStatus enum",
                            details: $"SearchHire {searchHireId}: no se pudo mapear statusValue='{statusValue}' al enum. Cae a config NULL y aborta money distribution. Causa típica: typo, valor legacy, o enum no actualizado. Error: {mapEx.Message}",
                            userId: initiatedByUserId ?? searchHire?.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync.R24",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId);
                    }

                    if (config == null)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Missing money distribution config",
                            details: $"Config not found for status {statusValue}",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { Status = statusValue }
                        );
                        return false;
                    }
                }

                // MODIFICACI├ôN: Validar que los porcentajes sumen 100% para evitar distribuciones incorrectas (best practice para configs financieras)
                if (Math.Abs(config.ClientPercentage + config.ExpertPercentage + config.PlatformPercentage - 100m) > 0.01m)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Invalid money distribution config",
                        details: $"Percentages do not sum to 100 for status {statusValue}",
                        userId: initiatedByUserId ?? searchHire.ClientId,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { Status = statusValue, Config = config }
                    );
                    return false;
                }

                // ✅ STRIPE TAX: Calcular sobre BASE PRE-TAX (sin IVA) para distribución interna
                // Esto asegura que las comisiones se calculen sobre el monto real, no sobre el tax
                var baseAmount = searchHire.BaseAmount ?? searchHire.Amount; // Fallback para datos antiguos

                // 🛡️ FIX #4: validar coherencia tax. Si BaseAmount + TaxAmount != Amount (>0.05€ tolerancia),
                // el tax fue mal calculado (drift en Stripe Tax o post-checkout). Log warning para que admin
                // pueda detectar discrepancias antes de reembolsar sobre datos corruptos.
                if (searchHire.BaseAmount.HasValue && searchHire.TaxAmount.HasValue)
                {
                    var expectedAmount = searchHire.BaseAmount.Value + searchHire.TaxAmount.Value;
                    var taxDrift = Math.Abs(expectedAmount - searchHire.Amount);
                    if (taxDrift > 0.05m)
                    {
                        await _loggingService.LogWarningAsync(
                            message: "FIX#4: Tax drift detected — BaseAmount + TaxAmount != Amount",
                            details: $"SearchHire {searchHireId}: Amount={searchHire.Amount}€, BaseAmount={searchHire.BaseAmount}€, TaxAmount={searchHire.TaxAmount}€, Expected (Base+Tax)={expectedAmount}€, Drift={taxDrift:F4}€ (>0.05€). " +
                                    $"El tax pudo haberse aplicado mal (Stripe Tax drift post-checkout). Los cálculos de refund proporcional pueden estar desfasados. " +
                                    $"ACCIÓN ADMIN: revisar PaymentIntent original y reconciliar manualmente si el drift es significativo. Status: {statusValue}, Reason: {reason}.",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync.Fix4",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new
                            {
                                Status = statusValue,
                                Amount = searchHire.Amount,
                                BaseAmount = searchHire.BaseAmount,
                                TaxAmount = searchHire.TaxAmount,
                                ExpectedTotal = expectedAmount,
                                Drift = taxDrift
                            }
                        );
                    }
                }

                if (searchHire.BaseAmount == null)
                {
                    // ⚠️ LOG WARNING: BaseAmount es null (datos antiguos o sin tax calculado)
                    // 🛡️ FIX #6: notifyUser=true para que el cliente sepa que su refund se procesa
                    // sobre datos potencialmente incompletos (no se queda colgado sin explicación).
                    await _loggingService.LogWarningAsync(
                        message: "BaseAmount is null - using Amount as fallback for money distribution",
                        details: $"SearchHire {searchHireId} does not have BaseAmount set. Using Amount ({searchHire.Amount}€) as fallback. " +
                                $"This may result in incorrect commission calculations if tax was included in Amount. " +
                                $"Status: {statusValue}, Reason: {reason}.",
                        userId: initiatedByUserId ?? searchHire.ClientId,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new {
                            Status = statusValue,
                            Reason = reason,
                            Amount = searchHire.Amount,
                            BaseAmount = searchHire.BaseAmount,
                            TaxAmount = searchHire.TaxAmount
                        },
                        // 🛡️ NOTIF-GUARD: detalle técnico interno (fallback de BaseAmount) —
                        // no aporta nada al usuario; solo log/admins.
                        notifyUser: false
                    );
                }

                // ✅ Calcular porcentajes sobre baseAmount (sin tax) para distribución interna
                var clientRefundAmountBase = baseAmount * (config.ClientPercentage / 100);
                var expertAmountBase = baseAmount * (config.ExpertPercentage / 100);
                var platformAmountBase = baseAmount * (config.PlatformPercentage / 100);

                // ✅ STRIPE TAX: Convertir montos base a BRUTO (con IVA proporcional) para Stripe API
                // IMPORTANTE: Solo los REFUNDS usan monto con tax proporcional
                // Los TRANSFERS usan monto base (sin tax) porque el tax ya fue pagado y se remite a autoridades fiscales
                decimal clientRefundAmountForStripe;
                decimal expertAmountForStripe;

                if (config.ClientPercentage == 100)
                {
                    // Reembolso total: devolver el monto exacto que pagó el cliente
                    clientRefundAmountForStripe = searchHire.Amount;
                }
                else if (searchHire.TaxAmount.HasValue && searchHire.TaxAmount.Value > 0 && baseAmount > 0)
                {
                    // Reembolso parcial con tax: calcular proporcionalmente sobre el total con tax
                    // Método: mantener la misma proporción de tax que el pago original
                    clientRefundAmountForStripe = searchHire.Amount * (config.ClientPercentage / 100);
                }
                else
                {
                    // Si no hay tax o es dato antiguo, usar el monto calculado directamente
                    clientRefundAmountForStripe = clientRefundAmountBase;
                }

                // ✅ CORRECCIÓN CRÍTICA: Transfer al experto NO debe incluir tax proporcional
                // El tax ya fue pagado por el cliente y se remite a autoridades fiscales
                // El experto recibe su parte del servicio (base amount), no el tax
                // Stripe transfers son pagos directos, no reembolsos, por lo que no necesitan tax proporcional
                expertAmountForStripe = expertAmountBase; // Siempre usar monto base (sin tax)

                // ✅ Usar montos base para cálculos internos y logs
                var clientRefundAmount = clientRefundAmountBase; // Para logs y cálculos internos
                var expertAmount = expertAmountBase; // Para logs y cálculos internos
                var platformAmount = platformAmountBase; // Para logs y cálculos internos

                // ✅ LOG INFORMATIVO: Breakdown completo de distribución de dinero
                await _loggingService.LogInfoAsync(
                    message: "Money distribution calculation - Stripe Tax aware",
                    details: $"SearchHire {searchHireId} money distribution calculated using BaseAmount (pre-tax). " +
                            $"Original: Amount={searchHire.Amount}€, BaseAmount={searchHire.BaseAmount}€, TaxAmount={searchHire.TaxAmount}€. " +
                            $"Distribution (base): Client={clientRefundAmount:F2}€ ({config.ClientPercentage}%), Expert={expertAmount:F2}€ ({config.ExpertPercentage}%), Platform={platformAmount:F2}€ ({config.PlatformPercentage}%). " +
                            $"Stripe amounts: Client Refund={clientRefundAmountForStripe:F2}€ (with proportional tax), Expert Transfer={expertAmountForStripe:F2}€ (base, no tax). " +
                            $"Status: {statusValue}, Reason: {reason}.",
                    userId: initiatedByUserId ?? searchHire.ClientId,
                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        Status = statusValue,
                        Reason = reason,
                        OriginalAmount = searchHire.Amount,
                        BaseAmount = baseAmount,
                        TaxAmount = searchHire.TaxAmount,
                        ClientRefundAmountBase = clientRefundAmountBase,
                        ExpertAmountBase = expertAmountBase,
                        PlatformAmountBase = platformAmountBase,
                        ClientRefundAmountForStripe = clientRefundAmountForStripe,
                        ExpertAmountForStripe = expertAmountForStripe,
                        ClientPercentage = config.ClientPercentage,
                        ExpertPercentage = config.ExpertPercentage,
                        PlatformPercentage = config.PlatformPercentage
                    }
                );

                // MODIFICACI├ôN: Estimar fees de Stripe y warning si platformAmount no cubre (para evitar p├®rdidas, seg├║n gu├¡as 2025)
                // ✅ Usar baseAmount para calcular fees (fees se calculan sobre el monto base, no sobre tax)
                // 🌍 Currency-aware: el fixed fee de Stripe varía por divisa (EUR 0.25€, GBP 0.20£, USD 0.30$, etc.)
                var stripeFeeEstimate = GetStripeFeeEstimate(baseAmount, searchHire.Currency);
                if (platformAmount < stripeFeeEstimate)
                {
                    // Opcional: Fallar si es cr├¡tico, pero por ahora warning
                }


                // Localizar el pago original
                var servicePayment = await _context.FinancialTransactions
                    .Where(ft => ft.UserId == searchHire.ClientId
                              && ft.TransactionType == "ServicePayment"
                              && ft.RelatedEntityType == "SearchHire"
                              && ft.RelatedEntityId == searchHireId
                              && !string.IsNullOrEmpty(ft.StripePaymentIntentId))
                    .FirstOrDefaultAsync();

                if (servicePayment == null)
                {
                    // ­ƒÜ¿ LOG CR├ìTICO: Pago original no encontrado (una sola vez, con toda la informaci├│n)
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Original payment not found - money distribution failed",
                        details: $"SearchHire {searchHireId} finalization failed because the original payment (ServicePayment) transaction was not found in the database. " +
                                $"This indicates a data consistency issue. " +
                                $"Status: {statusValue}, Reason: {reason}, ClientId: {searchHire.ClientId}, ExpertId: {searchHire.ExpertId}, Amount: {searchHire.Amount}€. " +
                                $"ACTION REQUIRED: Verify FinancialTransactions table for SearchHire {searchHireId} and ServicePayment transaction.",
                        userId: initiatedByUserId ?? searchHire.ClientId,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { 
                            Status = statusValue,
                            Reason = reason,
                            ClientId = searchHire.ClientId,
                            ExpertId = searchHire.ExpertId,
                            Amount = searchHire.Amount,
                            ClientRefundAmount = clientRefundAmount,
                            ExpertTransferAmount = expertAmount,
                            PlatformAmount = platformAmount
                        }
                    );
                    return false;
                }

                // MODIFICACI├ôN: Verificar balance disponible antes de cualquier outflow (best practice Stripe 2025 para evitar negativos)
                // 🛡️ Round 28 — FIX MULTI-DIVISA: el balance Stripe está SEGREGADO por divisa.
                // El comentario antiguo decía "todos los charges al cliente se hacen en EUR" — FALSO:
                // SubscriptionController crea Sessions con `service.Currency.ToLowerInvariant()`. Si el
                // hire es GBP/CHF/USD, el dinero está en el balance de esa divisa, no en EUR. Leer
                // solo `balance.Available["eur"]` bloqueaba falsamente refunds en cualquier divisa ≠ EUR.
                // 🛡️ F4c FIX (2026-07-06): con captura diferida (CaptureStatus="Authorized") el PI NO está
                // capturado: la rama N10 lo CANCELA (cero outflow), pero este pre-check contabilizaba el
                // 100% como si fuera refund y, con balance escaso en esa divisa, bloqueaba la cancelación
                // a 0€ en bucle (spam CRITICAL cada 15 min + tarjeta del cliente retenida hasta que el PI
                // expiraba solo a los 7 días). Si el flag estuviera obsoleto (PI realmente capturado), el
                // refund posterior fallaría en Stripe y lo recogerían las ramas de reintento existentes.
                var skipBalanceCheckAuthorizedPi = string.Equals(searchHire.CaptureStatus, "Authorized", StringComparison.OrdinalIgnoreCase);
                if (!skipBalanceCheckAuthorizedPi)
                try
                {
                    var balanceService = new BalanceService();
                    var balance = await balanceService.GetAsync();
                    // Divisa del hire (snapshot inmutable en SearchHire.Currency). Stripe usa lowercase.
                    var hireCurrencyForBalance = (searchHire.Currency ?? "EUR").Trim().ToLowerInvariant();
                    var availableInHireCurrency = balance.Available?
                        .FirstOrDefault(b => b.Currency == hireCurrencyForBalance)?.Amount / 100.0m ?? 0;
                    // ✅ CORRECCIÓN CRÍTICA: Verificación de balance debe usar montos reales que se enviarán a Stripe
                    // Refund usa monto con tax proporcional, Transfer usa monto base (sin tax)
                    var totalOutflow = clientRefundAmountForStripe + expertAmountBase;
                    if (availableInHireCurrency < totalOutflow)
                    {
                        // ­ƒÜ¿ LOG CR├ìTICO: Balance insuficiente (una sola vez, con informaci├│n completa)
                        // IMPORTANTE: Este log se crea ANTES de entrar en la transacci├│n, as├¡ que debe estar disponible inmediatamente
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Insufficient Stripe platform balance for money distribution",
                            details: $"SearchHire {searchHireId} finalization failed due to insufficient Stripe platform balance. " +
                                    $"Currency={hireCurrencyForBalance.ToUpperInvariant()}, Available Balance: {availableInHireCurrency} {hireCurrencyForBalance.ToUpperInvariant()}, Required Outflow: {totalOutflow} {hireCurrencyForBalance.ToUpperInvariant()} (Client Refund: {clientRefundAmountForStripe:F2} with tax, Expert Transfer: {expertAmountBase:F2} base). " +
                                    $"Distribution Plan: Client={config.ClientPercentage}%, Expert={config.ExpertPercentage}%, Platform={config.PlatformPercentage}%. " +
                                    $"Base amounts: Client={clientRefundAmount:F2}, Expert={expertAmount:F2}, Platform={platformAmount:F2}. " +
                                    $"Status: {statusValue}, Reason: {reason}, PaymentIntentId: {servicePayment.StripePaymentIntentId}. " +
                                    $"ACTION REQUIRED: Wait for balance to be available (from PaymentIntent capture) or manually verify Stripe balance and retry distribution.",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new {
                                Status = statusValue,
                                Reason = reason,
                                Currency = hireCurrencyForBalance.ToUpperInvariant(),
                                AvailableBalance = availableInHireCurrency,
                                TotalOutflow = totalOutflow,
                                ClientRefundAmountBase = clientRefundAmount,
                                ClientRefundAmountForStripe = clientRefundAmountForStripe,
                                ExpertTransferAmountBase = expertAmount,
                                ExpertTransferAmountForStripe = expertAmountForStripe,
                                PlatformAmount = platformAmount,
                                PaymentIntentId = servicePayment.StripePaymentIntentId
                            },
                            notifyUser: true, // 🛡️ FIX #6: avisar al usuario de que el dinero está retenido
                            // 🛡️ NOTIF-GUARD: mensaje pensado para el usuario — el volcado interno
                            // (balance de Stripe, PaymentIntentId, ACTION REQUIRED) solo va a admins.
                            userNotificationMessage: "El movimiento de dinero de tu servicio está tardando un poco más de lo habitual. No tienes que hacer nada: lo reintentaremos automáticamente y te avisaremos cuando se complete."
                        );

                        // Ô£à NO necesitamos delay - LoggingService usa su propio DbContext scoped
                        // que se commitea independientemente de la transacci├│n de RefundService
                        // Esto asegura que el log sea visible inmediatamente post-commit sin interferencia
                        return false;
                    }
                }
                catch (StripeException balanceEx)
                {
                    // ­ƒÜ¿ LOG CR├ìTICO: Error al verificar balance (una sola vez, con toda la informaci├│n)
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Error checking Stripe balance - money distribution failed",
                        details: $"SearchHire {searchHireId} finalization failed due to error checking Stripe platform balance. " +
                                $"Stripe Error: {balanceEx.Message}, Type: {balanceEx.StripeError?.Type}, Code: {balanceEx.StripeError?.Code}. " +
                                $"Required outflow: {clientRefundAmountForStripe + expertAmountBase}€ (Client Refund: {clientRefundAmountForStripe:F2}€ with tax, Expert Transfer: {expertAmountBase:F2}€ base). " +
                                $"ACTION REQUIRED: Verify Stripe balance manually and retry distribution if balance is sufficient.",
                        userId: initiatedByUserId ?? searchHire.ClientId,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { 
                            Status = statusValue,
                            StripeError = balanceEx.Message,
                            StripeErrorType = balanceEx.StripeError?.Type,
                            StripeErrorCode = balanceEx.StripeError?.Code,
                            RequiredOutflow = clientRefundAmountForStripe + expertAmountBase,
                            ClientRefundAmountBase = clientRefundAmount,
                            ClientRefundAmountForStripe = clientRefundAmountForStripe,
                            ExpertTransferAmountBase = expertAmount,
                            ExpertTransferAmountForStripe = expertAmountForStripe
                        }
                    );
                    return false;
                }

                // Ô£à Verificar que el PaymentIntent est├® capturado antes de intentar Transfer
                if (expertAmount > 0)
                {
                    try
                    {
                        // Ô£à Verificar que el PaymentIntent est├® capturado antes de intentar Transfer
                        var paymentIntentService = new PaymentIntentService();
                        var paymentIntent = await paymentIntentService.GetAsync(servicePayment.StripePaymentIntentId);
                        
                        if (paymentIntent.Status != "succeeded")
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Money distribution failed - PaymentIntent not captured",
                                details: $"SearchHire {searchHireId} finalization failed because PaymentIntent {servicePayment.StripePaymentIntentId} is not in 'succeeded' status. " +
                                        $"Current status: {paymentIntent.Status}. " +
                                        $"PENDING TRANSACTIONS TO COMPLETE MANUALLY: " +
                                        $"1) Ensure PaymentIntent is captured " +
                                        $"2) Transfer {expertAmount:F2}€ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) " +
                                        $"3) Platform retains {platformAmount:F2}€.",
                                userId: initiatedByUserId ?? searchHire.ClientId,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new { 
                                    Status = statusValue,
                                    PaymentIntentId = servicePayment.StripePaymentIntentId,
                                    PaymentIntentStatus = paymentIntent.Status,
                                    ExpertTransferAmount = expertAmount,
                                    PlatformAmount = platformAmount,
                                    ExpertId = searchHire.ExpertId
                                }
                            );
                            
                            return false;
                        }
                    }
                    catch (StripeException stripeEx)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Error verifying PaymentIntent for money distribution",
                            details: $"SearchHire {searchHireId} finalization failed due to error verifying PaymentIntent. " +
                                    $"PaymentIntentId: {servicePayment.StripePaymentIntentId}, Error: {stripeEx.Message}",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                PaymentIntentId = servicePayment.StripePaymentIntentId,
                                Error = stripeEx.Message
                            }
                        );
                        
                        return false;
                    }
                    catch (Exception ex)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Error verifying PaymentIntent for money distribution",
                            details: $"SearchHire {searchHireId} finalization failed due to error verifying PaymentIntent. " +
                                    $"PaymentIntentId: {servicePayment.StripePaymentIntentId}, Error: {ex.Message}",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                PaymentIntentId = servicePayment.StripePaymentIntentId,
                                Error = ex.Message
                            }
                        );
                        
                        return false;
                    }
                }

                // ===== FASE 2: CAMBIAR ESTADO (transacci├│n BD r├ípida, separada) =====
                if (updateState)
                {
                    // Ô£à CORRECCI├ôN: Verificar si ya hay una transacci├│n activa (ej: desde AccountDeletionService)
                    var existingTransaction = _context.Database.CurrentTransaction;
                    bool stateUpdateSuccess = false;
                    
                    // Ô£à Si no hay transacci├│n existente, crear una nueva con estrategia de reintento
                    if (existingTransaction == null)
                    {
                        var stateStrategy = _context.Database.CreateExecutionStrategy();
                        stateUpdateSuccess = await stateStrategy.ExecuteAsync(async () =>
                        {
                            using var stateTransaction = await _context.Database.BeginTransactionAsync(
                                System.Data.IsolationLevel.ReadCommitted
                            );
                            try
                            {
                            // Ô£à MEJORA GROK: Cargar entidades expl├¡citamente para evitar null references
                            var searchHireForState = await _context.SearchHires
                                .Include(sh => sh.Status)
                                .Include(sh => sh.Appointment)
                                    .ThenInclude(a => a.Status)
                                .FirstOrDefaultAsync(sh => sh.Id == searchHireId);
                        
                        if (searchHireForState == null)
                        {
                            await stateTransaction.RollbackAsync();
                            return false;
                        }
                        
                        // Ô£à MEJORA GROK: Verificar estado actual (evitar dobles cancelaciones)
                        if (searchHireForState.Status?.IsFinalizationStatus == true)
                        {
                            // Ya est├í finalizado, no cambiar estado pero continuar con dinero
                            await stateTransaction.CommitAsync();
                            // Continuar a Fase 3 para procesar dinero si es necesario
                            return true; // Estado ya estaba finalizado, continuar con dinero
                        }
                        else
                        {
                            // Mapear statusValue a estados finales
                            AppointmentStatus? appointmentStatus = MapAppointmentStatus(statusValue);
                            
                            // Ô£à MEJORA: Verificar si el estado objetivo ya est├í aplicado (evitar cambios redundantes)
                            bool stateNeedsUpdate = false;
                            
                            // Verificar Appointment.Status
                            if (appointmentStatus.HasValue && searchHireForState.Appointment != null)
                            {
                                var appointmentStatusRow = await _context.SystemStatuses
                                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                             s.StatusValue == statusValue);
                                if (appointmentStatusRow != null)
                                {
                                    // Ô£à Verificar si el estado actual es diferente al objetivo
                                    if (searchHireForState.Appointment.StatusId != appointmentStatusRow.Id)
                                    {
                                        searchHireForState.Appointment.StatusId = appointmentStatusRow.Id;
                                        searchHireForState.Appointment.UpdatedAt = DateTime.UtcNow;
                                        stateNeedsUpdate = true;
                                    }

                                    // 🗓️ P0 FIX: liberar el hueco del calendario al finalizar.
                                    // Cualquier estado terminal (cancelación o completado) que reparte
                                    // dinero por aquí ya no debe bloquear la agenda del experto. Sin esto,
                                    // los desenlaces negativos de la confirmación del experto
                                    // (reject / seller decline / timeout) — que finalizan vía esta
                                    // función con updateState:true — dejaban BlocksCalendar=true para
                                    // siempre y la exclusion constraint GiST impedía re-reservar esa
                                    // franja futura (hueco fantasma). Idempotente: solo flippea si está a true.
                                    // Mismo patrón que AppointmentService.CancelAppointmentAsync (FASE D · P0).
                                    if (searchHireForState.Appointment.BlocksCalendar)
                                    {
                                        searchHireForState.Appointment.BlocksCalendar = false;
                                        stateNeedsUpdate = true;
                                    }
                                }
                            }
                            
                            // Verificar SearchHire.Status
                            var targetSearchHireStatus = appointmentStatus.HasValue 
                                ? await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatus.Value)
                                : null;
                            
                            string? targetSearchHireStatusValue = null;
                            if (!targetSearchHireStatus.HasValue)
                            {
                                // Si no hay mapeo de AppointmentStatus, usar statusValue directamente
                                targetSearchHireStatusValue = statusValue;
                            }
                            else
                            {
                                targetSearchHireStatusValue = SearchHireStatusExtensions.ToStringValue(targetSearchHireStatus.Value);
                            }
                            
                            if (!string.IsNullOrEmpty(targetSearchHireStatusValue))
                            {
                                var searchHireStatusRow = await _context.SystemStatuses
                                    .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && 
                                                             s.StatusValue == targetSearchHireStatusValue);
                                if (searchHireStatusRow != null)
                                {
                                    // Ô£à Verificar si el estado actual es diferente al objetivo
                                    if (searchHireForState.StatusId != searchHireStatusRow.Id)
                                    {
                                        // 📜 Round 9 — A2: registrar transición ANTES de mutar
                                        var oldStatusForAudit = searchHireForState.StatusId;
                                        searchHireForState.StatusId = searchHireStatusRow.Id;
                                        searchHireForState.UpdatedAt = DateTime.UtcNow;
                                        stateNeedsUpdate = true;
                                        if (_statusAudit != null)
                                        {
                                            await _statusAudit.RecordTransitionAsync(
                                                searchHireId: searchHireForState.Id,
                                                oldStatusId: oldStatusForAudit,
                                                newStatusId: searchHireStatusRow.Id,
                                                changedByUserId: initiatedByUserId,
                                                source: "StripeRefundService.ProcessMoneyDistributionAsync.Phase2",
                                                reason: reason,
                                                additionalData: new { TargetStatusValue = targetSearchHireStatusValue, StatusValue = statusValue });
                                        }
                                    }
                                }
                            }

                            // Solo hacer SaveChanges si realmente hay cambios
                            if (stateNeedsUpdate)
                            {
                                await _context.SaveChangesAsync();
                            }
                            await stateTransaction.CommitAsync();
                            // Ô£à Estado verificado/actualizado y commiteado
                            return true; // Estado actualizado exitosamente
                        }
                    }
                    catch (DbUpdateConcurrencyException ex)
                    {
                        // Ô£à MEJORA GROK: Manejo espec├¡fico de concurrencia
                        await stateTransaction.RollbackAsync();
                        // Usar searchHire ya cargado o usar initiatedByUserId como fallback
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Concurrency conflict updating state",
                            details: $"Another process modified SearchHire {searchHireId} concurrently. Error: {ex.Message}",
                            userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                Error = ex.Message,
                                ErrorType = ex.GetType().Name
                            }
                        );
                        return false; // NO procesar dinero si no pudimos cambiar estado
                    }
                    catch (Exception ex)
                    {
                        // Error de BD al cambiar estado ÔåÆ Revertir
                        await stateTransaction.RollbackAsync();
                        // Usar searchHire ya cargado o usar initiatedByUserId como fallback
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Failed to update state before money distribution",
                            details: $"SearchHire {searchHireId} state update failed: {ex.Message}. StackTrace: {ex.StackTrace}",
                            userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                Error = ex.Message,
                                ErrorType = ex.GetType().Name,
                                StackTrace = ex.StackTrace
                            }
                        );
                        return false; // NO procesar dinero si no pudimos cambiar estado
                    }
                    });
                    }
                    else
                    {
                        // Ô£à Usar transacci├│n existente - ejecutar sin crear nueva transacci├│n
                        try
                        {
                            // Ô£à MEJORA GROK: Cargar entidades expl├¡citamente para evitar null references
                            var searchHireForState = await _context.SearchHires
                                .Include(sh => sh.Status)
                                .Include(sh => sh.Appointment)
                                    .ThenInclude(a => a.Status)
                                .FirstOrDefaultAsync(sh => sh.Id == searchHireId);
                        
                            if (searchHireForState == null)
                            {
                                stateUpdateSuccess = false;
                            }
                            else if (searchHireForState.Status?.IsFinalizationStatus == true)
                            {
                                // Ya est├í finalizado, no cambiar estado pero continuar con dinero
                                stateUpdateSuccess = true; // Estado ya estaba finalizado, continuar con dinero
                            }
                            else
                            {
                                // Mapear statusValue a estados finales
                                AppointmentStatus? appointmentStatus = MapAppointmentStatus(statusValue);
                                
                                // Ô£à MEJORA: Verificar si el estado objetivo ya est├í aplicado (evitar cambios redundantes)
                                bool stateNeedsUpdate = false;
                                
                                // Verificar Appointment.Status
                                if (appointmentStatus.HasValue && searchHireForState.Appointment != null)
                                {
                                    var appointmentStatusRow = await _context.SystemStatuses
                                        .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                                 s.StatusValue == statusValue);
                                    if (appointmentStatusRow != null)
                                    {
                                        // Ô£à Verificar si el estado actual es diferente al objetivo
                                        if (searchHireForState.Appointment.StatusId != appointmentStatusRow.Id)
                                        {
                                            searchHireForState.Appointment.StatusId = appointmentStatusRow.Id;
                                            searchHireForState.Appointment.UpdatedAt = DateTime.UtcNow;
                                            stateNeedsUpdate = true;
                                        }

                                        // 🗓️ P0 FIX (espejo de la rama sin-tx): liberar el hueco del calendario al
                                        // finalizar. Cualquier estado terminal que reparte dinero por aquí ya no debe
                                        // bloquear la agenda del experto. Idempotente (solo flippea si está a true).
                                        if (searchHireForState.Appointment.BlocksCalendar)
                                        {
                                            searchHireForState.Appointment.BlocksCalendar = false;
                                            stateNeedsUpdate = true;
                                        }
                                    }
                                }

                                // Verificar SearchHire.Status
                                var targetSearchHireStatus = appointmentStatus.HasValue
                                    ? await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatus.Value)
                                    : null;

                                string? targetSearchHireStatusValue = null;
                                if (!targetSearchHireStatus.HasValue)
                                {
                                    // Si no hay mapeo de AppointmentStatus, usar statusValue directamente
                                    targetSearchHireStatusValue = statusValue;
                                }
                                else
                                {
                                    targetSearchHireStatusValue = SearchHireStatusExtensions.ToStringValue(targetSearchHireStatus.Value);
                                }
                                
                                if (!string.IsNullOrEmpty(targetSearchHireStatusValue))
                                {
                                    var searchHireStatusRow = await _context.SystemStatuses
                                        .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" &&
                                                                 s.StatusValue == targetSearchHireStatusValue);
                                    if (searchHireStatusRow != null)
                                    {
                                        // Ô£à Verificar si el estado actual es diferente al objetivo
                                        if (searchHireForState.StatusId != searchHireStatusRow.Id)
                                        {
                                            // 📜 Round 9 — A2: audit log de la transición
                                            var oldStatusForAudit = searchHireForState.StatusId;
                                            searchHireForState.StatusId = searchHireStatusRow.Id;
                                            searchHireForState.UpdatedAt = DateTime.UtcNow;
                                            stateNeedsUpdate = true;
                                            if (_statusAudit != null)
                                            {
                                                await _statusAudit.RecordTransitionAsync(
                                                    searchHireId: searchHireForState.Id,
                                                    oldStatusId: oldStatusForAudit,
                                                    newStatusId: searchHireStatusRow.Id,
                                                    changedByUserId: initiatedByUserId,
                                                    source: "StripeRefundService.ProcessMoneyDistributionAsync.Phase2.Retry",
                                                    reason: reason,
                                                    additionalData: new { TargetStatusValue = targetSearchHireStatusValue, StatusValue = statusValue });
                                            }
                                        }
                                    }
                                }

                                // Solo hacer SaveChanges si realmente hay cambios
                                if (stateNeedsUpdate)
                                {
                                    await _context.SaveChangesAsync();
                                }
                                // Ô£à Estado verificado/actualizado (sin commit - usa transacci├│n existente)
                                stateUpdateSuccess = true; // Estado actualizado exitosamente
                            }
                        }
                        catch (DbUpdateConcurrencyException ex)
                        {
                            // Ô£à MEJORA GROK: Manejo espec├¡fico de concurrencia
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Concurrency conflict updating state",
                                details: $"Another process modified SearchHire {searchHireId} concurrently. Error: {ex.Message}. " +
                                        $"Note: Using existing transaction from caller, rollback will be handled by caller.",
                                userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new { 
                                    Status = statusValue,
                                    Error = ex.Message,
                                    ErrorType = ex.GetType().Name,
                                    UsingExistingTransaction = true
                                }
                            );
                            stateUpdateSuccess = false;
                        }
                        catch (Exception ex)
                        {
                            // Error de BD al cambiar estado
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Failed to update state before money distribution",
                                details: $"SearchHire {searchHireId} state update failed: {ex.Message}. StackTrace: {ex.StackTrace}. " +
                                        $"Note: Using existing transaction from caller, rollback will be handled by caller.",
                                userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new { 
                                    Status = statusValue,
                                    Error = ex.Message,
                                    ErrorType = ex.GetType().Name,
                                    StackTrace = ex.StackTrace,
                                    UsingExistingTransaction = true
                                }
                            );
                            stateUpdateSuccess = false;
                        }
                    }

                    // Ô£à Verificar si el cambio de estado fue exitoso
                    if (!stateUpdateSuccess)
                    {
                        // ÔÜá´©Å FALLBACK: Si fall├│ el cambio de estado, intentar cambiarlo manualmente para evitar bloqueos
                        // Esto es cr├¡tico para evitar que el sistema quede bloqueado
                        try
                        {
                            var fallbackSearchHire = await _context.SearchHires
                                .Include(sh => sh.Status)
                                .Include(sh => sh.Appointment)
                                    .ThenInclude(a => a.Status)
                                .FirstOrDefaultAsync(sh => sh.Id == searchHireId);
                            
                            if (fallbackSearchHire != null && fallbackSearchHire.Status?.IsFinalizationStatus != true)
                            {
                                // Mapear statusValue a estados finales
                                AppointmentStatus? appointmentStatus = MapAppointmentStatus(statusValue);
                                
                                // Cambiar Appointment.Status si aplica
                                if (appointmentStatus.HasValue && fallbackSearchHire.Appointment != null)
                                {
                                    var appointmentStatusRow = await _context.SystemStatuses
                                        .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                                 s.StatusValue == statusValue);
                                    if (appointmentStatusRow != null && fallbackSearchHire.Appointment.StatusId != appointmentStatusRow.Id)
                                    {
                                        fallbackSearchHire.Appointment.StatusId = appointmentStatusRow.Id;
                                        fallbackSearchHire.Appointment.UpdatedAt = DateTime.UtcNow;
                                    }
                                    // 🗓️ P0 FIX (espejo en el camino degradado): liberar el hueco también aquí.
                                    // Este fallback es el que de verdad podía re-introducir el hueco fantasma si la
                                    // Fase 2 normal falló: finalizaba el estado pero dejaba BlocksCalendar=true.
                                    if (appointmentStatusRow != null && fallbackSearchHire.Appointment.BlocksCalendar)
                                    {
                                        fallbackSearchHire.Appointment.BlocksCalendar = false;
                                    }
                                }
                                
                                // Cambiar SearchHire.Status
                                var targetSearchHireStatus = appointmentStatus.HasValue 
                                    ? await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatus.Value)
                                    : null;
                                
                                string? targetSearchHireStatusValue = null;
                                if (!targetSearchHireStatus.HasValue)
                                {
                                    targetSearchHireStatusValue = statusValue;
                                }
                                else
                                {
                                    targetSearchHireStatusValue = SearchHireStatusExtensions.ToStringValue(targetSearchHireStatus.Value);
                                }
                                
                                if (!string.IsNullOrEmpty(targetSearchHireStatusValue))
                                {
                                    var searchHireStatusRow = await _context.SystemStatuses
                                        .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && 
                                                                 s.StatusValue == targetSearchHireStatusValue);
                                    if (searchHireStatusRow != null && fallbackSearchHire.StatusId != searchHireStatusRow.Id)
                                    {
                                        fallbackSearchHire.StatusId = searchHireStatusRow.Id;
                                        fallbackSearchHire.UpdatedAt = DateTime.UtcNow;
                                    }
                                }
                                
                                await _context.SaveChangesAsync();
                                
                                await _loggingService.LogWarningAsync(
                                    message: "State updated manually after ProcessMoneyDistributionAsync state phase failure",
                                    details: $"SearchHire {searchHireId} state was manually updated as fallback because ProcessMoneyDistributionAsync failed in Phase 2 (state change). " +
                                            $"This prevents the system from being blocked. Status changed to: {targetSearchHireStatusValue ?? statusValue}",
                                    userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { 
                                        Status = statusValue,
                                        FallbackStateChange = true
                                    }
                                );
                                
                                // Continuar con procesamiento de dinero aunque haya fallado la Fase 2
                                // El estado ya est├í cambiado, as├¡ que podemos intentar procesar el dinero
                            }
                        }
                        catch (Exception fallbackEx)
                        {
                            // Si el fallback tambi├®n falla, log cr├¡tico pero continuar
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Fallback state update also failed",
                                details: $"SearchHire {searchHireId} state update failed in both main phase and fallback. " +
                                        $"Fallback error: {fallbackEx.Message}. " +
                                        $"System may be blocked. Manual intervention required.",
                                userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new {
                                    Status = statusValue,
                                    FallbackError = fallbackEx.Message
                                }
                            );
                            // 🛡️ F24 FIX: NO continuar a Fase 3 moviendo dinero si el estado NUNCA se persistió.
                            // Antes seguíamos a la distribución de dinero aunque el SaveChanges del fallback de estado
                            // lanzó → dinero movido sin estado confirmado. Encolamos el reintento idempotente (sus
                            // guards evitan doble pago) y abortamos esta ejecución para no mover dinero sin estado.
                            Hangfire.BackgroundJob.Schedule<StripeRefundService>(
                                s => s.RetryMoneyDistributionJobAsync(
                                    searchHireId,
                                    statusValue,
                                    reason,
                                    initiatedByUserId),
                                TimeSpan.FromMinutes(2));
                            return false;
                        }
                    }
                }

                // ===== FASE 3: PROCESAR DINERO (fuera de transacci├│n de estado) =====
                // Orquestaci├│n bajo estrategia de reintento y transacci├│n
                // Ô£à CORRECCI├ôN: Verificar si ya hay una transacci├│n activa ANTES de usar CreateExecutionStrategy
                var existingTransactionForMoney = _context.Database.CurrentTransaction;
                
                // Ô£à Funci├│n auxiliar para procesar dinero (reutilizable)
                async Task<bool> ProcessMoneyAsync()
                {
                    IDbContextTransaction transaction = null;
                    if (existingTransactionForMoney == null)
                    {
                        transaction = await _context.Database.BeginTransactionAsync();
                    }
                    // MODIFICACI├ôN: Declarar variables fuera del try para acceso en catch blocks
                    string createdTransferId = null;
                    string createdRefundId = null;

                    try
                    {
                        // 🛡️ FIX TX-7 (2026-06-11): serializar TODA la fase de dinero por hire.
                        // El lock de Fase 1 vive en una micro-tx que se commitea ANTES de llegar
                        // aquí cuando el caller NO trae transacción exterior (ResolveDispute,
                        // RetryMoneyDistributionJobAsync): los guards existingRefund/existingTransfer
                        // y las llamadas Stripe corrían SIN lock → dos distribuciones concurrentes
                        // del MISMO hire con statusValue distinto (retry viejo + resolución nueva)
                        // pasaban ambas los guards (check-then-act) y, al usar claves de idempotencia
                        // distintas, AMBAS movían dinero (hasta ~193% del hire). Tomar el advisory
                        // lock DENTRO de esta transacción lo ata hasta el commit (que incluye las
                        // FTs), así el segundo flujo espera y sus guards ven las FTs ya commiteadas.
                        // Reentrante: los callers con tx exterior (complete-service, cancel) ya lo
                        // tienen de Fase 1 — re-tomarlo en la misma sesión es no-op acumulativo.
                        await _context.Database.ExecuteSqlInterpolatedAsync(
                            $"SELECT pg_advisory_xact_lock({(long)searchHireId})");
                        // ✅ CRÍTICO: Verificar si el dinero ya fue procesado (prevenir duplicados)
                        var existingRefund = await _context.FinancialTransactions
                            .FirstOrDefaultAsync(ft => ft.RelatedEntityType == "SearchHire" &&
                                                       ft.RelatedEntityId == searchHireId &&
                                                       ft.TransactionType == "Refund" &&
                                                       ft.StripePaymentIntentId == servicePayment.StripePaymentIntentId);

                        // 🛡️ W1 FIX (Round 8 A15): SUM check para refunds acumulativos.
                        // El guard `existingRefund` solo verifica "¿existe UN refund?" pero NO valida
                        // que la suma de refunds previos + el nuevo refund <= Amount original del
                        // ServicePayment. Escenario malo: admin parcial refund 50€ + chargeback auto
                        // 60€ = 110€ > 100€ ServicePayment → cliente sobre-reembolsado, plataforma
                        // pierde dinero. Validar SUMA aquí antes de procesar. Idempotente: re-llamar
                        // con misma key no hace nada porque Stripe lo deduplica, pero protegemos en BD.
                        var sumOfExistingRefunds = await _context.FinancialTransactions
                            .Where(ft => ft.RelatedEntityType == "SearchHire" &&
                                         ft.RelatedEntityId == searchHireId &&
                                         ft.TransactionType == "Refund" &&
                                         ft.StripePaymentIntentId == servicePayment.StripePaymentIntentId)
                            .SumAsync(ft => ft.Amount);

                        var serviceAmountAbs = Math.Abs(servicePayment.Amount); // ServicePayment es negativo
                        var sumExistingAbs = Math.Abs(sumOfExistingRefunds);    // Refunds son positivos pero validamos abs
                        var newRefundAbs = clientRefundAmountForStripe;          // Lo que SE VA a reembolsar

                        if (sumExistingAbs + newRefundAbs > serviceAmountAbs + 0.01m) // tolerancia 1 céntimo redondeo
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL W1: Refund acumulativo excede ServicePayment original — bloqueado",
                                details: $"SearchHire {searchHireId}: sumPriorRefunds={sumExistingAbs:F2}€, newRefund={newRefundAbs:F2}€, total={sumExistingAbs + newRefundAbs:F2}€ > ServicePayment={serviceAmountAbs:F2}€. Stripe PI: {servicePayment.StripePaymentIntentId}. NO se procesa. ACCIÓN ADMIN: revisar manualmente — posible doble admin refund o chargeback duplicado.",
                                userId: searchHire.ClientId,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync.W1",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new
                                {
                                    SearchHireId = searchHireId,
                                    SumPriorRefunds = sumExistingAbs,
                                    NewRefundAmount = newRefundAbs,
                                    ServicePaymentAmount = serviceAmountAbs,
                                    StripePaymentIntentId = servicePayment.StripePaymentIntentId
                                });
                            return false; // Abort: no procesar refund que excede
                        }
                        
                        var existingTransfer = await _context.FinancialTransactions
                            .FirstOrDefaultAsync(ft => ft.RelatedEntityType == "SearchHire" &&
                                                       ft.RelatedEntityId == searchHireId &&
                                                       ft.TransactionType == "Payout" &&
                                                       !string.IsNullOrEmpty(ft.StripeTransferId));
                        
                        // Si ya existe refund o transfer, verificar si es necesario procesar de nuevo
                        bool refundAlreadyProcessed = existingRefund != null && !string.IsNullOrEmpty(existingRefund.StripeRefundId);
                        bool transferAlreadyProcessed = existingTransfer != null && !string.IsNullOrEmpty(existingTransfer.StripeTransferId);

                        // 🛡️ T4 FIX (auditoría 2026-07-06): si el transfer "ya procesado" en realidad FALLÓ
                        // (webhook transfer.failed insertó el marcador FT "TransferFailed" para su
                        // StripeTransferId), el guard de arriba lo daba por bueno y el retry del watchdog
                        // F18 era un no-op eterno: el experto nunca se re-pagaba y F18 re-encolaba en bucle.
                        // NO auto-creamos un transfer nuevo (riesgo de doble pago si el fallo fue espurio o
                        // el dinero ya se movió por otra vía): marcamos RequiresManualReview UNA vez (F18
                        // excluye ahora los hires marcados → el bucle para) y alertamos con la acción admin.
                        // El dinero queda RETENIDO en la plataforma, no perdido.
                        if (transferAlreadyProcessed && existingTransfer != null && !searchHire.RequiresManualReview)
                        {
                            var transferMarkedFailed = await _context.FinancialTransactions.AnyAsync(ft =>
                                ft.RelatedEntityType == "SearchHire" &&
                                ft.RelatedEntityId == searchHireId &&
                                ft.TransactionType == "TransferFailed" &&
                                ft.StripeTransferId == existingTransfer.StripeTransferId);
                            if (transferMarkedFailed)
                            {
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL T4: Transfer al experto FALLIDO detectado en retry - requiere re-pago manual",
                                    details: $"SearchHire {searchHireId}: el Payout registrado apunta al transfer {existingTransfer.StripeTransferId}, que Stripe reportó FALLIDO (FT TransferFailed). El retry automático NO re-paga (el guard lo trata como procesado y crear otro transfer automáticamente arriesga doble pago). Importe retenido por la plataforma: {Math.Abs(existingTransfer.Amount):F2} {searchHire.Currency ?? "EUR"}. ACCIÓN ADMIN: verificar el transfer en el Dashboard de Stripe y re-pagar manualmente al experto (UserId {searchHire.ExpertId}). Se marca RequiresManualReview (para el digest diario y para frenar el bucle de F18).",
                                    userId: searchHire.ExpertId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync.T4",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId);
                                searchHire.RequiresManualReview = true;
                                try { await _context.SaveChangesAsync(); }
                                catch (DbUpdateConcurrencyException)
                                {
                                    try
                                    {
                                        await _context.Entry(searchHire).ReloadAsync();
                                        searchHire.RequiresManualReview = true;
                                        await _context.SaveChangesAsync();
                                    }
                                    catch { /* best-effort: el LogCritical ya alertó */ }
                                }
                                catch { /* best-effort: el LogCritical ya alertó */ }
                            }
                        }
                        
                        // 🔁 A2: ¿queda un CLAWBACK pendiente? (refund hecho + transfer al experto hecho,
                        // pero su nueva parte es MENOR que lo transferido y aún NO se revirtió). Si lo hay, NO
                        // cortocircuitar como "ya procesado" — antes el guard devolvía true y un clawback que
                        // falló tras el refund quedaba ABANDONADO en el reintento → el experto se quedaba el
                        // sobre-pago y la plataforma perdía ~85%.
                        bool clawbackPending = false;
                        if (transferAlreadyProcessed && existingTransfer != null
                            && clientRefundAmount > 0
                            && (Math.Abs(existingTransfer.Amount) - expertAmountForStripe) >= 0.01m)
                        {
                            var clawbackAlreadyDone = await _context.FinancialTransactions.AnyAsync(ft =>
                                ft.RelatedEntityType == "SearchHire" &&
                                ft.RelatedEntityId == searchHireId &&
                                ft.TransactionType == "TransferReversal" &&
                                ft.StripeTransferId == existingTransfer.StripeTransferId);
                            clawbackPending = !clawbackAlreadyDone;
                        }

                        // Si ambos ya están procesados (y NO queda clawback pendiente), retornar true (idempotencia)
                        if (refundAlreadyProcessed && (transferAlreadyProcessed || expertAmount == 0) && !clawbackPending)
                        {
                            await _loggingService.LogInfoAsync(
                                message: "Money distribution already processed - idempotent call",
                                details: $"SearchHire {searchHireId} money distribution was already processed. " +
                                        $"Refund: {(refundAlreadyProcessed ? $"Already processed ({existingRefund.StripeRefundId})" : "Not needed")}, " +
                                        $"Transfer: {(transferAlreadyProcessed ? $"Already processed ({existingTransfer.StripeTransferId})" : expertAmount == 0 ? "Not needed" : "Not processed")}. " +
                                        $"Status: {statusValue}, Reason: {reason}",
                                userId: initiatedByUserId ?? searchHire.ClientId,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new { 
                                    Status = statusValue,
                                    RefundAlreadyProcessed = refundAlreadyProcessed,
                                    TransferAlreadyProcessed = transferAlreadyProcessed,
                                    ExistingRefundId = existingRefund?.StripeRefundId,
                                    ExistingTransferId = existingTransfer?.StripeTransferId
                                }
                            );
                            
                            // 🛡️ M5b FIX (2026-07-06): el dinero ya está movido → limpiar RefundFailedAt.
                            // Nunca se limpiaba tras un reintento exitoso, así que el watchdog M5
                            // (RetryRefundFailedHiresAsync) re-encolaba el hire durante 7 días aunque
                            // el refund/transfer ya se hubiera completado.
                            if (searchHire.RefundFailedAt != null)
                            {
                                searchHire.RefundFailedAt = null;
                                try { await _context.SaveChangesAsync(); } catch { /* best-effort: el próximo éxito lo limpia */ }
                            }
                            if (transaction != null)
                            {
                                await transaction.CommitAsync();
                            }
                            return true; // Ô£à Ya procesado, retornar ├®xito
                        }
                        
                        // Si solo uno est├í procesado, log warning pero continuar con el que falta
                        if (refundAlreadyProcessed || transferAlreadyProcessed)
                        {
                            await _loggingService.LogWarningAsync(
                                message: "Partial money distribution detected - processing missing transactions",
                                details: $"SearchHire {searchHireId} has partial money distribution. " +
                                        $"Refund: {(refundAlreadyProcessed ? $"Already processed ({existingRefund.StripeRefundId})" : "Needs processing")}, " +
                                        $"Transfer: {(transferAlreadyProcessed ? $"Already processed ({existingTransfer.StripeTransferId})" : "Needs processing")}. " +
                                        $"Processing missing transactions only.",
                                userId: initiatedByUserId ?? searchHire.ClientId,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new { 
                                    Status = statusValue,
                                    RefundAlreadyProcessed = refundAlreadyProcessed,
                                    TransferAlreadyProcessed = transferAlreadyProcessed
                                }
                            );
                        }

                        // 🔑 IDEMPOTENCIA (frente 7). El anti-doble-pago REAL lo dan: (1) el FOR UPDATE sobre
                        // el hire (línea ~48, serializa finalizaciones concurrentes del MISMO hire) y (2) el guard
                        // de fila Payout/Refund de arriba (líneas ~916-951, corta como "ya procesado"). La clave
                        // de Stripe es defensa SECUNDARIA contra reintentos del MISMO movimiento.
                        var idempotencyKey = $"md-{searchHireId}";
                        // 🔧 FIX P5: clave de transfer/refund discriminada SOLO por estado lógico (statusValue),
                        // NO por importe. El importe (expertAmountForStripe = baseAmount * %) puede derivar 1
                        // céntimo entre reintentos del MISMO movimiento (fallback BaseAmount??Amount, cambio de
                        // BaseAmount/TaxAmount, redondeo Math.Round) → si lo metemos en la clave, un reintento
                        // legítimo (Hangfire) genera clave nueva, Stripe NO deduplica → DOBLE TRANSFER/REFUND.
                        // statusValue basta para discriminar operaciones lógicas distintas sobre el mismo hire:
                        // cada statusValue de finalización mapea 1:1 a un reparto fijo (StatusConfigurations), así
                        // que un revert con otro importe SIEMPRE lleva otro statusValue (no colisiona), y dos
                        // ejecuciones con el mismo statusValue son el mismo movimiento (deben deduplicar).
                        var transferIdempotencyKey = $"md-{searchHireId}-transfer-{statusValue}";
                        var refundIdempotencyKey = $"md-{searchHireId}-refund-{statusValue}";

                        // 🔁 A3: si hubo un CHARGEBACK (contracargo) en este pago, Stripe YA devolvió el dinero
                        // al cliente. NO crear un refund interno encima → evita el DOBLE reembolso (chargeback +
                        // resolución interna de disputa). Si el experto ya fue pagado, su transfer aún debe
                        // revertirse (alertado por el handler del chargeback / clawback manual).
                        var hasChargeback = await _context.FinancialTransactions.AnyAsync(ft =>
                            ft.RelatedEntityType == "SearchHire" &&
                            ft.RelatedEntityId == searchHireId &&
                            ft.TransactionType == "Chargeback" &&
                            ft.StripePaymentIntentId == servicePayment.StripePaymentIntentId);
                        if (hasChargeback && clientRefundAmount > 0)
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Chargeback present - skipping internal client refund to avoid double refund",
                                details: $"SearchHire {searchHireId}: existe un marcador de chargeback para PaymentIntent {servicePayment.StripePaymentIntentId}. Stripe ya devolvió fondos al cliente vía el contracargo, así que el refund interno se OMITE (status {statusValue}). Si el experto ya cobró, su transfer aún necesita reversión (clawback/manual).",
                                userId: initiatedByUserId ?? searchHire.ClientId,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId);
                        }

                        // Si hay refund y transfer, ejecutar primero la transferencia y despu├®s el refund; si el refund falla, revertir la transferencia
                        var needsRefund = clientRefundAmount > 0 && !refundAlreadyProcessed && !hasChargeback;
                        var needsTransfer = expertAmount > 0 && searchHire.ExpertId.HasValue && !transferAlreadyProcessed && !hasChargeback;
                        // 🛡️ FIX [W19-REFUNDFAILED-CLEAR] (auditoría 2026-07-13): las ramas de aborto de transfer
                        // (404 de cuenta, transfers-disabled, currency-mismatch) fijan RefundFailedAt=NOW para
                        // marcar el payout del experto como RETENIDO y que las redes de reintento lo recojan.
                        // Pero en un hire 'completed' puro (0/95/5 → needsRefund=false) la ejecución alcanza el
                        // clear M5b de RefundFailedAt (~L2600) y BORRABA el flag recién puesto, cegando a
                        // RetryRefundFailedHiresAsync (filtra RefundFailedAt!=null) y a la recuperación MUD-CB-2
                        // al reactivar la cuenta (idem). Este flag distingue "aborto-y-retención en ESTA pasada"
                        // del clear legítimo de un RefundFailedAt viejo tras mover dinero con éxito.
                        bool transferDeferredForManualReview = false;

                        // 🛡️ FIX (fuga de principal): re-leer el marcador Chargeback JUSTO antes de CREAR un transfer
                        // NUEVO al experto. Si hubo contracargo (Stripe ya devolvió el 100% al cliente y lo retiró del
                        // balance de la plataforma) y el experto AÚN no había cobrado (transferAlreadyProcessed=false),
                        // crear el transfer aquí = doble salida del ~95% (pérdida real). El clawback de ~l.2008 y
                        // ReverseExpertTransferForChargebackAsync solo revierten un transfer EXISTENTE; no cubren uno nuevo.
                        // Paralelo a FIX #2 (refund) y FIX #9 (clawback): cierra la ventana de carrera con el webhook
                        // charge.dispute.created que pudo insertar el FT Chargeback tras la lectura inicial (~l.1291).
                        if (needsTransfer)
                        {
                            var chargebackBeforeTransfer = hasChargeback
                                || await _context.FinancialTransactions.AnyAsync(ft =>
                                    ft.RelatedEntityType == "SearchHire" &&
                                    ft.RelatedEntityId == searchHireId &&
                                    ft.TransactionType == "Chargeback" &&
                                    ft.StripePaymentIntentId == servicePayment.StripePaymentIntentId);
                            if (chargebackBeforeTransfer)
                            {
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Chargeback present - skipping NEW expert transfer to avoid double loss of principal",
                                    details: $"SearchHire {searchHireId}: existe un marcador Chargeback para PaymentIntent {servicePayment.StripePaymentIntentId} y el experto aún no había cobrado (transferAlreadyProcessed=false). Stripe ya retiró el 100% del balance de la plataforma vía el contracargo; crear un transfer nuevo de {expertAmount:F2} al experto sería una SEGUNDA salida (pérdida real). Se OMITE el transfer (status {statusValue}). ACCIÓN ADMIN: reconciliar manualmente si el experto realmente debe cobrar pese al chargeback.",
                                    userId: searchHire.ExpertId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId);
                                needsTransfer = false;
                            }

                            // 🛡️ T1 FIX (auditoría 2026-07-06, fuga de principal): mismo guard pero para
                            // REFUNDS que ya cubren el 100% del cargo. Un refund EXTERNO total (Dashboard de
                            // Stripe / API / auto-refund de Radar) inserta FT "Refund" vía HandleChargeRefunded
                            // pero NO finaliza el hire ni deja marcador "Chargeback" — si el hire completa
                            // después, hasChargeback no corta y se transferiría ~95% de un cargo ya devuelto
                            // al 100% (salida total ≈195%, sin sanación: la reversal encolada por el webhook
                            // fue no-op porque el transfer aún no existía). Re-leemos la suma AQUÍ (no la de
                            // ~l.1216) para cerrar la ventana con un webhook concurrente, igual que el re-read
                            // del chargeback de arriba. Un reparto legítimo con transfer nunca tiene refunds
                            // por el 100% (los porcentajes suman 100), así que este guard no bloquea retries
                            // parciales (p.ej. tramo 50/50 con refund ya hecho y transfer pendiente).
                            if (needsTransfer)
                            {
                                var refundedSumBeforeTransfer = Math.Abs(await _context.FinancialTransactions
                                    .Where(ft => ft.RelatedEntityType == "SearchHire" &&
                                                 ft.RelatedEntityId == searchHireId &&
                                                 ft.TransactionType == "Refund" &&
                                                 ft.StripePaymentIntentId == servicePayment.StripePaymentIntentId)
                                    .SumAsync(ft => ft.Amount));
                                if (refundedSumBeforeTransfer >= serviceAmountAbs - 0.01m)
                                {
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL T1: Charge fully refunded - skipping NEW expert transfer to avoid double loss of principal",
                                        details: $"SearchHire {searchHireId}: la suma de FT Refund ({refundedSumBeforeTransfer:F2}) ya cubre el ServicePayment ({serviceAmountAbs:F2}) del PaymentIntent {servicePayment.StripePaymentIntentId} — probable refund EXTERNO total (Dashboard/API/Radar) previo a la finalización. Crear un transfer nuevo de {expertAmount:F2} al experto sería una SEGUNDA salida (pérdida real). Se OMITE el transfer (status {statusValue}) y se marca RequiresManualReview. ACCIÓN ADMIN: decidir si el experto debe cobrar pese al refund.",
                                        userId: searchHire.ExpertId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId);
                                    searchHire.RequiresManualReview = true;
                                    try { await _context.SaveChangesAsync(); }
                                    catch (DbUpdateConcurrencyException)
                                    {
                                        // xmin obsoleto: el propio webhook T1b pudo marcar el flag en paralelo.
                                        // Recargar y reintentar una vez para no dejar la entidad sucia con xmin
                                        // viejo (envenenaría el SaveChanges final del commit → return false).
                                        try
                                        {
                                            await _context.Entry(searchHire).ReloadAsync();
                                            searchHire.RequiresManualReview = true;
                                            await _context.SaveChangesAsync();
                                        }
                                        catch { /* best-effort: el LogCritical ya alertó */ }
                                    }
                                    catch { /* best-effort: el LogCritical ya alertó */ }
                                    needsTransfer = false;
                                }
                            }
                        }

                        // Transfer primero (si aplica)
                        if (needsTransfer)
                        {
                            var expertStripeAccountId = searchHire.Expert?.ExpertProfile?.StripeAccountId;
                            if (string.IsNullOrEmpty(expertStripeAccountId))
                            {
                                // ­ƒÜ¿ LOG CR├ìTICO: Cuenta de Stripe del experto faltante
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Expert Stripe account missing - money distribution failed",
                                    details: $"SearchHire {searchHireId} finalization failed because Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) has no Stripe account configured. " +
                                            $"PENDING TRANSACTIONS TO COMPLETE MANUALLY: " +
                                            $"1) Refund {clientRefundAmount:F2}€ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) " +
                                            $"2) Transfer {expertAmount:F2}€ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) - REQUIRES MANUAL SETUP " +
                                            $"3) Platform retains {platformAmount:F2}€. " +
                                            $"Configuration: Client {config.ClientPercentage}%, Expert {config.ExpertPercentage}%, Platform {config.PlatformPercentage}%",
                                    userId: searchHire.ExpertId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "Transfer",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { 
                                        Status = statusValue,
                                        ClientRefundAmount = clientRefundAmount,
                                        ExpertTransferAmount = expertAmount,
                                        PlatformAmount = platformAmount,
                                        ClientId = searchHire.ClientId,
                                        ExpertId = searchHire.ExpertId,
                                        ClientName = searchHire.Client?.Name,
                                        ExpertName = searchHire.Expert?.Name,
                                        ExpertStripeAccountId = expertStripeAccountId
                                    }
                                );
                                // Ô£à CORRECCI├ôN: Solo hacer rollback si creamos la transacci├│n
                                if (transaction != null)
                                {
                                await transaction.RollbackAsync();
                                }
                                return false;
                            }

                            // MODIFICACI├ôN: Chequear status de connected account (best practice 2025 para cumplimiento)
                            var accountService = new AccountService();
                            Stripe.Account expertAccount;
                            try
                            {
                                expertAccount = await accountService.GetAsync(expertStripeAccountId);
                            }
                            catch (StripeException stripeAccEx) when (stripeAccEx.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                // 🛡️ Round 28 FIX: NO bloquear el refund al cliente cuando la cuenta del experto
                                // fue eliminada. El refund NO necesita la cuenta del experto — Stripe lo deriva
                                // del PaymentIntent original y descuenta del balance de la plataforma. Antes
                                // hacíamos `return false` aquí (bug): cliente sin reembolso aunque pagó por un
                                // servicio que el experto ya no puede entregar. Ahora marcamos el hire para
                                // revisión manual del transfer perdido, pero permitimos que el bloque
                                // `if (needsRefund)` siga ejecutándose.
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL R14: Expert Stripe account not found (404) — refund continues",
                                    details: $"SearchHire {searchHireId}: la cuenta Stripe {expertStripeAccountId} retornó 404 (eliminada o nunca existió). " +
                                             $"REFUND AL CLIENTE CONTINÚA (no requiere la cuenta del experto). " +
                                             $"TRANSFER AL EXPERTO ABORTADO: el monto {expertAmount:F2} {searchHire.Currency ?? "EUR"} queda RETENIDO por la plataforma — " +
                                             $"ACCIÓN ADMIN: reconciliar manualmente. Error: {stripeAccEx.Message}",
                                    userId: searchHire.ExpertId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync.R14",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { StripeAccountId = expertStripeAccountId, ExpertId = searchHire.ExpertId, RetainedExpertAmount = expertAmount, Currency = searchHire.Currency ?? "EUR" });
                                searchHire.RequiresManualReview = true;
                                searchHire.RefundFailedAt = DateTime.UtcNow;
                                transferDeferredForManualReview = true; // W19: no borrar RefundFailedAt en el clear M5b
                                // Marcador de que el transfer se omitió por cuenta inexistente. needsTransfer
                                // se desactiva y saltamos al final del bloque para que el refund siga.
                                needsTransfer = false;
                                goto endTransferBlock;
                            }
                            // 🔧 FIX (pagos): en separate charges & transfers el experto SOLO necesita la capability
                            // "transfers" + payouts; NO "charges". El onboarding pide solo "transfers", así que
                            // ChargesEnabled es false de forma legítima -> el guard antiguo bloqueaba TODO pago al
                            // experto (dinero atascado). Comprobamos la capability transfers activa + PayoutsEnabled.
                            if (expertAccount.PayoutsEnabled == false || expertAccount.Capabilities?.Transfers != "active")
                            {
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Expert account not enabled for transfers",
                                    details: $"Expert {searchHire.ExpertId} account {expertStripeAccountId} is not fully verified.",
                                    userId: searchHire.ExpertId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "Account",
                                    relatedEntityId: (int)searchHire.ExpertId,
                                    additionalData: new { AccountId = expertStripeAccountId, TransfersCapability = expertAccount.Capabilities?.Transfers, PayoutsEnabled = expertAccount.PayoutsEnabled }
                                );
                                // 🔧 Round 26 REFUND-DISABLED-4: marcar para revisión manual igual que la rama de currency-mismatch,
                                // para que el dashboard admin pueda surfacear hires atascados por capability/payouts disabled.
                                // 🛡️ F14 FIX: antes hacíamos RollbackAsync + return false → cliente NUNCA recibía su
                                // refund porque la rama needsRefund nunca corría (transfers disabled del experto dejaba
                                // el dinero atascado). Patrón paralelo al R14 (acct 404) y al currency-mismatch: saltar
                                // al refund block, marcar el transfer como pendiente de revisión manual, y dejar que el
                                // cliente reciba su reembolso. El refund NO necesita la cuenta del experto.
                                searchHire.RequiresManualReview = true;
                                searchHire.RefundFailedAt = DateTime.UtcNow;
                                transferDeferredForManualReview = true; // W19: no borrar RefundFailedAt en el clear M5b
                                needsTransfer = false; // saltar al refund block
                                await _context.SaveChangesAsync();
                                goto endTransferBlock;
                            }

                        // 🌍 Round 9 — A5 FIX: divisa derivada del Connect account del experto.
                        // Hardcodear "eur" rompía transfers a GB/CH/US/CA (currency_mismatch).
                        // Preferimos expertAccount.DefaultCurrency (verdad real de Stripe); fallback a mapping por país.
                        var transferCurrency = newApi.Common.StripeCurrencyMapping.ResolveTransferCurrency(
                            expertAccount?.DefaultCurrency,
                            searchHire.ExpertCountry);

                        // 🛡️ FIX: validar coherencia divisa SearchHire vs cuenta Stripe del experto ANTES
                        // de llamar a Stripe. Si el SearchHire fue creado/cobrado en una divisa distinta a la
                        // default_currency del Connect account, NO desperdiciamos llamada a Stripe solo para
                        // recibir StripeException "currency_mismatch": abortamos limpio, marcamos para revisión
                        // manual, y el admin reconcilia (re-cobro en divisa correcta o transfer manual).
                        // NOTA: aunque el flujo actual usa transferCurrency (derivado del expert account) para
                        // evitar el rechazo de Stripe, una discordancia con searchHire.Currency revela una
                        // inconsistencia de datos (ej. experto cambió de país tras el cobro) que merece
                        // intervención humana antes de mover dinero con conversión silenciosa.
                        var expectedCurrency = (searchHire.Currency ?? "EUR").ToLowerInvariant();
                        var accountCurrency = (expertAccount?.DefaultCurrency ?? "eur").ToLowerInvariant();
                        // ⚠️ AUDITORÍA [M1] Medium: esta rama currency-mismatch marca RequiresManualReview pero NO setea RefundFailedAt (sus hermanas en l.~1370 y l.~1399 sí lo hacen). El goto endTransferBlock deja que el refund corra y la función retorna true → SIN excepción.
                        // Disparo/ataque: searchHire.Currency (p.ej. chf) distinto de expertAccount.DefaultCurrency (p.ej. eur), con expertAmount>0 y experto con transfers activos. El pago del experto queda retenido y, al no setear RefundFailedAt, escapa al digest diario (LoggingService filtra RefundFailedAt!=null) y al watchdog Hangfire (solo actúa en FailedState; aquí no hay throw). Dinero del experto atascado e INVISIBLE.
                        // Fix: añadir searchHire.RefundFailedAt = DateTime.UtcNow; junto al RequiresManualReview de abajo, para paridad con las ramas 404 y transfers-disabled.
                        // 🛡️ FIX (auditoría 2026-07-06): incluir también transferCurrency en la guarda.
                        // Si Stripe devolviera la cuenta con default_currency VACÍO, la comparación de abajo
                        // pasaba por el fallback "?? eur" (eur==eur) pero transferCurrency caía al mapping por
                        // PAÍS (p.ej. gbp para GB) → el Amount calculado en EUR se enviaba etiquetado como GBP
                        // sin conversión (transfer de £95 por un hire de 100€ ≈ +15% de pérdida). Fail-closed:
                        // cualquier divergencia → revisión manual, igual que el mismatch clásico.
                        if (expectedCurrency != accountCurrency
                            || !string.Equals(transferCurrency, expectedCurrency, StringComparison.OrdinalIgnoreCase))
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "Currency mismatch — transfer aborted",
                                details: $"SearchHire #{searchHire.Id} Currency={expectedCurrency} but expert account {expertStripeAccountId} default currency={accountCurrency} (resolved transferCurrency={transferCurrency}). Cannot transfer.",
                                userId: searchHire.ExpertId,
                                source: "RefundService.ProcessMoneyDistribution",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new
                                {
                                    SearchHireId = searchHire.Id,
                                    SearchHireCurrency = expectedCurrency,
                                    ExpertStripeAccountId = expertStripeAccountId,
                                    ExpertAccountDefaultCurrency = accountCurrency,
                                    ExpertCountry = searchHire.ExpertCountry,
                                    Status = statusValue
                                });
                            // 🔔 NOTIF-FIX [currency-experto]: avisar al EXPERTO de que su pago quedó
                            // RETENIDO (pendiente de revisión manual) por descuadre de divisa. Antes el
                            // LogCritical de arriba iba solo a admin (NOTIF-GUARD bloquea Critical al
                            // usuario) → el experto no sabía que su cobro estaba parado.
                            // 🔁 ANTI-BUCLE: solo en la PRIMERA detección (RefundFailedAt aún null). Los
                            // watchdogs RetryTransferFailed/RetryRefundFailed re-llaman a esta distribución
                            // y volverían a caer en currency-mismatch (permanente hasta que el experto
                            // corrija su cuenta); el in-app/email lo frenaría el SPAM-GUARD 24h del logger,
                            // pero el SMS es directo → sin este gate se reenviaría en cada reintento.
                            if (searchHire.ExpertId.HasValue && searchHire.RefundFailedAt == null)
                            {
                                try
                                {
                                    await _loggingService.LogWarningAsync(
                                        message: "Tu pago está en revisión",
                                        details: $"El pago del servicio #{searchHireId} ha quedado pendiente de revisión manual por un tema de divisa entre tu cuenta y el importe cobrado. Nuestro equipo lo resolverá; no tienes que hacer nada.",
                                        userId: searchHire.ExpertId,
                                        source: "RefundService.ProcessMoneyDistribution.CurrencyMismatch",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        notifyUser: true);
                                    if (_inAppNotifications != null)
                                    {
                                        try
                                        {
                                            await _inAppNotifications.SendImportantSmsAsync(searchHire.ExpertId.Value,
                                                $"Inspecciono: el pago del servicio #{searchHireId} esta en revision (tema de divisa). Lo resolveremos; no tienes que hacer nada.");
                                        }
                                        catch { /* SMS best-effort */ }
                                    }
                                }
                                catch { /* best-effort: el aviso nunca rompe la distribución */ }
                            }
                            // 🛡️ Round 28 MUD-AH (CRITICAL fix): antes hacíamos RollbackAsync + return false
                            // → cliente NUNCA recibía su refund porque la rama del refund nunca corría.
                            // Patrón paralelo al R14 (acct 404): saltar al refund block, marcar el transfer
                            // como pendiente de revisión manual, dejar que el cliente reciba su reembolso.
                            // Sin esto, "cliente pagó CHF + experto mudó a IE EUR + cancela" = client refund stranded forever.
                            // ✅ FIX AUDITORÍA [M1] Medium: añadido `searchHire.RefundFailedAt = DateTime.UtcNow;` (línea siguiente). Sin él, este hire con transfer del experto pendiente nunca aparecía en el Refund-failed digest (LoggingService .Where(h => h.RefundFailedAt != null)) ni lo marcaba el Hangfire filter (que solo actúa si el job entra en FailedState; este flujo hace goto y retorna true). Ahora hay paridad con las ramas 404 (l.~1370) y transfers-disabled (l.~1399).
                            searchHire.RequiresManualReview = true;
                            searchHire.RefundFailedAt = DateTime.UtcNow;
                            transferDeferredForManualReview = true; // W19: no borrar RefundFailedAt en el clear M5b
                            needsTransfer = false; // saltar al refund block
                            await _context.SaveChangesAsync();
                            goto endTransferBlock;
                        }

                        var transferOptions = new TransferCreateOptions
                        {
                            Amount = newApi.Common.StripeMinorUnits.ToMinorUnitsOutbound(expertAmountForStripe, transferCurrency), // ✅ monto base (sin tax). FIX HUF: helper redondea a múltiplo de 100 para HUF/ISK/TWD; para EUR/2-dec = Math.Round(x*100) idéntico.
                                Currency = transferCurrency, // 🌍 A5: era "eur" hardcodeado — fallaba para GB/CH/US/CA
                                Destination = expertStripeAccountId,
                                Metadata = new Dictionary<string, string>
                                {
                                    { "searchHireId", searchHireId.ToString() },
                                    { "statusValue", statusValue },
                                    { "clientPercentage", config.ClientPercentage.ToString() },
                                    { "expertPercentage", config.ExpertPercentage.ToString() },
                                    { "platformPercentage", config.PlatformPercentage.ToString() },
                                    { "reason", reason },
                                    { "clientId", searchHire.ClientId.ToString() }, // MODIFICACI├ôN: M├ís metadata para trazabilidad
                                    { "expertId", searchHire.ExpertId?.ToString() ?? "N/A" },
                                    { "transferCurrency", transferCurrency }, // 🌍 A5: divisa real usada para auditoría (vs charge en EUR)
                                    { "expertCountry", searchHire.ExpertCountry ?? "unknown" } // 🌍 A5: país snapshot del experto
                                }
                            };

                            // MODIFICACI├ôN: Idempotency correcta con RequestOptions (antes estaba en metadata, lo cual no funciona)
                            var transferRequestOptions = new RequestOptions
                            {
                                IdempotencyKey = transferIdempotencyKey // 🔧 FIX E: discriminada por estado+importe
                            };

                            var transferSvc = new TransferService();

                            // MODIFICACI├ôN: Reintento simple para transients (hasta 3 veces, sin Polly)
                            Transfer transfer = null;
                            const int maxRetries = 3;
                            for (int attempt = 1; attempt <= maxRetries; attempt++)
                            {
                                try
                                {
                                    // ⚠️ AUDITORÍA [M2] Medium: el dinero SALE de Stripe AQUÍ, pero la fila Payout no se persiste/commitea hasta ~2298/2319. El estado del hire ya se commiteó en FASE 2 (~802) como "completed".
                                    // Disparo/ataque: crash/OOM/redeploy/timeout de Hangfire en la ventana 1487→2298 → transfer real en Stripe + fila Payout revertida + hire queda "completed" (NO "transfer_failed"). El watchdog RetryTransferFailedHiresAsync solo mira "transfer_failed" (PlatformMaintenanceService ~483) → NUNCA lo reencola = hueco contable silencioso; reintento manual con otro statusValue rompe la idempotencia → doble transfer.
                                    // Fix: outbox/marca de intención persistida ANTES del transfer (o mover hire a estado recoverable) y ampliar el watchdog para reconciliar "completed sin fila Payout vs transfer existente en Stripe".
                                    transfer = await transferSvc.CreateAsync(transferOptions, transferRequestOptions);
                                    break;
                                }
                                catch (StripeException ex) when ((int)ex.HttpStatusCode >= 500 || (int)ex.HttpStatusCode == 429) // Server errors or rate limits
                                {
                                    if (attempt == maxRetries)
                                        throw;
                                    await Task.Delay(1000 * attempt); // Exponential backoff simple
                                }
                            }
                            createdTransferId = transfer.Id;

                            // 🌍 Round 14 — Q7 F1: telemetría de fees de conversión cross-currency.
                            // Si transferCurrency != "eur" (cliente EUR → experto GB/CH/US/CA), Stripe
                            // aplica un exchange rate + fee (~2%) que sale del balance EUR de la
                            // plataforma. Antes no leíamos balance_transaction → ceguera total al
                            // sangrado financiero. Ahora expandimos y logueamos el fee real para que
                            // admin pueda detectar drift de margen vs el % de plataforma pactado.
                            // Best-effort: si falla la expansión, no rompe el flujo.
                            if (!string.Equals(transferCurrency, "eur", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    if (!string.IsNullOrEmpty(transfer.BalanceTransactionId))
                                    {
                                        var balanceTxSvc = new BalanceTransactionService();
                                        var bt = await balanceTxSvc.GetAsync(transfer.BalanceTransactionId);
                                        await _loggingService.LogInfoAsync(
                                            message: $"Q7: Cross-currency transfer cost EUR->{transferCurrency.ToUpperInvariant()}",
                                            details: $"SearchHire {searchHireId}: transfer {transfer.Id}. Gross={bt.Amount/100m} {bt.Currency}, Fee={bt.Fee/100m} {bt.Currency}, Net={bt.Net/100m} {bt.Currency}, ExchangeRate={bt.ExchangeRate}. Platform absorbe la fee.",
                                            userId: searchHire.ExpertId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync.Q7",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId,
                                            additionalData: new
                                            {
                                                TransferId = transfer.Id,
                                                BalanceTransactionId = transfer.BalanceTransactionId,
                                                GrossCents = bt.Amount,
                                                FeeCents = bt.Fee,
                                                NetCents = bt.Net,
                                                Currency = bt.Currency,
                                                ExchangeRate = bt.ExchangeRate,
                                                DestinationCurrency = transferCurrency
                                            });

                                        // 🛡️ FIX #8: alertar si la FX fee es > 1% del margen de plataforma.
                                        // Sin esto el sangrado de fees de conversión queda silencioso y solo se detecta
                                        // en reconciliación mensual. Comparar fee absoluta con platformAmount (lo que
                                        // se queda la plataforma); si > 1%, alertar para que admin pueda actuar.
                                        if (bt.Fee > 0 && platformAmount > 0)
                                        {
                                            var feeEur = Math.Abs(bt.Fee) / 100m; // convertir céntimos a EUR
                                            var feeRatioVsPlatform = feeEur / platformAmount;
                                            if (feeRatioVsPlatform > 0.01m)
                                            {
                                                await _loggingService.LogWarningAsync(
                                                    message: "FIX#8: FX fee excede 1% del margen de plataforma — drift de margen",
                                                    details: $"SearchHire {searchHireId}: transfer cross-currency EUR->{transferCurrency.ToUpperInvariant()} costó FX fee={feeEur:F4}€ ({feeRatioVsPlatform:P2} del platformAmount={platformAmount:F2}€). " +
                                                            $"Plataforma absorbe la fee, reduciendo el margen efectivo. ACCIÓN ADMIN: revisar reporte mensual de fees vs % platform configurado.",
                                                    userId: searchHire.ExpertId,
                                                    source: "StripeRefundService.ProcessMoneyDistributionAsync.Fix8",
                                                    relatedEntityType: "SearchHire",
                                                    relatedEntityId: searchHireId,
                                                    additionalData: new
                                                    {
                                                        TransferId = transfer.Id,
                                                        FxFeeEur = feeEur,
                                                        PlatformAmountEur = platformAmount,
                                                        FeeRatioVsPlatform = feeRatioVsPlatform,
                                                        DestinationCurrency = transferCurrency
                                                    });
                                            }
                                        }
                                    }
                                }
                                catch (Exception btEx)
                                {
                                    // Telemetría es best-effort; no afecta el flujo de transfer.
                                    try
                                    {
                                        await _loggingService.LogWarningAsync(
                                            message: "Q7: no se pudo leer balance_transaction del transfer",
                                            details: $"Transfer {transfer.Id}: {btEx.Message}",
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync.Q7");
                                    }
                                    catch { /* swallow */ }
                                }
                            }

                            // 🔧 FIX (#1): NO registrar como Payout activo un transfer que YA está revertido.
                            // Escenario: en un intento anterior este transfer se creó y, al fallar el refund, se
                            // REVIRTIÓ + se hizo rollback (sin dejar fila Payout). En el reintento, CreateAsync con la
                            // MISMA idempotency key NO crea nada: Stripe REPLICA la respuesta CACHEADA de la creación
                            // original (amount_reversed=0), así que el objeto devuelto NO refleja la reversión. Si lo
                            // registráramos como Payout, el ledger diría que el experto cobró cuando el dinero ya
                            // volvió a la plataforma (descuadre). Solución: leer el estado VIVO (GetAsync NO se cachea
                            // por idempotency) y, si está revertido, crear uno NUEVO con clave derivada del transfer
                            // muerto (determinista por-intento) para pagar de verdad al experto.
                            var liveTransfer = await transferSvc.GetAsync(transfer.Id);
                            int freshTransferAttempts = 0;
                            while ((liveTransfer.Reversed || liveTransfer.AmountReversed >= liveTransfer.Amount)
                                   && freshTransferAttempts++ < 5)
                            {
                                var freshTransferKey = $"{transferIdempotencyKey}-after-{liveTransfer.Id}";
                                transfer = await transferSvc.CreateAsync(
                                    transferOptions,
                                    new RequestOptions { IdempotencyKey = freshTransferKey });
                                createdTransferId = transfer.Id;
                                liveTransfer = await transferSvc.GetAsync(transfer.Id);
                            }
                            if (liveTransfer.Reversed || liveTransfer.AmountReversed >= liveTransfer.Amount)
                            {
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Expert transfer keeps returning reversed - aborting to protect ledger",
                                    details: $"SearchHire {searchHireId}: el transfer al experto vuelve REVERTIDO tras " +
                                             $"{freshTransferAttempts} intentos con clave fresca (replay idempotente de transfers " +
                                             $"muertos). Se ABORTA para NO registrar un Payout fantasma. Requiere intervención manual.",
                                    userId: searchHire.ExpertId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { Status = statusValue, LastTransferId = createdTransferId });
                                if (transaction != null)
                                {
                                    await transaction.RollbackAsync();
                                }
                                return false;
                            }

                            // 🛡️ FIX #1: re-verificar estado de la cuenta del experto DESPUÉS del transfer.
                            // Si la cuenta fue desactivada entre la validación inicial (línea ~1216) y el
                            // CreateAsync (línea ~1298), Stripe ya creó el transfer pero el destino puede
                            // estar inválido. Si detectamos el cambio: revertir el transfer y abortar para
                            // evitar que el ledger marque "Payout" cuando el experto no podrá recibir.
                            try
                            {
                                var postTransferAccount = await accountService.GetAsync(expertStripeAccountId);
                                if (postTransferAccount.PayoutsEnabled == false || postTransferAccount.Capabilities?.Transfers != "active")
                                {
                                    // Intentar revertir el transfer recién creado.
                                    string reversalAttemptId = null;
                                    string reversalErrorMsg = null;
                                    try
                                    {
                                        var postTransferReversalSvc = new TransferReversalService();
                                        var postTransferReversalOptions = new TransferReversalCreateOptions
                                        {
                                            Amount = newApi.Common.StripeMinorUnits.ToMinorUnitsOutbound(expertAmountForStripe, transferCurrency), // FIX HUF: múltiplo de 100 para HUF/ISK/TWD; EUR = Math.Round(x*100).
                                            Metadata = new Dictionary<string, string>
                                            {
                                                { "searchHireId", searchHireId.ToString() },
                                                { "reason", "expert account became invalid mid-transaction" }
                                            }
                                        };
                                        var postTransferReversalRequestOptions = new RequestOptions
                                        {
                                            IdempotencyKey = $"md-{searchHireId}-postcheck-reversal-{createdTransferId}"
                                        };
                                        var postReversal = await postTransferReversalSvc.CreateAsync(
                                            createdTransferId, postTransferReversalOptions, postTransferReversalRequestOptions);
                                        reversalAttemptId = postReversal.Id;
                                    }
                                    catch (Exception postRevEx)
                                    {
                                        reversalErrorMsg = postRevEx.Message;
                                    }

                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL FIX#1: Expert account became invalid AFTER transfer was created",
                                        details: $"SearchHire {searchHireId}: la cuenta Stripe {expertStripeAccountId} pasó válida en la pre-validación pero está inválida tras CreateAsync (PayoutsEnabled={postTransferAccount.PayoutsEnabled}, TransfersCapability={postTransferAccount.Capabilities?.Transfers}). Transfer {createdTransferId} fue creado en Stripe. Intento de reversal: {(reversalAttemptId != null ? $"OK ({reversalAttemptId})" : $"FALLÓ ({reversalErrorMsg})")}. NO se persiste FT Payout. ACCIÓN ADMIN: reconciliar manualmente si la reversal falló.",
                                        userId: searchHire.ExpertId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync.Fix1",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new
                                        {
                                            StripeAccountId = expertStripeAccountId,
                                            TransferId = createdTransferId,
                                            PostCheckPayoutsEnabled = postTransferAccount.PayoutsEnabled,
                                            PostCheckTransfersCapability = postTransferAccount.Capabilities?.Transfers,
                                            ReversalId = reversalAttemptId,
                                            ReversalError = reversalErrorMsg
                                        });

                                    if (transaction != null)
                                    {
                                        await transaction.RollbackAsync();
                                    }
                                    return false;
                                }
                            }
                            catch (StripeException postCheckEx)
                            {
                                // No abortamos por error en el re-check (best-effort); solo log warning.
                                await _loggingService.LogWarningAsync(
                                    message: "FIX#1: post-transfer account re-check failed (best-effort)",
                                    details: $"SearchHire {searchHireId}: no se pudo re-verificar la cuenta {expertStripeAccountId} tras el transfer {createdTransferId}: {postCheckEx.Message}. El transfer ya está creado; continuamos con la persistencia del FT Payout.",
                                    userId: searchHire.ExpertId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync.Fix1",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId);
                            }
                            // 🛡️ Round 28: label para saltar aquí desde el catch 404 (refund continúa).
                            endTransferBlock: ;
                        }

                        // Refund despu├®s (si aplica)
                        // 🔧 FIX (#2, carrera chargeback): re-verificar el marcador Chargeback JUSTO antes del
                        // refund interno. Entre la lectura inicial de hasChargeback (~l.1021) y este punto hay
                        // llamadas de red a Stripe (balance, transfer...), abriendo una ventana de segundos. Si un
                        // charge.dispute.created se dio de alta en ese hueco, omitimos el refund para evitar la
                        // DOBLE devolución al cliente (contracargo de Stripe + refund interno).
                        if (needsRefund)
                        {
                            var chargebackAppeared = await _context.FinancialTransactions.AnyAsync(ft =>
                                ft.RelatedEntityType == "SearchHire" &&
                                ft.RelatedEntityId == searchHireId &&
                                ft.TransactionType == "Chargeback" &&
                                ft.StripePaymentIntentId == servicePayment.StripePaymentIntentId);
                            if (chargebackAppeared)
                            {
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Chargeback detectado justo antes del refund interno - OMITIDO para evitar doble devolución",
                                    details: $"SearchHire {searchHireId}: apareció un Chargeback (PaymentIntent {servicePayment.StripePaymentIntentId}) entre la comprobación inicial y la emisión del refund. Se OMITE el refund interno (status {statusValue}). Si el experto cobró, lo revierte el handler del chargeback.",
                                    userId: initiatedByUserId ?? searchHire.ClientId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId);
                                needsRefund = false;
                            }
                        }

                        // 🛡️ N10 FIX: si el PI NO está capturado (requires_capture/action/payment_method),
                        // Stripe rechaza el refund con error. Hay que CANCELAR el PI en su lugar.
                        // Esto cubre el caso de un cliente que abandona el checkout justo cuando el flujo
                        // termina (PI autorizado pero no capturado) y luego se procesa una resolución
                        // (refund_client o cancellation).
                        if (needsRefund)
                        {
                            try
                            {
                                var n10PiService = new PaymentIntentService();
                                var n10Pi = await n10PiService.GetAsync(servicePayment.StripePaymentIntentId);
                                if (n10Pi.Status == "canceled")
                                {
                                    // 🛡️ N10-IDEM FIX (BUG #1): el PI ya fue cancelado en una pasada previa (el
                                    // watchdog de expiración re-selecciona el hire porque N10 no deja FT 'Refund').
                                    // Tratarlo como ÉXITO BENIGNO —paralelo al catch de 'charge_already_refunded'—:
                                    // no hay nada que reembolsar. Sin esto, caería al Refund.CreateAsync sobre un PI
                                    // cancelado → StripeException → catch general → RequiresManualReview + CRITICAL falso.
                                    await _loggingService.LogInfoAsync(
                                        message: "N10: PI ya estaba 'canceled' — no-op idempotente",
                                        details: $"SearchHire {searchHireId}: PI {servicePayment.StripePaymentIntentId} ya estaba 'canceled' (cancelación de una pasada previa). No se intenta refund: no se movió dinero.",
                                        userId: searchHire.ClientId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync.N10AlreadyCanceled",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId);
                                    servicePayment.IsRefunded = true;
                                    needsRefund = false;
                                }
                                else if (n10Pi.Status == "requires_capture"
                                    || n10Pi.Status == "requires_action"
                                    || n10Pi.Status == "requires_payment_method"
                                    || n10Pi.Status == "requires_confirmation")
                                {
                                    // PI no capturado → cancelar (no se puede refundear lo que no se cobró).
                                    try
                                    {
                                        await n10PiService.CancelAsync(
                                            servicePayment.StripePaymentIntentId,
                                            new PaymentIntentCancelOptions
                                            {
                                                CancellationReason = "requested_by_customer"
                                            },
                                            new RequestOptions { IdempotencyKey = $"n10-cancel-{servicePayment.StripePaymentIntentId}" });
                                        await _loggingService.LogInfoAsync(
                                            message: "N10: PI no capturado → canceled en lugar de refund",
                                            details: $"SearchHire {searchHireId}: PI {servicePayment.StripePaymentIntentId} estaba en '{n10Pi.Status}'. Cancelado en lugar de intentar refund (que habría fallado). No se crea FT Refund porque no se cobró nada.",
                                            userId: searchHire.ClientId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync.N10Cancel",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId);
                                        // Marcar refund como "completado" para el resto del flujo (no hay nada que persistir como FT Refund).
                                        servicePayment.IsRefunded = true;
                                        // 🛡️ FIX 23505 (Round 28): NO escribir un literal en servicePayment.StripeRefundId.
                                        // El índice único parcial IX_FT_StripeRefundId_uq no filtra por TransactionType ni por
                                        // valor, así que el mismo literal "n10-canceled-pre-capture" en 2 cancelaciones
                                        // pre-capture distintas haría chocar la segunda con 23505. IsRefunded=true ya marca
                                        // el estado funcional; no se necesita StripeRefundId para una cancelación pre-capture
                                        // (no hubo refund real en Stripe, solo PI canceled).
                                        needsRefund = false; // skip el CreateAsync de refund
                                    }
                                    catch (StripeException cancelEx) when (cancelEx.StripeError?.Code == "payment_intent_unexpected_state")
                                    {
                                        // Race: el PI cambió de estado entre el GetAsync y el CancelAsync.
                                        // Caer al refund normal (tal vez ya está succeeded).
                                        await _loggingService.LogWarningAsync(
                                            message: "N10: PI cambió de estado entre GET y CANCEL — intentando refund",
                                            details: $"SearchHire {searchHireId}: PI {servicePayment.StripePaymentIntentId} cambió. Fallback a refund normal.",
                                            userId: searchHire.ClientId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync.N10Race",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId);
                                    }
                                }
                            }
                            catch (Exception n10CheckEx)
                            {
                                // Si falla el check de PI, log warning pero seguir al refund (puede que ya esté succeeded).
                                await _loggingService.LogWarningAsync(
                                    message: "N10: failed to pre-check PI status — proceeding with refund attempt",
                                    details: $"SearchHire {searchHireId}: error verificando PI: {n10CheckEx.Message}. Continuando con refund normal.",
                                    userId: searchHire.ClientId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync.N10CheckFail",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId);
                            }
                        }

                        if (needsRefund)
                        {
                            var refundOptions = new RefundCreateOptions
                            {
                                PaymentIntent = servicePayment.StripePaymentIntentId,
                                Amount = newApi.Common.StripeMinorUnits.ToMinorUnitsOutbound(clientRefundAmountForStripe, searchHire.Currency), // ✅ monto con tax proporcional. FIX HUF: múltiplo de 100 para HUF/ISK/TWD; EUR = Math.Round(x*100).
                                Reason = RefundReasons.RequestedByCustomer,
                                Metadata = new Dictionary<string, string>
                                {
                                    { "searchHireId", searchHireId.ToString() },
                                    { "statusValue", statusValue },
                                    { "clientPercentage", config.ClientPercentage.ToString() },
                                    { "expertPercentage", config.ExpertPercentage.ToString() },
                                    { "platformPercentage", config.PlatformPercentage.ToString() },
                                    { "reason", reason },
                                    { "originalTransactionId", servicePayment.Id.ToString() },
                                    { "clientId", searchHire.ClientId.ToString() } // MODIFICACI├ôN: M├ís metadata
                                }
                            };

                            // MODIFICACI├ôN: Idempotency correcta con RequestOptions
                            var refundRequestOptions = new RequestOptions
                            {
                                IdempotencyKey = refundIdempotencyKey // 🔧 FIX P5: discriminada SOLO por statusValue (estado lógico), NO por importe (ver l.1306-1314)
                            };

                            try
                            {
                                var refundSvc = new RefundService();

                                // MODIFICACI├ôN: Reintento simple similar
                                Refund refund = null;
                                const int maxRetries = 3;
                                for (int attempt = 1; attempt <= maxRetries; attempt++)
                                {
                                    try
                                    {
                                        // ⚠️ AUDITORÍA [L3] Low: refund creado en Stripe AQUÍ, pero su fila FT solo se vuelve durable en el CommitAsync (~L2319). Dual-write no atómico.
                                        // Disparo/ataque: caller sin tx exterior (RetryMoneyDistributionJobAsync de Hangfire) → muerte del proceso (deploy/OOM/PG failover) entre el SaveChanges ~L2217 y el commit ~L2319 → rollback descarta la FT Refund aunque el refund exista en Stripe. Autorrecuperable: el retry ve existingRefund=null y reinvoca con la misma idempotency key (Stripe devuelve el refund cacheado, sin doble reembolso); StripeReconciliationService lo detecta como RefundsInStripeMissingInDb.
                                        // Fix: patrón outbox — commitear la intención del refund en la MISMA tx ANTES de llamar a Stripe (o registrar el efecto en tabla outbox commiteada y disparar el side-effect tras commit), para que el efecto Stripe y la fila FT compartan límite de durabilidad.
                                        refund = await refundSvc.CreateAsync(refundOptions, refundRequestOptions);
                                        break;
                                    }
                                    catch (StripeException ex) when ((int)ex.HttpStatusCode >= 500 || (int)ex.HttpStatusCode == 429)
                                    {
                                        if (attempt == maxRetries)
                                            throw;
                                        await Task.Delay(1000 * attempt);
                                    }
                                }
                                createdRefundId = refund.Id;
                            }
                            // 🛡️ N11 FIX: idempotency hits específicos antes del catch general. Si Stripe responde
                            // con `charge_already_refunded` o `idempotency_error`, significa que el refund YA se
                            // procesó (sea por replica de webhook, sea por reintento anterior cuya SaveChanges falló).
                            // No es un error → tratamos como éxito y continuamos. El refund real está en Stripe;
                            // la BD se reconcilia abajo (el guard refundAlreadyProcessed lo detectará en la próxima).
                            catch (StripeException idemRefundEx) when (
                                idemRefundEx.StripeError?.Code == "charge_already_refunded"
                                || idemRefundEx.StripeError?.Code == "idempotency_error")
                            {
                                await _loggingService.LogWarningAsync(
                                    message: "N11: refund idempotency hit (already done)",
                                    details: $"SearchHire {searchHireId}: Stripe respondió '{idemRefundEx.StripeError?.Code}' al crear refund — ya estaba aplicado. Marcando como completado sin crear FT Refund nueva (el refund original está en Stripe; StripeReconciliationService rellenará la fila FT en la próxima pasada diaria).",
                                    userId: searchHire.ClientId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync.N11Refund",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId);
                                // 🛡️ FIX 23505 (Round 28): NO usar literal "n11-already-refunded" como StripeRefundId.
                                // El índice único IX_FT_StripeRefundId_uq haría chocar dos hits N11 concurrentes con el
                                // mismo literal. Setear createdRefundId=null + needsRefund=false hace que el bloque
                                // L1995 se salte y NO se inserte FT Refund con placeholder. La fila FT real la creará
                                // StripeReconciliationService.RunDailyReconciliationAsync (job Hangfire cron 3:00 UTC)
                                // detectando la divergencia Stripe-vs-BD para este PaymentIntent.
                                createdRefundId = null;
                                needsRefund = false;
                                servicePayment.IsRefunded = true;
                            }
                            catch (StripeException refundEx)
                            {
                                // 🛡️ FIX TX-6 (2026-06-11): NO auto-reversar el transfer cuando falla el refund.
                                //
                                // El comportamiento anterior ("todo o nada": reversal del transfer + rollback)
                                // CORROMPÍA el dinero al combinarse con el retry de Hangfire:
                                //   1) transfer 8% al experto OK → 2) refund 90% falla → 3) reversal OK
                                //   (experto neto 0) → 4) rollback borra la FT Payout → 5) el retry re-crea
                                //   el transfer con la MISMA idempotency key → Stripe hace REPLAY del transfer
                                //   ORIGINAL (que ya está revertido: NO mueve dinero) → se persiste FT Payout
                                //   → el refund sale → RESULTADO: el experto pierde su parte en silencio y la
                                //   contabilidad dice que cobró.
                                //
                                // Patrón correcto (coherente con el resto del diseño idempotente): DEJAR el
                                // transfer hecho y que el retry (RetryMoneyDistributionJobAsync) complete SOLO
                                // el refund — el flujo de "partial money distribution" (guard L1212) ya soporta
                                // exactamente eso. La reversal INTENCIONAL (clawback de un transfer previo en
                                // resoluciones de disputa) vive en su propio bloque MUD-AV y no se toca.
                                //
                                // ✅ CORRECCIÓN: Solo hacer rollback si creamos la transacción
                                if (transaction != null)
                                {
                                    await transaction.RollbackAsync();
                                }

                                // Persistir el marcador DESPUÉS del rollback y con SQL crudo: un SaveChanges
                                // aquí re-aplicaría TODO el change-tracker descartado (FTs incluidas) fuera
                                // de la transacción. RefundFailedAt alimenta el digest diario P3-1.
                                try
                                {
                                    await _context.Database.ExecuteSqlInterpolatedAsync(
                                        $"UPDATE \"SearchHires\" SET \"RequiresManualReview\" = TRUE, \"RefundFailedAt\" = {DateTime.UtcNow} WHERE \"Id\" = {searchHireId}");
                                }
                                catch (Exception markEx)
                                {
                                    await _loggingService.LogWarningAsync(
                                        message: "TX-6: no se pudo marcar RequiresManualReview tras fallo de refund",
                                        details: $"SearchHire {searchHireId}: {markEx.Message}. El retry de Hangfire sigue en pie.",
                                        userId: initiatedByUserId ?? searchHire.ClientId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync.TX6",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId);
                                }

                                // 🚨 LOG CRÍTICO: Reembolso falló (tras el rollback para que la fila persista)
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Refund failed - retry will complete the refund (transfer kept)",
                                    details: $"SearchHire {searchHireId} finalization: refund to client failed. " +
                                            $"PENDING (auto-retry via Hangfire; manual only if retries exhaust): " +
                                            $"1) Refund {clientRefundAmount:F2}€ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) - FAILED, WILL RETRY " +
                                            $"2) Transfer {expertAmount:F2}€ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) - " +
                                            $"{(string.IsNullOrEmpty(createdTransferId) ? "NOT PROCESSED" : $"DONE in Stripe ({createdTransferId}) and KEPT (TX-6: no auto-reversal); FT row will be recreated by the retry's idempotent replay")} " +
                                            $"3) Platform retains {platformAmount:F2}€. " +
                                            $"RefundError: {refundEx.Message}",
                                    userId: initiatedByUserId ?? searchHire.ClientId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new {
                                        Status = statusValue,
                                        ClientRefundAmount = clientRefundAmount,
                                        ExpertTransferAmount = expertAmount,
                                        PlatformAmount = platformAmount,
                                        ClientId = searchHire.ClientId,
                                        ExpertId = searchHire.ExpertId,
                                        TransferKept = createdTransferId,
                                        RefundError = refundEx.Message
                                    }
                                );

                                return false;
                            }
                        }

                        // Registrar en base de datos solo si Stripe tuvo ├®xito en ambos pasos necesarios
                        // 🔁 C3: CLAWBACK (parcial) del transfer al experto cuando se reembolsa al cliente
                        // y el experto YA fue pagado en una distribución previa. Caso típico: el servicio se
                        // completó (Completed -> transfer 95% al experto) y DESPUÉS se resolvió una disputa a
                        // favor del cliente (p.ej. dispute_resolved_client = 90/8/2). El experto debe quedarse
                        // SOLO con su nueva parte (expertAmountForStripe, p.ej. 8%), así que se revierte la
                        // DIFERENCIA entre lo ya transferido y lo que le corresponde ahora. Sin esto el cliente
                        // cobra su reembolso pero el experto SE QUEDA el transfer íntegro y la plataforma asume
                        // la pérdida (~85%). Importes en base (sin tax), igual que el transfer original.
                        // Se activa si: hay refund al cliente + transfer previo NO revertido + lo ya
                        // transferido SUPERA la nueva parte del experto (clawback = transferido - nueva parte).
                        // Si la nueva parte >= lo transferido (p.ej. dispute_resolved_expert tras Completed),
                        // clawbackAmountEur <= 0 y NO se revierte nada. (Antes solo disparaba con experto==0%,
                        // por eso 90/8/2 dejaba al experto cobrado de más y la plataforma perdía ~85%.)
                        // 🔧 FIX (céntimos): calcular el clawback en CÉNTIMOS enteros, no sobre el Amount decimal
                        // crudo del ledger (que podía guardar 18.0595 cuando a Stripe se envió 18.06). Usar
                        // AmountCents si está poblado (filas nuevas); si es una fila antigua con AmountCents=0,
                        // caer al Amount redondeado a céntimo (no al crudo). Así el clawback casa con lo transferido.
                        long transferredCents = existingTransfer == null
                            ? 0L
                            : (existingTransfer.AmountCents != 0
                                ? Math.Abs(existingTransfer.AmountCents)
                                // 🛡️ FIX (auditoría 2026-07-06): el fallback para filas legacy sin AmountCents
                                // pasa por el helper de minor units — Math.Round(x*100) crudo podía producir un
                                // clawback NO múltiplo de 100 en HUF → Stripe 400 en la TransferReversal →
                                // reintentos eternos. Para EUR/2-dec el helper es idéntico a Math.Round(x*100).
                                : newApi.Common.StripeMinorUnits.ToMinorUnitsOutbound(Math.Abs(existingTransfer.Amount), searchHire.Currency));
                        long expertOwedCents = newApi.Common.StripeMinorUnits.ToMinorUnitsOutbound(expertAmountForStripe, searchHire.Currency); // FIX HUF: consistente con el transfer (múltiplo de 100 en HUF/ISK/TWD)
                        long clawbackCents = Math.Max(0L, transferredCents - expertOwedCents);
                        var clawbackAmountEur = clawbackCents / 100m;
                        // 🔁 A2: dispara también si el refund YA estaba hecho (reintento de un clawback que
                        // falló antes), no solo cuando se acaba de crear el refund en esta ejecución.
                        // 🛡️ FIX #9: re-leer marcador Chargeback JUSTO antes de evaluar el clawback.
                        // hasChargeback se leyó al principio (~l.1152), entre medias hubo I/O a Stripe (segundos);
                        // si llegó un charge.dispute.created en esa ventana e insertó FT Chargeback, el clawback
                        // interno + la reversión total del chargeback (ReverseExpertTransferForChargeback)
                        // intentarían revertir el MISMO transfer → doble-reversión. Re-leer ahora cierra esa ventana.
                        var hasChargebackNow = hasChargeback
                            || await _context.FinancialTransactions.AnyAsync(ft =>
                                ft.RelatedEntityType == "SearchHire" &&
                                ft.RelatedEntityId == searchHireId &&
                                ft.TransactionType == "Chargeback" &&
                                ft.StripePaymentIntentId == servicePayment.StripePaymentIntentId);
                        if (hasChargebackNow && !hasChargeback)
                        {
                            await _loggingService.LogWarningAsync(
                                message: "FIX#9: Chargeback apareció entre la lectura inicial y el clawback — clawback OMITIDO",
                                details: $"SearchHire {searchHireId}: un Chargeback (PaymentIntent {servicePayment.StripePaymentIntentId}) se insertó entre la lectura inicial de hasChargeback y este punto. Se OMITE el clawback interno para evitar doble-reversión del transfer {existingTransfer?.StripeTransferId}. La reversión total la hará ReverseExpertTransferForChargebackAsync.",
                                userId: searchHire.ExpertId,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync.Fix9",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId);
                        }

                        if (((needsRefund && !string.IsNullOrEmpty(createdRefundId)) || refundAlreadyProcessed)
                            && transferAlreadyProcessed
                            && existingTransfer != null
                            && !string.IsNullOrEmpty(existingTransfer.StripeTransferId)
                            && clawbackAmountEur >= 0.01m
                            // ✅ [M4] RESUELTO (verificado 2026-06-17): omitir el clawback interno cuando hay
                            // Chargeback es SEGURO porque la reversión del transfer SÍ se encola siempre. La rama
                            // T9 de HandleChargeDisputeCreated (SubscriptionController ~l.9030-9053) ahora encola
                            // ReverseExpertTransferForChargebackAsync TANTO con dispute interna activa COMO sin ella
                            // (antes solo en el else). Ese job revierte el transfer COMPLETO y es idempotente, así
                            // que duplicar aquí el clawback parcial provocaría doble-reversión. Por eso se mantiene
                            // el skip cuando hasChargebackNow. (El comentario anterior describía el bug ya corregido.)
                            && !hasChargebackNow) // 🔁 R3 + 🛡️ FIX #9: hasChargebackNow incluye re-lectura justo antes del clawback (cierra la ventana de carrera con webhook)
                        {
                            var alreadyReversed = await _context.FinancialTransactions.AnyAsync(ft =>
                                ft.RelatedEntityType == "SearchHire" &&
                                ft.RelatedEntityId == searchHireId &&
                                ft.TransactionType == "TransferReversal" &&
                                ft.StripeTransferId == existingTransfer.StripeTransferId);

                            if (!alreadyReversed)
                            {
                                try
                                {
                                    var clawbackSvc = new TransferReversalService();
                                    var clawbackOptions = new TransferReversalCreateOptions
                                    {
                                        Amount = clawbackCents, // 🔧 FIX: céntimos exactos (calculados arriba), no Math.Round del decimal crudo
                                        Metadata = new Dictionary<string, string>
                                        {
                                            { "searchHireId", searchHireId.ToString() },
                                            { "statusValue", statusValue },
                                            { "reason", "clawback on client refund" }
                                        }
                                    };
                                    // 🔧 Clave PROPIA del clawback ("-reversal-"), DISTINTA de la del chargeback
                                    // ("-cbreversal-" en ReverseExpertTransferForChargebackAsync): revierten importes
                                    // distintos del mismo transfer, así que compartir clave daba idempotency_error.
                                    // La doble reversión concurrente clawback↔chargeback se evita porque el chargeback
                                    // lee el remanente VIVO y Stripe rechaza revertir por encima de AmountReversed.
                                    var clawbackRequestOptions = new RequestOptions { IdempotencyKey = $"md-{searchHireId}-reversal-{existingTransfer.StripeTransferId}" };
                                    var clawbackReversal = await clawbackSvc.CreateAsync(existingTransfer.StripeTransferId, clawbackOptions, clawbackRequestOptions);

                                    // Registrar la reversión en el ledger (importe negativo para el experto).
                                    _context.FinancialTransactions.Add(new FinancialTransaction
                                    {
                                        UserId = searchHire.ExpertId,
                                        Amount = -clawbackAmountEur,
                                        AmountCents = -clawbackCents,
                                        Currency = searchHire.Currency, // 🌍 Round 25: snapshot ISO 4217 desde el hire asociado
                                        TransactionType = "TransferReversal",
                                        RelatedEntityType = "SearchHire",
                                        RelatedEntityId = searchHireId,
                                        StripeTransferId = existingTransfer.StripeTransferId,
                                        StripePaymentIntentId = servicePayment.StripePaymentIntentId,
                                        CreatedAt = DateTime.UtcNow
                                    });

                                    // 🛡️ C4 FIX: SaveChanges INMEDIATO tras el Add, antes de que el flujo continúe
                                    // ~77 líneas hasta el SaveChanges global. Si el proceso muere en esa ventana, Stripe
                                    // ya tiene el reversal hecho pero la BD no — el reintento (guard alreadyReversed
                                    // busca en BD) reentra y Stripe replica la respuesta cacheada por idempotencia →
                                    // fila DUPLICADA en BD al final. Persistiendo aquí, el guard ya la encuentra.
                                    // Si SaveChanges falla, log critical: hay desincronía Stripe↔BD que requiere
                                    // reconciliación manual (el clawback Stripe ya ocurrió).
                                    try
                                    {
                                        await _context.SaveChangesAsync();
                                    }
                                    catch (Exception persistEx)
                                    {
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: Clawback applied in Stripe but FT TransferReversal failed to persist",
                                            details: $"SearchHire {searchHireId}: Stripe reversal {clawbackReversal.Id} de {clawbackAmountEur:F2}€ en transfer {existingTransfer.StripeTransferId} se ejecutó OK, pero el ledger BD no se actualizó. RECONCILIACIÓN MANUAL: insertar fila FinancialTransaction TransferReversal con esos datos. Error: {persistEx.Message}",
                                            userId: searchHire.ExpertId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync.C4Persist",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId,
                                            additionalData: new { ReversalId = clawbackReversal.Id, TransferId = existingTransfer.StripeTransferId, ClawbackAmount = clawbackAmountEur, Error = persistEx.Message });
                                        throw; // re-throw para no marcar el dispute como Resolved si el ledger está roto
                                    }

                                    await _loggingService.LogInfoAsync(
                                        message: "Expert transfer reversed on client refund (clawback)",
                                        details: $"SearchHire {searchHireId}: reversed {clawbackAmountEur:F2}€ of expert transfer {existingTransfer.StripeTransferId} (originally {existingTransfer.Amount:F2}€) because the client was refunded (status {statusValue}). Expert keeps {expertAmountForStripe:F2}€ ({config.ExpertPercentage}%). ReversalId: {clawbackReversal.Id}.",
                                        userId: searchHire.ExpertId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId);
                                }
                                // 🛡️ N11 FIX: idempotency hit en clawback — el transfer YA fue revertido
                                // (caso típico: reintento de Hangfire tras crash post-Stripe pre-BD).
                                // Tratar como no-op (no fallar, no abortar el flow).
                                catch (StripeException idemClawbackEx) when (
                                    idemClawbackEx.StripeError?.Code == "transfer_already_reversed"
                                    || idemClawbackEx.StripeError?.Code == "idempotency_error")
                                {
                                    await _loggingService.LogWarningAsync(
                                        message: "N11: clawback idempotency hit (already reversed)",
                                        details: $"SearchHire {searchHireId}: Stripe respondió '{idemClawbackEx.StripeError?.Code}' al revertir transfer {existingTransfer.StripeTransferId} — ya estaba revertido. Continuando.",
                                        userId: searchHire.ExpertId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync.N11Clawback",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId);
                                }
                                // 🛡️ Round 28 MUD-AV: experto ya retiró el balance Stripe Connect
                                // a su banco externo → reversal del transfer es imposible vía API.
                                // Stripe devuelve "insufficient_funds_for_transfer_reversal" o
                                // "balance_insufficient". SIN este catch dedicado, caía al catch
                                // genérico y el Hangfire retry job lo reintentaba indefinidamente
                                // (5+ veces antes de RefundFailedAt). Cada reintento = call Stripe
                                // API + LogCritical → email admin spam con la MISMA alerta.
                                //
                                // Acción correcta: registrar FT TransferReversal con marker de
                                // pérdida + Critical UNA SOLA VEZ con notifyUser, y NO throw → el
                                // flow continúa marcando el dispute Resolved-with-LossToPlatform.
                                // El admin recupera el dinero off-Stripe (transferencia bancaria
                                // directa entre el ex-experto y la plataforma) o lo asume como
                                // pérdida. La FT marker evita reentry del guard clawbackPending.
                                catch (StripeException balCbEx) when (
                                    balCbEx.StripeError?.Code == "insufficient_funds_for_transfer_reversal"
                                    || balCbEx.StripeError?.Code == "balance_insufficient")
                                {
                                    // Marker FT con Amount=0 — no movimiento real, solo señal al
                                    // guard clawbackPending de que ya intentamos el clawback (con
                                    // misma TransactionType+StripeTransferId). Detalle textual va
                                    // al log Critical.
                                    _context.FinancialTransactions.Add(new FinancialTransaction
                                    {
                                        UserId = searchHire.ExpertId,
                                        Amount = 0m,
                                        AmountCents = 0,
                                        Currency = searchHire.Currency,
                                        TransactionType = "TransferReversal",
                                        RelatedEntityType = "SearchHire",
                                        RelatedEntityId = searchHireId,
                                        StripeTransferId = existingTransfer.StripeTransferId,
                                        StripePaymentIntentId = servicePayment.StripePaymentIntentId,
                                        CreatedAt = DateTime.UtcNow
                                    });
                                    try { await _context.SaveChangesAsync(); }
                                    catch (Exception persistEx)
                                    {
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL MUD-AV: failed to persist clawback-impossible marker",
                                            details: $"SearchHire {searchHireId}: Stripe code={balCbEx.StripeError?.Code}, persist falló: {persistEx.Message}. El guard clawbackPending puede reintentar — admin debe insertar manualmente FT TransferReversal marker.",
                                            userId: searchHire.ExpertId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync.MUD-AV.PersistFailed",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId);
                                    }
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL MUD-AV: clawback impossible — expert already withdrew balance (platform absorbs loss)",
                                        details: $"SearchHire {searchHireId}: Stripe respondió '{balCbEx.StripeError?.Code}' al revertir transfer {existingTransfer.StripeTransferId} ({clawbackAmountEur:F2} {searchHire.Currency}). El experto retiró el balance Stripe Connect a su banco externo ANTES del clawback. No podemos reversar vía API. ACCIÓN ADMIN: (a) reclamar el dinero off-Stripe (transferencia bancaria directa expert→platform) o (b) asumir como pérdida operativa. NO se reintenta automáticamente (FT marker insertada). Cliente sigue refundado correctamente.",
                                        userId: searchHire.ExpertId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync.MUD-AV.WithdrawnBalance",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new { TransferId = existingTransfer.StripeTransferId, ClawbackAmount = clawbackAmountEur, Currency = searchHire.Currency, StripeError = balCbEx.StripeError?.Code },
                                        notifyUser: true);
                                    // 🛡️ MUD-BD: encolar en ClawbackQueues para dashboard admin.
                                    if (_clawbackQueue != null && searchHire.ExpertId.HasValue)
                                    {
                                        await _clawbackQueue.EnqueueAsync(
                                            userId: searchHire.ExpertId.Value,
                                            stripeAccountId: searchHire.Expert?.ExpertProfile?.StripeAccountId,
                                            amountMajor: clawbackAmountEur,
                                            currency: searchHire.Currency ?? "EUR",
                                            reason: "WithdrawnBalance",
                                            notes: $"Clawback impossible: experto retiró balance pre-clawback ({balCbEx.StripeError?.Code}). Transfer {existingTransfer.StripeTransferId}.",
                                            searchHireId: searchHireId);
                                    }
                                }
                                catch (Exception clawbackEx)
                                {
                                    // 🛡️ FIX #11: clawback falló DESPUÉS de que refund/transfer ya fueron persistidos.
                                    // NO revertimos el refund (debe quedar reembolsado). El reintento se dispara por:
                                    // 1) Hangfire (RetryMoneyDistributionJobAsync) que llama de nuevo ProcessMoneyDistributionAsync
                                    // 2) El guard clawbackPending (línea ~1103) detecta que hay refund+transfer pero NO
                                    //    TransferReversal y permite re-entrar al bloque del clawback.
                                    // Si Hangfire no lo detecta (sin enqueue automático aquí), alertamos como CRITICAL
                                    // con notifyUser para que admin actúe. La FT TransferReversal NO se persistió, así
                                    // que clawbackPending=true en la próxima ejecución.
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL FIX#11: Failed to reverse prior expert transfer on client refund (clawback)",
                                        details: $"SearchHire {searchHireId}: the client was refunded but {clawbackAmountEur:F2}€ of the prior expert transfer {existingTransfer.StripeTransferId} (originally {existingTransfer.Amount:F2}€) could NOT be reversed. " +
                                                 $"The expert may keep overpaid funds for a refunded order — MANUAL INTERVENTION REQUIRED. " +
                                                 $"RETRY: el guard clawbackPending (línea ~1103) detectará la falta de FT TransferReversal y permitirá reintentar el clawback en la próxima ejecución de ProcessMoneyDistributionAsync (Hangfire retry job o re-llamada manual). " +
                                                 $"Error: {clawbackEx.Message}",
                                        userId: searchHire.ExpertId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync.Fix11",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new { TransferId = existingTransfer.StripeTransferId, OriginalAmount = existingTransfer.Amount, ClawbackAmount = clawbackAmountEur, Error = clawbackEx.Message, RetryMechanism = "clawbackPending guard on next ProcessMoneyDistributionAsync call" },
                                        notifyUser: true // 🛡️ FIX #11: notificar admin que requiere intervención
                                    );
                                }
                            }
                        }

                        if (needsRefund && !string.IsNullOrEmpty(createdRefundId))
                        {
                            var refundTx = new FinancialTransaction
                            {
                                UserId = searchHire.ClientId,
                                Amount = Math.Round(clientRefundAmountForStripe, 2), // 🔧 redondeado a céntimo para casar con Stripe
                                AmountCents = newApi.Common.StripeMinorUnits.ToMinorUnitsOutbound(clientRefundAmountForStripe, searchHire.Currency), // céntimos exactos refundados (casan con Stripe; múltiplo-100 en HUF)
                                Currency = searchHire.Currency, // 🌍 Round 25: snapshot ISO 4217 desde el hire asociado
                                TransactionType = "Refund",
                                RelatedEntityType = "SearchHire",
                                RelatedEntityId = searchHireId,
                                StripePaymentIntentId = servicePayment.StripePaymentIntentId,
                                StripeRefundId = createdRefundId,
                                CreatedAt = DateTime.UtcNow
                            };
                            _context.FinancialTransactions.Add(refundTx);

                            servicePayment.IsRefunded = true;
                            // 🛡️ FIX 23505 (Round 28): NO escribir servicePayment.StripeRefundId aquí.
                            // El índice único parcial IX_FT_StripeRefundId_uq filtra solo por
                            // StripeRefundId IS NOT NULL (sin discriminar TransactionType), por lo que
                            // emitir el mismo `re_xxx` en la fila Refund (INSERT) y en la fila
                            // ServicePayment (UPDATE) dentro del MISMO SaveChanges viola el unique.
                            // El cross-link funcional se preserva vía StripePaymentIntentId (ambas filas
                            // lo comparten) + IsRefunded=true como marcador de estado. La fila Refund
                            // ya guarda StripeRefundId como fuente canónica. Verificado: ninguna lectura
                            // en código vivo depende de servicePayment.StripeRefundId.
                            // Mismo patrón ya aplicado en SubscriptionController.cs:7597 (R27-T27-1-5).

                            // 🛡️ N3 FIX (refund principal): SaveChanges INMEDIATO tras el Add. Antes había
                            // ~252 líneas entre refundSvc.CreateAsync (línea ~1254) y el SaveChanges de la
                            // línea siguiente (~1506) — ventana donde Stripe ya hizo el refund pero la BD
                            // podía morir sin persistir. Mismo patrón que C4 (clawback).
                            try
                            {
                                // ⚠️ AUDITORÍA [L3] Low: este SaveChanges N3 inserta la FT Refund pero NO la commitea — sigue dentro de la transacción abierta en ~L1117, que solo commitea en ~L2319.
                                // Disparo/ataque: un crash entre este INSERT y el CommitAsync ~L2319 hace ROLLBACK de la fila; el refund de Stripe (creado en ~L1827) queda huérfano en el ledger hasta el retry o el job diario de reconciliación.
                                // Fix: este SaveChanges adelantado NO cierra el gap real (INSERT→muerte→sin COMMIT); requiere outbox o que el side-effect Stripe se ejecute tras el commit de la fila, no antes.
                                await _context.SaveChangesAsync();
                            }
                            // 🛡️ FIX 23505 (Round 28): absorber colisión del índice único IX_FT_StripeRefundId_uq
                            // de forma idempotente. Se dispara si la fila FT Refund con este StripeRefundId
                            // YA existe (replay de NpgsqlRetryingExecutionStrategy, webhook concurrente, o
                            // residuo de la mutación legacy en servicePayment.StripeRefundId pre-fix). El
                            // refund REAL ya está en Stripe + alguna fila local lo registra → idempotencia OK.
                            catch (DbUpdateException dbEx) when (
                                dbEx.InnerException is PostgresException pgEx
                                && pgEx.SqlState == "23505"
                                && (pgEx.ConstraintName?.Contains("StripeRefundId") ?? false))
                            {
                                // Detach la entidad pendiente: sin esto, el SaveChanges global posterior
                                // reintentaría el mismo INSERT y volveríamos a chocar.
                                _context.Entry(refundTx).State = EntityState.Detached;

                                // Re-aplicar IsRefunded por si el rollback interno de EF descartó la mutación.
                                servicePayment.IsRefunded = true;

                                // Sanity check: la fila Refund con este StripeRefundId DEBE existir.
                                // Si NO existe, la colisión vino por otra ruta sutil → re-lanzar al catch genérico.
                                var existing = await _context.FinancialTransactions
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync(ft => ft.StripeRefundId == createdRefundId);
                                if (existing == null)
                                {
                                    await _loggingService.LogCriticalAsync(
                                        message: "23505 IX_FT_StripeRefundId_uq absorbed but no existing row found — abort",
                                        details: $"SearchHire {searchHireId}: PostgresException 23505 en {pgEx.ConstraintName} para refund {createdRefundId}, pero AsNoTracking lookup NO encuentra fila preexistente. Posible inconsistencia: re-lanzando. Stripe: refund SÍ ejecutado, BD: vacía → RECONCILIACIÓN MANUAL.",
                                        userId: searchHire.ClientId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync.23505NoRow",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new { RefundId = createdRefundId, ConstraintName = pgEx.ConstraintName, SqlState = pgEx.SqlState });
                                    throw;
                                }

                                await _loggingService.LogInfoAsync(
                                    message: "23505 IX_FT_StripeRefundId_uq absorbed (idempotent)",
                                    details: $"SearchHire {searchHireId}: refund {createdRefundId} ya existe en FT (id={existing.Id}, type={existing.TransactionType}). Colisión esperada en retries/replays — continuando sin re-throw.",
                                    userId: searchHire.ClientId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync.23505Idem",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { RefundId = createdRefundId, ExistingFtId = existing.Id, ExistingType = existing.TransactionType });
                            }
                            catch (Exception persistEx)
                            {
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Refund applied in Stripe but FT Refund failed to persist",
                                    details: $"SearchHire {searchHireId}: Stripe refund {createdRefundId} de {clientRefundAmountForStripe:F2}€ se ejecutó OK, pero el ledger BD no se actualizó. RECONCILIACIÓN MANUAL: insertar fila FinancialTransaction Refund con esos datos. Error: {persistEx.Message}",
                                    userId: searchHire.ClientId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync.N3RefundPersist",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { RefundId = createdRefundId, Amount = clientRefundAmountForStripe, Error = persistEx.Message });
                                throw;
                            }
                        }

                        if (needsTransfer && !string.IsNullOrEmpty(createdTransferId))
                        {
                            var expertTx = new FinancialTransaction
                            {
                                UserId = searchHire.ExpertId.Value,
                                Amount = Math.Round(expertAmountForStripe, 2), // 🔧 redondeado a céntimo para casar con Stripe
                                AmountCents = newApi.Common.StripeMinorUnits.ToMinorUnitsOutbound(expertAmountForStripe, searchHire.Currency), // céntimos exactos transferidos (casan con Stripe; múltiplo-100 en HUF)
                                Currency = searchHire.Currency, // 🌍 Round 25: snapshot ISO 4217 desde el hire asociado
                                TransactionType = "Payout",
                                RelatedEntityType = "SearchHire",
                                RelatedEntityId = searchHireId,
                                StripeTransferId = createdTransferId,
                                StripePaymentIntentId = servicePayment.StripePaymentIntentId, // 🔧 trazabilidad: vincular Payout al cargo (se propaga a la reversión por chargeback)
                                CreatedAt = DateTime.UtcNow
                            };
                            _context.FinancialTransactions.Add(expertTx);

                            // ⚠️ AUDITORÍA [M2] Medium: el N3 FIX acortó el gap pero NO lo cerró: sigue habiendo ventana entre el transfer (1487) y este SaveChanges/commit (2298/2319) en la que un crash deja el transfer hecho en Stripe sin fila Payout, con el hire ya commiteado como "completed" en FASE 2.
                            // Disparo/ataque: muerte del proceso aquí (peor en cross-currency por la lectura de BalanceTransaction intermedia) → ledger interno cree que NO se pagó al experto; reconciliación mensual descuadra; reintento manual con statusValue distinto = doble pago.
                            // Fix: registrar la fila Payout como "pending" ANTES del CreateAsync y confirmarla después (patrón outbox), de modo que el crash deje rastro reencolable de forma idempotente.
                            // 🛡️ N3 FIX (transfer principal): SaveChanges INMEDIATO. Antes había ~368 líneas
                            // de gap entre transferSvc.CreateAsync y el SaveChanges global → riesgo idéntico.
                            try
                            {
                                await _context.SaveChangesAsync();
                            }
                            catch (Exception persistEx)
                            {
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Transfer applied in Stripe but FT Payout failed to persist",
                                    details: $"SearchHire {searchHireId}: Stripe transfer {createdTransferId} de {expertAmountForStripe:F2}€ se ejecutó OK, pero el ledger BD no se actualizó. RECONCILIACIÓN MANUAL: insertar fila FinancialTransaction Payout con esos datos. Error: {persistEx.Message}",
                                    userId: searchHire.ExpertId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync.N3TransferPersist",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { TransferId = createdTransferId, Amount = expertAmountForStripe, Error = persistEx.Message });
                                throw;
                            }
                        }

                        // 🛡️ M5b FIX (2026-07-06): distribución completada → limpiar RefundFailedAt para
                        // que el watchdog M5 no re-encole un hire cuyo dinero ya se movió (sin el guard
                        // F4b podía llegar a crear un transfer del 95% sobre un hire cancelado).
                        // Best-effort: si el hire fue modificado concurrentemente (xmin obsoleto), NO
                        // tumbar el commit del dinero por limpiar un flag — se detacha y el camino
                        // idempotente del próximo reintento lo limpia.
                        // 🛡️ FIX [W19-REFUNDFAILED-CLEAR]: NO borrar el flag si en ESTA pasada una rama de aborto
                        // de transfer lo puso para marcar el payout del experto como retenido (si no, cegamos
                        // RetryRefundFailedHiresAsync y la recuperación MUD-CB-2). Solo se limpia el RefundFailedAt
                        // VIEJO (de un intento previo) cuando el dinero SÍ se movió con éxito en esta pasada.
                        if (searchHire.RefundFailedAt != null && !transferDeferredForManualReview)
                        {
                            searchHire.RefundFailedAt = null;
                            try { await _context.SaveChangesAsync(); }
                            catch (DbUpdateConcurrencyException) { _context.Entry(searchHire).State = EntityState.Detached; }
                        }
                        await _context.SaveChangesAsync(); // no-op si N3 ya persistió; útil si quedan tracked changes
                        
                        // Ô£à CORRECCI├ôN: Solo hacer commit si creamos la transacci├│n
                        if (transaction != null)
                        {
                        // ⚠️ AUDITORÍA [L3] Low: ÚNICO punto donde la fila FT Refund (Add ~L2197) se vuelve durable; está muy lejos de la creación del refund en Stripe (~L1827).
                        // Disparo/ataque: cualquier excepción/kill del worker entre ~L2217 y esta línea → rollback de la FT; refund vivo en Stripe sin registro en BD → ledger infravalora refunds (riesgo de que el SUM-check W1 de ~L1164 no contabilice este refund hasta reconciliar). Impacto acotado a un refund por incidente, sin doble pago.
                        // Fix: alinear el efecto Stripe con este commit (outbox / efecto post-commit) para eliminar la ventana no atómica.
                        await transaction.CommitAsync();
                        }

                        // ✅ Notificar a usuarios sobre movimientos de dinero exitosos
                        // 🛡️ Round 28 Sprint US-2 (SUS2-3): texto reescrito en UTF-8 limpio (antes
                        // tenía mojibake "proces├│", "llegar├í", "d├¡as" por edición Windows-1252).
                        // 🛡️ Sprint 3: usar divisa real del hire en lugar de € hardcoded.
                        var notifCurrencyLabel = (searchHire.Currency ?? "EUR").Trim().ToUpperInvariant();
                        if (needsRefund && !string.IsNullOrEmpty(createdRefundId))
                        {
                            // Refund exitoso - notificar al cliente
                            await _loggingService.LogInfoAsync(
                                message: "Reembolso procesado",
                                details: $"Se procesó tu reembolso de {clientRefundAmountForStripe:F2} {notifCurrencyLabel} por el servicio #{searchHireId}. El dinero llegará a tu cuenta en 5-10 días hábiles.",
                                userId: searchHire.ClientId,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                notifyUser: true
                            );
                            // 🔔 NOTIF-FIX [SMS-dinero]: refuerzo SMS al cliente (auto-gateado por móvil
                            // verificado → no-op para la mayoría; llega a quien lo tenga). Best-effort.
                            if (_inAppNotifications != null && searchHire.ClientId.HasValue)
                            {
                                try
                                {
                                    await _inAppNotifications.SendImportantSmsAsync(searchHire.ClientId.Value,
                                        $"Inspecciono: reembolso de {clientRefundAmountForStripe:F2} {notifCurrencyLabel} procesado (servicio #{searchHireId}). Llegara a tu cuenta en 5-10 dias habiles.");
                                }
                                catch { /* SMS best-effort */ }
                            }
                        }

                        if (needsTransfer && !string.IsNullOrEmpty(createdTransferId) && searchHire.ExpertId.HasValue)
                        {
                            // Transfer exitoso - notificar al experto
                            await _loggingService.LogInfoAsync(
                                message: "Pago recibido",
                                details: $"Has recibido {expertAmountForStripe:F2} {notifCurrencyLabel} por el servicio #{searchHireId}. El dinero está disponible en tu cuenta de Stripe.",
                                userId: searchHire.ExpertId.Value,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                notifyUser: true
                            );
                            // 🔔 NOTIF-FIX [SMS-dinero]: refuerzo SMS al experto — cobrar es EL evento;
                            // móvil verificado por KYC, así que el SMS le llega siempre. Best-effort.
                            if (_inAppNotifications != null)
                            {
                                try
                                {
                                    await _inAppNotifications.SendImportantSmsAsync(searchHire.ExpertId.Value,
                                        $"Inspecciono: has recibido {expertAmountForStripe:F2} {notifCurrencyLabel} por el servicio #{searchHireId}. Ya esta disponible en tu cuenta de Stripe.");
                                }
                                catch { /* SMS best-effort */ }
                            }
                        }

                        return true;
                    }
                    catch (StripeException ex)
                    {
                        // Ô£à CORRECCI├ôN: Solo hacer rollback si creamos la transacci├│n
                        if (transaction != null)
                    {
                        await transaction.RollbackAsync();
                        }
                        
                        // Ô£à MEJORA GROK: Notificar al experto si hay error de Stripe (estado ya est├í cambiado)
                        if (searchHire.ExpertId.HasValue)
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Stripe error - state already updated",
                                details: $"El estado del servicio #{searchHireId} se actualiz├│ correctamente, pero hubo un error al procesar el pago. " +
                                        $"Error de Stripe: {ex.Message}. " +
                                        $"Se requiere procesamiento manual del pago. " +
                                        $"Plan de distribuci├│n: Cliente={clientRefundAmount:F2}€ ({config.ClientPercentage}%), Experto={expertAmount:F2}€ ({config.ExpertPercentage}%), Plataforma={platformAmount:F2}€ ({config.PlatformPercentage}%). " +
                                        $"Estado: {statusValue}, Raz├│n: {reason}. " +
                                        $"Transfer={(createdTransferId != null ? $"Creado ({createdTransferId})" : "No intentado")}, Refund={(createdRefundId != null ? $"Creado ({createdRefundId})" : "No intentado")}.",
                                userId: searchHire.ExpertId.Value,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                notifyUser: true, // Ô£à Notificar al experto
                                additionalData: new { 
                                    Status = statusValue,
                                    Reason = reason,
                                    ClientRefundAmount = clientRefundAmount,
                                    ExpertTransferAmount = expertAmount,
                                    PlatformAmount = platformAmount,
                                    PaymentIntentId = servicePayment.StripePaymentIntentId,
                                    ExpertAccountId = searchHire.Expert?.ExpertProfile?.StripeAccountId,
                                    CreatedTransferId = createdTransferId,
                                    CreatedRefundId = createdRefundId,
                                    StripeError = ex.Message,
                                    StripeErrorType = ex.StripeError?.Type,
                                    StripeErrorCode = ex.StripeError?.Code
                                }
                            );
                        }
                        
                        // ­ƒÜ¿ LOG CR├ìTICO: Error de Stripe durante distribuci├│n (una sola vez, con informaci├│n completa)
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Stripe exception during money distribution transaction",
                            details: $"Stripe exception occurred during money distribution transaction for SearchHire {searchHireId}. " +
                                    $"Distribution Plan: Client={clientRefundAmount}€ ({config.ClientPercentage}%), Expert={expertAmount}€ ({config.ExpertPercentage}%), Platform={platformAmount}€ ({config.PlatformPercentage}%). " +
                                    $"Stripe Error: {ex.Message}, Type: {ex.StripeError?.Type}, Code: {ex.StripeError?.Code}, DeclineCode: {ex.StripeError?.DeclineCode}, Param: {ex.StripeError?.Param}. " +
                                    $"PaymentIntentId: {servicePayment.StripePaymentIntentId}, ExpertAccountId: {searchHire.Expert?.ExpertProfile?.StripeAccountId}. " +
                                    $"Transaction Status: Transfer={(createdTransferId != null ? $"Created ({createdTransferId})" : "Not attempted")}, Refund={(createdRefundId != null ? $"Created ({createdRefundId})" : "Not attempted")}. " +
                                    $"NOTE: State was already updated in Phase 2. ACTION REQUIRED: Review Stripe error details and retry distribution if applicable. If transfer was created, verify if reversal is needed.",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                Reason = reason,
                                ClientRefundAmount = clientRefundAmount,
                                ExpertTransferAmount = expertAmount,
                                PlatformAmount = platformAmount,
                                PaymentIntentId = servicePayment.StripePaymentIntentId,
                                ExpertAccountId = searchHire.Expert?.ExpertProfile?.StripeAccountId,
                                CreatedTransferId = createdTransferId,
                                CreatedRefundId = createdRefundId,
                                StripeError = ex.Message,
                                StripeErrorType = ex.StripeError?.Type,
                                StripeErrorCode = ex.StripeError?.Code,
                                StripeDeclineCode = ex.StripeError?.DeclineCode,
                                StripeParam = ex.StripeError?.Param
                            }
                        );
                        return false;
                    }
                    catch (Exception ex)
                    {
                        // Ô£à CORRECCI├ôN: Solo hacer rollback si creamos la transacci├│n
                        if (transaction != null)
                    {
                        await transaction.RollbackAsync();
                        }
                        // ­ƒÜ¿ LOG CR├ìTICO: Error general durante distribuci├│n (una sola vez, con informaci├│n completa)
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Unexpected exception during money distribution transaction",
                            details: $"An unexpected exception occurred during money distribution transaction for SearchHire {searchHireId}. " +
                                    $"Distribution Plan: Client={clientRefundAmount}€ ({config.ClientPercentage}%), Expert={expertAmount}€ ({config.ExpertPercentage}%), Platform={platformAmount}€ ({config.PlatformPercentage}%). " +
                                    $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                                    $"PaymentIntentId: {servicePayment.StripePaymentIntentId}, ExpertAccountId: {searchHire.Expert?.ExpertProfile?.StripeAccountId}. " +
                                    $"Transaction Status: Transfer={(createdTransferId != null ? $"Created ({createdTransferId})" : "Not attempted")}, Refund={(createdRefundId != null ? $"Created ({createdRefundId})" : "Not attempted")}. " +
                                    $"Stack Trace: {ex.StackTrace}. " +
                                    $"ACTION REQUIRED: Review exception details. If transfer/refund were created, verify if reversal is needed.",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                Reason = reason,
                                ClientRefundAmount = clientRefundAmount,
                                ExpertTransferAmount = expertAmount,
                                PlatformAmount = platformAmount,
                                PaymentIntentId = servicePayment.StripePaymentIntentId,
                                ExpertAccountId = searchHire.Expert?.ExpertProfile?.StripeAccountId,
                                CreatedTransferId = createdTransferId,
                                CreatedRefundId = createdRefundId,
                                ErrorType = ex.GetType().Name,
                                ErrorMessage = ex.Message,
                                StackTrace = ex.StackTrace,
                                InnerException = ex.InnerException?.Message
                            }
                        );
                        return false;
                    }
                };
                
                // Ô£à Si no hay transacci├│n existente, usar estrategia de reintento
                if (existingTransactionForMoney == null)
                {
                    var strategy = _context.Database.CreateExecutionStrategy();
                    return await strategy.ExecuteAsync(ProcessMoneyAsync);
                }
                else
                {
                    // Ô£à Usar transacci├│n existente - ejecutar directamente sin estrategia de reintento
                    // (el reintento se maneja a nivel de la transacci├│n global)
                    return await ProcessMoneyAsync();
                }
            }
            catch (Exception ex)
            {
                // ­ƒÜ¿ LOG CR├ìTICO: Error general fuera de la transacci├│n (una sola vez, con informaci├│n completa)
                // Este error ocurre ANTES de entrar en la transacci├│n, por lo que no hay datos de distribuci├│n calculados
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: ProcessMoneyDistributionAsync failed - outer catch",
                    details: $"An unexpected exception occurred in ProcessMoneyDistributionAsync before entering transaction for SearchHire {searchHireId}. " +
                            $"Status: {statusValue}, Reason: {reason}, InitiatedByUserId: {initiatedByUserId}. " +
                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                            $"Stack Trace: {ex.StackTrace}. " +
                            $"ACTION REQUIRED: Review error - this indicates a pre-transaction validation, data loading, or configuration issue.",
                    userId: initiatedByUserId,
                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        Status = statusValue,
                        Reason = reason,
                        InitiatedByUserId = initiatedByUserId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );
                return false;
            }
        }

        /// <summary>
        /// Job de Hangfire para REINTENTAR la distribución de dinero de forma asíncrona cuando un
        /// finalizador (completar/cancelar/resolver disputa) no pudo mover el dinero pero SÍ avanzó
        /// el estado. Filosofía "el flujo continúa para el usuario; el dinero se reintenta y se avisa".
        /// ProcessMoneyDistributionAsync es idempotente (claves de idempotencia + guardas en BD), así que
        /// reintentar es seguro (no duplica pagos). Si sigue fallando se LANZA para que Hangfire reintente;
        /// la causa ya se registró como Critical (que ahora avisa por email al admin).
        /// </summary>
        // ⚠️ AUDITORÍA [L5] Low: este comentario R15 describe MAL el alcance del lock. DisableConcurrentExecution sin
        // override de GetResource NO bloquea "para el mismo hire": bloquea por Type+Method → lock global compartido por
        // todos los searchHireId. La protección anti-doble-pago real son idempotencia Stripe + guards FT + R16, no este lock.
        // 🛡️ R15: lock distribuido POR-HIRE (clave "retry-money-{searchHireId}") evita que dos jobs concurrentes
        // (encolados desde distintos sitios, ej DisputeController + AppointmentService + SearchHireController) corran
        // simultáneamente PARA EL MISMO hire. Sin esto ambos ven el guard idempotente pasar a la vez y pueden
        // duplicar transfer/refund. Timeout 600s cubre cualquier ProcessMoneyDistribution legítimo (Stripe API + SaveChanges).
        // ✅ FIX AUDITORÍA [L5] Low: antes se usaba [DisableConcurrentExecution] SIN override de GetResource, que bloquea
        // por Type+Method ("StripeRefundService.RetryMoneyDistributionJobAsync"), NO por searchHireId → lock GLOBAL que
        // serializaba los ~12 sitios de encolado para TODOS los hires (no solo el mismo). Eso provocaba head-of-line
        // blocking: varios hires en 'transfer_failed' a la vez (watchdog PlatformMaintenanceService.cs:496 encola N retries
        // en ráfaga) corrían EN SERIE; un job lento retrasaba TODOS los payouts/refunds horas (vía AutomaticRetry).
        // Reemplazado por AcquireDistributedLock scoped al searchHireId → el lock ahora es por-hire, mejora el throughput
        // y NO afecta la seguridad anti-doble-pago (que ya la dan idempotencia Stripe + guards FT + re-verificación R16).
        [Hangfire.AutomaticRetry(Attempts = 5, DelaysInSeconds = new[] { 120, 600, 1800, 3600, 7200 })]
        public async Task RetryMoneyDistributionJobAsync(int searchHireId, string statusValue, string reason, int? initiatedByUserId)
        {
            // 🛡️ R15 (por-hire): lock distribuido scoped al searchHireId. Si AcquireDistributedLock lanza por timeout
            // (600s), dejamos propagar la excepción → Hangfire reintentará vía [AutomaticRetry].
            using (Hangfire.JobStorage.Current.GetConnection().AcquireDistributedLock($"retry-money-{searchHireId}", TimeSpan.FromSeconds(600)))
            {
            // 🛡️ R16 FIX: re-verify state DEL HIRE en lugar de asumir statusValue del enqueue time.
            // Entre el enqueue (delay 2 min) y la ejecución el hire pudo cambiar de estado por otro
            // flow (admin re-resolvió, chargeback llegó, etc.). Si el estado actual no coincide con
            // el statusValue del enqueue, abortar — el flow alternativo ya manejó el dinero.
            var currentHire = await _context.SearchHires
                .AsNoTracking()
                .Include(sh => sh.Status)
                .Include(sh => sh.Appointment)
                    .ThenInclude(a => a.Status)
                .FirstOrDefaultAsync(sh => sh.Id == searchHireId);
            if (currentHire == null)
            {
                await _loggingService.LogWarningAsync(
                    message: "R16: RetryMoneyDistribution skipped — SearchHire no encontrado",
                    details: $"SearchHire {searchHireId} no existe (¿borrado entre enqueue y ejecución?). Statusvalue del enqueue era {statusValue}.",
                    userId: initiatedByUserId,
                    source: "StripeRefundService.RetryMoneyDistributionJobAsync.R16",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId);
                return; // no throw → no reintento (es irreparable)
            }
            // 🛡️ R16b FIX (2026-06-11): statusValue puede ser un AppointmentStatus (las
            // cancelaciones por timer encolan p.ej. "appointment_cancelled_by_client_no_proposal"
            // mientras el HIRE ya quedó en "cancelled" vía mapping). Comparar SOLO contra el
            // hire hacía que TODOS los reintentos de dinero de timers fueran no-op silenciosos
            // ("Succeeded" sin mover dinero — caso real: hire 16 en prod, job hangfire #5156,
            // 100€ sin mover). Aceptar también el match contra el estado del Appointment.
            var actualStatus = currentHire.Status?.StatusValue;
            var appointmentStatus = currentHire.Appointment?.Status?.StatusValue;
            var statusStillMatches =
                string.Equals(actualStatus, statusValue, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(appointmentStatus, statusValue, StringComparison.OrdinalIgnoreCase);
            if (!statusStillMatches)
            {
                // 🛡️ F29 FIX: el cambio de estado NO garantiza que el dinero se movió. Si otro flow avanzó
                // el estado pero murió ANTES de la distribución (o nunca la encoló), un no-op silencioso aquí
                // deja al cliente/experto sin su dinero para siempre. Antes de skip-no-op, comprobar en
                // FinancialTransactions que realmente exista un movimiento (Refund/Payout/TransferReversal)
                // para este hire. Solo entonces el skip es seguro.
                //
                // 🛡️ FIX [W22b-SIDE-AWARE] (2026-07-13): en 'dispute_resolved_client' (90/8/2) el experto YA
                // cobró en el 'completed' previo → existe una Payout vieja que NO es de ESTA distribución. La
                // resolución pro-cliente crea Refund + TransferReversal (clawback). Incluir Payout en el check
                // daría FALSO POSITIVO (moneyMoved=true por la Payout vieja) → skip → el reembolso ordenado por
                // el admin nunca se ejecuta (misma pérdida silenciosa que arregla W22 en B4). Para ese estado
                // comprobamos Refund/TransferReversal, no Payout. El resto de estados no tienen Payout previa
                // coexistiendo con un refund nuevo, así que su check original es correcto.
                var isDisputeClientRetry = string.Equals(statusValue,
                    SearchHireStatus.DisputeResolvedClient.ToStringValue(), StringComparison.OrdinalIgnoreCase);
                var moneyMoved = isDisputeClientRetry
                    ? await _context.FinancialTransactions
                        .AnyAsync(ft => ft.RelatedEntityType == "SearchHire" &&
                                        ft.RelatedEntityId == searchHireId &&
                                        (ft.TransactionType == "Refund" ||
                                         ft.TransactionType == "TransferReversal"))
                    : await _context.FinancialTransactions
                        .AnyAsync(ft => ft.RelatedEntityType == "SearchHire" &&
                                        ft.RelatedEntityId == searchHireId &&
                                        (ft.TransactionType == "Refund" ||
                                         ft.TransactionType == "Payout" ||
                                         ft.TransactionType == "TransferReversal"));
                if (moneyMoved)
                {
                    await _loggingService.LogWarningAsync(
                        message: "R16: RetryMoneyDistribution skipped — estado cambió entre enqueue y ejecución",
                        details: $"SearchHire {searchHireId}: statusValue del enqueue='{statusValue}', estado actual hire='{actualStatus}', appointment='{appointmentStatus}'. Otro flow ya completó la transición Y el dinero ya se movió (FT Refund/Payout/TransferReversal presente); este reintento es no-op.",
                        userId: initiatedByUserId,
                        source: "StripeRefundService.RetryMoneyDistributionJobAsync.R16",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId);
                    return; // no throw → no reintento (es benign — otro flow tomó el caso y movió el dinero)
                }
                // El estado cambió pero NO hay movimiento de dinero → el dinero sigue pendiente. Procesar con
                // el statusValue del enqueue (sus guards evitan doble pago); updateState:false porque el estado
                // ya lo gestionó otro flow. Si falla, lanzar para que Hangfire reintente.
                await _loggingService.LogWarningAsync(
                    message: "F29: estado cambió entre enqueue y ejecución PERO el dinero sigue pendiente — procesando",
                    details: $"SearchHire {searchHireId}: statusValue del enqueue='{statusValue}', estado actual hire='{actualStatus}', appointment='{appointmentStatus}'. No existe FT Refund/Payout/TransferReversal → NO es no-op: se ejecuta la distribución (updateState:false) para no dejar el dinero atascado.",
                    userId: initiatedByUserId,
                    source: "StripeRefundService.RetryMoneyDistributionJobAsync.R16",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId);
                var movedOk = await ProcessMoneyDistributionAsync(searchHireId, statusValue, reason, initiatedByUserId, updateState: false);
                if (!movedOk)
                {
                    throw new InvalidOperationException(
                        $"Money distribution still pending for SearchHire {searchHireId} (status {statusValue}, state changed but no FT). Hangfire will retry.");
                }
                return;
            }

            // El estado ya fue finalizado por el llamador → updateState:false (solo mover dinero).
            var ok = await ProcessMoneyDistributionAsync(searchHireId, statusValue, reason, initiatedByUserId, updateState: false);
            if (!ok)
            {
                throw new InvalidOperationException(
                    $"Money distribution still pending for SearchHire {searchHireId} (status {statusValue}). Hangfire will retry.");
            }
            } // end using AcquireDistributedLock (lock por-hire R15)
        }

        /// <summary>
        /// 🔧 Auto-sanación del estado intermedio "Resolving" (P1). El claim atómico de
        /// DisputeController.ResolveDispute deja la disputa en "Resolving" mientras mueve el dinero. Si la
        /// request muere en esa ventana (deploy/OOM/timeout) la disputa quedaría atascada en "Resolving" para
        /// siempre (todos los caminos de re-resolución exigen "Pending" y no hay watchdog que la recoja). Este
        /// job se PROGRAMA al hacer el claim; Hangfire lo persiste, así que sobrevive a la caída del proceso.
        /// Al dispararse SOLO actúa si la disputa SIGUE en "Resolving" (una resolución normal ya habría llegado
        /// a "Resolved" o reseteado a "Pending" en segundos): la devuelve a "Pending" para re-resolución manual
        /// (la distribución de dinero es idempotente) y avisa como crítico. No-op en el caso normal.
        /// </summary>
        [Hangfire.AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 900 })]
        public async Task RescueStuckResolvingDisputeAsync(int disputeId)
        {
            // 🛡️ B4 FIX: antes de devolver a 'Pending', comprobar si el SearchHire vinculado YA
            // está en un estado terminal de disputa (DisputeResolvedClient/Expert). Si lo está,
            // el money distribution se completó — la request murió en la ventana entre el dinero
            // y el dispute.Status='Resolved'. Devolver a Pending en ese caso permitiría al admin
            // ejecutar una SEGUNDA resolución a favor del LADO CONTRARIO → doble movimiento de
            // dinero en direcciones opuestas y ledger contradictorio. En su lugar: catch-up a
            // 'Resolved' directamente (idempotente: si el resto ya pasó, esto es no-op).
            var dispute = await _context.Disputes
                .Include(d => d.SearchHire)
                .ThenInclude(sh => sh.Status)
                .FirstOrDefaultAsync(d => d.Id == disputeId);
            if (dispute == null) return;

            var shStatusValue = dispute.SearchHire?.Status?.StatusValue;
            var isResolvedClient = shStatusValue == SearchHireStatus.DisputeResolvedClient.ToStringValue();
            var isResolvedExpert = shStatusValue == SearchHireStatus.DisputeResolvedExpert.ToStringValue();
            var statusTerminal = isResolvedClient || isResolvedExpert;

            if (statusTerminal && dispute.Status == "Resolving")
            {
                // 🛡️ FIX [W22-B4-FT-CHECK] (auditoría 2026-07-13): el estado terminal del hire NO garantiza
                // que el dinero se movió. DisputeController.ResolveDispute fija hire.Status=dispute_resolved_*
                // ANTES de mover el dinero y FUERA de una transacción; si el proceso muere en esa ventana el
                // hire queda terminal SIN FT. Antes B4 deducía "moneyAlreadyMoved" solo del estado → catch-up
                // CIEGO a 'Resolved' → el reembolso ordenado por el admin se perdía en SILENCIO (el cliente
                // nunca cobraba y R7 bloquea re-resolver). Comprobamos el movimiento REAL en FinancialTransactions
                // y, si no lo hubo, EJECUTAMOS la distribución que el admin ordenó.
                //
                // 🛡️ FIX [W22b-SIDE-AWARE] (2ª revisión adversarial): el movimiento que crea CADA resolución
                // depende del LADO, y NO debe confundirse con FT de una distribución PREVIA (ledger append-only):
                //  • pro-CLIENTE (90/8/2): la resolución crea un Refund (90%) + un TransferReversal (clawback). Si
                //    el hire ya se completó, EXISTE una Payout vieja del 'completed' — incluirla daría FALSO
                //    POSITIVO (moneyMoved=true por la Payout) → catch-up sin reembolsar → JUSTO la pérdida que
                //    este fix evita, en el caso DOMINANTE (disputa sobre hire ya pagado). Por eso se EXCLUYE Payout.
                //  • pro-EXPERTO (0/95/5): el desenlace correcto es que el experto tenga su transfer; la Payout
                //    (nueva, o la del 'completed' previo con el mismo reparto) es el indicador válido → catch-up.
                var moneyMoved = isResolvedClient
                    ? await _context.FinancialTransactions.AnyAsync(ft =>
                        ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == dispute.SearchHire!.Id &&
                        (ft.TransactionType == "Refund" || ft.TransactionType == "TransferReversal"))
                    : await _context.FinancialTransactions.AnyAsync(ft =>
                        ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == dispute.SearchHire!.Id &&
                        ft.TransactionType == "Payout");

                if (moneyMoved)
                {
                    // Dinero SÍ movido (crash entre el dinero y dispute.Status='Resolved'): solo cerrar. Idempotente.
                    var completed = await _context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"Disputes\" SET \"Status\" = 'Resolved' WHERE \"Id\" = {disputeId} AND \"Status\" = 'Resolving'");
                    if (completed > 0)
                    {
                        await _loggingService.LogWarningAsync(
                            message: "B4: Stuck dispute in 'Resolving' CATCH-UP a 'Resolved' (money already moved)",
                            details: $"Dispute {disputeId}: SearchHire terminal '{shStatusValue}' y FT de movimiento presente → catch-up a 'Resolved' (evita segunda resolución contradictoria).",
                            userId: null,
                            source: "StripeRefundService.RescueStuckResolvingDisputeAsync",
                            relatedEntityType: "Dispute",
                            relatedEntityId: disputeId);
                    }
                    return;
                }

                // Estado terminal pero SIN FT → el dinero NUNCA se movió. Ejecutar la distribución ORDENADA por
                // el admin (hire.Status = dispute_resolved_client 90/8/2 o _expert 0/95/5). updateState:false: el
                // estado ya está fijado. Los guards de ProcessMoneyDistributionAsync (advisory lock +
                // existingRefund/existingTransfer + claves idempotentes de Stripe) impiden doble pago aunque
                // corriera dos veces. NO reseteamos a 'Pending' a propósito: el admin ya decidió y el estado lo
                // refleja; solo faltaba ejecutar el dinero de ESE lado (no abrir la puerta a resolver el opuesto).
                var movedNow = await ProcessMoneyDistributionAsync(
                    dispute.SearchHire!.Id, shStatusValue!,
                    $"B4-W22 rescate: completar la distribución de la disputa {disputeId} cuyo pago no se ejecutó (crash entre el commit del estado y el movimiento de dinero).",
                    null, updateState: false);
                if (movedNow)
                {
                    await _context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"Disputes\" SET \"Status\" = 'Resolved' WHERE \"Id\" = {disputeId} AND \"Status\" = 'Resolving'");
                    await _loggingService.LogCriticalAsync(
                        message: "B4-W22: distribución de disputa RECUPERADA (estado terminal sin FT) y cerrada a 'Resolved'",
                        details: $"Dispute {disputeId} / SearchHire {dispute.SearchHire!.Id}: el hire estaba en '{shStatusValue}' pero NO existía FT de movimiento (la resolución del admin murió entre el commit del estado y el pago). Ejecutada la distribución ahora; cliente/experto ya reciben lo ordenado.",
                        userId: null,
                        source: "StripeRefundService.RescueStuckResolvingDisputeAsync.W22",
                        relatedEntityType: "Dispute",
                        relatedEntityId: disputeId);
                }
                else
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL B4-W22: no se pudo completar la distribución de la disputa recuperada",
                        details: $"Dispute {disputeId} / SearchHire {dispute.SearchHire!.Id}: ProcessMoneyDistributionAsync devolvió false; la disputa se deja en 'Resolving' para el AutomaticRetry de este job (o intervención admin).",
                        userId: null,
                        source: "StripeRefundService.RescueStuckResolvingDisputeAsync.W22",
                        relatedEntityType: "Dispute",
                        relatedEntityId: disputeId);
                    throw new InvalidOperationException(
                        $"B4-W22: money distribution still pending for dispute {disputeId} (hire {dispute.SearchHire!.Id}, status {shStatusValue}). Hangfire will retry.");
                }
                return;
            }

            // NOTA: este watchdog NO auto-resuelve disputas ni mueve dinero por timeout del experto.
            // La resolución de disputas es SIEMPRE manual del admin (DisputeController.ResolveDispute).
            // Aquí solo se rescata el estado intermedio 'Resolving' que deja una resolución admin que
            // murió a mitad: si el dinero ya se movió → catch-up arriba (B4); si no → reset a 'Pending'
            // para que el admin la re-resuelva. (El job EscalateStaleDisputesAsync ya NO encola esto:
            // solo avisa al admin de que la ventana del experto venció.)

            // Caso normal: el money distribution NO se completó → revertir a Pending para reintento.
            // Atómico: solo resetea si SIGUE en "Resolving" (no pisa una que ya llegó a "Resolved"/"Pending").
            var reset = await _context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Disputes\" SET \"Status\" = 'Pending' WHERE \"Id\" = {disputeId} AND \"Status\" = 'Resolving'");

            if (reset > 0)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Stuck dispute in 'Resolving' rescued to 'Pending'",
                    details: $"Dispute {disputeId} permanecio en 'Resolving' mas alla de la ventana de resolucion " +
                             "(la request de resolucion probablemente murio tras el claim atomico, antes de marcar " +
                             "'Resolved' o resetear). Devuelta a 'Pending' para re-resolucion (la distribucion de " +
                             "dinero es idempotente). REVISAR si el dinero llego a moverse parcialmente.",
                    userId: null,
                    source: "StripeRefundService.RescueStuckResolvingDisputeAsync",
                    relatedEntityType: "Dispute",
                    relatedEntityId: disputeId,
                    additionalData: new
                    {
                        metric_name = "dispute_stuck_resolving_rescued_total",
                        metric_kind = "counter",
                        event_type = "dispute_stuck_resolving_rescued",
                        severity = "critical",
                        DisputeId = disputeId,
                        TimestampUtc = DateTime.UtcNow
                    });
            }
            // reset == 0 → caso normal (ya 'Resolved'/'Pending'): no-op silencioso.
        }

        /// <summary>
        /// 🔁 A3 (R3): REVERSIÓN TOTAL del transfer al experto cuando hay un CHARGEBACK (contracargo).
        /// Un chargeback revierte el cargo ENTERO (el banco devuelve el 100% al cliente y Stripe retira el
        /// bruto de la plataforma), así que el experto NO debe quedarse su transfer — se revierte COMPLETO
        /// (a diferencia del clawback por disputa interna, que usa el % de la config). Idempotente: no hace
        /// nada si no hubo transfer o si ya se revirtió (fila TransferReversal para ese StripeTransferId).
        /// Se encola desde HandleChargeDisputeCreated. Lanza si Stripe falla → Hangfire reintenta + el filtro avisa.
        /// </summary>
        [Hangfire.AutomaticRetry(Attempts = 5, DelaysInSeconds = new[] { 120, 600, 1800, 3600, 7200 })]
        public async Task ReverseExpertTransferForChargebackAsync(int searchHireId, string reason)
        {
            var payout = await _context.FinancialTransactions
                .FirstOrDefaultAsync(ft => ft.RelatedEntityType == "SearchHire" &&
                                           ft.RelatedEntityId == searchHireId &&
                                           ft.TransactionType == "Payout" &&
                                           !string.IsNullOrEmpty(ft.StripeTransferId));
            if (payout == null || string.IsNullOrEmpty(payout.StripeTransferId))
            {
                await _loggingService.LogInfoAsync(
                    message: "Chargeback reversal: no expert transfer to reverse",
                    details: $"SearchHire {searchHireId}: no Payout transfer found — nothing to reverse (the client was made whole by the chargeback). Reason: {reason}.",
                    userId: null,
                    source: "StripeRefundService.ReverseExpertTransferForChargebackAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId);
                return;
            }

            // 🔧 FIX A-ii (regresión de A-i): NO usar un guard binario "¿existe alguna fila TransferReversal?".
            // Un clawback PARCIAL previo (dispute_resolved_client 90/8/2) ya deja una fila TransferReversal, y el
            // guard antiguo daba el chargeback por hecho => el experto conservaba el remanente de un cargo devuelto
            // al 100% y la plataforma perdía. Leemos el estado VIVO del transfer (GetAsync NO se cachea por
            // idempotency) y revertimos solo el REMANENTE no-revertido. Stripe expone Amount/AmountReversed en
            // CÉNTIMOS (long). Un chargeback aislado (sin clawback previo) tiene AmountReversed=0 => revierte el 100%.
            var liveTransfer = await new TransferService().GetAsync(payout.StripeTransferId);
            var remainderCents = liveTransfer.Amount - liveTransfer.AmountReversed; // céntimos aún reversibles
            if (remainderCents <= 0)
            {
                await _loggingService.LogInfoAsync(
                    message: "Chargeback reversal: expert transfer already fully reversed (idempotent no-op)",
                    details: $"SearchHire {searchHireId}: transfer {payout.StripeTransferId} sin remanente reversible (amount={liveTransfer.Amount}c, reversed={liveTransfer.AmountReversed}c). Nada que revertir.",
                    userId: payout.UserId,
                    source: "StripeRefundService.ReverseExpertTransferForChargebackAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId);
                return;
            }

            var reverseAmount = remainderCents / 100m; // EUR, solo para ledger/logs
            try
            {
                var reversalSvc = new TransferReversalService();
                var reversalOptions = new TransferReversalCreateOptions
                {
                    Amount = remainderCents, // revierte el REMANENTE vivo, no Abs(payout.Amount)
                    Metadata = new Dictionary<string, string>
                    {
                        { "searchHireId", searchHireId.ToString() },
                        { "reason", "chargeback reversal (remainder)" }
                    }
                };
                // 🔧 FIX A-ii: clave DISTINTA de la del clawback ("-reversal-"). Clawback parcial y chargeback
                // revierten importes DISTINTOS del mismo transfer; con clave compartida la 2ª chocaba con
                // idempotency_error y entraba en bucle de reintentos de Hangfire. Con "-cbreversal-" cada camino
                // deduplica solo consigo mismo; la doble reversión CONCURRENTE sigue cubierta porque ambos leen el
                // remanente vivo y Stripe rechaza revertir por encima de AmountReversed.
                var requestOptions = new RequestOptions { IdempotencyKey = $"md-{searchHireId}-cbreversal-{payout.StripeTransferId}" };
                var reversal = await reversalSvc.CreateAsync(payout.StripeTransferId, reversalOptions, requestOptions);

                // 🛡️ C4 FIX (chargeback path): persistencia INMEDIATA del FT tras el reversal Stripe.
                // El SaveChanges ya estaba inline aquí (línea siguiente), así que la ventana de riesgo
                // era pequeña — pero formalizamos el catch para que un fallo no quede silencioso (Stripe
                // ya ejecutó el reversal, la BD necesita reconciliación manual).
                _context.FinancialTransactions.Add(new FinancialTransaction
                {
                    UserId = payout.UserId,
                    Amount = -reverseAmount,
                    AmountCents = -remainderCents,
                    Currency = payout.Currency, // 🌍 Round 25: heredar del Payout original (mismo SearchHire)
                    // 🛡️ B2 FIX: TransactionType distinto del clawback ("TransferReversal") para que
                    // HandleChargeDisputeClosed (caso "won") pueda discriminar al calcular el monto
                    // a reintegrar al experto: solo lo revertido POR EL CHARGEBACK debe reintegrarse,
                    // NUNCA el clawback de la disputa interna previa (sigue siendo legítimo).
                    TransactionType = "ChargebackReversal",
                    RelatedEntityType = "SearchHire",
                    RelatedEntityId = searchHireId,
                    StripeTransferId = payout.StripeTransferId,
                    StripePaymentIntentId = payout.StripePaymentIntentId,
                    CreatedAt = DateTime.UtcNow
                });
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (Exception persistEx)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Chargeback reversal applied in Stripe but FT ChargebackReversal failed to persist",
                        details: $"SearchHire {searchHireId}: Stripe reversal {reversal.Id} de {reverseAmount:F2}€ en transfer {payout.StripeTransferId} se ejecutó OK, pero el ledger BD no se actualizó. RECONCILIACIÓN MANUAL: insertar fila FinancialTransaction ChargebackReversal con esos datos. Error: {persistEx.Message}",
                        userId: payout.UserId,
                        source: "StripeRefundService.ReverseExpertTransferForChargebackAsync.C4Persist",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { ReversalId = reversal.Id, TransferId = payout.StripeTransferId, ReverseAmount = reverseAmount, Error = persistEx.Message });
                    throw;
                }

                await _loggingService.LogInfoAsync(
                    message: "Expert transfer fully reversed on chargeback",
                    details: $"SearchHire {searchHireId}: fully reversed expert transfer {payout.StripeTransferId} ({reverseAmount:F2}€) because the charge was charged back. ReversalId: {reversal.Id}. Reason: {reason}.",
                    userId: payout.UserId,
                    source: "StripeRefundService.ReverseExpertTransferForChargebackAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId);
            }
            // 🛡️ N11 FIX: idempotency hit en chargeback reversal — el transfer YA fue revertido (reintento Hangfire post-crash).
            catch (StripeException idemCbEx) when (
                idemCbEx.StripeError?.Code == "transfer_already_reversed"
                || idemCbEx.StripeError?.Code == "idempotency_error")
            {
                await _loggingService.LogWarningAsync(
                    message: "N11: chargeback reversal idempotency hit (already done)",
                    details: $"SearchHire {searchHireId}: Stripe respondió '{idemCbEx.StripeError?.Code}' al revertir transfer {payout.StripeTransferId} por chargeback — ya estaba revertido. No-op.",
                    userId: payout.UserId,
                    source: "StripeRefundService.ReverseExpertTransferForChargebackAsync.N11",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId);
                return;
            }
            // 🛡️ Round 28 MUD-BJ: experto ya retiró balance Stripe Connect a banco externo
            // ANTES del chargeback. Stripe devuelve insufficient_funds_for_transfer_reversal
            // o balance_insufficient. SIN este catch, caía al genérico → Hangfire reintenta
            // infinitamente → spam Critical sin auto-resolver → pérdida silente platform.
            // Mismo patrón que MUD-AV en ProcessMoneyDistributionAsync (clawback): inserta
            // FT marker Amount=0 para que reentries lo detecten + log Critical UNA vez +
            // encola en ClawbackQueues para dashboard admin + NO throw (no Hangfire retry).
            catch (StripeException balCbEx) when (
                balCbEx.StripeError?.Code == "insufficient_funds_for_transfer_reversal"
                || balCbEx.StripeError?.Code == "balance_insufficient")
            {
                _context.FinancialTransactions.Add(new FinancialTransaction
                {
                    UserId = payout.UserId,
                    Amount = 0m,
                    AmountCents = 0,
                    Currency = payout.Currency ?? "EUR",
                    TransactionType = "ChargebackReversal",
                    RelatedEntityType = "SearchHire",
                    RelatedEntityId = searchHireId,
                    StripeTransferId = payout.StripeTransferId,
                    StripePaymentIntentId = payout.StripePaymentIntentId,
                    CreatedAt = DateTime.UtcNow
                });
                try { await _context.SaveChangesAsync(); }
                catch (Exception persistEx2)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL MUD-BJ: failed to persist chargeback-impossible marker",
                        details: $"SearchHire {searchHireId} Stripe code={balCbEx.StripeError?.Code} persist falló: {persistEx2.Message}. Hangfire podría reintentar — admin debe insertar manualmente FT ChargebackReversal marker.",
                        userId: payout.UserId,
                        source: "StripeRefundService.ReverseExpertTransferForChargebackAsync.MUD-BJ.PersistFailed",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId);
                }
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL MUD-BJ: chargeback reversal impossible — expert already withdrew balance",
                    details: $"SearchHire {searchHireId}: Stripe '{balCbEx.StripeError?.Code}' al revertir transfer {payout.StripeTransferId} ({reverseAmount:F2} {payout.Currency}). Experto retiró el balance Stripe Connect a banco externo ANTES del chargeback. Sin reversal posible. ACCIÓN ADMIN: (a) reclamar off-Stripe (transferencia directa expert→platform) o (b) absorber pérdida. NO se reintenta (FT marker insertada). Cliente sigue refundado por el chargeback Stripe.",
                    userId: payout.UserId,
                    source: "StripeRefundService.ReverseExpertTransferForChargebackAsync.MUD-BJ.WithdrawnBalance",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { TransferId = payout.StripeTransferId, ReverseAmount = reverseAmount, Currency = payout.Currency, StripeError = balCbEx.StripeError?.Code },
                    notifyUser: true);
                if (_clawbackQueue != null && payout.UserId.HasValue)
                {
                    await _clawbackQueue.EnqueueAsync(
                        userId: payout.UserId.Value,
                        stripeAccountId: null, // payout no tiene acctId directo aquí
                        amountMajor: reverseAmount,
                        currency: payout.Currency ?? "EUR",
                        reason: "WithdrawnBalance",
                        notes: $"Chargeback reversal impossible: experto retiró balance pre-chargeback ({balCbEx.StripeError?.Code}). Transfer {payout.StripeTransferId}. Reason: {reason}",
                        searchHireId: searchHireId);
                }
                return;
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Failed to reverse expert transfer on chargeback",
                    details: $"SearchHire {searchHireId}: could NOT reverse expert transfer {payout.StripeTransferId} ({reverseAmount:F2}€) after a chargeback. The expert may keep funds for a charged-back order — Hangfire will retry; MANUAL intervention if it keeps failing. Error: {ex.Message}",
                    userId: payout.UserId,
                    source: "StripeRefundService.ReverseExpertTransferForChargebackAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId);
                throw; // Hangfire reintenta
            }
        }

        /// <summary>
        /// 🌍 Estimación currency-aware de la fee que cobra Stripe por procesar el cargo.
        /// El cargo es 2.9% del monto + una cantidad fija que depende de la divisa de origen.
        /// Las tarifas reales varían por región de la tarjeta (europea vs internacional);
        /// usamos el fixed amount conservador en la divisa fuente como referencia para los
        /// guard-rails de platformAmount. No es exacto al céntimo — sólo orientativo.
        /// </summary>
        private static decimal GetStripeFeeEstimate(decimal baseAmount, string? currency)
        {
            // Conservative estimate — Stripe charges 2.9% + fixed amount in source currency.
            // Real fees depend on card region (European vs international).
            var normalized = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.ToUpperInvariant();
            decimal fixedFee = normalized switch
            {
                "EUR" => 0.25m,
                "GBP" => 0.20m,
                "USD" => 0.30m,
                "MXN" => 5.00m,
                "BRL" => 0.50m,
                _ => 0.30m, // safe default
            };
            return baseAmount * 0.029m + fixedFee;
        }

        private static AppointmentStatus? MapAppointmentStatus(string statusValue)
        {
            if (string.IsNullOrWhiteSpace(statusValue))
            {
                return null;
            }
            try
            {
                return AppointmentStatusExtensions.FromStringValue(statusValue);
            }
            catch
            {
                return null;
            }
        }
    }
}
