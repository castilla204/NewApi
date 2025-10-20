using Microsoft.EntityFrameworkCore;
using Stripe;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;
using newApi.DataLayer.Models;
using newApi.Common;

namespace newApi.Services
{
    public class StripeRefundService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<StripeRefundService> _logger;
        private readonly SystemStatusService _systemStatusService;
        private readonly ILoggingService _loggingService;

        public StripeRefundService(AppDbContext context, ILogger<StripeRefundService> logger, SystemStatusService systemStatusService, ILoggingService loggingService)
        {
            _context = context;
            _logger = logger;
            _systemStatusService = systemStatusService;
            _loggingService = loggingService;
        }

        /// <summary>
        /// Procesa refund automático real a Stripe
        /// </summary>
        /// <param name="searchHireId">ID del servicio contratado</param>
        /// <param name="reason">Razón del reembolso</param>
        /// <param name="statusValue">Estado específico para buscar configuración (ej: "appointment_cancelled_by_expert_second")</param>
        /// <returns>True si se procesó correctamente, false en caso contrario</returns>
        public async Task<bool> ProcessAutomaticClientRefundAsync(int searchHireId, string reason, string statusValue)
        {
            _logger.LogInformation("🔍 PROCESSING AUTOMATIC CLIENT REFUND - SearchHireId: {SearchHireId}, Reason: {Reason}", searchHireId, reason);

            try
            {
                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", searchHireId)
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                            .ThenInclude(st => st.ServiceTypeCategory)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    _logger.LogError("❌ SEARCH HIRE NOT FOUND - SearchHireId: {SearchHireId}", searchHireId);
                    return false;
                }

                _logger.LogInformation("🔍 SEARCH HIRE FOUND - Id: {Id}, Amount: {Amount}, Status: {Status}, ClientId: {ClientId}", 
                    searchHire.Id, searchHire.Amount, searchHire.Status?.StatusValue, searchHire.ClientId);

                _logger.LogInformation("🔍 GETTING MONEY DISTRIBUTION CONFIG - Status: {Status}, CategoryId: {CategoryId}, ServiceTypeCategoryId: {ServiceTypeCategoryId}", 
                    statusValue, searchHire.SearchService?.CategoryId, searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);
                
                var config = await _systemStatusService.GetMoneyDistributionConfigAsync(statusValue, 
                    searchHire.SearchService?.CategoryId, 
                    searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);

                _logger.LogInformation("🔍 MONEY DISTRIBUTION CONFIG RESULT - Config: {Config}", 
                    config != null ? $"Client: {config.ClientPercentage}%, Expert: {config.ExpertPercentage}%, Platform: {config.PlatformPercentage}%" : "NULL");
                
                if (config == null)
                {
                    _logger.LogError("No money distribution configuration found for {Status} status for searchHireId={SearchHireId}", statusValue, searchHireId);
                    return false;
                }

                // Calcular montos según porcentajes de la base de datos
                var clientRefundAmount = searchHire.Amount * (config.ClientPercentage / 100);
                var expertAmount = searchHire.Amount * (config.ExpertPercentage / 100);
                var platformAmount = searchHire.Amount * (config.PlatformPercentage / 100);

                _logger.LogInformation("Money distribution for searchHireId={SearchHireId}: Client={ClientAmount}€ ({ClientPercentage}%), Expert={ExpertAmount}€ ({ExpertPercentage}%), Platform={PlatformAmount}€ ({PlatformPercentage}%)",
                    searchHireId, clientRefundAmount, config.ClientPercentage, expertAmount, config.ExpertPercentage, platformAmount, config.PlatformPercentage);

                // Buscar la transacción de pago original del servicio
                var servicePayment = await _context.FinancialTransactions
                    .Where(ft => ft.UserId == searchHire.ClientId 
                              && ft.TransactionType == "ServicePayment"
                              && ft.RelatedEntityType == "SearchHire"
                              && ft.RelatedEntityId == searchHireId
                              && !string.IsNullOrEmpty(ft.StripePaymentIntentId))
                    .FirstOrDefaultAsync();

                if (servicePayment == null)
                {
                    _logger.LogError("Original service payment not found for searchHireId={SearchHireId}", searchHireId);
                    return false;
                }

                // ✅ USAR EXECUTION STRATEGY para compatibilidad con NpgsqlRetryingExecutionStrategy
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        // 💳 CREAR REFUND REAL EN STRIPE (solo el porcentaje del cliente)
                        var refundOptions = new RefundCreateOptions
                        {
                            PaymentIntent = servicePayment.StripePaymentIntentId,
                            Amount = (long)(clientRefundAmount * 100), // Solo el porcentaje del cliente en céntimos
                            Reason = RefundReasons.RequestedByCustomer,
                            Metadata = new Dictionary<string, string>
                            {
                                { "userId", searchHire.ClientId.ToString() },
                                { "searchHireId", searchHireId.ToString() },
                                { "refundType", "expert_cancellation" },
                                { "reason", reason },
                                { "originalTransactionId", servicePayment.Id.ToString() },
                                { "clientPercentage", config.ClientPercentage.ToString() },
                                { "expertPercentage", config.ExpertPercentage.ToString() },
                                { "platformPercentage", config.PlatformPercentage.ToString() }
                            }
                        };

                        var refundService = new RefundService();
                        var refund = await refundService.CreateAsync(refundOptions);

                        // Actualizar transacción original como refundada
                        servicePayment.IsRefunded = true;
                        servicePayment.StripeRefundId = refund.Id;

                        // Crear transacción de refund para el cliente
                        var refundTransaction = new FinancialTransaction
                        {
                            UserId = searchHire.ClientId,
                            Amount = clientRefundAmount, // Solo el porcentaje del cliente
                            TransactionType = "Refund",
                            RelatedEntityType = "SearchHire",
                            RelatedEntityId = searchHireId,
                            StripePaymentIntentId = servicePayment.StripePaymentIntentId,
                            StripeRefundId = refund.Id,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.FinancialTransactions.Add(refundTransaction);

                        // Si el experto debe recibir algo, crear transacción de pago al experto
                        if (expertAmount > 0 && searchHire.ExpertId.HasValue)
                        {
                            var expertTransaction = new FinancialTransaction
                            {
                                UserId = searchHire.ExpertId.Value,
                                Amount = expertAmount,
                                TransactionType = "Payout",
                                RelatedEntityType = "SearchHire",
                                RelatedEntityId = searchHireId,
                                CreatedAt = DateTime.UtcNow
                            };
                            _context.FinancialTransactions.Add(expertTransaction);
                        }

                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();

                        _logger.LogInformation("Successfully processed automatic client refund for searchHireId={SearchHireId}, refundId={RefundId}, clientRefund={ClientRefund}€, expertAmount={ExpertAmount}€, platformAmount={PlatformAmount}€, reason={Reason}",
                            searchHireId, refund.Id, clientRefundAmount, expertAmount, platformAmount, reason);

                        return true;
                    }
                    catch (StripeException ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Stripe error processing refund for searchHireId={SearchHireId}: {ErrorMessage}", searchHireId, ex.Message);
                        
                        // Log critical error for money transaction failure
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Stripe refund transaction failed",
                            details: ex.ToString(),
                            userId: searchHire?.ClientId,
                            source: "StripeRefundService.ProcessAutomaticClientRefundAsync",
                            relatedEntityType: "Refund",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                SearchHireId = searchHireId,
                                Amount = searchHire?.Amount,
                                ClientId = searchHire?.ClientId,
                                StripeError = ex.Message,
                                StripeErrorType = ex.StripeError?.Type,
                                StripeErrorCode = ex.StripeError?.Code
                            }
                        );
                        
