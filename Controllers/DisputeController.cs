using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class DisputeController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<DisputeController> _logger;

        public DisputeController(AppDbContext context, ILogger<DisputeController> logger)
        {
            _context = context;
            _logger = logger;
        }

        [HttpGet("details/{searchHireId}")]
        public async Task<IActionResult> GetDisputeDetails(int searchHireId)
        {
            _logger.LogInformation("GetDisputeDetails endpoint invoked for searchHireId={SearchHireId}", searchHireId);

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                    return NotFound(new { message = "Service not found" });
                }

                var isAdmin = User.FindFirst(ClaimTypes.Email)?.Value == "dcastillaa@gmail.com";
                if (!isAdmin && searchHire.ClientId != userId && searchHire.ExpertId != userId)
                {
                    _logger.LogError("User not authorized to view dispute details: userId={UserId}, searchHireId={SearchHireId}", userId, searchHireId);
                    return Unauthorized(new { message = "Not authorized to view dispute details" });
                }

                var dispute = await _context.Disputes
                    .FirstOrDefaultAsync(d => d.SearchHireId == searchHireId);

                if (dispute == null)
                {
                    _logger.LogError("No dispute found for searchHireId={SearchHireId}", searchHireId);
                    return NotFound(new { message = "No dispute found" });
                }

                // Check FinancialTransactions for a refund to determine if resolved in favor of client
                bool? resolvedInFavorOfClient = null;
                if (searchHire.Status == "dispute-resolved")
                {
                    var hasRefund = await _context.FinancialTransactions
                        .AnyAsync(ft => ft.RelatedEntityId == searchHireId &&
                                       ft.RelatedEntityType == "SearchHire" &&
                                       ft.TransactionType == "Refund" &&
                                       ft.UserId == searchHire.ClientId);
                    resolvedInFavorOfClient = hasRefund;
                }

                var disputeDetails = new DisputeDetailsDto
                {
                    SearchHireId = dispute.SearchHireId,
                    Reason = dispute.Reason,
                    Resolution = dispute.ResolutionComments,
                    Status = dispute.Status,
                    ResolvedInFavorOfClient = resolvedInFavorOfClient,
                    CreatedAt = dispute.CreatedAt
                };

                _logger.LogInformation("Successfully retrieved dispute details for searchHireId={SearchHireId}", searchHireId);
                var disputedetailsjson = JsonSerializer.Serialize(disputeDetails);
                return Ok(disputeDetails);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving dispute details for searchHireId={SearchHireId}", searchHireId);
                return StatusCode(500, new { message = "Failed to retrieve dispute details" });
            }
        }

        public class DisputeDetailsDto
        {
            public int SearchHireId { get; set; }
            public string Reason { get; set; }
            public string Resolution { get; set; }
            public string Status { get; set; }
            public bool? ResolvedInFavorOfClient { get; set; }
            public DateTime CreatedAt { get; set; }
        }
    }
}