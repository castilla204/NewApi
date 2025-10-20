using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using newApi.Services;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System;
using System.Linq;
using Stripe;
using newApi.DataLayer.Models.DTOs;
using Hangfire;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SearchHireController : ControllerBase
    {
        private readonly SearchHireService _searchHireService;
        private readonly AppDbContext _context;
        private readonly ILogger<SearchHireController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IAuthorizationServices _authService;
        private readonly StripeRefundService _refundService;

        public SearchHireController(
            SearchHireService searchHireService,
            AppDbContext context,
            ILogger<SearchHireController> logger,
            IConfiguration configuration,
            IAuthorizationServices authService,
            StripeRefundService refundService)
        {
            _searchHireService = searchHireService;
            _context = context;
            _logger = logger;
            _configuration = configuration;
            _authService = authService;
            _refundService = refundService;
            StripeConfiguration.ApiKey = configuration["Stripe:SecretKey"];
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

        // POST: api/searchhire
        [HttpPost]
        public async Task<IActionResult> CreateSearchHire([FromBody] CreateSearchHireDto dto)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                var search = await _context.Searches.FindAsync(dto.SearchId);
                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                if (search.UserId != userId && !_authService.IsAdmin(User))
                {
                    return Unauthorized(new { message = "You are not authorized to create a search hire for this search" });
                }

                if (!dto.ExpertId.HasValue)
                {
                    return BadRequest(new { message = "Expert ID is required to create a search hire" });
                }

                var expert = await _context.Users
                    .Include(u => u.ExpertProfile)
                    .FirstOrDefaultAsync(u => u.Id == dto.ExpertId.Value);
                if (expert == null || expert.Role != UserRole.Expert)
                {
                    return BadRequest(new { message = "Invalid or non-expert user specified" });
                }

                if (expert.ExpertProfile == null)
                {
                    return BadRequest(new { message = "Expert has no profile configured" });
                }

                // Get the search category, if available
                var searchParameter = await _context.SearchParameters
                    .Where(sp => sp.SearchId == dto.SearchId)
                    .FirstOrDefaultAsync();
                int? categoryId = searchParameter?.Category;

                // Find a SearchService for the expert, preferably matching the search category
                SearchService searchService;
                if (categoryId.HasValue)
                {
                    searchService = await _context.SearchServices
                        .Where(ss => ss.ExpertProfileId == expert.ExpertProfile.Id && ss.CategoryId == categoryId.Value)
                        .FirstOrDefaultAsync();
                }
                else
                {
                    // Fallback to any SearchService for the expert
                    searchService = await _context.SearchServices
                        .Where(ss => ss.ExpertProfileId == expert.ExpertProfile.Id)
                        .FirstOrDefaultAsync();
                }

                if (searchService == null)
                {
                    return BadRequest(new { message = "No suitable SearchService found for the expert" });
                }

                var searchHire = new SearchHire
                {
                    SearchId = dto.SearchId,
                    ClientId = search.UserId,
                    ExpertId = dto.ExpertId.Value,
                    SearchServiceId = searchService.Id,
                    StatusId = await GetStatusIdByValueAsync("pending"),
                    Amount = searchService.Price,
                    CreatedAt = DateTime.UtcNow,
                    Conversations = new List<Conversation>()
                };

                _context.SearchHires.Add(searchHire);

                // Automatically create a Conversation
                var conversation = new Conversation
                {
                    SearchHireId = searchHire.Id,
                    ClientId = searchHire.ClientId,
                    ExpertId = searchHire.ExpertId.Value,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Messages = new List<Message>()
                };

                _context.Conversations.Add(conversation);
                searchHire.Conversations.Add(conversation);

                await _context.SaveChangesAsync();

                // Programar automáticamente la verificación de respuesta del experto para 24 horas después
                var scheduledTime = searchHire.CreatedAt.AddHours(24);
                BackgroundJob.Schedule(
                    () => CheckExpertResponseAsync(searchHire.Id),
                    scheduledTime - DateTime.UtcNow
                );

                _logger.LogInformation("SearchHire created and expert response check scheduled for searchHireId={SearchHireId}, scheduledTime={ScheduledTime}", 
                    searchHire.Id, scheduledTime);

                return CreatedAtAction(nameof(GetSearchHire), new { id = searchHire.Id }, searchHire);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating search hire");
                return StatusCode(500, new { message = "Failed to create search hire" });
            }
        }

        /// <summary>
        /// Verifica si el experto ha respondido en las primeras 24 horas (método para Hangfire)
        /// </summary>
        /// <param name="searchHireId">ID del servicio contratado</param>
        public async Task CheckExpertResponseAsync(int searchHireId)
        {
            _logger.LogInformation("Checking expert response for searchHireId={SearchHireId}", searchHireId);

            try
            {
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                    return;
                }

                // Verificar que el servicio esté activo
                if (searchHire.Status.StatusValue != "active")
                {
                    _logger.LogInformation("SearchHire is not active for searchHireId={SearchHireId}, current status={Status}", 
                        searchHireId, searchHire.Status.StatusValue);
                    return;
                }

                // Calcular si han pasado 24 horas desde la contratación
                var timeSinceHire = DateTime.UtcNow - searchHire.CreatedAt;
                if (timeSinceHire.TotalHours < 24)
                {
                    _logger.LogInformation("Less than 24 hours have passed for searchHireId={SearchHireId}, hours={Hours}", 
                        searchHireId, timeSinceHire.TotalHours);
                    return;
                }

                // Verificar si el experto ha enviado algún mensaje
                var hasExpertMessage = await _context.Messages
                    .AnyAsync(m => m.Conversation.SearchHireId == searchHireId && 
                                   m.SenderId == searchHire.ExpertId && 
                                   m.SentAt > searchHire.CreatedAt);

                if (!hasExpertMessage)
                {
                    _logger.LogWarning("Expert has not responded within 24 hours for searchHireId={SearchHireId}, processing automatic refund", searchHireId);
                    
                    // Orquestar distribución de dinero por estado final 'cancelled'
                    var refundReason = "Expert did not respond within 24 hours - automatic refund";
                    var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                        searchHireId,
                        "cancelled",
                        refundReason);
                    if (!refundSuccess)
                    {
                        _logger.LogError("Failed to process Stripe refund for automatic refund searchHireId={SearchHireId}", searchHireId);
                        return;
                    }

                    // Actualizar estado del servicio
                    searchHire.StatusId = await GetStatusIdByValueAsync("cancelled");
                    searchHire.UpdatedAt = DateTime.UtcNow;
                    
                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Real Stripe automatic refund processed successfully for searchHireId={SearchHireId}, reason={Reason}", 
                        searchHireId, refundReason);
                }
                else
                {
                    _logger.LogInformation("Expert has responded for searchHireId={SearchHireId}, no action needed", searchHireId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CheckExpertResponseAsync for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
            }
        }

        // GET: api/searchhire/{id}
        [HttpGet("{id}")]
        public async Task<IActionResult> GetSearchHire(int id)
        {
            try
            {
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Search)
                    .Include(sh => sh.Conversations)
                    .FirstOrDefaultAsync(sh => sh.Id == id);

                if (searchHire == null)
                {
                    return NotFound(new { message = "Search hire not found" });
                }

                return Ok(searchHire);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving search hire");
                return StatusCode(500, new { message = "Failed to retrieve search hire" });
            }
        }

        // GET: api/searchhire/client
        [HttpGet("client")]
        public async Task<IActionResult> GetClientHires()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var hires = await _searchHireService.GetClientHires(userId);
                return Ok(hires);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving client hires");
                return StatusCode(500, new { message = "Failed to retrieve hires" });
            }
        }

        // GET: api/searchhire/expert
        [HttpGet("expert")]
        public async Task<IActionResult> GetExpertHires()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var hires = await _searchHireService.GetExpertHires(userId);
                return Ok(hires);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving expert hires");
                return StatusCode(500, new { message = "Failed to retrieve hires" });
            }
        }

        // PUT: api/searchhire/{hireId}/status
        [HttpPut("{hireId}/status")]
        public async Task<IActionResult> UpdateHireStatus(int hireId, [FromBody] UpdateSearchHireStatusDto request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var result = await _searchHireService.UpdateHireStatus(userId, hireId, request.Status);
                if (!result.Success)
                {
                    return BadRequest(new { message = result.ErrorMessage });
                }

                return Ok(new { message = "Status updated successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating hire status");
                return StatusCode(500, new { message = "Failed to update status" });
            }
        }
    }

    public class CreateSearchHireDto
    {
        public int SearchId { get; set; }
        public int? ExpertId { get; set; }
    }
}