                        return false;
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Error processing refund for searchHireId={SearchHireId}: {ErrorMessage}", searchHireId, ex.Message);
                        
                        // Log critical error for money transaction failure
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Refund transaction failed",
                            details: ex.ToString(),
                            userId: searchHire?.ClientId,
                            source: "StripeRefundService.ProcessAutomaticClientRefundAsync",
                            relatedEntityType: "Refund",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                SearchHireId = searchHireId,
                                Amount = searchHire?.Amount,
                                ClientId = searchHire?.ClientId,
                                ErrorMessage = ex.Message
                            }
                        );
                        
                        return false;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProcessAutomaticClientRefundAsync for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
                
                // Log critical error for money transaction failure
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: ProcessAutomaticClientRefundAsync failed",
                    details: ex.ToString(),
                    source: "StripeRefundService.ProcessAutomaticClientRefundAsync",
                    relatedEntityType: "Refund",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        ErrorMessage = ex.Message
                    }
                );
                
                return false;
            }
        }

        /// <summary>
        /// Procesa transferencia al experto
        /// </summary>
        /// <param name="searchHireId">ID del servicio contratado</param>
        /// <returns>True si se procesó correctamente, false en caso contrario</returns>
        public async Task<bool> ProcessTransferToExpertAsync(int searchHireId)
        {
            _logger.LogInformation("Processing transfer to expert for searchHireId={SearchHireId}", searchHireId);

            try
            {
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                    return false;
                }

                if (searchHire.ExpertId == null)
                {
                    _logger.LogError("No expert assigned to searchHireId={SearchHireId}", searchHireId);
                    return false;
                }

                // Obtener configuración de distribución de dinero
                var config = await _systemStatusService.GetMoneyDistributionConfigAsync("completed", 
                    searchHire.SearchService?.CategoryId, 
                    searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);
                
                if (config == null)
                {
                    _logger.LogError("No money distribution configuration found for completed status for searchHireId={SearchHireId}", searchHireId);
                    return false;
                }

                var expertAmount = searchHire.Amount * (config.ExpertPercentage / 100);

                if (expertAmount <= 0)
                {
                    _logger.LogInformation("No expert amount to transfer for searchHireId={SearchHireId}", searchHireId);
                    return true;
                }

                // ✅ USAR EXECUTION STRATEGY para compatibilidad con NpgsqlRetryingExecutionStrategy
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        // Crear transacción de pago al experto
                        var expertTransaction = new FinancialTransaction
                        {
                            UserId = searchHire.ExpertId.Value,
                            Amount = expertAmount,
                            TransactionType = "Payout",
                            RelatedEntityType = "SearchHire",
                            RelatedEntityId = searchHireId,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.FinancialTransactions.Add(expertTransaction);

                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();

                        _logger.LogInformation("Successfully processed expert transfer for searchHireId={SearchHireId}, expertAmount={ExpertAmount}€", 
                            searchHireId, expertAmount);

                        return true;
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Error processing expert transfer for searchHireId={SearchHireId}: {ErrorMessage}", searchHireId, ex.Message);
                        
                        // Log critical error for money transaction failure
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Expert transfer transaction failed",
                            details: ex.ToString(),
                            userId: searchHire?.ExpertId,
                            source: "StripeRefundService.ProcessTransferToExpertAsync",
                            relatedEntityType: "Transfer",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                SearchHireId = searchHireId,
                                Amount = searchHire?.Amount,
                                ExpertId = searchHire?.ExpertId,
                                ErrorMessage = ex.Message
                            }
                        );
                        
                        return false;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProcessTransferToExpertAsync for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
                
                // Log critical error for money transaction failure
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: ProcessTransferToExpertAsync failed",
                    details: ex.ToString(),
                    source: "StripeRefundService.ProcessTransferToExpertAsync",
                    relatedEntityType: "Transfer",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        ErrorMessage = ex.Message
                    }
                );
                
                return false;
            }
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
            _logger.LogInformation("🔄 PROCESS MONEY DISTRIBUTION - SearchHireId={SearchHireId}, Status={Status}, Reason={Reason}", searchHireId, statusValue, reason);

            try
            {
                // Bloqueo a nivel de fila para consistencia
                var searchHire = await _context.SearchHires
                    .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", searchHireId)
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                        .ThenInclude(e => e.ExpertProfile)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    _logger.LogError("❌ SEARCH HIRE NOT FOUND - SearchHireId: {SearchHireId}", searchHireId);
                    return false;
                }

                // Validar si el estado es de finalización cuando proviene de AppointmentStatus
                try
                {
                    var statusRow = await _context.SystemStatuses
                        .FirstOrDefaultAsync(s => s.StatusValue == statusValue);
                    if (statusRow != null && statusRow.StatusType == "AppointmentStatus" && statusRow.IsFinalizationStatus == false)
                    {
                        _logger.LogInformation("⏭️ SKIP DISTRIBUTION - Non-finalization appointment status: {StatusValue}", statusValue);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Warning checking IsFinalizationStatus for status={Status}", statusValue);
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
                                        _logger.LogInformation("⏭️ SKIP DISTRIBUTION - Mapped target is non-finalization: {FinalStatusValue}", finalStatusValue);
                                        return false;
                                    }
                                }
                                catch (Exception ex2)
                                {
                                    _logger.LogWarning(ex2, "Warning checking IsFinalizationStatus for mapped status={Status}", finalStatusValue);
                                }
                                _logger.LogInformation("🔁 FALLBACK TO FINAL STATUS - From {From} → {To}", statusValue, finalStatusValue);
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
                        _logger.LogWarning(mapEx, "Warning while mapping status fallback for {Status}", statusValue);
                    }

                    if (config == null)
                    {
                        _logger.LogError("❌ NO MONEY CONFIG - Status={Status}, SearchHireId={SearchHireId}", statusValue, searchHireId);
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

                var clientRefundAmount = searchHire.Amount * (config.ClientPercentage / 100);
                var expertAmount = searchHire.Amount * (config.ExpertPercentage / 100);
                var platformAmount = searchHire.Amount * (config.PlatformPercentage / 100);

                _logger.LogInformation("💰 DISTRIBUTION - SH={SearchHireId} Client={Client}€({ClientPct}%) Expert={Expert}€({ExpertPct}%) Platform={Platform}€({PlatformPct}%)",
                    searchHireId, clientRefundAmount, config.ClientPercentage, expertAmount, config.ExpertPercentage, platformAmount, config.PlatformPercentage);

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
                    _logger.LogError("❌ ORIGINAL PAYMENT NOT FOUND - SearchHireId={SearchHireId}", searchHireId);
                    return false;
                }

                // Pre-check: si habrá transferencia al experto, validar saldo disponible en Stripe para evitar estados parciales
                if (expertAmount > 0)
                {
                    try
                    {
                        var balanceSvc = new BalanceService();
                        var balance = await balanceSvc.GetAsync();
                        var availableInEur = balance.Available?.Where(b => b.Currency == "eur").Sum(b => b.Amount) ?? 0;
                        var requiredCents = (long)(expertAmount * 100);
                        if (availableInEur < requiredCents)
                        {
                            _logger.LogError("❌ INSUFFICIENT PLATFORM BALANCE - Required={Required}¢, Available={Available}¢", requiredCents, availableInEur);
                            return false;
                        }
                    }
                    catch (Exception balEx)
                    {
                        _logger.LogError(balEx, "❌ Error checking Stripe balance before transfer");
                        return false;
                    }
                }

                // Orquestación bajo estrategia de reintento y transacción
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        var idempotencyKey = $"sh:{searchHireId}:status:{statusValue}";

                        string createdTransferId = null;
                        // Si hay refund y transfer, ejecutar primero la transferencia y después el refund; si el refund falla, revertir la transferencia
                        var needsRefund = clientRefundAmount > 0;
                        var needsTransfer = expertAmount > 0 && searchHire.ExpertId.HasValue;

                        // Transfer primero (si aplica)
                        if (needsTransfer)
                        {
                            var expertStripeAccountId = searchHire.Expert?.ExpertProfile?.StripeAccountId;
                            if (string.IsNullOrEmpty(expertStripeAccountId))
                            {
                                _logger.LogError("❌ EXPERT STRIPE ACCOUNT MISSING - SearchHireId={SearchHireId}, ExpertId={ExpertId}", searchHireId, searchHire.ExpertId);
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Expert Stripe account missing",
                                    details: $"Expert {searchHire.ExpertId} has no Stripe account configured",
                                    userId: searchHire.ExpertId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "Transfer",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { Status = statusValue }
                                );
                                await transaction.RollbackAsync();
                                return false;
                            }

                            var transferOptions = new TransferCreateOptions
                            {
                                Amount = (long)(expertAmount * 100),
                                Currency = "eur",
                                Destination = expertStripeAccountId,
                                Metadata = new Dictionary<string, string>
                                {
                                    { "idempotencyKey", idempotencyKey },
                                    { "searchHireId", searchHireId.ToString() },
                                    { "statusValue", statusValue },
                                    { "clientPercentage", config.ClientPercentage.ToString() },
                                    { "expertPercentage", config.ExpertPercentage.ToString() },
                                    { "platformPercentage", config.PlatformPercentage.ToString() },
                                    { "reason", reason }
                                }
                            };

                            var transferSvc = new TransferService();
                            var transfer = await transferSvc.CreateAsync(transferOptions);
                            createdTransferId = transfer.Id;
                        }

                        // Refund después (si aplica)
                        string createdRefundId = null;
                        if (needsRefund)
                        {
                            var refundOptions = new RefundCreateOptions
                            {
                                PaymentIntent = servicePayment.StripePaymentIntentId,
                                Amount = (long)(clientRefundAmount * 100),
                                Reason = RefundReasons.RequestedByCustomer,
                                Metadata = new Dictionary<string, string>
                                {
                                    { "idempotencyKey", idempotencyKey },
                                    { "searchHireId", searchHireId.ToString() },
                                    { "statusValue", statusValue },
                                    { "clientPercentage", config.ClientPercentage.ToString() },
                                    { "expertPercentage", config.ExpertPercentage.ToString() },
                                    { "platformPercentage", config.PlatformPercentage.ToString() },
                                    { "reason", reason },
                                    { "originalTransactionId", servicePayment.Id.ToString() }
                                }
                            };

                            try
                            {
                                var refundSvc = new RefundService();
                                var refund = await refundSvc.CreateAsync(refundOptions);
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
                                        await reversalSvc.CreateAsync(createdTransferId, new TransferReversalCreateOptions
                                        {
                                            // Revertir el total transferido
                                        });
                                    }
                                    catch (Exception revEx)
                                    {
                                        _logger.LogCritical(revEx, "❌ CRITICAL: Failed to reverse transfer after refund failure - TransferId={TransferId}", createdTransferId);
                                    }
                                }

                                await transaction.RollbackAsync();
                                _logger.LogError(refundEx, "Stripe refund failed, rolled back distribution - SH={SearchHireId}", searchHireId);
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
                        await transaction.CommitAsync();

                        _logger.LogInformation("✅ MONEY DISTRIBUTION DONE - SH={SearchHireId}", searchHireId);
                        return true;
                    }
                    catch (StripeException ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Stripe error processing money distribution for SH={SearchHireId}: {Error}", searchHireId, ex.Message);
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Stripe error in money distribution",
                            details: ex.ToString(),
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { Status = statusValue, Error = ex.Message, StripeType = ex.StripeError?.Type, StripeCode = ex.StripeError?.Code }
                        );
                        return false;
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Error processing money distribution for SH={SearchHireId}: {Error}", searchHireId, ex.Message);
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Error in money distribution",
                            details: ex.ToString(),
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { Status = statusValue, Error = ex.Message }
                        );
                        return false;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProcessMoneyDistributionAsync for SH={SearchHireId}: {Error}", searchHireId, ex.Message);
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: ProcessMoneyDistributionAsync failed",
                    details: ex.ToString(),
                    userId: initiatedByUserId,
                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { Status = statusValue, Error = ex.Message }
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
