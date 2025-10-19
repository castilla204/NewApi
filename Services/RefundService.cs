using Microsoft.EntityFrameworkCore;
using Stripe;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;
using newApi.DataLayer.Models;

namespace newApi.Services
{
    public class StripeRefundService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<StripeRefundService> _logger;
        private readonly SystemStatusService _systemStatusService;

        public StripeRefundService(AppDbContext context, ILogger<StripeRefundService> logger, SystemStatusService systemStatusService)
        {
            _context = context;
            _logger = logger;
            _systemStatusService = systemStatusService;
        }

        /// <summary>
        /// Procesa refund automático real a Stripe
        /// </summary>
        /// <param name="searchHireId">ID del servicio contratado</param>
        /// <param name="reason">Razón del reembolso</param>
        /// <returns>True si se procesó correctamente, false en caso contrario</returns>
        public async Task<bool> ProcessAutomaticClientRefundAsync(int searchHireId, string reason)
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

                // Obtener configuración de distribución de dinero para cancelación de experto
                _logger.LogInformation("🔍 GETTING MONEY DISTRIBUTION CONFIG - Status: appointment_cancelled_by_expert, CategoryId: {CategoryId}, ServiceTypeCategoryId: {ServiceTypeCategoryId}", 
                    searchHire.SearchService?.CategoryId, searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);
                
                var config = await _systemStatusService.GetMoneyDistributionConfigAsync("appointment_cancelled_by_expert", 
                    searchHire.SearchService?.CategoryId, 
                    searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);

                _logger.LogInformation("🔍 MONEY DISTRIBUTION CONFIG RESULT - Config: {Config}", 
                    config != null ? $"Client: {config.ClientPercentage}%, Expert: {config.ExpertPercentage}%, Platform: {config.PlatformPercentage}%" : "NULL");
                
                if (config == null)
                {
                    _logger.LogError("No money distribution configuration found for appointment_cancelled_by_expert status for searchHireId={SearchHireId}", searchHireId);
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
                        return false;
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Error processing refund for searchHireId={SearchHireId}: {ErrorMessage}", searchHireId, ex.Message);
                        return false;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProcessAutomaticClientRefundAsync for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
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
                        return false;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProcessTransferToExpertAsync for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
                return false;
            }
        }
    }
}
