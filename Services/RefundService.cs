using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
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

        public StripeRefundService(AppDbContext context, SystemStatusService systemStatusService, ILoggingService loggingService, ISearchHireStatusAuditService? statusAudit = null)
        {
            _context = context;
            _systemStatusService = systemStatusService;
            _loggingService = loggingService;
            _statusAudit = statusAudit; // opcional para no romper tests existentes
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
                // Bloqueo a nivel de fila para consistencia
                var searchHire = await _context.SearchHires
                    .FromSqlInterpolated($"SELECT *, xmin FROM \"SearchHires\" WHERE \"Id\" = {searchHireId} FOR UPDATE")
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                        .ThenInclude(e => e.ExpertProfile)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                    .FirstOrDefaultAsync();

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
                MoneyDistributionConfigDto? config;
                if (searchHire.ClientPercentageSnapshot.HasValue
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
                
                if (searchHire.BaseAmount == null)
                {
                    // ⚠️ LOG WARNING: BaseAmount es null (datos antiguos o sin tax calculado)
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
                        }
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
                var stripeFeeEstimate = baseAmount * 0.029m + 0.30m; // 2.9% + 0.30€ estándar para EUR
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
                // 🌍 Round 9 — A5: Comprobamos solo el balance EUR porque todos los charges al cliente
                // se hacen en EUR (la pasarela europea de la plataforma). Si el experto es GB/CH/US/CA,
                // Stripe convierte automáticamente desde EUR a la divisa destino al crear el transfer.
                // El balance EUR es por tanto la única fuente de liquidez relevante para outflows.
                try
                {
                    var balanceService = new BalanceService();
                    var balance = await balanceService.GetAsync();
                    var availableEur = balance.Available?.FirstOrDefault(b => b.Currency == "eur")?.Amount / 100.0m ?? 0;
                    // ✅ CORRECCIÓN CRÍTICA: Verificación de balance debe usar montos reales que se enviarán a Stripe
                    // Refund usa monto con tax proporcional, Transfer usa monto base (sin tax)
                    var totalOutflow = clientRefundAmountForStripe + expertAmountBase;
                    if (availableEur < totalOutflow)
                    {
                        // ­ƒÜ¿ LOG CR├ìTICO: Balance insuficiente (una sola vez, con informaci├│n completa)
                        // IMPORTANTE: Este log se crea ANTES de entrar en la transacci├│n, as├¡ que debe estar disponible inmediatamente
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Insufficient Stripe platform balance for money distribution",
                            details: $"SearchHire {searchHireId} finalization failed due to insufficient Stripe platform balance. " +
                                    $"Available Balance: {availableEur}€, Required Outflow: {totalOutflow}€ (Client Refund: {clientRefundAmountForStripe:F2}€ with tax, Expert Transfer: {expertAmountBase:F2}€ base). " +
                                    $"Distribution Plan: Client={config.ClientPercentage}%, Expert={config.ExpertPercentage}%, Platform={config.PlatformPercentage}%. " +
                                    $"Base amounts: Client={clientRefundAmount:F2}€, Expert={expertAmount:F2}€, Platform={platformAmount:F2}€. " +
                                    $"Status: {statusValue}, Reason: {reason}, PaymentIntentId: {servicePayment.StripePaymentIntentId}. " +
                                    $"ACTION REQUIRED: Wait for balance to be available (from PaymentIntent capture) or manually verify Stripe balance and retry distribution.",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                Reason = reason,
                                AvailableBalance = availableEur,
                                TotalOutflow = totalOutflow,
                                ClientRefundAmountBase = clientRefundAmount,
                                ClientRefundAmountForStripe = clientRefundAmountForStripe,
                                ExpertTransferAmountBase = expertAmount,
                                ExpertTransferAmountForStripe = expertAmountForStripe,
                                PlatformAmount = platformAmount,
                                PaymentIntentId = servicePayment.StripePaymentIntentId
                            }
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
                            // A├║n as├¡, intentar procesar dinero (puede que el estado ya est├® correcto)
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
                        var needsTransfer = expertAmount > 0 && searchHire.ExpertId.HasValue && !transferAlreadyProcessed;

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
                                // 🛡️ R14 FIX: la cuenta Stripe del experto fue eliminada (probablemente por el N4 fix
                                // del AccountDeletionService que ahora SÍ borra la cuenta Stripe al eliminar User).
                                // El transfer al experto no se puede hacer — abortar con critical para reconciliación.
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL R14: Expert Stripe account not found (404)",
                                    details: $"SearchHire {searchHireId}: la cuenta Stripe {expertStripeAccountId} retornó 404 (eliminada o nunca existió). Refund al cliente sí puede proceder pero el transfer al experto está bloqueado. ACCIÓN ADMIN: reconciliar manualmente (devolver al cliente lo que era del experto o re-asignar payout). Error: {stripeAccEx.Message}",
                                    userId: searchHire.ExpertId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync.R14",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { StripeAccountId = expertStripeAccountId, ExpertId = searchHire.ExpertId });
                                if (transaction != null) await transaction.RollbackAsync();
                                return false;
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
                                if (transaction != null)
                                {
                                await transaction.RollbackAsync();
                                }
                                return false;
                            }

                        // 🌍 Round 9 — A5 FIX: divisa derivada del Connect account del experto.
                        // Hardcodear "eur" rompía transfers a GB/CH/US/CA (currency_mismatch).
                        // Preferimos expertAccount.DefaultCurrency (verdad real de Stripe); fallback a mapping por país.
                        var transferCurrency = newApi.Common.StripeCurrencyMapping.ResolveTransferCurrency(
                            expertAccount?.DefaultCurrency,
                            searchHire.ExpertCountry);

                        var transferOptions = new TransferCreateOptions
                        {
                            Amount = checked((long)Math.Round(expertAmountForStripe * 100)), // ✅ Usar monto base (sin tax) - transfers no incluyen tax. Round (no truncar) para no perder céntimos ni descuadrar el ledger.
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
                                if (n10Pi.Status == "requires_capture"
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
                                        servicePayment.StripeRefundId = "n10-canceled-pre-capture";
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
                                Amount = checked((long)Math.Round(clientRefundAmountForStripe * 100)), // ✅ Usar monto con tax proporcional para Stripe. Round (no truncar) para no devolver de menos ni descuadrar el ledger.
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
                                IdempotencyKey = refundIdempotencyKey // 🔧 FIX E: discriminada por estado+importe
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
                                    details: $"SearchHire {searchHireId}: Stripe respondió '{idemRefundEx.StripeError?.Code}' al crear refund — ya estaba aplicado. Marcando como completado sin crear FT Refund nueva (el refund original está en Stripe).",
                                    userId: searchHire.ClientId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync.N11Refund",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId);
                                // No tenemos el refund.Id real, marcamos placeholder para que el flujo continúe.
                                createdRefundId = "n11-already-refunded";
                                servicePayment.IsRefunded = true;
                            }
                            catch (StripeException refundEx)
                            {
                                // Si el refund falla y ya hicimos transfer, revertir la transferencia para mantener "todo o nada"
                                if (!string.IsNullOrEmpty(createdTransferId))
                                {
                                    try
                                    {
                                        var reversalSvc = new TransferReversalService();
                                        // MODIFICACI├ôN: Agregar idempotency a reversal tambi├®n
                                        var reversalOptions = new TransferReversalCreateOptions { Amount = checked((long)Math.Round(expertAmountForStripe * 100)) }; // ✅ Revertir el monto real enviado a Stripe (base, sin tax). Round para casar con el transfer.
                                        var reversalRequestOptions = new RequestOptions { IdempotencyKey = transferIdempotencyKey + "-reversal" };
                                        await reversalSvc.CreateAsync(createdTransferId, reversalOptions, reversalRequestOptions);
                                    }
                                    catch (Exception revEx)
                                    {
                                        // ­ƒÜ¿ LOG CR├ìTICO: Error al revertir transferencia
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: Failed to reverse transfer after refund failure",
                                            details: $"SearchHire {searchHireId} finalization failed: refund failed and transfer reversal also failed. " +
                                                    $"EXPERT ALREADY RECEIVED {expertAmount:F2}€ - MANUAL INTERVENTION REQUIRED. " +
                                                    $"PENDING TRANSACTIONS TO COMPLETE MANUALLY: " +
                                                    $"1) Refund {clientRefundAmount:F2}€ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) " +
                                                    $"2) Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) already received {expertAmount:F2}€ - NO ACTION NEEDED " +
                                                    $"3) Platform retains {platformAmount:F2}€. " +
                                                    $"TransferId: {createdTransferId}, RefundError: {refundEx.Message}, ReversalError: {revEx.Message}",
                                            userId: initiatedByUserId ?? searchHire.ClientId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId,
                                            additionalData: new { 
                                                Status = statusValue,
                                                TransferId = createdTransferId,
                                                ClientRefundAmount = clientRefundAmount,
                                                ExpertTransferAmount = expertAmount,
                                                PlatformAmount = platformAmount,
                                                ClientId = searchHire.ClientId,
                                                ExpertId = searchHire.ExpertId,
                                                RefundError = refundEx.Message,
                                                ReversalError = revEx.Message
                                            }
                                        );
                                    }
                                }

                                // Ô£à CORRECCI├ôN: Solo hacer rollback si creamos la transacci├│n
                                if (transaction != null)
                                {
                                    await transaction.RollbackAsync();
                                }
                                // ­ƒÜ¿ LOG CR├ìTICO: Reembolso fall├│
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Refund failed - money distribution rolled back",
                                    details: $"SearchHire {searchHireId} finalization failed: refund to client failed. " +
                                            $"PENDING TRANSACTIONS TO COMPLETE MANUALLY: " +
                                            $"1) Refund {clientRefundAmount:F2}€ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) - FAILED " +
                                            $"2) Transfer {expertAmount:F2}€ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) - NOT PROCESSED " +
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
                                : checked((long)Math.Round(Math.Abs(existingTransfer.Amount) * 100)));
                        long expertOwedCents = checked((long)Math.Round(expertAmountForStripe * 100));
                        long clawbackCents = Math.Max(0L, transferredCents - expertOwedCents);
                        var clawbackAmountEur = clawbackCents / 100m;
                        // 🔁 A2: dispara también si el refund YA estaba hecho (reintento de un clawback que
                        // falló antes), no solo cuando se acaba de crear el refund en esta ejecución.
                        if (((needsRefund && !string.IsNullOrEmpty(createdRefundId)) || refundAlreadyProcessed)
                            && transferAlreadyProcessed
                            && existingTransfer != null
                            && !string.IsNullOrEmpty(existingTransfer.StripeTransferId)
                            && clawbackAmountEur >= 0.01m
                            && !hasChargeback) // 🔁 R3: si hubo chargeback, la reversión TOTAL la hace ReverseExpertTransferForChargebackAsync → el clawback interno NO debe duplicarla (paths mutuamente excluyentes; evita doble-reversión en carrera)
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
                                catch (Exception clawbackEx)
                                {
                                    // No revertimos el refund al cliente (debe quedar reembolsado); alertamos para intervención manual.
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL: Failed to reverse prior expert transfer on client refund (clawback)",
                                        details: $"SearchHire {searchHireId}: the client was refunded but {clawbackAmountEur:F2}€ of the prior expert transfer {existingTransfer.StripeTransferId} (originally {existingTransfer.Amount:F2}€) could NOT be reversed. " +
                                                 $"The expert may keep overpaid funds for a refunded order — MANUAL INTERVENTION REQUIRED. Error: {clawbackEx.Message}",
                                        userId: searchHire.ExpertId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new { TransferId = existingTransfer.StripeTransferId, OriginalAmount = existingTransfer.Amount, ClawbackAmount = clawbackAmountEur, Error = clawbackEx.Message });
                                }
                            }
                        }

                        if (needsRefund && !string.IsNullOrEmpty(createdRefundId))
                        {
                            var refundTx = new FinancialTransaction
                            {
                                UserId = searchHire.ClientId,
                                Amount = Math.Round(clientRefundAmountForStripe, 2), // 🔧 redondeado a céntimo para casar con Stripe
                                AmountCents = checked((long)Math.Round(clientRefundAmountForStripe * 100)), // céntimos exactos refundados
                                TransactionType = "Refund",
                                RelatedEntityType = "SearchHire",
                                RelatedEntityId = searchHireId,
                                StripePaymentIntentId = servicePayment.StripePaymentIntentId,
                                StripeRefundId = createdRefundId,
                                CreatedAt = DateTime.UtcNow
                            };
                            _context.FinancialTransactions.Add(refundTx);

                            servicePayment.IsRefunded = true;
                            servicePayment.StripeRefundId = createdRefundId;

                            // 🛡️ N3 FIX (refund principal): SaveChanges INMEDIATO tras el Add. Antes había
                            // ~252 líneas entre refundSvc.CreateAsync (línea ~1254) y el SaveChanges de la
                            // línea siguiente (~1506) — ventana donde Stripe ya hizo el refund pero la BD
                            // podía morir sin persistir. Mismo patrón que C4 (clawback).
                            try
                            {
                                await _context.SaveChangesAsync();
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
                                AmountCents = checked((long)Math.Round(expertAmountForStripe * 100)), // céntimos exactos transferidos
                                TransactionType = "Payout",
                                RelatedEntityType = "SearchHire",
                                RelatedEntityId = searchHireId,
                                StripeTransferId = createdTransferId,
                                StripePaymentIntentId = servicePayment.StripePaymentIntentId, // 🔧 trazabilidad: vincular Payout al cargo (se propaga a la reversión por chargeback)
                                CreatedAt = DateTime.UtcNow
                            };
                            _context.FinancialTransactions.Add(expertTx);

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

                        await _context.SaveChangesAsync(); // no-op si N3 ya persistió; útil si quedan tracked changes
                        
                        // Ô£à CORRECCI├ôN: Solo hacer commit si creamos la transacci├│n
                        if (transaction != null)
                        {
                        await transaction.CommitAsync();
                        }

                        // Ô£à Notificar a usuarios sobre movimientos de dinero exitosos
                        if (needsRefund && !string.IsNullOrEmpty(createdRefundId))
                        {
                            // Refund exitoso - notificar al cliente
                            await _loggingService.LogInfoAsync(
                                message: "Reembolso procesado",
                                details: $"Se proces├│ tu reembolso de {clientRefundAmountForStripe:F2}€ por el servicio #{searchHireId}. El dinero llegar├í a tu cuenta en 5-10 d├¡as h├íbiles.",
                                userId: searchHire.ClientId,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                notifyUser: true
                            );
                        }

                        if (needsTransfer && !string.IsNullOrEmpty(createdTransferId) && searchHire.ExpertId.HasValue)
                        {
                            // Transfer exitoso - notificar al experto
                            await _loggingService.LogInfoAsync(
                                message: "Pago recibido",
                                details: $"Has recibido {expertAmountForStripe:F2}€ por el servicio #{searchHireId}. El dinero est├í disponible en tu cuenta de Stripe.",
                                userId: searchHire.ExpertId.Value,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                notifyUser: true
                            );
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
        // 🛡️ R15: DisableConcurrentExecution evita que dos jobs concurrentes (encolados desde
        // distintos sitios, ej DisputeController + AppointmentService + SearchHireController) corran
        // simultáneamente para el mismo hire. Sin esto ambos ven el guard idempotente pasar a la vez
        // y pueden duplicar transfer/refund. Timeout 600s cubre cualquier ProcessMoneyDistribution
        // legítimo (Stripe API + SaveChanges).
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
        [Hangfire.AutomaticRetry(Attempts = 5, DelaysInSeconds = new[] { 120, 600, 1800, 3600, 7200 })]
        public async Task RetryMoneyDistributionJobAsync(int searchHireId, string statusValue, string reason, int? initiatedByUserId)
        {
            // 🛡️ R16 FIX: re-verify state DEL HIRE en lugar de asumir statusValue del enqueue time.
            // Entre el enqueue (delay 2 min) y la ejecución el hire pudo cambiar de estado por otro
            // flow (admin re-resolvió, chargeback llegó, etc.). Si el estado actual no coincide con
            // el statusValue del enqueue, abortar — el flow alternativo ya manejó el dinero.
            var currentHire = await _context.SearchHires
                .AsNoTracking()
                .Include(sh => sh.Status)
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
            var actualStatus = currentHire.Status?.StatusValue;
            if (!string.Equals(actualStatus, statusValue, StringComparison.OrdinalIgnoreCase))
            {
                await _loggingService.LogWarningAsync(
                    message: "R16: RetryMoneyDistribution skipped — estado cambió entre enqueue y ejecución",
                    details: $"SearchHire {searchHireId}: statusValue del enqueue='{statusValue}', estado actual='{actualStatus}'. Otro flow ya completó la transición; este reintento es no-op.",
                    userId: initiatedByUserId,
                    source: "StripeRefundService.RetryMoneyDistributionJobAsync.R16",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId);
                return; // no throw → no reintento (es benign — otro flow tomó el caso)
            }

            // El estado ya fue finalizado por el llamador → updateState:false (solo mover dinero).
            var ok = await ProcessMoneyDistributionAsync(searchHireId, statusValue, reason, initiatedByUserId, updateState: false);
            if (!ok)
            {
                throw new InvalidOperationException(
                    $"Money distribution still pending for SearchHire {searchHireId} (status {statusValue}). Hangfire will retry.");
            }
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
            var moneyAlreadyMoved = shStatusValue == SearchHireStatus.DisputeResolvedClient.ToStringValue()
                                 || shStatusValue == SearchHireStatus.DisputeResolvedExpert.ToStringValue();

            if (moneyAlreadyMoved && dispute.Status == "Resolving")
            {
                // Catch-up: cierra el dispute en lugar de devolverlo a Pending.
                var completed = await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"Disputes\" SET \"Status\" = 'Resolved' WHERE \"Id\" = {disputeId} AND \"Status\" = 'Resolving'");
                if (completed > 0)
                {
                    await _loggingService.LogWarningAsync(
                        message: "B4: Stuck dispute in 'Resolving' CATCH-UP a 'Resolved' (money already moved)",
                        details: $"Dispute {disputeId}: SearchHire en estado terminal '{shStatusValue}' → catch-up a 'Resolved' (evita segunda resolución contradictoria que movería dinero al lado opuesto).",
                        userId: null,
                        source: "StripeRefundService.RescueStuckResolvingDisputeAsync",
                        relatedEntityType: "Dispute",
                        relatedEntityId: disputeId);
                }
                return;
            }

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
