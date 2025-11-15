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
        public AccountDeletionController(IAccountDeletionService accountDeletionService)
        {
            _accountDeletionService = accountDeletionService;
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

                var status = await _accountDeletionService.CheckDeletionStatusAsync(userId, HttpContext.RequestAborted);
                return Ok(status);
            }
            catch (Exception ex)
            {
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
                var result = await _accountDeletionService.DeleteAccountAsync(userId, request, HttpContext.RequestAborted);

                if (result.Success)
                {
                    return Ok(result);
                }
                else
                {
                    return BadRequest(result);
                }
            }
            catch (Exception ex)
            {
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
                var result = await _accountDeletionService.DeleteAccountAsync(userId, request, HttpContext.RequestAborted);

                if (result.Success)
                {
                    return Ok(result);
                }
                else
                {
                    return BadRequest(result);
                }
            }
            catch (Exception ex)
            {
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
                var status = await _accountDeletionService.CheckDeletionStatusAsync(userId, HttpContext.RequestAborted);
                return Ok(status);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error checking deletion status", detail = ex.Message });
            }
        }
    }
}
