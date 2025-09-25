using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AdModel = newApi.DataLayer.Models.AdModel;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models;


namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SearchResultController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<SearchResultController> _logger;
        private readonly IMapper _mapper;

        public SearchResultController(AppDbContext context, ILogger<SearchResultController> logger, IMapper mapper)
        {
            _context = context;
            _logger = logger;
            _mapper = mapper;
        }

        [HttpGet("{searchId}/results")]
        public async Task<IActionResult> GetSearchResults(int searchId)
        {
            try
            {
                // Obtiene los resultados de búsqueda de la base de datos
                var ads = await _context.SearchResults
                    .Where(sr => sr.SearchId == searchId)
                    .Select(sr => sr.Ad)
                    .ToListAsync();

                // Mapea la lista de Ad a AdModel
                var adModels = _mapper.Map<List<AdModel>>(ads);

                return Ok(adModels);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving search results");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost("{searchId}/manual-ad")]
        public async Task<IActionResult> AddManualAd(int searchId, [FromBody] ManualAdRequest request)
        {
            try
            {
                // Fetch the search
                var search = await _context.Searches.FindAsync(searchId);
                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                // Get the authenticated user's ID and email from claims
                var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                // Convert user ID to int (assuming User.Id is an int in the User model)
                if (!int.TryParse(userIdString, out var userId))
                {
                    return Unauthorized(new { message = "Invalid user ID" });
                }

                // 🔐 SEGURIDAD: Verificar rol en lugar de email
                var isAdmin = User.IsInRole("Admin");
                var searchHire = await _context.SearchHires
                    .FirstOrDefaultAsync(z => z.SearchId == searchId && z.ExpertId == userId);

                // Authorize: Allow admins or the expert assigned to the search
                if (!isAdmin && (searchHire == null || searchHire.ExpertId != userId))
                {
                    return Unauthorized(new { message = "Access denied. You must be an admin or the expert assigned to this search." });
                }

                // Create new ad
                var ad = new Ad
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = request.Title,
                    Description = request.Description,
                    Price = request.Price,
                    Url = request.Url,
                    Slug = request.Title.ToLower().Replace(" ", "-").Replace("/", "-"),
                    Images = request.Images,
                    BadThings = new string[] { },
                    GoodThings = new string[] { },
                    Tags = new string[] { },
                    SellerType = request.SellerType ?? "particular",
                    PublishDate = DateTime.UtcNow,
                    Category = request.Category,
                    Province = request.Province,
                    City = request.City,
                    PlatformId = request.PlatformId,
                    ScrappedDate = DateTime.UtcNow
                };

                await _context.Ads.AddAsync(ad);

                // Create search result
                var searchResult = new SearchResult
                {
                    SearchId = searchId,
                    AdId = ad.Id,
                    FoundAt = DateTime.UtcNow
                };

                await _context.SearchResults.AddAsync(searchResult);
                await _context.SaveChangesAsync();

                var adModel = _mapper.Map<AdModel>(ad);
                return Ok(adModel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding manual ad");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        public class ManualAdRequest
        {
            public string Title { get; set; }
            public string Description { get; set; }
            public decimal Price { get; set; }
            public string Url { get; set; }
            public string[] Images { get; set; }
            public string Category { get; set; }
            public string Province { get; set; }
            public string City { get; set; }
            public string? SellerType { get; set; }
            public int PlatformId { get; set; }
        }
    }
}