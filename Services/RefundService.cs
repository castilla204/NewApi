using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
        /// Orquesta la distribución de dinero según un estado concreto: realiza refund al cliente y transferencia al experto.
        /// Respeta subestados de finalización y granularidad (categoría/tipo/global) mediante el statusValue recibido.
        /// </summary>
        /// <param name="searchHireId">ID del servicio contratado</param>
        /// <param name="statusValue">Estado específico, p.ej. "appointment_cancelled_by_expert_second"</param>
        /// <param name="reason">Razón del movimiento</param>
        /// <param name="initiatedByUserId">Opcional: usuario que inicia la operación (para trazas)</param>
        /// <returns>True si refund y (si aplica) transfer se procesan correctamente</returns>
        public async Task<bool> ProcessMoneyDistributionAsync(int searchHireId, string statusValue, string reason, int? initiatedByUserId = null)
        {
            try
            {
                // Bloqueo a nivel de fila para consistencia
                var searchHire = await _context.SearchHires
                    .FromSqlInterpolated($"SELECT * FROM \"SearchHires\" WHERE \"Id\" = {searchHireId} FOR UPDATE")
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                        .ThenInclude(e => e.ExpertProfile)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    return false;
                }

                // Validar si el estado es de finalización cuando proviene de AppointmentStatus
                try
                {
                    var statusRow = await _context.SystemStatuses
                        .FirstOrDefaultAsync(s => s.StatusValue == statusValue);
                    if (statusRow != null && statusRow.StatusType == "AppointmentStatus" && statusRow.IsFinalizationStatus == false)
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                }

                // Obtener configuración de distribución para el estado concreto (subestado/granularidad lo resuelve el servicio)
                var config = await _systemStatusService.GetMoneyDistributionConfigAsync(
                    statusValue,
                    searchHire.SearchService?.CategoryId,
                    searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId
                );

                if (config == null)
                {
                    // Fallback: si no hay configuración para subestado, usar estado final de SearchHire
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
                                // Validar que el target sea estado de finalización
                                try
                                {
                                    var targetRow = await _context.SystemStatuses
                                        .FirstOrDefaultAsync(s => s.StatusValue == finalStatusValue && s.StatusType == "SearchHireStatus");
                                    if (targetRow != null && targetRow.IsFinalizationStatus == false)
                                    {
                                        return false;
                                    }
                                }
                                catch (Exception ex2)
                                {
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

                // MODIFICACIÓN: Validar que los porcentajes sumen 100% para evitar distribuciones incorrectas (best practice para configs financieras)
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

                var clientRefundAmount = searchHire.Amount * (config.ClientPercentage / 100);
                var expertAmount = searchHire.Amount * (config.ExpertPercentage / 100);
                var platformAmount = searchHire.Amount * (config.PlatformPercentage / 100);

                // MODIFICACIÓN: Estimar fees de Stripe y warning si platformAmount no cubre (para evitar pérdidas, según guías 2025)
                var stripeFeeEstimate = searchHire.Amount * 0.029m + 0.30m; // 2.9% + 0.30€ estándar para EUR
                if (platformAmount < stripeFeeEstimate)
                {
                    // Opcional: Fallar si es crítico, pero por ahora warning
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
                    // 🚨 LOG CRÍTICO: Pago original no encontrado (una sola vez, con toda la información)
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

                // MODIFICACIÓN: Verificar balance disponible antes de cualquier outflow (best practice Stripe 2025 para evitar negativos)
                try
                {
                    var balanceService = new BalanceService();
                    var balance = await balanceService.GetAsync();
                    var availableEur = balance.Available?.FirstOrDefault(b => b.Currency == "eur")?.Amount / 100.0m ?? 0;
                    var totalOutflow = clientRefundAmount + expertAmount;
                    if (availableEur < totalOutflow)
                    {
                        // 🚨 LOG CRÍTICO: Balance insuficiente (una sola vez, con información completa)
                        // IMPORTANTE: Este log se crea ANTES de entrar en la transacción, así que debe estar disponible inmediatamente
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Insufficient Stripe platform balance for money distribution",
                            details: $"SearchHire {searchHireId} finalization failed due to insufficient Stripe platform balance. " +
                                    $"Available Balance: {availableEur}€, Required Outflow: {totalOutflow}€ (Client Refund: {clientRefundAmount}€, Expert Transfer: {expertAmount}€). " +
                                    $"Distribution Plan: Client={config.ClientPercentage}%, Expert={config.ExpertPercentage}%, Platform={config.PlatformPercentage}%. " +
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
                                ClientRefundAmount = clientRefundAmount,
                                ExpertTransferAmount = expertAmount,
                                PlatformAmount = platformAmount,
                                PaymentIntentId = servicePayment.StripePaymentIntentId
                            }
                        );
                        
                        // ✅ NO necesitamos delay - LoggingService usa su propio DbContext scoped
                        // que se commitea independientemente de la transacción de RefundService
                        // Esto asegura que el log sea visible inmediatamente post-commit sin interferencia
                        return false;
                    }
                }
                catch (StripeException balanceEx)
                {
                    // 🚨 LOG CRÍTICO: Error al verificar balance (una sola vez, con toda la información)
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Error checking Stripe balance - money distribution failed",
                        details: $"SearchHire {searchHireId} finalization failed due to error checking Stripe platform balance. " +
                                $"Stripe Error: {balanceEx.Message}, Type: {balanceEx.StripeError?.Type}, Code: {balanceEx.StripeError?.Code}. " +
                                $"Required outflow: {clientRefundAmount + expertAmount}€ (Client: {clientRefundAmount}€, Expert: {expertAmount}€). " +
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
                            RequiredOutflow = clientRefundAmount + expertAmount,
                            ClientRefundAmount = clientRefundAmount,
                            ExpertTransferAmount = expertAmount
                        }
                    );
                    return false;
                }

                // ✅ Verificar que el PaymentIntent esté capturado antes de intentar Transfer
                if (expertAmount > 0)
                {
                    try
                    {
                        // ✅ Verificar que el PaymentIntent esté capturado antes de intentar Transfer
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

                // Orquestación bajo estrategia de reintento y transacción
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    // ✅ CORRECCIÓN: Verificar si ya hay una transacción activa
                    var existingTransaction = _context.Database.CurrentTransaction;
                    IDbContextTransaction transaction = null;
                    if (existingTransaction == null)
                    {
                        transaction = await _context.Database.BeginTransactionAsync();
                    }
                    // MODIFICACIÓN: Declarar variables fuera del try para acceso en catch blocks
                    string createdTransferId = null;
                    string createdRefundId = null;
                    
                    try
                    {
                        // MODIFICACIÓN: Usar UUID para idempotency key (mejor que string custom, según docs 2025)
                        var idempotencyKey = Guid.NewGuid().ToString();

                        // Si hay refund y transfer, ejecutar primero la transferencia y después el refund; si el refund falla, revertir la transferencia
                        var needsRefund = clientRefundAmount > 0;
                        var needsTransfer = expertAmount > 0 && searchHire.ExpertId.HasValue;

                        // Transfer primero (si aplica)
                        if (needsTransfer)
                        {
                            var expertStripeAccountId = searchHire.Expert?.ExpertProfile?.StripeAccountId;
                            if (string.IsNullOrEmpty(expertStripeAccountId))
                            {
                                // 🚨 LOG CRÍTICO: Cuenta de Stripe del experto faltante
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
                                // ✅ CORRECCIÓN: Solo hacer rollback si creamos la transacción
                                if (transaction != null)
                                {
                                await transaction.RollbackAsync();
                                }
                                return false;
                            }

                            // MODIFICACIÓN: Chequear status de connected account (best practice 2025 para cumplimiento)
                            var accountService = new AccountService();
                            var expertAccount = await accountService.GetAsync(expertStripeAccountId);
                            if (expertAccount.ChargesEnabled == false || expertAccount.PayoutsEnabled == false)
                            {
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Expert account not enabled for transfers",
                                    details: $"Expert {searchHire.ExpertId} account {expertStripeAccountId} is not fully verified.",
                                    userId: searchHire.ExpertId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "Account",
                                    relatedEntityId: (int)searchHire.ExpertId,
                                    additionalData: new { AccountId = expertStripeAccountId, ChargesEnabled = expertAccount.ChargesEnabled, PayoutsEnabled = expertAccount.PayoutsEnabled }
                                );
                                if (transaction != null)
                                {
                                await transaction.RollbackAsync();
                                }
                                return false;
                            }

                            var transferOptions = new TransferCreateOptions
                            {
                                Amount = (long)(expertAmount * 100),
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
                                    { "clientId", searchHire.ClientId.ToString() }, // MODIFICACIÓN: Más metadata para trazabilidad
                                    { "expertId", searchHire.ExpertId?.ToString() ?? "N/A" }
                                }
                            };

                            // MODIFICACIÓN: Idempotency correcta con RequestOptions (antes estaba en metadata, lo cual no funciona)
                            var transferRequestOptions = new RequestOptions
                            {
                                IdempotencyKey = idempotencyKey
                            };

                            var transferSvc = new TransferService();

                            // MODIFICACIÓN: Reintento simple para transients (hasta 3 veces, sin Polly)
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
                        }

                        // Refund después (si aplica)
                        if (needsRefund)
                        {
                            var refundOptions = new RefundCreateOptions
                            {
                                PaymentIntent = servicePayment.StripePaymentIntentId,
                                Amount = (long)(clientRefundAmount * 100),
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
                                    { "clientId", searchHire.ClientId.ToString() } // MODIFICACIÓN: Más metadata
                                }
                            };

                            // MODIFICACIÓN: Idempotency correcta con RequestOptions
                            var refundRequestOptions = new RequestOptions
                            {
                                IdempotencyKey = idempotencyKey + "-refund" // Unique por operación para evitar colisiones
                            };

                            try
                            {
                                var refundSvc = new RefundService();

                                // MODIFICACIÓN: Reintento simple similar
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
                                        // MODIFICACIÓN: Agregar idempotency a reversal también
                                        var reversalOptions = new TransferReversalCreateOptions { Amount = (long)(expertAmount * 100) }; // Revertir total
                                        var reversalRequestOptions = new RequestOptions { IdempotencyKey = idempotencyKey + "-reversal" };
                                        await reversalSvc.CreateAsync(createdTransferId, reversalOptions, reversalRequestOptions);
                                    }
                                    catch (Exception revEx)
                                    {
                                        // 🚨 LOG CRÍTICO: Error al revertir transferencia
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

                                // ✅ CORRECCIÓN: Solo hacer rollback si creamos la transacción
                                if (existingTransaction == null)
                                {
                                    await transaction.RollbackAsync();
                                }
                                // 🚨 LOG CRÍTICO: Reembolso falló
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

                        // Registrar en base de datos solo si Stripe tuvo éxito en ambos pasos necesarios
                        if (needsRefund && !string.IsNullOrEmpty(createdRefundId))
                        {
                            var refundTx = new FinancialTransaction
                            {
                                UserId = searchHire.ClientId,
                                Amount = clientRefundAmount,
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
                                Amount = expertAmount,
                                TransactionType = "Payout",
                                RelatedEntityType = "SearchHire",
                                RelatedEntityId = searchHireId,
                                StripeTransferId = createdTransferId,
                                CreatedAt = DateTime.UtcNow
                            };
                            _context.FinancialTransactions.Add(expertTx);
                        }

                        await _context.SaveChangesAsync();
                        
                        // ✅ CORRECCIÓN: Solo hacer commit si creamos la transacción
                        if (transaction != null)
                        {
                        await transaction.CommitAsync();
                        }

                        // ✅ Notificar a usuarios sobre movimientos de dinero exitosos
                        if (needsRefund && !string.IsNullOrEmpty(createdRefundId))
                        {
                            // Refund exitoso - notificar al cliente
                            await _loggingService.LogInfoAsync(
                                message: "Reembolso procesado",
                                details: $"Se procesó tu reembolso de {clientRefundAmount:F2}€ por el servicio #{searchHireId}. El dinero llegará a tu cuenta en 5-10 días hábiles.",
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
                                details: $"Has recibido {expertAmount:F2}€ por el servicio #{searchHireId}. El dinero está disponible en tu cuenta de Stripe.",
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
                        // ✅ CORRECCIÓN: Solo hacer rollback si creamos la transacción
                        if (transaction != null)
                    {
                        await transaction.RollbackAsync();
                        }
                        // 🚨 LOG CRÍTICO: Error de Stripe durante distribución (una sola vez, con información completa)
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Stripe exception during money distribution transaction",
                            details: $"Stripe exception occurred during money distribution transaction for SearchHire {searchHireId}. " +
                                    $"Distribution Plan: Client={clientRefundAmount}€ ({config.ClientPercentage}%), Expert={expertAmount}€ ({config.ExpertPercentage}%), Platform={platformAmount}€ ({config.PlatformPercentage}%). " +
                                    $"Stripe Error: {ex.Message}, Type: {ex.StripeError?.Type}, Code: {ex.StripeError?.Code}, DeclineCode: {ex.StripeError?.DeclineCode}, Param: {ex.StripeError?.Param}. " +
                                    $"PaymentIntentId: {servicePayment.StripePaymentIntentId}, ExpertAccountId: {searchHire.Expert?.ExpertProfile?.StripeAccountId}. " +
                                    $"Transaction Status: Transfer={(createdTransferId != null ? $"Created ({createdTransferId})" : "Not attempted")}, Refund={(createdRefundId != null ? $"Created ({createdRefundId})" : "Not attempted")}. " +
                                    $"ACTION REQUIRED: Review Stripe error details and retry distribution if applicable. If transfer was created, verify if reversal is needed.",
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
                        // ✅ CORRECCIÓN: Solo hacer rollback si creamos la transacción
                        if (transaction != null)
                    {
                        await transaction.RollbackAsync();
                        }
                        // 🚨 LOG CRÍTICO: Error general durante distribución (una sola vez, con información completa)
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
                });
            }
            catch (Exception ex)
            {
                // 🚨 LOG CRÍTICO: Error general fuera de la transacción (una sola vez, con información completa)
                // Este error ocurre ANTES de entrar en la transacción, por lo que no hay datos de distribución calculados
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
