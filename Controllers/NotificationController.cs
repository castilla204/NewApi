namespace newApi.Controllers
{
    using global::newApi.ScrapperGateway.DataLayer.Models;
    using global::newApi.DataLayer.Models.PostGresModels;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.EntityFrameworkCore;
    using System.Security.Claims;
    using global::newApi.DataLayer.Models;

    namespace newApi.Controllers
    {
        [Route("api/[controller]")]
        [ApiController]
        [Authorize]
        public class NotificationController : ControllerBase
        {
            private readonly AppDbContext _context;
            private readonly ILogger<NotificationController> _logger;

            public NotificationController(AppDbContext context, ILogger<NotificationController> logger)
            {
                _context = context;
                _logger = logger;
            }

            [HttpGet]
            public async Task<IActionResult> GetUserNotifications()
            {
                try
                {
                    var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                    {
                        return Unauthorized(new { message = "Invalid user identification" });
                    }

                    var notifications = await _context.Notifications
                        .Where(n => n.UserId == userId || n.UserId == null)
                        .OrderByDescending(n => n.CreatedAt)
                        .ToListAsync();

                    return Ok(notifications);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error retrieving notifications");
                    return StatusCode(500, new { message = ex.Message });
                }
            }

            [HttpPost]
            [Authorize]
            public async Task<IActionResult> CreateNotification([FromBody] CreateNotificationDto request)
            {
                try
                {
                    // 🔐 SEGURIDAD: Verificar rol en lugar de email
                    if (!User.IsInRole("Admin"))
                    {
                        return Unauthorized(new { message = "Admin access required" });
                    }

                    var notification = new Notification
                    {
                        Title = request.Title,
                        Message = request.Message,
                        Type = request.Type,
                        UserId = request.UserId, // null for broadcast
                        CreatedAt = DateTime.UtcNow
                    };

                    await _context.Notifications.AddAsync(notification);
                    await _context.SaveChangesAsync();

                    return Ok(notification);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating notification");
                    return StatusCode(500, new { message = ex.Message });
                }
            }

            [HttpPut("{id}/read")]
            public async Task<IActionResult> MarkAsRead(Guid id)
            {
                try
                {
                    var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                    {
                        return Unauthorized(new { message = "Invalid user identification" });
                    }

                    var notification = await _context.Notifications.FindAsync(id);
                    if (notification == null)
                    {
                        return NotFound(new { message = "Notification not found" });
                    }

                    if (notification.UserId != userId && notification.UserId != null)
                    {
                        return Unauthorized(new { message = "Cannot mark other users' notifications as read" });
                    }

                    notification.Read = true;
                    notification.ReadAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();

                    return Ok(new { message = "Notification marked as read" });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error marking notification as read");
                    return StatusCode(500, new { message = ex.Message });
                }
            }

            [HttpDelete("{id}")]
            [Authorize]
            public async Task<IActionResult> DeleteNotification(Guid id)
            {
                try
                {
                    // 🔐 SEGURIDAD: Verificar rol en lugar de email
                    if (!User.IsInRole("Admin"))
                    {
                        return Unauthorized(new { message = "Admin access required" });
                    }

                    var notification = await _context.Notifications.FindAsync(id);
                    if (notification == null)
                    {
                        return NotFound(new { message = "Notification not found" });
                    }

                    _context.Notifications.Remove(notification);
                    await _context.SaveChangesAsync();

                    return NoContent();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting notification");
                    return StatusCode(500, new { message = ex.Message });
                }
            }
        }

        public class CreateNotificationDto
        {
            public string Title { get; set; }
            public string Message { get; set; }
            public string Type { get; set; }
            public int? UserId { get; set; }
        }
    }
}
