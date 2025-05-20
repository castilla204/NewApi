using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using newApi.Common;
using newApi.Controllers;
using newApi.DataLayer.Models;

namespace newApi.Services
{
    public class SubscriptionService : ISubscriptionService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<SubscriptionService> _logger;

        public SubscriptionService(AppDbContext context, ILogger<SubscriptionService> logger)
        {
            _context = context;
            _logger = logger;
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
    }
}