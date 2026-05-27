using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Stripe;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;
using newApi.DataLayer.Models;
using newApi.Common;
using System;

namespace newApi.Services
{
    public class StripeRefundService
    {
        private readonly AppDbContext _context;
        private readonly SystemStatusService _systemStatusService;
        private readonly ILoggingService _loggingService;

        public StripeRefundService(AppDbContext context, SystemStatusService systemStatusService, ILoggingService loggingService)
        {
            _context = context;
            _systemStatusService = systemStatusService;
            _loggingService = loggingService;
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

                // Obtener configuraci├│n de distribuci├│n para el estado concreto (subestado/granularidad lo resuelve el servicio)
                var config = await _systemStatusService.GetMoneyDistributionConfigAsync(
                    statusValue,
                    searchHire.SearchService?.CategoryId,
                    searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId
                );

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
                                $"Status: {statusValue}, Reason: {reason}, ClientId: {searchHire.ClientId}, ExpertId: {searchHire.ExpertId}, Amount: {searchHire.Amount}Ôé¼. " +
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
                                    $"Available Balance: {availableEur}Ôé¼, Required Outflow: {totalOutflow}Ôé¼ (Client Refund: {clientRefundAmountForStripe:F2}Ôé¼ with tax, Expert Transfer: {expertAmountBase:F2}Ôé¼ base). " +
                                    $"Distribution Plan: Client={config.ClientPercentage}%, Expert={config.ExpertPercentage}%, Platform={config.PlatformPercentage}%. " +
                                    $"Base amounts: Client={clientRefundAmount:F2}Ôé¼, Expert={expertAmount:F2}Ôé¼, Platform={platformAmount:F2}Ôé¼. " +
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
                                $"Required outflow: {clientRefundAmountForStripe + expertAmountBase}Ôé¼ (Client Refund: {clientRefundAmountForStripe:F2}Ôé¼ with tax, Expert Transfer: {expertAmountBase:F2}Ôé¼ base). " +
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
                                        $"2) Transfer {expertAmount:F2}Ôé¼ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) " +
                                        $"3) Platform retains {platformAmount:F2}Ôé¼.",
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
                                        searchHireForState.StatusId = searchHireStatusRow.Id;
                                        searchHireForState.UpdatedAt = DateTime.UtcNow;
                                        stateNeedsUpdate = true;
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
                                            searchHireForState.StatusId = searchHireStatusRow.Id;
                                            searchHireForState.UpdatedAt = DateTime.UtcNow;
                                            stateNeedsUpdate = true;
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
                        // Ô£à CR├ìTICO: Verificar si el dinero ya fue procesado (prevenir duplicados)
                        var existingRefund = await _context.FinancialTransactions
                            .FirstOrDefaultAsync(ft => ft.RelatedEntityType == "SearchHire" &&
                                                       ft.RelatedEntityId == searchHireId &&
                                                       ft.TransactionType == "Refund" &&
                                                       ft.StripePaymentIntentId == servicePayment.StripePaymentIntentId);
                        
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
                                            $"1) Refund {clientRefundAmount:F2}Ôé¼ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) " +
                                            $"2) Transfer {expertAmount:F2}Ôé¼ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) - REQUIRES MANUAL SETUP " +
                                            $"3) Platform retains {platformAmount:F2}Ôé¼. " +
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
                            var expertAccount = await accountService.GetAsync(expertStripeAccountId);
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

                        var transferOptions = new TransferCreateOptions
                        {
                            Amount = checked((long)Math.Round(expertAmountForStripe * 100)), // ✅ Usar monto base (sin tax) - transfers no incluyen tax. Round (no truncar) para no perder céntimos ni descuadrar el ledger.
                                Currency = "eur",
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
                                    { "expertId", searchHire.ExpertId?.ToString() ?? "N/A" }
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
                                                    $"EXPERT ALREADY RECEIVED {expertAmount:F2}Ôé¼ - MANUAL INTERVENTION REQUIRED. " +
                                                    $"PENDING TRANSACTIONS TO COMPLETE MANUALLY: " +
                                                    $"1) Refund {clientRefundAmount:F2}Ôé¼ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) " +
                                                    $"2) Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) already received {expertAmount:F2}Ôé¼ - NO ACTION NEEDED " +
                                                    $"3) Platform retains {platformAmount:F2}Ôé¼. " +
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
                                            $"1) Refund {clientRefundAmount:F2}Ôé¼ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) - FAILED " +
                                            $"2) Transfer {expertAmount:F2}Ôé¼ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) - NOT PROCESSED " +
                                            $"3) Platform retains {platformAmount:F2}Ôé¼. " +
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

                                    await _loggingService.LogInfoAsync(
                                        message: "Expert transfer reversed on client refund (clawback)",
                                        details: $"SearchHire {searchHireId}: reversed {clawbackAmountEur:F2}€ of expert transfer {existingTransfer.StripeTransferId} (originally {existingTransfer.Amount:F2}€) because the client was refunded (status {statusValue}). Expert keeps {expertAmountForStripe:F2}€ ({config.ExpertPercentage}%). ReversalId: {clawbackReversal.Id}.",
                                        userId: searchHire.ExpertId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
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
                        }

                        await _context.SaveChangesAsync();
                        
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
                                details: $"Se proces├│ tu reembolso de {clientRefundAmountForStripe:F2}Ôé¼ por el servicio #{searchHireId}. El dinero llegar├í a tu cuenta en 5-10 d├¡as h├íbiles.",
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
                                details: $"Has recibido {expertAmountForStripe:F2}Ôé¼ por el servicio #{searchHireId}. El dinero est├í disponible en tu cuenta de Stripe.",
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
                                        $"Plan de distribuci├│n: Cliente={clientRefundAmount:F2}Ôé¼ ({config.ClientPercentage}%), Experto={expertAmount:F2}Ôé¼ ({config.ExpertPercentage}%), Plataforma={platformAmount:F2}Ôé¼ ({config.PlatformPercentage}%). " +
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
                                    $"Distribution Plan: Client={clientRefundAmount}Ôé¼ ({config.ClientPercentage}%), Expert={expertAmount}Ôé¼ ({config.ExpertPercentage}%), Platform={platformAmount}Ôé¼ ({config.PlatformPercentage}%). " +
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
                                    $"Distribution Plan: Client={clientRefundAmount}Ôé¼ ({config.ClientPercentage}%), Expert={expertAmount}Ôé¼ ({config.ExpertPercentage}%), Platform={platformAmount}Ôé¼ ({config.PlatformPercentage}%). " +
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
        [Hangfire.AutomaticRetry(Attempts = 5, DelaysInSeconds = new[] { 120, 600, 1800, 3600, 7200 })]
        public async Task RetryMoneyDistributionJobAsync(int searchHireId, string statusValue, string reason, int? initiatedByUserId)
        {
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

                _context.FinancialTransactions.Add(new FinancialTransaction
                {
                    UserId = payout.UserId,
                    Amount = -reverseAmount,
                    AmountCents = -remainderCents,
                    TransactionType = "TransferReversal",
                    RelatedEntityType = "SearchHire",
                    RelatedEntityId = searchHireId,
                    StripeTransferId = payout.StripeTransferId,
                    StripePaymentIntentId = payout.StripePaymentIntentId,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();

                await _loggingService.LogInfoAsync(
                    message: "Expert transfer fully reversed on chargeback",
                    details: $"SearchHire {searchHireId}: fully reversed expert transfer {payout.StripeTransferId} ({reverseAmount:F2}€) because the charge was charged back. ReversalId: {reversal.Id}. Reason: {reason}.",
                    userId: payout.UserId,
                    source: "StripeRefundService.ReverseExpertTransferForChargebackAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId);
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
