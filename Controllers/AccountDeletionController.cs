using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using newApi.DataLayer.Models.DTOs;
using newApi.Services;
using System.Security.Claims;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize] // Requiere autenticación
    public class AccountDeletionController : ControllerBase
    {
        private readonly IAccountDeletionService _accountDeletionService;
        private readonly ILogger<AccountDeletionController> _logger;

        public AccountDeletionController(
            IAccountDeletionService accountDeletionService,
            ILogger<AccountDeletionController> logger)
        {
            _accountDeletionService = accountDeletionService;
            _logger = logger;
        }

        /// <summary>
        /// Verifica el estado de borrado de la cuenta del usuario autenticado
        /// </summary>
        [HttpGet("status")]
        public async Task<IActionResult> GetDeletionStatus()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var status = await _accountDeletionService.CheckDeletionStatusAsync(userId);
                return Ok(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking deletion status");
                return StatusCode(500, new { message = "Error checking deletion status", detail = ex.Message });
            }
        }

        /// <summary>
        /// Elimina la cuenta del usuario autenticado
        /// </summary>
        [HttpPost("delete")]
        public async Task<IActionResult> DeleteAccount([FromBody] AccountDeletionRequestDto request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                if (request == null)
                {
                    return BadRequest(new { message = "Request body is required" });
                }

                _logger.LogInformation("User {UserId} requesting account deletion with reason: {Reason}", userId, request.Reason);

                var result = await _accountDeletionService.DeleteAccountAsync(userId, request);

                if (result.Success)
                {
                    _logger.LogInformation("Account successfully deleted for user {UserId}", userId);
                    return Ok(result);
                }
                else
                {
                    _logger.LogWarning("Account deletion failed for user {UserId}: {Message}", userId, result.Message);
                    return BadRequest(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting account");
                return StatusCode(500, new { message = "Error deleting account", detail = ex.Message });
            }
        }

        /// <summary>
        /// Endpoint para administradores - eliminar cuenta de cualquier usuario
        /// </summary>
        [HttpPost("admin/delete/{userId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> AdminDeleteAccount(int userId, [FromBody] AccountDeletionRequestDto request)
        {
            try
            {
                var adminUserIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(adminUserIdClaim) || !int.TryParse(adminUserIdClaim, out int adminUserId))
                {
                    return Unauthorized(new { message = "Invalid admin identification" });
                }

                if (request == null)
                {
                    return BadRequest(new { message = "Request body is required" });
                }

                _logger.LogInformation("Admin {AdminUserId} requesting account deletion for user {UserId} with reason: {Reason}", 
                    adminUserId, userId, request.Reason);

                var result = await _accountDeletionService.DeleteAccountAsync(userId, request);

                if (result.Success)
                {
                    _logger.LogInformation("Account successfully deleted by admin {AdminUserId} for user {UserId}", adminUserId, userId);
                    return Ok(result);
                }
                else
                {
                    _logger.LogWarning("Account deletion failed by admin {AdminUserId} for user {UserId}: {Message}", 
                        adminUserId, userId, result.Message);
                    return BadRequest(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in admin account deletion");
                return StatusCode(500, new { message = "Error deleting account", detail = ex.Message });
            }
        }

        /// <summary>
        /// Endpoint para administradores - verificar estado de borrado de cualquier usuario
        /// </summary>
        [HttpGet("admin/status/{userId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> AdminGetDeletionStatus(int userId)
        {
            try
            {
                var status = await _accountDeletionService.CheckDeletionStatusAsync(userId);
                return Ok(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking deletion status for user {UserId}", userId);
                return StatusCode(500, new { message = "Error checking deletion status", detail = ex.Message });
            }
        }
    }
}
