using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using DataLayer.Models;
using DataLayer.Models.DTOs;
using DataLayer.Models.PostGresModels;
using newApi.Services;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class AISearchController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IGPTService _gptService;

        public AISearchController(AppDbContext context, IGPTService gptService)
        {
            _context = context;
            _gptService = gptService;
        }

        [HttpPost("create")]
        public async Task<IActionResult> CreateAISearch([FromBody] AISearchRequest request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Parse user input and create search parameters
                var searchParams = await _gptService.AnalyzeSearchInput(request.UserInput);

                // Create search
                var search = new Search
                {
                    UserId = userId,
                    Frequency = request.Frequency ?? 21600, // 6 hours in seconds by default
                    Title = searchParams.Title,
                    Description = searchParams.Description,
                    IsActive = true,
                    StartDate = request.StartDate ?? DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                };

                await _context.Searches.AddAsync(search);
                await _context.SaveChangesAsync();

                // Create search parameters
                var searchParameter = new SearchParameter
                {
                    Keywords = searchParams.Keywords,
                    UserSearch = request.UserInput,
                    Latitude = searchParams.Latitude,
                    Longitude = searchParams.Longitude,
                    ShippingAvailable = searchParams.ShippingAvailable,
                    Category = searchParams.Category,
                    LocationRange = searchParams.LocationRange,
                    MinPrice = searchParams.MinPrice,
                    MaxPrice = searchParams.MaxPrice,
                    BrandId = searchParams.BrandId,
                    ModelId = searchParams.ModelId,
                    SearchId = search.Id
                };

                await _context.SearchParameters.AddAsync(searchParameter);
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    searchId = search.Id,
                    searchParameterId = searchParameter.SearchParameterId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost("rewrite-description")]
        public async Task<IActionResult> RewriteDescription([FromBody] RewriteDescriptionRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Text))
                {
                    return BadRequest(new { message = "El texto a reescribir es obligatorio" });
                }

                DescriptionKind kind;
                switch (request.Kind)
                {
                    case "expertProfile":
                        kind = DescriptionKind.ExpertProfile;
                        break;
                    case "serviceConditions":
                        kind = DescriptionKind.ServiceConditions;
                        break;
                    default:
                        return BadRequest(new { message = "kind debe ser 'expertProfile' o 'serviceConditions'" });
                }

                var trimmed = request.Text.Trim();
                if (trimmed.Length < 10)
                {
                    return BadRequest(new { message = "Escribe un poco más para poder reescribirlo (mínimo 10 caracteres)" });
                }

                var rewritten = await _gptService.RewriteDescription(kind, trimmed);
                return Ok(new { rewritten });
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { message = "No se pudo generar el texto. Inténtalo de nuevo." });
            }
        }
    }

    public class AISearchRequest
    {
        public string UserInput { get; set; }
        public int? Frequency { get; set; }
        public DateTime? StartDate { get; set; }
    }

    public class RewriteDescriptionRequest
    {
        public string Kind { get; set; }
        public string Text { get; set; }
    }

    public class SearchParamsResult
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string Keywords { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
        public bool ShippingAvailable { get; set; }
        public int? Category { get; set; }
        public int? LocationRange { get; set; }
        public int? MinPrice { get; set; }
        public int? MaxPrice { get; set; }
        public int? BrandId { get; set; }
        public int? ModelId { get; set; }
    }
}