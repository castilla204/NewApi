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
                ExpertTransferId = hire.ExpertTransferId,
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