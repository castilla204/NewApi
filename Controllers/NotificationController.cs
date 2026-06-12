using global::newApi.ScrapperGateway.DataLayer.Models;
using global::newApi.DataLayer.Models.PostGresModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using global::newApi.DataLayer.Models;
using newApi.Services;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class NotificationController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IAuthorizationServices _authService;

        public NotificationController(AppDbContext context, IAuthorizationServices authService)
        {
        _context = context;
        _authService = authService;
        }

        /// <summary>
        /// Historial completo del sistema (solo administradores). Debe ir antes de rutas con {id}.
        /// </summary>
        [HttpGet("admin")]
        public async Task<IActionResult> GetAllNotificationsForAdmin([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                if (!_authService.IsAdmin(User))
                {
                    return Unauthorized(new { message = "Admin access required" });
                }

                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                var query = _context.Notifications.AsQueryable();
                var totalCount = await query.CountAsync();

                var notifications = await query
                    .OrderByDescending(n => n.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                return Ok(new
                {
                    notifications,
                    pagination = new
                    {
                        page,
                        pageSize,
                        totalCount,
                        totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                        hasNextPage = page * pageSize < totalCount,
                        hasPreviousPage = page > 1
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetUserNotifications([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                // ✅ Solo administradores pueden ver notificaciones globales (UserId == null)
                var isAdmin = _authService.IsAdmin(User);
                var query = _context.Notifications
                    .Where(n => n.UserId == userId || (n.UserId == null && isAdmin));

                var totalCount = await query.CountAsync();

                var notifications = await query
                    .OrderByDescending(n => n.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                return Ok(new
                {
                    notifications,
                    pagination = new
                    {
                        page,
                        pageSize,
                        totalCount,
                        totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                        hasNextPage = page * pageSize < totalCount,
                        hasPreviousPage = page > 1
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateNotification([FromBody] CreateNotificationDto request)
        {
            try
            {
                // ?? SEGURIDAD: Verificar rol en lugar de email
                if (!_authService.IsAdmin(User))
                {
                    return Unauthorized(new { message = "Admin access required" });
                }

                if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Message))
                {
                    return BadRequest(new { message = "Title and Message are required" });
                }

                var notification = new Notification
                {
                    Title = request.Title.Trim(),
                    Message = request.Message.Trim(),
                    Type = string.IsNullOrWhiteSpace(request.Type) ? "info" : request.Type.Trim(),
                    UserId = request.UserId, // null for broadcast
                    Url = string.IsNullOrWhiteSpace(request.Url) ? null : request.Url.Trim(),
                    ImageUrl = string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl.Trim(),
                    CreatedAt = DateTime.UtcNow
                };

                await _context.Notifications.AddAsync(notification);
                await _context.SaveChangesAsync();

                // 🔔 NOTIF-RT: trigger en tiempo real (best-effort; payload mínimo —
                // el contenido se lee por el endpoint autenticado).
                try
                {
                    var realtime = HttpContext.RequestServices.GetService<ISupabaseRealtimeService>();
                    if (realtime != null)
                    {
                        await realtime.NotifyUserNotificationAsync(notification.UserId, new { id = notification.Id, createdAt = notification.CreatedAt });
                    }
                }
                catch { /* hay polling de respaldo en el frontend */ }

                return Ok(notification);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("{id:guid}/read")]
        public async Task<IActionResult> MarkAsRead(Guid id)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var isAdmin = _authService.IsAdmin(User);
                var notification = await _context.Notifications.FindAsync(id);
                if (notification == null)
                {
                    return NotFound(new { message = "Notification not found" });
                }

                if (notification.UserId == null)
                {
                    if (!isAdmin)
                    {
                        return Unauthorized(new { message = "Admin access required to modify broadcast notifications" });
                    }
                }
                else if (notification.UserId != userId)
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
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /// <summary>
        /// Marca TODAS las notificaciones del usuario como leídas.
        /// Llamar cuando el usuario ABRE el panel de notificaciones para quitar el badge/alerta.
        /// </summary>
        [HttpPut("read-all")]
        public async Task<IActionResult> MarkAllAsRead()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var now = DateTime.UtcNow;
                
                // Marcar todas las notificaciones NO leídas del usuario como leídas
                var unreadNotifications = await _context.Notifications
                    .Where(n => n.UserId == userId && !n.Read)
                    .ToListAsync();

                if (unreadNotifications.Count == 0)
                {
                    return Ok(new { message = "No unread notifications", markedCount = 0 });
                }

                foreach (var notification in unreadNotifications)
                {
                    notification.Read = true;
                    notification.ReadAt = now;
                }

                await _context.SaveChangesAsync();

                return Ok(new { 
                    message = "All notifications marked as read", 
                    markedCount = unreadNotifications.Count 
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /// <summary>
        /// Obtiene el conteo de notificaciones NO leídas (para mostrar el badge/número en el icono).
        /// Llamar periódicamente o al cargar la app para actualizar el badge.
        /// </summary>
        [HttpGet("unread-count")]
        public async Task<IActionResult> GetUnreadCount()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var unreadCount = await _context.Notifications
                    .CountAsync(n => n.UserId == userId && !n.Read);

                return Ok(new { unreadCount });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpDelete("{id:guid}")]
        [Authorize]
        public async Task<IActionResult> DeleteNotification(Guid id)
        {
            try
            {
                // ?? SEGURIDAD: Verificar rol en lugar de email
                if (!_authService.IsAdmin(User))
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
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }

    public class CreateNotificationDto
    {
        public string? Title { get; set; }
        public string? Message { get; set; }
        public string? Type { get; set; }
        public int? UserId { get; set; }
        public string? Url { get; set; }
        public string? ImageUrl { get; set; }
    }
}
