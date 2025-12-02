using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using System.Security.Claims;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class LogController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;
        private readonly IAuthorizationServices _authService;

        public LogController(AppDbContext context, ILoggingService loggingService, IAuthorizationServices authService)
        {
            _context = context;
            _loggingService = loggingService;
            _authService = authService;
        }

        [HttpGet("types")]
        public async Task<IActionResult> GetLogTypes()
        {
            try
            {
                var logTypes = await _context.LogTypes
                    .Where(lt => lt.IsActive)
                    .OrderBy(lt => lt.Name)
                    .ThenBy(lt => lt.SeverityId)
                    .ToListAsync();

                return Ok(logTypes);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpGet("critical")]
        public async Task<IActionResult> GetCriticalLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                // Solo administradores pueden ver logs críticos
                if (!_authService.IsAdmin(User))
                {
                    return Unauthorized(new { message = "Admin access required" });
                }

                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                var query = _context.Logs
                    .Include(l => l.LogType)
                    .Include(l => l.User)
                    .Where(l => l.LogType != null && l.LogType.Name == "Critical");

                var totalCount = await query.CountAsync();

                var criticalLogs = await query
                    .OrderByDescending(l => l.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                return Ok(new
                {
                    logs = criticalLogs,
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

        [HttpPost("test-critical")]
        public async Task<IActionResult> TestCriticalLog()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Probar el sistema de logging crítico
                await _loggingService.LogCriticalAsync(
                    "Test critical log message",
                    "This is a test critical log entry",
                    userId,
                    "LogController.TestCriticalLog",
                    "Test",
                    123,
                    new { TestData = "This is test data", Timestamp = DateTime.UtcNow }
                );

                return Ok(new { message = "Critical log test completed successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost("test-error")]
        public async Task<IActionResult> TestErrorLog()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Probar el sistema de logging de error
                await _loggingService.LogErrorAsync(
                    "Test error log message",
                    "This is a test error log entry",
                    userId,
                    "LogController.TestErrorLog",
                    "Test",
                    456,
                    new { TestData = "This is test error data", Timestamp = DateTime.UtcNow }
                );

                return Ok(new { message = "Error log test completed successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}
