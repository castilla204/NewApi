using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [EnableRateLimiting("auth")] // ✅ SEGURIDAD: 5 intentos cada 5 minutos por IP
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly UserService _userService;
        private readonly MfaService _mfaService;

        public AuthController(AppDbContext context, IConfiguration configuration, UserService userService, MfaService mfaService)
        {
            _context = context;
            _configuration = configuration;
            _userService = userService;
            _mfaService = mfaService;
        }

        /// <summary>
        /// ✅ SEGURIDAD 2025: Renovar Access Token usando Refresh Token
        /// Best Practice: Rotación de tokens (invalida el refresh token antiguo y genera uno nuevo)
        /// </summary>
        [HttpPost("refresh-token")]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequestDto request)
        {
            try
            {
                // 1. Buscar refresh token en BD
                var storedToken = await _context.RefreshTokens
                    .Include(rt => rt.User)
                    .FirstOrDefaultAsync(rt => rt.Token == request.RefreshToken);

                // 2. Validaciones de seguridad
                if (storedToken == null)
                {
                    return Unauthorized(new { message = "Invalid refresh token" });
                }

                // ✅ VALIDACIÓN: Usuario bloqueado - Revocar token y rechazar
                if (storedToken.User.IsBlocked)
                {
                    storedToken.IsRevoked = true;
                    storedToken.RevokedAt = DateTime.UtcNow;
                    storedToken.RevokedByIp = GetClientIpAddress();
                    await _context.SaveChangesAsync();
                    return Unauthorized(new { message = "User account is blocked" });
                }

                if (storedToken.IsRevoked)
                {
                    // ✅ SEGURIDAD: Token ya usado (posible ataque) - revocar todos los tokens del usuario
                    await RevokeAllUserTokensAsync(storedToken.UserId, "Token reuse detected");
                    return Unauthorized(new { message = "Token revoked. All sessions have been terminated for security." });
                }

                if (storedToken.IsExpired)
                {
                    return Unauthorized(new { message = "Refresh token expired. Please log in again." });
                }

                // 3. Generar nuevo Access Token
                var user = storedToken.User;
                var newAccessToken = GenerateJwtToken(user);
                var newAccessTokenExpiration = DateTime.UtcNow.AddMinutes(30);

                // 4. ✅ ROTACIÓN DE TOKENS: Revocar token actual y generar uno nuevo
                storedToken.IsRevoked = true;
                storedToken.RevokedAt = DateTime.UtcNow;
                storedToken.RevokedByIp = GetClientIpAddress();

                var newRefreshToken = new RefreshToken
                {
                    Token = GenerateSecureToken(),
                    UserId = user.Id,
                    ExpiresAt = DateTime.UtcNow.AddDays(7),
                    CreatedByIp = GetClientIpAddress(),
                    DeviceInfo = GetDeviceInfo()
                };

                storedToken.ReplacedByToken = newRefreshToken.Token;
                _context.RefreshTokens.Update(storedToken);
                _context.RefreshTokens.Add(newRefreshToken);

                await _context.SaveChangesAsync();

                // 5. Devolver nuevos tokens
                return Ok(new RefreshTokenResponseDto
                {
                    AccessToken = newAccessToken,
                    RefreshToken = newRefreshToken.Token,
                    AccessTokenExpiresAt = newAccessTokenExpiration,
                    RefreshTokenExpiresAt = newRefreshToken.ExpiresAt
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error refreshing token", error = ex.Message });
            }
        }

        /// <summary>
        /// ✅ SEGURIDAD 2025: Logout seguro (revoca el refresh token)
        /// </summary>
        [HttpPost("logout")]
        [Authorize]
        public async Task<IActionResult> Logout([FromBody] LogoutRequestDto request)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid user" });
                }

                if (!string.IsNullOrEmpty(request.RefreshToken))
                {
                    // Revocar el refresh token específico
                    var refreshToken = await _context.RefreshTokens
                        .FirstOrDefaultAsync(rt => rt.Token == request.RefreshToken && rt.UserId == userId);

                    if (refreshToken != null && refreshToken.IsActive)
                    {
                        refreshToken.IsRevoked = true;
                        refreshToken.RevokedAt = DateTime.UtcNow;
                        refreshToken.RevokedByIp = GetClientIpAddress();

                        _context.RefreshTokens.Update(refreshToken);
                        await _context.SaveChangesAsync();
                    }
                }

                return Ok(new { message = "Logged out successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error during logout", error = ex.Message });
            }
        }

        /// <summary>
        /// ✅ SEGURIDAD: Revocar todos los tokens del usuario (en caso de compromiso)
        /// </summary>
        [HttpPost("revoke-all")]
        [Authorize]
        public async Task<IActionResult> RevokeAllTokens()
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid user" });
                }

                await RevokeAllUserTokensAsync(userId, "User requested revocation");

                return Ok(new { message = "All sessions terminated successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error revoking tokens", error = ex.Message });
            }
        }

        #region Private Methods

        private string GenerateJwtToken(User user)
        {
            var roleName = user.Role switch
            {
                UserRole.Client => "Client",
                UserRole.Expert => "Expert",
                UserRole.Admin => "Admin",
                _ => "Client"
            };

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.Name),
                new Claim(ClaimTypes.Role, roleName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(_configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not found")));

            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(30),
                notBefore: DateTime.UtcNow,
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private string GenerateSecureToken()
        {
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            var randomBytes = new byte[64];
            rng.GetBytes(randomBytes);
            return Convert.ToBase64String(randomBytes);
        }

        private async Task RevokeAllUserTokensAsync(int userId, string reason)
        {
            var userTokens = await _context.RefreshTokens
                .Where(rt => rt.UserId == userId && rt.IsActive)
                .ToListAsync();

            foreach (var token in userTokens)
            {
                token.IsRevoked = true;
                token.RevokedAt = DateTime.UtcNow;
                token.RevokedByIp = $"{GetClientIpAddress()} - Reason: {reason}";
            }

            _context.RefreshTokens.UpdateRange(userTokens);
            await _context.SaveChangesAsync();
        }

        private string GetClientIpAddress()
        {
            return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        private string? GetDeviceInfo()
        {
            return HttpContext.Request.Headers.UserAgent.ToString();
        }

        #endregion

        #region MFA (Multi-Factor Authentication) Endpoints

        /// <summary>
        /// ✅ SEGURIDAD 2025: Configurar MFA (paso 1 - obtener QR code)
        /// </summary>
        [HttpPost("mfa/setup")]
        [Authorize]
        public async Task<IActionResult> SetupMfa()
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                // ✅ FIX: Guardar secreto temporalmente (sin habilitar MFA aún)
                var (secret, qrCodeBase64, manualEntryKey) = await _mfaService.SaveTotpSecretForSetupAsync(userId);

                return Ok(new
                {
                    qrCodeBase64,
                    manualEntryKey,
                    message = "Scan the QR code with your authenticator app (Google Authenticator, Microsoft Authenticator, etc.) or enter the key manually. Then call /mfa/enable with the 6-digit code."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error setting up MFA", detail = ex.Message });
            }
        }

        /// <summary>
        /// ✅ SEGURIDAD 2025: Habilitar MFA (paso 2 - confirmar con código TOTP)
        /// </summary>
        [HttpPost("mfa/enable")]
        [Authorize]
        public async Task<IActionResult> EnableMfa([FromBody] EnableMfaRequestDto request)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                var result = await _mfaService.EnableMfaAsync(userId, request.TotpCode);

                return Ok(new
                {
                    success = true,
                    qrCodeBase64 = result.QrCodeBase64,
                    manualEntryKey = result.ManualEntryKey,
                    recoveryCodes = result.RecoveryCodes,
                    message = "⚠️ IMPORTANT: Save these recovery codes in a safe place! Each code can only be used once."
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error enabling MFA", detail = ex.Message });
            }
        }

        /// <summary>
        /// ✅ SEGURIDAD 2025: Verificar código MFA durante login
        /// </summary>
        [HttpPost("mfa/verify")]
        [Authorize]
        public async Task<IActionResult> VerifyMfa([FromBody] VerifyMfaRequestDto request)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                var (isValid, message) = await _mfaService.VerifyMfaCodeAsync(userId, request.Code, request.IsRecoveryCode);

                if (!isValid)
                {
                    return Unauthorized(new { message = message ?? "Invalid MFA code" });
                }

                // Generar nuevo token después de verificación exitosa
                var user = await _context.Users.FindAsync(userId);
                if (user == null)
                {
                    return Unauthorized(new { message = "User not found" });
                }

                // ✅ VALIDACIÓN: Usuario bloqueado
                if (user.IsBlocked)
                {
                    return Unauthorized(new { message = "User account is blocked" });
                }

                var accessToken = _userService.GenerateJwtToken(user);
                var refreshToken = await _userService.GenerateRefreshTokenAsync(userId, GetClientIpAddress());

                return Ok(new VerifyMfaResponseDto
                {
                    IsValid = true,
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    Message = message
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error verifying MFA code", detail = ex.Message });
            }
        }

        /// <summary>
        /// ✅ SEGURIDAD 2025: Deshabilitar MFA (solo requiere código TOTP)
        /// ✅ CORRECCIÓN: Eliminado parámetro password - todos los usuarios usan Google OAuth
        /// </summary>
        [HttpPost("mfa/disable")]
        [Authorize]
        public async Task<IActionResult> DisableMfa([FromBody] DisableMfaRequestDto request)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                var success = await _mfaService.DisableMfaAsync(userId, request.TotpCode);

                if (!success)
                {
                    return BadRequest(new { message = "Invalid TOTP code" });
                }

                return Ok(new { message = "MFA disabled successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error disabling MFA", detail = ex.Message });
            }
        }

        /// <summary>
        /// ✅ SEGURIDAD 2025: Obtener estado de MFA del usuario
        /// </summary>
        [HttpGet("mfa/status")]
        [Authorize]
        public async Task<IActionResult> GetMfaStatus()
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                var status = await _mfaService.GetMfaStatusAsync(userId);

                return Ok(status);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error getting MFA status", detail = ex.Message });
            }
        }

        #endregion
    }
}

