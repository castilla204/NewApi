namespace newApi.Controllers
{
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.EntityFrameworkCore;
    using System.Security.Claims;
    using global::newApi.ScrapperGateway.DataLayer.Models;

    namespace newApi.Controllers
    {
        [Route("api/[controller]")]
        [ApiController]
        [Authorize]
        public class UserSettingsController : ControllerBase
        {
            private readonly AppDbContext _context;
            private readonly ILogger<UserSettingsController> _logger;

            public UserSettingsController(AppDbContext context, ILogger<UserSettingsController> logger)
            {
                _context = context;
                _logger = logger;
            }

            [HttpGet]
            public async Task<IActionResult> GetSettings()
            {
                try
                {
                    var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                    {
                        return Unauthorized(new { message = "Invalid user identification" });
                    }

                    var settings = await _context.UserSettings.FirstOrDefaultAsync(us => us.UserId == userId);
                    if (settings == null)
                    {
                        return NotFound(new { message = "User settings not found" });
                    }

                    return Ok(new
                    {
                        isWhatsAppEnabled = settings.IsWhatsAppEnabled,
                        isEmailEnabled = settings.IsEmailEnabled,
                        theme = settings.Theme
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error retrieving user settings");
                    return StatusCode(500, new { message = "Failed to retrieve settings" });
                }
            }

            [HttpPut]
            public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsRequest request)
            {
                try
                {
                    var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                    {
                        return Unauthorized(new { message = "Invalid user identification" });
                    }

                    var settings = await _context.UserSettings.FirstOrDefaultAsync(us => us.UserId == userId);
                    if (settings == null)
                    {
                        return NotFound(new { message = "User settings not found" });
                    }

                    // Only update provided fields
                    if (request.IsWhatsAppEnabled.HasValue)
                        settings.IsWhatsAppEnabled = request.IsWhatsAppEnabled.Value;
                    if (request.IsEmailEnabled.HasValue)
                        settings.IsEmailEnabled = request.IsEmailEnabled.Value;
                    if (!string.IsNullOrEmpty(request.Theme))
                        settings.Theme = request.Theme;

                    settings.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();

                    return Ok(new
                    {
                        isWhatsAppEnabled = settings.IsWhatsAppEnabled,
                        isEmailEnabled = settings.IsEmailEnabled,
                        theme = settings.Theme
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating user settings");
                    return StatusCode(500, new { message = "Failed to update settings" });
                }
            }
        }

        public class UpdateSettingsRequest
        {
            public bool? IsWhatsAppEnabled { get; set; }
            public bool? IsEmailEnabled { get; set; }
            public string? Theme { get; set; }
        }
    }
}
