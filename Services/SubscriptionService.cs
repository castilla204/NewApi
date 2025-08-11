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
        private readonly AppDbContext _context; private readonly ILogger _logger; private readonly ICheckingClientDecisionService _checkingClientDecisionService;

        public SubscriptionService(AppDbContext context, ILogger<SubscriptionService> logger, ICheckingClientDecisionService checkingClientDecisionService)
        {
            _context = context;
            _logger = logger;
            _checkingClientDecisionService = checkingClientDecisionService;
        }

        public async Task<SubscriptionLimits> GetUserSubscriptionLimits(int userId)
        {
            try
            {
                // Get user with subscription plan
                var user = await _context.Users
                    .Include(u => u.SubscriptionPlan)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                // If no subscription found, get the free plan
                if (user?.SubscriptionPlan == null)
                {
                    var freePlan = await _context.SubscriptionPlans
                        .FirstOrDefaultAsync(p => p.PriceYearly == 0);

                    if (freePlan == null)
                    {
                        _logger.LogWarning("No free plan found in database");
                        return new SubscriptionLimits
                        {
                            MaxSearches = 1,
                            MinSearchInterval = 24
                        };
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
        }

        public async Task ProcessExpiredServicesAsync()
        {
            try
            {
                var expiredHires = await _context.SearchHires
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .Include(sh => sh.Client)
                    .Where(sh => sh.Status == SearchHireStatus.Pending.ToStringValue()
                              && sh.CompletionDeadline <= DateTime.UtcNow)
                    .ToListAsync();

                _logger.LogInformation("Found {Count} expired SearchHires to process", expiredHires.Count);

                foreach (var searchHire in expiredHires)
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        if (searchHire.Status != SearchHireStatus.Pending.ToStringValue())
                        {
                            _logger.LogWarning("SearchHireId={SearchHireId} is no longer in pending, skipping", searchHire.Id);
                            await transaction.CommitAsync();
                            continue;
                        }

                        searchHire.Status = SearchHireStatus.AwaitingClientDecision.ToStringValue();
                        searchHire.UpdatedAt = DateTime.UtcNow;
                        _logger.LogInformation("Moved searchHireId={SearchHireId} to awaiting_client_decision", searchHire.Id);

                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Error processing searchHireId={SearchHireId}", searchHire.Id);
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
                var cutoffDate = DateTime.UtcNow.AddDays(-2);

                if (_context == null)
                {
                    throw new InvalidOperationException("Database context is not initialized");
                }

                var query = _context.SearchHires
                    .Where(sh => sh.Status == SearchHireStatus.AwaitingClientDecision.ToStringValue()
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
                                .FirstOrDefaultAsync(sh => sh.Id == item.Id);

                            if (searchHire == null || searchHire.Status != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                            {
                                continue;
                            }

                            // Call ProcessTransferToExpert first
                            try
                            {
                                await _checkingClientDecisionService.ProcessTransferToExpert(item.Id);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to process transfer for SearchHireId={SearchHireId}", item.Id);
                                continue; // Skip to next record if transfer fails
                            }

                            // Only update status and create notifications if transfer succeeds
                            searchHire.ClientApproved = true;
                            searchHire.Status = SearchHireStatus.Completed.ToStringValue();
                            searchHire.UpdatedAt = DateTime.UtcNow;

                            if (item.ExpertId.HasValue)
                            {
                                _context.Notifications.Add(new Notification
                                {
                                    Id = Guid.NewGuid(),
                                    UserId = item.ExpertId.Value,
                                    Title = "Pago Automático Recibido",
                                    Message = $"Has recibido el pago de €{item.Amount:F2} por tu servicio. El cliente no respondió en 2 días.",
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