using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using newApi.Common;
using newApi.Controllers;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public class SubscriptionService : ISubscriptionService
    {
        private readonly AppDbContext _context; 
        private readonly ILogger _logger; 
        private readonly StripeRefundService _refundService;
        private readonly ILoggingService _loggingService;

        public SubscriptionService(AppDbContext context, ILogger<SubscriptionService> logger, StripeRefundService refundService, ILoggingService loggingService)
        {
            _context = context;
            _logger = logger;
            _refundService = refundService;
            _loggingService = loggingService;
        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue
        /// </summary>
        private async Task<int> GetStatusIdByValueAsync(string statusValue)
        {
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == "SearchHireStatus");
            
            if (systemStatus == null)
            {
                _logger.LogWarning("SystemStatus not found for StatusValue: {StatusValue}", statusValue);
                // Default to "pending" (ID = 1)
                return 1;
            }
            
            return systemStatus.Id;
        }

        public async Task<SubscriptionLimits> GetUserSubscriptionLimits(int userId)
        {
            // Suscripciones periódicas deshabilitadas: se elimina lectura de SubscriptionPlan
            // Implementación anterior comentada para referencia:
            /*
            try
            {
                var user = await _context.Users
                    .Include(u => u.SubscriptionPlan)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user?.SubscriptionPlan == null)
                {
                    var freePlan = await _context.SubscriptionPlans
                        .FirstOrDefaultAsync(p => p.PriceYearly == 0);

                    if (freePlan == null)
                    {
                        _logger.LogWarning("No free plan found in database");
                        return new SubscriptionLimits { MaxSearches = 1, MinSearchInterval = 24 };
                    }

                    return new SubscriptionLimits
                    {
                        MaxSearches = freePlan.MaxSearches,
                        MinSearchInterval = freePlan.MinSearchInterval
                    };
                }

                return new SubscriptionLimits
                {
                    MaxSearches = user.SubscriptionPlan.MaxSearches,
                    MinSearchInterval = user.SubscriptionPlan.MinSearchInterval
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting subscription limits for user {UserId}", userId);
                throw;
            }
            */

            // Devolver límites neutros por ahora
            await Task.CompletedTask;
            return new SubscriptionLimits { MaxSearches = 9999, MinSearchInterval = 0 };
        }

        public async Task ProcessExpiredServicesAsync()
        {
            try
            {
                var expiredHires = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .Include(sh => sh.Client)
                    .Where(sh => sh.Status.StatusValue == SearchHireStatus.Pending.ToStringValue()
                              && sh.CompletionDeadline <= DateTime.UtcNow)
                    .ToListAsync();

                _logger.LogInformation("Found {Count} expired SearchHires to process", expiredHires.Count);

                foreach (var searchHire in expiredHires)
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        if (searchHire.Status.StatusValue != SearchHireStatus.Pending.ToStringValue())
                        {
                            _logger.LogWarning("SearchHireId={SearchHireId} is no longer in pending, skipping", searchHire.Id);
                            await transaction.CommitAsync();
                            continue;
                        }

                        // Si el experto no responde en 2 días, usar orquestador central
                        var success = await _refundService.ProcessMoneyDistributionAsync(
                            searchHire.Id,
                            "cancelled_by_no_response",
                            "Expert did not respond within deadline",
                            null);
                        
                        if (success)
                        {
                            searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                            searchHire.UpdatedAt = DateTime.UtcNow;
                            _logger.LogInformation("Processed money distribution and cancelled searchHireId={SearchHireId} due to expert timeout", searchHire.Id);
                        }
                        else
                        {
                            // Si falla el reembolso, marcar como transfer_failed para revisión manual
                            searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.TransferFailed.ToStringValue());
                            searchHire.UpdatedAt = DateTime.UtcNow;
                            _logger.LogError("Failed to refund client for expired searchHireId={SearchHireId}, marked as transfer_failed", searchHire.Id);
                            
                            // Log critical error for money transaction failure
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Failed to refund client for expired service",
                                details: $"Failed to process refund for expired SearchHire {searchHire.Id}",
                                userId: searchHire.ClientId,
                                source: "SubscriptionService.ProcessExpiredServicesAsync",
                                relatedEntityType: "Refund",
                                relatedEntityId: searchHire.Id,
                                additionalData: new { 
                                    SearchHireId = searchHire.Id,
                                    Amount = searchHire.Amount,
                                    ClientId = searchHire.ClientId,
                                    ExpertId = searchHire.ExpertId,
                                    Reason = "Expert did not respond within deadline"
                                }
                            );
                        }

                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Error processing searchHireId={SearchHireId}", searchHire.Id);
                        
                        // Log critical error for money transaction failure
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Error processing expired service",
                            details: ex.ToString(),
                            userId: searchHire.ClientId,
                            source: "SubscriptionService.ProcessExpiredServicesAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id,
                            additionalData: new { 
                                SearchHireId = searchHire.Id,
                                Amount = searchHire.Amount,
                                ClientId = searchHire.ClientId,
                                ExpertId = searchHire.ExpertId,
                                ErrorMessage = ex.Message
                            }
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing expired services");
                throw;
            }
        }


        public async Task ProcessAwaitingClientDecisionAsync()
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddHours(-24); // UNIFICADO: 24h para disputas y aprobación automática

                if (_context == null)
                {
                    throw new InvalidOperationException("Database context is not initialized");
                }

                // Procesar contrataciones donde el experto completó el trabajo pero el cliente no ha decidido en 24h
                // En este caso, aprobamos automáticamente y transferimos al experto
                var query = _context.SearchHires
                    .Include(sh => sh.Status)
                    .Where(sh => sh.Status.StatusValue == SearchHireStatus.AwaitingClientDecision.ToStringValue()
                              && sh.UpdatedAt.HasValue
                              && sh.UpdatedAt.Value <= cutoffDate)
                    .Select(sh => new { sh.Id, sh.ClientId, sh.ExpertId, sh.Amount });

                var awaitingDecisionHires = await query.ToListAsync();

                if (awaitingDecisionHires == null || !awaitingDecisionHires.Any())
                {
                    return;
                }

                const int batchSize = 10; // Smaller batch size to reduce transaction time
                for (int i = 0; i < awaitingDecisionHires.Count; i += batchSize)
                {
                    var batch = awaitingDecisionHires.Skip(i).Take(batchSize).ToList();
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        foreach (var item in batch)
                        {
                            var searchHire = await _context.SearchHires
                                .Include(sh => sh.Status)
                                .FirstOrDefaultAsync(sh => sh.Id == item.Id);

                            if (searchHire == null || searchHire.Status.StatusValue != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                            {
                                continue;
                            }

                            // Call central orchestrator directly
                            try
                            {
                                await _refundService.ProcessMoneyDistributionAsync(
                                    item.Id,
                                    "completed_without_client_approval",
                                    "Auto transfer after client timeout (24h)",
                                    null);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to process transfer for SearchHireId={SearchHireId}", item.Id);
                                
                                // Log critical error for money transaction failure
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Failed to process transfer to expert",
                                    details: ex.ToString(),
                                    userId: item.ExpertId,
                                    source: "SubscriptionService.ProcessAwaitingClientDecisionAsync",
                                    relatedEntityType: "Transfer",
                                    relatedEntityId: item.Id,
                                    additionalData: new { 
                                        SearchHireId = item.Id,
                                        Amount = item.Amount,
                                        ClientId = item.ClientId,
                                        ExpertId = item.ExpertId,
                                        ErrorMessage = ex.Message
                                    }
                                );
                                
                                continue; // Skip to next record if transfer fails
                            }

                            // Only update status and create notifications if transfer succeeds
                            searchHire.ClientApproved = true;
                            searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Completed.ToStringValue());
                            searchHire.UpdatedAt = DateTime.UtcNow;

                            if (item.ExpertId.HasValue)
                            {
                                _context.Notifications.Add(new Notification
                                {
                                    Id = Guid.NewGuid(),
                                    UserId = item.ExpertId.Value,
                                    Title = "Pago Automático Recibido",
                                    Message = $"Has recibido el pago de €{item.Amount:F2} por tu servicio. El cliente no respondió en 24h.",
                                    Type = "payment",
                                    Read = false,
                                    CreatedAt = DateTime.UtcNow
                                });
                            }

                            _context.Notifications.Add(new Notification
                            {
                                Id = Guid.NewGuid(),
                                UserId = item.ClientId,
                                Title = "Servicio Completado Automáticamente",
                                Message = $"Tu servicio de €{item.Amount:F2} se ha completado automáticamente.",
                                Type = "service_completion",
                                Read = false,
                                CreatedAt = DateTime.UtcNow
                            });
                        }

                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Error processing batch of SearchHires starting at index {Index}", i);
                        
                        // Log critical error for money transaction failure
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Error processing batch of SearchHires",
                            details: ex.ToString(),
                            source: "SubscriptionService.ProcessAwaitingClientDecisionAsync",
                            relatedEntityType: "BatchTransfer",
                            additionalData: new { 
                                BatchIndex = i,
                                BatchSize = batch.Count,
                                ErrorMessage = ex.Message
                            }
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing awaiting client decision services");
                throw;
            }
        }

    }
}