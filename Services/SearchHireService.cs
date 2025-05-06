using Microsoft.EntityFrameworkCore;
using Stripe.Checkout;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public class SearchHireService : ISearchHireService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<SearchHireService> _logger;
        private readonly string _domain = "https://atrapo.io";

        public SearchHireService(
            AppDbContext context,
            ILogger<SearchHireService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<Session> CreateCheckoutSession(int userId, int serviceId)
        {
            var service = await _context.SearchServices
                .Include(s => s.ExpertProfile)
                .FirstOrDefaultAsync(s => s.Id == serviceId);

            if (service == null || service.ExpertProfile.UserId == userId)
                return null;

            var options = new SessionCreateOptions
            {
                PaymentMethodTypes = new List<string> { "card" },
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions
                    {
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            Currency = "eur",
                            UnitAmount = (long)(service.Price * 100),
                            ProductData = new SessionLineItemPriceDataProductDataOptions
                            {
                                Name = $"Search Service - {service.DurationInHours}h",
                                Description = service.Conditions
                            }
                        },
                        Quantity = 1
                    }
                },
                Mode = "payment",
                SuccessUrl = $"{_domain}/hire/success?session_id={{CHECKOUT_SESSION_ID}}",
                CancelUrl = $"{_domain}/hire/cancel",
                Metadata = new Dictionary<string, string>
                {
                    { "serviceId", serviceId.ToString() },
                    { "clientId", userId.ToString() },
                    { "expertId", service.ExpertProfile.UserId.ToString() }
                }
            };

            return await new SessionService().CreateAsync(options);
        }

        public async Task<bool> HandleCheckoutSession(Session session)
        {
            if (!int.TryParse(session.Metadata["serviceId"], out int serviceId) ||
                !int.TryParse(session.Metadata["clientId"], out int clientId) ||
                !int.TryParse(session.Metadata["expertId"], out int expertId))
                return false;

            var service = await _context.SearchServices.FindAsync(serviceId);
            if (service == null)
                return false;

            var searchHire = new SearchHire
            {
                ClientId = clientId,
                ExpertId = expertId,
                SearchServiceId = serviceId,
                Status = "Pending",
                StripeTransactionId = session.PaymentIntentId,
                Amount = service.Price,
                CreatedAt = DateTime.UtcNow
            };

            await _context.SearchHires.AddAsync(searchHire);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<IEnumerable<SearchHireResponseDto>> GetClientHires(int userId)
        {
            return await _context.SearchHires
                .Include(h => h.Client)
                .Include(h => h.Expert)
                .Include(h => h.SearchService)
                    .ThenInclude(s => s.Images)
                .Where(h => h.ClientId == userId)
                .Select(h => MapToResponseDto(h))
                .OrderByDescending(h => h.CreatedAt)
                .ToListAsync();
        }

        public async Task<IEnumerable<SearchHireResponseDto>> GetExpertHires(int userId)
        {
            return await _context.SearchHires
                .Include(h => h.Client)
                .Include(h => h.Expert)
                .Include(h => h.SearchService)
                    .ThenInclude(s => s.Images)
                .Where(h => h.ExpertId == userId)
                .Select(h => MapToResponseDto(h))
                .OrderByDescending(h => h.CreatedAt)
                .ToListAsync();
        }

        public async Task<bool> UpdateHireStatus(int userId, int hireId, string status)
        {
            var hire = await _context.SearchHires.FindAsync(hireId);
            if (hire == null || hire.ExpertId != userId)
                return false;

            var validStatuses = new[] { "InProgress", "Completed", "Cancelled" };
            if (!validStatuses.Contains(status))
                return false;

            hire.Status = status;
            if (status == "Completed")
            {
                hire.CompletedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            return true;
        }

        private static SearchHireResponseDto MapToResponseDto(SearchHire hire)
        {
            return new SearchHireResponseDto
            {
                Id = hire.Id,
                ClientId = hire.ClientId,
                ExpertId = hire.ExpertId,
                SearchServiceId = hire.SearchServiceId,
                SearchId = hire.SearchId,
                Status = hire.Status,
                StripeTransactionId = hire.StripeTransactionId,
                Amount = hire.Amount,
                CreatedAt = hire.CreatedAt,
                CompletedAt = hire.CompletedAt,
                Client = new UserDto
                {
                    Name = hire.Client.Name,
                    Email = hire.Client.Email
                },
                Expert = new UserDto
                {
                    Name = hire.Expert.Name,
                    Email = hire.Expert.Email
                },
                Service = new SearchServiceResponseDto
                {
                    Id = hire.SearchService.Id,
                    CategoryId = hire.SearchService.CategoryId,
                    Price = hire.SearchService.Price,
                    Conditions = hire.SearchService.Conditions,
                    DurationInHours = hire.SearchService.DurationInHours,
                    CreatedAt = hire.SearchService.CreatedAt,
                    ImageUrls = hire.SearchService.Images.Select(i => i.ImageUrl).ToList()
                }
            };
        }
    }
}