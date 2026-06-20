using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
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
        private readonly IAccountDeletionReauthService _reauth;
        private readonly IEmailVerificationService _emailVerification;
        private readonly AppDbContext _db;

        public AccountDeletionController(
            IAccountDeletionService accountDeletionService,
            IAccountDeletionReauthService reauth,
            IEmailVerificationService emailVerification,
            AppDbContext db)
        {
            _accountDeletionService = accountDeletionService;
            _reauth = reauth;
            _emailVerification = emailVerification;
            _db = db;
        }

        private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error checking deletion status", errorCode = "DELETION_STATUS_ERROR" });
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

                // 🛡️ SEC-1 — Reautenticación obligatoria ANTES de cualquier mutación o movimiento
                // de dinero. Contraseña para cuentas con password; OTP step-up por email para OAuth.
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, HttpContext.RequestAborted);
                if (user == null)
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var reauth = await _reauth.VerifyAsync(user, request, ClientIp, HttpContext.RequestAborted);
                if (!reauth.Success)
                {
                    return BadRequest(new AccountDeletionResponseDto
                    {
                        Success = false,
                        Message = reauth.ErrorMessage ?? "Reautenticación requerida para eliminar la cuenta."
                    });
                }

                var result = await _accountDeletionService.DeleteAccountAsync(userId, request, cancellationToken: HttpContext.RequestAborted);

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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error deleting account", errorCode = "ACCOUNT_DELETION_ERROR" });
            }
        }

        /// <summary>
        /// 🛡️ SEC-1 — Solicita un código OTP (step-up) por email para confirmar el borrado.
        /// Sólo necesario para cuentas OAuth (sin contraseña). Para cuentas con contraseña
        /// devuelve requiresOtp=false (el cliente pedirá la contraseña en su lugar).
        /// </summary>
        [HttpPost("request-otp")]
        public async Task<IActionResult> RequestDeletionOtp()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, HttpContext.RequestAborted);
                if (user == null)
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Cuenta con contraseña → no se usa OTP; el cliente pedirá la contraseña.
                if (!string.IsNullOrEmpty(user.Password))
                {
                    return Ok(new { requiresOtp = false });
                }

                var issue = await _emailVerification.IssueAsync(
                    user.Email,
                    EmailVerificationPurpose.StepUp,
                    userId,
                    ClientIp,
                    shouldSendEmail: true,
                    ct: HttpContext.RequestAborted);

                return Ok(new
                {
                    requiresOtp = true,
                    success = issue.Success,
                    verificationToken = issue.VerificationToken,
                    expiresAt = issue.ExpiresAt,
                    message = issue.ErrorMessage
                });
            }
            catch (Exception ex)
            {
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error requesting verification code", errorCode = "DELETION_OTP_ERROR" });
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
                // 🛡️ B3: el endpoint admin SÍ puede forzar (salta solo guards blandos, nunca dispute/chargeback).
                var result = await _accountDeletionService.DeleteAccountAsync(userId, request, adminForce: true, adminUserId: adminUserId, cancellationToken: HttpContext.RequestAborted);

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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error deleting account", errorCode = "ACCOUNT_DELETION_ERROR" });
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error checking deletion status", errorCode = "DELETION_STATUS_ERROR" });
            }
        }
    }
}
