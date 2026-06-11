using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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

        // 🛡️ Round 16: dependencias para email/password + OTP + Apple.
        private readonly IPasswordHashingService _passwordHasher;
        private readonly IEmailVerificationService _emailVerifier;
        private readonly IAppleAuthService _appleAuth;
        private readonly ILoggingService _logging;

        public AuthController(
            AppDbContext context,
            IConfiguration configuration,
            UserService userService,
            MfaService mfaService,
            IPasswordHashingService passwordHasher,
            IEmailVerificationService emailVerifier,
            IAppleAuthService appleAuth,
            ILoggingService logging)
        {
            _context = context;
            _configuration = configuration;
            _userService = userService;
            _mfaService = mfaService;
            _passwordHasher = passwordHasher;
            _emailVerifier = emailVerifier;
            _appleAuth = appleAuth;
            _logging = logging;
        }

        /// <summary>
        /// 🛡️ SEC-MAJ-5 FIX: setea el refresh token como cookie HttpOnly+Secure+SameSite=Strict
        /// con Path=/api/Auth. Antes el frontend lo guardaba en localStorage (~XSS-readable, 90d
        /// TTL → secuestro de sesión que sobrevive a reset de password). Con cookie httpOnly, el JS
        /// no puede leerlo: un XSS reflejado/stored o un script third-party comprometido (Stripe.js,
        /// Maps...) ya no puede exfiltrar el refresh.
        ///
        /// MIGRACIÓN BACK-COMPAT: durante este release, el endpoint AÚN devuelve refreshToken en el
        /// body para clientes legacy (mobile, builds anteriores). El frontend stops persisting al
        /// localStorage simultáneamente. Tras un ciclo (~1h, primer refresh) todos los users tendrán
        /// la cookie y el body se podrá retirar en R29.
        ///
        /// PENDIENTE FOLLOW-UP: los endpoints de login regular / Google / Apple (UserController)
        /// también deben llamar este helper para evitar el window post-login (1ª recarga después
        /// de signin donde sólo está en memoria). Aplicar el mismo patrón cuando se toquen.
        /// </summary>
        private void SetRefreshTokenCookie(string token, DateTime expiresAt)
        {
            var isHttps = HttpContext.Request.IsHttps ||
                HttpContext.Request.Headers["X-Forwarded-Proto"].ToString()
                    .Equals("https", StringComparison.OrdinalIgnoreCase);
            HttpContext.Response.Cookies.Append("refresh_token", token, new Microsoft.AspNetCore.Http.CookieOptions
            {
                HttpOnly = true,
                Secure = isHttps,
                SameSite = isHttps
                    ? Microsoft.AspNetCore.Http.SameSiteMode.Strict
                    : Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                Expires = expiresAt,
                // 🛡️ SEC-MAJ-5: scope mínimo. Sólo /api/Auth/refresh-token y /api/Auth/logout lo necesitan.
                Path = "/api/Auth"
            });
        }

        private void ClearRefreshTokenCookie()
        {
            HttpContext.Response.Cookies.Delete("refresh_token", new Microsoft.AspNetCore.Http.CookieOptions
            {
                Path = "/api/Auth",
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict
            });
        }

        /// <summary>
        /// ✅ SEGURIDAD 2025: Renovar Access Token usando Refresh Token
        /// Best Practice: Rotación de tokens (invalida el refresh token antiguo y genera uno nuevo)
        ///
        /// 🛡️ SEC-MAJ-5 FIX: lee el refresh token de la cookie HttpOnly `refresh_token` primero;
        /// fall back al body para clientes legacy (mobile/builds antiguos). En el response setea
        /// la cookie con el nuevo refresh — los frontends nuevos no persisten el body.
        /// </summary>
        [HttpPost("refresh-token")]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequestDto request)
        {
            try
            {
                // 🛡️ R29 FIX (fiabilidad): priorizar BODY sobre cookie — inversión deliberada de
                // SEC-MAJ-5. Los logins (Google/Apple/password) NO setean la cookie; solo este
                // endpoint y mfa/verify. Una cookie vieja de una sesión anterior (ya rotada o
                // revocada) ganaba al refresh vivo que el cliente envía en el body → caía en
                // reuse-detection → se revocaban TODAS las sesiones y el usuario quedaba en un
                // bucle de re-login. El body es el canal que todos los clientes actuales
                // mantienen al día; la cookie queda como fallback para cuando ningún body llega.
                var incomingRefresh = request?.RefreshToken;
                if (string.IsNullOrEmpty(incomingRefresh))
                {
                    incomingRefresh = HttpContext.Request.Cookies["refresh_token"];
                }
                if (string.IsNullOrEmpty(incomingRefresh))
                {
                    return BadRequest(new { message = "Refresh token requerido (cookie o body)" });
                }

                // 1. Buscar refresh token en BD
                var storedToken = await _context.RefreshTokens
                    .Include(rt => rt.User)
                    .FirstOrDefaultAsync(rt => rt.Token == incomingRefresh);

                // 2. Validaciones de seguridad
                if (storedToken == null)
                {
                    return Unauthorized(new { message = "Invalid refresh token" });
                }

                // 🛡️ T1 FIX: Usuario eliminado (soft-delete) - Revocar token y rechazar.
                // Sin esta validación, un atacante (o el propio ex-usuario) podría usar un
                // RefreshToken obtenido ANTES del DeleteAccount para renovar su AccessToken
                // y mantener sesión activa indefinidamente. AccountDeletionService.R1 borra
                // los RefreshTokens en el delete, pero hay race window: si el atacante hace
                // refresh entre Stripe.AccountDelete y el DELETE RefreshTokens, conserva la
                // sesión. Esta defensa cubre TODOS los casos.
                if (storedToken.User.IsDeleted)
                {
                    storedToken.IsRevoked = true;
                    storedToken.RevokedAt = DateTime.UtcNow;
                    storedToken.RevokedByIp = GetClientIpAddress();
                    await _context.SaveChangesAsync();
                    return Unauthorized(new { message = "User account has been deleted" });
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
                    // 🛡️ R29 FIX (fiabilidad): ventana de gracia para rotación concurrente.
                    // Dos pestañas (o un retry tras timeout donde el server SÍ procesó el primer
                    // intento) refrescan con el mismo token: el segundo llega con el token recién
                    // rotado. Antes se trataba como ataque y se revocaban TODAS las sesiones del
                    // usuario — incluida la recién creada y las de otros dispositivos. Ahora, si
                    // la rotación fue hace <60s y el reemplazo sigue activo, respondemos
                    // idempotentemente con el token de reemplazo: ambos clientes convergen a la
                    // misma cadena. Un token revocado SIN reemplazo (logout, bloqueo) o fuera de
                    // la ventana sigue tratándose como reuse → revocación total.
                    if (!string.IsNullOrEmpty(storedToken.ReplacedByToken) &&
                        storedToken.RevokedAt.HasValue &&
                        DateTime.UtcNow - storedToken.RevokedAt.Value < TimeSpan.FromSeconds(60))
                    {
                        var replacement = await _context.RefreshTokens
                            .FirstOrDefaultAsync(rt => rt.Token == storedToken.ReplacedByToken && rt.UserId == storedToken.UserId);

                        if (replacement != null && replacement.IsActive)
                        {
                            var graceAccessToken = GenerateJwtToken(storedToken.User);
                            SetRefreshTokenCookie(replacement.Token, replacement.ExpiresAt);
                            return Ok(new RefreshTokenResponseDto
                            {
                                AccessToken = graceAccessToken,
                                RefreshToken = replacement.Token,
                                AccessTokenExpiresAt = DateTime.UtcNow.AddHours(24),
                                RefreshTokenExpiresAt = replacement.ExpiresAt
                            });
                        }
                    }

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
                // 🛡️ Round 15 — R2 FIX: access TTL 1h uniforme con UserService.GenerateJwtToken
                // (era 30min aquí pero 1h en login → inconsistencia). 1h balancea seguridad y UX
                // de mobile (menos rotación → menos batería + menos llamadas).
                // 🛡️ MUD-AF: bumped a 24h (sync con UserService.GenerateJwtToken).
                var newAccessTokenExpiration = DateTime.UtcNow.AddHours(24);

                // 4. ✅ ROTACIÓN DE TOKENS: Revocar token actual y generar uno nuevo
                storedToken.IsRevoked = true;
                storedToken.RevokedAt = DateTime.UtcNow;
                storedToken.RevokedByIp = GetClientIpAddress();

                var newRefreshToken = new RefreshToken
                {
                    Token = GenerateSecureToken(),
                    UserId = user.Id,
                    // 🛡️ Round 15 — R2 FIX: 90 días (era 7d → cada rotación acortaba la sesión).
                    // El usuario se quejaba "no se mantenga su sesión, tiene que iniciar sesión
                    // todo el rato". Causa raíz: login emitía 30d pero AuthController.RefreshToken
                    // emitía 7d en cada rotación → tras 1ª rotación (en <1h) sesión cae a 7d.
                    // Ahora ambos emiten 90d (límite superior cómodo para mobile sin sacrificar
                    // seguridad — la rotación + detección de reuse cubre los riesgos).
                    ExpiresAt = DateTime.UtcNow.AddDays(90),
                    CreatedByIp = GetClientIpAddress(),
                    DeviceInfo = GetDeviceInfo()
                };

                storedToken.ReplacedByToken = newRefreshToken.Token;
                _context.RefreshTokens.Update(storedToken);
                _context.RefreshTokens.Add(newRefreshToken);

                await _context.SaveChangesAsync();

                // 🛡️ SEC-MAJ-5: setear cookie HttpOnly con el nuevo refresh. Frontends nuevos
                // ignoran el body.RefreshToken; los legacy lo siguen usando hasta que actualicen.
                SetRefreshTokenCookie(newRefreshToken.Token, newRefreshToken.ExpiresAt);

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

                // 🛡️ R29 FIX: body primero, cookie como fallback — mismo orden que refresh-token
                // (la cookie puede ser de una sesión anterior y revocaría el token equivocado).
                var incomingRefresh = request?.RefreshToken;
                if (string.IsNullOrEmpty(incomingRefresh))
                {
                    incomingRefresh = HttpContext.Request.Cookies["refresh_token"];
                }

                if (!string.IsNullOrEmpty(incomingRefresh))
                {
                    // Revocar el refresh token específico
                    var refreshToken = await _context.RefreshTokens
                        .FirstOrDefaultAsync(rt => rt.Token == incomingRefresh && rt.UserId == userId);

                    if (refreshToken != null && refreshToken.IsActive)
                    {
                        refreshToken.IsRevoked = true;
                        refreshToken.RevokedAt = DateTime.UtcNow;
                        refreshToken.RevokedByIp = GetClientIpAddress();

                        _context.RefreshTokens.Update(refreshToken);
                        await _context.SaveChangesAsync();
                    }
                }

                // 🛡️ SEC-MAJ-5: limpiar la cookie SIEMPRE, aunque no haya refresh válido — evita
                // residuos en el browser tras logout incompleto.
                ClearRefreshTokenCookie();

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
                // 🛡️ MUD-AF: bumped a 24h (sync con UserService.GenerateJwtToken).
                expires: DateTime.UtcNow.AddHours(24),
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
            // 🛡️ R29 FIX: IsActive es una propiedad computada NO mapeada — EF no puede
            // traducirla a SQL y esta query lanzaba InvalidOperationException SIEMPRE.
            // Consecuencia en prod: la detección de reuse devolvía 500 (y no revocaba nada),
            // y el frontend, ante un error no-401 del refresh, hacía logout() → sesión zombi.
            // Misma forma traducible que usa UserService.cs:119.
            var userTokens = await _context.RefreshTokens
                .Where(rt => rt.UserId == userId && !rt.IsRevoked && rt.ExpiresAt > DateTime.UtcNow)
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

                // 🛡️ SEC-MAJ-5: setear cookie HttpOnly en respuesta de MFA verify. Es el primer
                // momento en que tenemos un HttpContext + refresh recién generado para usuarios MFA.
                // Los usuarios sin MFA pasan por login regular (UserController) que aún devuelve
                // refresh en body — actualizar ese flujo en follow-up R28-followup.
                SetRefreshTokenCookie(refreshToken, DateTime.UtcNow.AddDays(90));

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

        #region 🛡️ Round 16: Email/Password + OTP Endpoints

        /// <summary>
        /// Inicia el flujo de registro: valida email/password, comprueba que el email no esté ya
        /// asociado a una cuenta activa, y envía un OTP de verificación. NO crea el User aún —
        /// la cuenta se crea tras verificar el OTP (POST /auth/verify-email).
        ///
        /// Anti-enumeración: la respuesta es 200 con un VerificationToken opaco SIEMPRE, exista
        /// o no el email. Si existe, el OTP se "envía" a un sink (no-op) y el token devuelto es fake.
        /// </summary>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequestDto request)
        {
            if (request == null)
                return BadRequest(new { message = "Datos inválidos." });

            var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
            var name = (request.Name ?? string.Empty).Trim();
            var password = request.Password ?? string.Empty;

            if (string.IsNullOrEmpty(email) || !IsValidEmail(email))
                return BadRequest(new { code = "invalid_email", message = "Introduce un correo válido." });

            if (string.IsNullOrEmpty(name) || name.Length < 2 || name.Length > 100)
                return BadRequest(new { code = "invalid_name", message = "El nombre debe tener entre 2 y 100 caracteres." });

            if (!_passwordHasher.ValidatePolicy(password, out var policyReason))
                return BadRequest(new { code = "weak_password", message = policyReason });

            // HIBP check (best-effort, network call con timeout 3s). Si el password está pwned > 100 veces
            // lo bloqueamos, < 100 solo loguea (no rompe UX por una falsa alerta).
            var hibpCount = await _passwordHasher.CheckHibpAsync(password);
            if (hibpCount >= 100)
                return BadRequest(new { code = "pwned_password", message = "Esta contraseña aparece en filtraciones públicas. Elige una diferente para tu seguridad." });

            // Lookup anti-enumeración: SIEMPRE devolvemos token aunque el email ya exista.
            var existing = await _context.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Email.ToLower() == email);

            bool shouldSendEmail = true;
            if (existing != null)
            {
                if (existing.IsBlocked || existing.IsDeleted)
                {
                    // Cuenta bloqueada/eliminada: no enviar OTP, devolver token fake.
                    shouldSendEmail = false;
                }
                else if (!string.IsNullOrEmpty(existing.Password))
                {
                    // Ya hay cuenta con password — no avisamos al frontend de que existe (anti-enum).
                    // Enviamos un email distinto avisando al dueño REAL que alguien intentó registrarse.
                    // Pero para no leakear info, no devolvemos error.
                    shouldSendEmail = false;
                    _ = _logging.LogWarningAsync(
                        message: "Intento de registro con email ya registrado",
                        details: $"Email={email} (cuenta existente, anti-enum: respuesta 200 con token fake)",
                        userId: existing.Id,
                        source: "AuthController.Register");
                }
                // Si el usuario existe SIN password (OAuth-only): le enviamos OTP para que pueda
                // añadir password a su cuenta existente — flujo "convert OAuth to email+password".
            }

            // Guardamos name + password (hash) en una blob temporal asociada al OTP. Como no tenemos
            // tabla auxiliar, usamos la columna User pero NO la crearemos hasta verify-email. La
            // alternativa más segura: poner el hash y nombre en el VerificationCode.RequestIp (mal),
            // o crear una tabla "PendingRegistration". Por simplicidad: stash en memoria via cache
            // con key=verificationToken.
            var passwordHash = _passwordHasher.Hash(password);
            var issue = await _emailVerifier.IssueAsync(
                email: email,
                purpose: EmailVerificationPurpose.EmailVerification,
                userId: existing?.Id,
                requestIp: GetClientIpAddress(),
                shouldSendEmail: shouldSendEmail);

            if (!issue.Success && shouldSendEmail)
            {
                // SMTP falló — no podemos enviar OTP. Devolvemos 503 visible al cliente
                // para que muestre error y el usuario pueda reintentar (no se queda colgado
                // en la pantalla de OTP esperando un email que nunca llegó).
                return StatusCode(503, new
                {
                    code = "email_send_failed",
                    message = issue.ErrorMessage ?? "No pudimos enviar el código por correo. Inténtalo de nuevo en unos segundos."
                });
            }

            // Stash en MemoryCache: { token → (name, passwordHash) }. TTL 15min (mayor que OTP TTL).
            // Si el OTP se verifica, leemos esto para crear el User. Si caduca, se pierde.
            _registrationCache.Set($"reg:{issue.VerificationToken}",
                new PendingRegistration(email, name, passwordHash),
                TimeSpan.FromMinutes(15));

            // linkedAccount=true cuando vamos a vincular password a una cuenta OAuth existente
            // (existe el user, no tiene password, no está bloqueado ni eliminado). El frontend
            // usa este flag para mostrar copy del tipo "Vincularemos tu cuenta de Google a una
            // contraseña" en lugar de "Te crearemos una cuenta nueva".
            bool linkedAccount = existing != null
                && string.IsNullOrEmpty(existing.Password)
                && !existing.IsBlocked
                && !existing.IsDeleted;

            // Siempre 200, anti-enum.
            return Ok(new
            {
                verificationToken = issue.VerificationToken,
                expiresAt = issue.ExpiresAt,
                linkedAccount,
                message = "Te hemos enviado un código de verificación a tu correo."
            });
        }

        /// <summary>
        /// Verifica el OTP de registro. Si correcto, crea el User con EmailVerified=true y
        /// devuelve access+refresh tokens (login automático).
        /// </summary>
        [HttpPost("verify-email")]
        public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailRequestDto request)
        {
            if (request == null || string.IsNullOrEmpty(request.VerificationToken) || string.IsNullOrEmpty(request.Code))
                return BadRequest(new { message = "Datos inválidos." });

            // 🛡️ FIX falso "código ya utilizado": transacción alrededor de verify + creación de
            // cuenta. Si CUALQUIER paso posterior al consumo del OTP falla, el consumo se
            // revierte y el usuario puede reintentar con el MISMO código (antes el código
            // quedaba "quemado" y el reintento devolvía "este código ya se ha utilizado").
            // EnableRetryOnFailure está activo → la transacción manual exige ExecutionStrategy.
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
                _context.Database.AutoSavepointsEnabled = false;
                await using var tx = await _context.Database.BeginTransactionAsync();

                var verify = await _emailVerifier.VerifyAsync(
                    request.VerificationToken,
                    request.Code,
                    GetClientIpAddress());

                if (!verify.Success)
                {
                    // Commit para persistir el contador de intentos fallidos (anti brute-force).
                    await tx.CommitAsync();
                    return BadRequest(new
                    {
                        code = "verification_failed",
                        message = verify.ErrorMessage ?? "Código inválido.",
                        attemptsRemaining = verify.AttemptsRemaining
                    });
                }

                // ─── Recuperar datos del registro pendiente ─────────────────────────────
                // "Reclamamos" la entrada de la cache (get + remove) para que una request
                // duplicada no cree el usuario dos veces; si algo falla después, la
                // reponemos en el catch para que el reintento del usuario funcione.
                var cacheKey = $"reg:{request.VerificationToken}";
                PendingRegistration? pending = null;
                if (_registrationCache.TryGetValue<PendingRegistration>(cacheKey, out var cachedPending) && cachedPending != null)
                {
                    pending = cachedPending;
                    _registrationCache.Remove(cacheKey);
                }

                try
                {
                    User? user;
                    bool wasLinked = false;

                    if (pending == null)
                    {
                        if (verify.Purpose != EmailVerificationPurpose.EmailVerification)
                        {
                            // OTP de otro flujo (p.ej. password reset enviado al endpoint equivocado).
                            // Rollback: NO consumimos el código, sigue siendo válido para su flujo real.
                            await tx.RollbackAsync();
                            return Ok(new { verifiedEmail = verify.Email, requiresProfile = false });
                        }

                        // Sin registro pendiente: o el OTP venía del flujo "login con email sin
                        // verificar" (nunca hubo pending), o el stash de registro caducó / el
                        // servidor se reinició entre register y verify-email.
                        user = verify.UserId.HasValue
                            ? await _context.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == verify.UserId.Value)
                            : await _context.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email.ToLower() == verify.Email!.ToLower());

                        if (user == null)
                        {
                            // Verificó el código pero ya no tenemos sus datos de registro → no
                            // podemos crear la cuenta. Error claro y accionable (antes se devolvía
                            // un falso éxito y la cuenta nunca llegaba a crearse).
                            await tx.RollbackAsync();
                            return BadRequest(new
                            {
                                code = "registration_expired",
                                message = "Tu sesión de registro ha caducado. Vuelve a empezar el registro para recibir un código nuevo."
                            });
                        }

                        if (user.IsBlocked) { await tx.RollbackAsync(); return Unauthorized(new { code = "account_blocked", message = "Cuenta bloqueada." }); }
                        if (user.IsDeleted) { await tx.RollbackAsync(); return Unauthorized(new { code = "account_deleted", message = "Cuenta eliminada." }); }

                        // 🛡️ FIX bucle "verifica tu email": este caso (login-password con email sin
                        // verificar) consumía el código pero NUNCA marcaba EmailVerified → el usuario
                        // quedaba en bucle infinito pidiendo códigos. Ahora marcamos y logueamos.
                        user.EmailVerified = true;
                        user.EmailVerifiedAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                    }
                    else
                    {
                        // ─── Crear o actualizar User (flujo registro) ───────────────────────
                        // Si existe usuario con ese email (caso OAuth-only añadiendo password):
                        var preExistingUser = await _context.Users
                            .IgnoreQueryFilters()
                            .FirstOrDefaultAsync(u => u.Email.ToLower() == pending.Email);

                        // Capturamos AHORA si era una vinculación (user OAuth sin password) — más abajo
                        // mutamos user.Password y perderíamos el estado original. El frontend usa este flag
                        // para mostrar "Contraseña vinculada" vs "Cuenta creada".
                        wasLinked = preExistingUser != null && string.IsNullOrEmpty(preExistingUser.Password);

                        user = preExistingUser;
                        if (user != null)
                        {
                            if (user.IsBlocked) { await tx.RollbackAsync(); return Unauthorized(new { code = "account_blocked", message = "Cuenta bloqueada." }); }
                            if (user.IsDeleted) { await tx.RollbackAsync(); return Unauthorized(new { code = "account_deleted", message = "Cuenta eliminada." }); }
                            // Solo añadimos password si NO tenía ya uno (no sobrescribir password ajeno).
                            if (string.IsNullOrEmpty(user.Password))
                            {
                                user.Password = pending.PasswordHash;
                                user.PasswordChangedAt = DateTime.UtcNow;
                            }
                            user.EmailVerified = true;
                            user.EmailVerifiedAt = DateTime.UtcNow;
                        }
                        else
                        {
                            // 🛡️ La columna Users.GoogleId/AppleId del esquema legacy es NOT NULL +
                            // UNIQUE aunque el modelo C# las declare nullable (deriva de
                            // migraciones). Para usuarios email-only, tokenizamos con un GUID
                            // prefijado — mismo patrón que AccountDeletionService al soft-delete.
                            // El prefijo "email:" no colisiona con `sub` reales de Google/Apple
                            // (siempre numéricos / hex), así que el lookup OAuth por GoogleId
                            // sigue funcionando.
                            var emailOnlyPlaceholder = $"email:{Guid.NewGuid():N}";
                            user = new User
                            {
                                Name = pending.Name,
                                Email = pending.Email,
                                Password = pending.PasswordHash,
                                GoogleId = emailOnlyPlaceholder,
                                AppleId = emailOnlyPlaceholder,
                                PasswordChangedAt = DateTime.UtcNow,
                                EmailVerified = true,
                                EmailVerifiedAt = DateTime.UtcNow,
                                CreatedAt = DateTime.UtcNow,
                                Role = UserRole.Client,
                            };
                            _context.Users.Add(user);
                            await _context.SaveChangesAsync();

                            _context.UserSettings.Add(new UserSetting
                            {
                                UserId = user.Id,
                                IsWhatsAppEnabled = true,
                                IsEmailEnabled = true,
                                Theme = "light",
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            });
                        }
                        await _context.SaveChangesAsync();
                    }

                    // ─── Emitir tokens (login automático) ──────────────────────────────────
                    var accessToken = _userService.GenerateJwtToken(user);
                    var refreshToken = await _userService.GenerateRefreshTokenAsync(user.Id, GetClientIpAddress());

                    await tx.CommitAsync();

                    // Logging FUERA de la transacción: un fallo del log no debe revertir el registro.
                    await _logging.LogInfoAsync(
                        message: "Registro email/password completado",
                        details: $"User {user.Id} ({user.Email}) verificó email y se registró.",
                        userId: user.Id,
                        source: "AuthController.VerifyEmail");

                    return Ok(new
                    {
                        token = $"{accessToken}|{refreshToken}",
                        accountAction = wasLinked ? "password_linked" : (pending == null ? "email_verified" : "account_created"),
                        user = new
                        {
                            id = user.Id,
                            name = user.Name,
                            email = user.Email,
                            role = user.Role.ToString(),
                            emailVerified = user.EmailVerified,
                        }
                    });
                }
                catch
                {
                    // Reponer el registro pendiente para que el reintento (con el mismo código,
                    // cuyo consumo también se revierte con el rollback) pueda completarse.
                    if (pending != null)
                        _registrationCache.Set(cacheKey, pending, TimeSpan.FromMinutes(15));
                    throw;
                }
            });
        }

        /// <summary>
        /// Reenvía OTP usando el VerificationToken activo. Respeta cooldown de 30s + rate limit.
        /// </summary>
        [HttpPost("resend-otp")]
        public async Task<IActionResult> ResendOtp([FromBody] ResendOtpRequestDto request)
        {
            if (request == null || string.IsNullOrEmpty(request.VerificationToken))
                return BadRequest(new { message = "Datos inválidos." });

            var result = await _emailVerifier.ResendAsync(request.VerificationToken, GetClientIpAddress());
            if (!result.Success)
            {
                return StatusCode(429, new
                {
                    message = result.ErrorMessage,
                    retryAfter = result.RetryAfterSeconds
                });
            }
            return Ok(new { verificationToken = result.VerificationToken, expiresAt = result.ExpiresAt });
        }

        /// <summary>
        /// Login con email + password. Si MFA está activado, devuelve mfaRequired=true en lugar de tokens.
        /// Bloqueo progresivo tras 10 intentos fallidos en 15min (LockedUntil).
        /// </summary>
        [HttpPost("login-password")]
        public async Task<IActionResult> LoginPassword([FromBody] LoginPasswordRequestDto request)
        {
            if (request == null || string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password))
                return BadRequest(new { message = "Email y contraseña son obligatorios." });

            var email = request.Email.Trim().ToLowerInvariant();
            // ✅ Tracking del intento por IP para auditoría
            var ip = GetClientIpAddress();

            var user = await _context.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Email.ToLower() == email);

            // 🛡️ Constant-time: incluso si el usuario NO existe, ejecutamos un BCrypt.Verify dummy
            // para que el atacante no pueda distinguir "email-existe" vs "email-no-existe" por timing.
            const string DummyHash = "$2a$12$abcdefghijklmnopqrstuv.WXYZabcdefghijklmnopqrstuvwx.AB";
            var passwordOk = false;
            if (user != null && !string.IsNullOrEmpty(user.Password))
            {
                passwordOk = _passwordHasher.Verify(request.Password, user.Password);
            }
            else
            {
                _passwordHasher.Verify(request.Password, DummyHash); // discard, solo para timing
            }

            // Caso 1: usuario no existe O password incorrecto → mensaje genérico anti-enum.
            if (user == null || !passwordOk)
            {
                // Si user existe, registramos intento fallido para activar lockout.
                if (user != null)
                {
                    user.FailedLoginAttempts++;
                    if (user.FailedLoginAttempts >= 10)
                    {
                        user.LockedUntil = DateTime.UtcNow.AddMinutes(15);
                        await _logging.LogWarningAsync(
                            message: "Cuenta bloqueada por intentos fallidos",
                            details: $"User {user.Id} bloqueado tras 10 intentos. IP={ip}",
                            userId: user.Id,
                            source: "AuthController.LoginPassword");
                    }
                    await _context.SaveChangesAsync();
                }
                return Unauthorized(new { code = "invalid_credentials", message = "Email o contraseña incorrectos." });
            }

            // Caso 2: usuario existe pero está bloqueado/eliminado.
            if (user.IsBlocked)
                return Unauthorized(new { code = "account_blocked", message = "Tu cuenta está bloqueada." });
            if (user.IsDeleted)
                return Unauthorized(new { code = "account_deleted", message = "Esta cuenta ya no existe." });

            // Caso 3: lockout activo.
            if (user.LockedUntil.HasValue && user.LockedUntil.Value > DateTime.UtcNow)
            {
                var remaining = (int)Math.Ceiling((user.LockedUntil.Value - DateTime.UtcNow).TotalMinutes);
                return Unauthorized(new
                {
                    code = "account_locked",
                    message = $"Demasiados intentos fallidos. Vuelve a intentarlo en {remaining} minutos."
                });
            }

            // Caso 4: email no verificado → forzar verificación antes de login.
            if (!user.EmailVerified)
            {
                // Emitir OTP automáticamente para que el frontend muestre la pantalla de verificación.
                var verify = await _emailVerifier.IssueAsync(
                    email: user.Email,
                    purpose: EmailVerificationPurpose.EmailVerification,
                    userId: user.Id,
                    requestIp: ip);
                return Ok(new
                {
                    code = "email_verification_required",
                    message = "Tu email aún no está verificado. Te hemos enviado un código.",
                    verificationToken = verify.VerificationToken,
                    expiresAt = verify.ExpiresAt
                });
            }

            // ─── Login OK ─────────────────────────────────────────────────────────
            user.FailedLoginAttempts = 0;
            user.LockedUntil = null;
            await _context.SaveChangesAsync();

            // ─── Si MFA está activada, devolvemos challenge en lugar de tokens ────
            var mfaStatus = await _mfaService.GetMfaStatusAsync(user.Id);
            if (mfaStatus != null && mfaStatus.IsEnabled)
            {
                // El frontend debe llamar a /api/Auth/mfa/verify con totpCode y userId.
                return Ok(new
                {
                    mfaRequired = true,
                    userId = user.Id,
                    message = "Introduce el código de tu app de autenticación."
                });
            }

            var accessToken = _userService.GenerateJwtToken(user);
            var refreshToken = await _userService.GenerateRefreshTokenAsync(user.Id, ip);
            return Ok(new
            {
                token = $"{accessToken}|{refreshToken}",
                user = new
                {
                    id = user.Id,
                    name = user.Name,
                    email = user.Email,
                    role = user.Role.ToString(),
                    emailVerified = user.EmailVerified
                }
            });
        }

        /// <summary>
        /// Inicia flujo "olvidé mi contraseña". Anti-enum: SIEMPRE devuelve 200 + token, exista o no el email.
        /// </summary>
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequestDto request)
        {
            if (request == null || string.IsNullOrEmpty(request.Email))
                return BadRequest(new { message = "Email obligatorio." });

            var email = request.Email.Trim().ToLowerInvariant();
            if (!IsValidEmail(email))
                return BadRequest(new { code = "invalid_email", message = "Email no válido." });

            var user = await _context.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Email.ToLower() == email);

            // shouldSend=true si user existe + no eliminado/bloqueado.
            // OJO: NO exigimos que tenga password. Esto permite a usuarios OAuth-only (Google/Apple/etc.)
            // usar este mismo flujo para AÑADIR una contraseña inicial a su cuenta. ResetPassword
            // se comporta igual en ambos casos: sobrescribe el hash (o lo crea si era null).
            bool shouldSend = user != null && !user.IsBlocked && !user.IsDeleted;
            var issue = await _emailVerifier.IssueAsync(
                email: email,
                purpose: EmailVerificationPurpose.PasswordReset,
                userId: user?.Id,
                requestIp: GetClientIpAddress(),
                shouldSendEmail: shouldSend);

            if (!issue.Success && shouldSend)
            {
                return StatusCode(503, new
                {
                    code = "email_send_failed",
                    message = issue.ErrorMessage ?? "No pudimos enviar el código. Inténtalo de nuevo en unos segundos."
                });
            }

            return Ok(new
            {
                verificationToken = issue.VerificationToken,
                expiresAt = issue.ExpiresAt,
                message = "Si el email está registrado, te enviaremos un código para restablecer o crear tu contraseña."
            });
        }

        /// <summary>
        /// Completa el flujo de reset: verifica OTP + cambia password + emite tokens (login automático).
        /// DUAL-PURPOSE: este endpoint sirve tanto para RESET (sobrescribir un hash existente) como
        /// para SET INICIAL (crear el primer hash de un usuario OAuth-only que se registró con
        /// Google/Apple/etc. y aún no tenía password). En ambos casos la lógica es idéntica:
        /// asignamos un nuevo hash a user.Password — no importa si antes era null o ya existía.
        /// </summary>
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequestDto request)
        {
            if (request == null
                || string.IsNullOrEmpty(request.VerificationToken)
                || string.IsNullOrEmpty(request.Code)
                || string.IsNullOrEmpty(request.NewPassword))
                return BadRequest(new { message = "Datos inválidos." });

            if (!_passwordHasher.ValidatePolicy(request.NewPassword, out var reason))
                return BadRequest(new { code = "weak_password", message = reason });

            // 🛡️ FIX falso "código ya utilizado": transacción alrededor de verify + cambio de
            // password. Si algo falla tras consumir el OTP, el consumo se revierte y el usuario
            // puede reintentar con el mismo código. ExecutionStrategy por EnableRetryOnFailure.
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
                _context.Database.AutoSavepointsEnabled = false;
                await using var tx = await _context.Database.BeginTransactionAsync();

                var verify = await _emailVerifier.VerifyAsync(
                    request.VerificationToken,
                    request.Code,
                    GetClientIpAddress());

                if (!verify.Success || verify.Purpose != EmailVerificationPurpose.PasswordReset)
                {
                    if (!verify.Success)
                    {
                        // Commit para persistir el contador de intentos fallidos (anti brute-force).
                        await tx.CommitAsync();
                    }
                    else
                    {
                        // Código correcto pero de OTRO flujo (p.ej. verificación de registro enviada
                        // aquí por error): rollback para no quemar el código de su flujo real.
                        await tx.RollbackAsync();
                    }
                    return BadRequest(new
                    {
                        code = "verification_failed",
                        message = verify.ErrorMessage ?? "Código inválido.",
                        attemptsRemaining = verify.AttemptsRemaining
                    });
                }

                var user = await _context.Users
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(u => u.Email.ToLower() == verify.Email!.ToLower());

                if (user == null || user.IsDeleted || user.IsBlocked)
                {
                    // No revelar — al usuario "honesto" no le pasa, al atacante no le damos info.
                    await tx.CommitAsync();
                    return Unauthorized(new { code = "verification_failed", message = "No se pudo completar la operación." });
                }

                // Asignación unconditional: si user.Password era null (OAuth-only) ahora pasa a tener
                // hash → el usuario podrá iniciar sesión también con email+password. Si ya tenía hash
                // previo, simplemente lo sobrescribimos (flujo de reset clásico).
                user.Password = _passwordHasher.Hash(request.NewPassword);
                user.PasswordChangedAt = DateTime.UtcNow;
                user.FailedLoginAttempts = 0;
                user.LockedUntil = null;
                await _context.SaveChangesAsync();

                // Revocar TODOS los refresh tokens activos del usuario — buena práctica tras cambio de password.
                await RevokeAllUserTokensAsync(user.Id, "password_reset");

                // Emitir tokens nuevos (login automático).
                var accessToken = _userService.GenerateJwtToken(user);
                var refreshToken = await _userService.GenerateRefreshTokenAsync(user.Id, GetClientIpAddress());

                await tx.CommitAsync();

                // Logging FUERA de la transacción: un fallo del log no debe revertir el reset.
                await _logging.LogInfoAsync(
                    message: "Password reset completado",
                    details: $"User {user.Id} ({user.Email}) restableció su password",
                    userId: user.Id,
                    source: "AuthController.ResetPassword");

                return Ok(new
                {
                    token = $"{accessToken}|{refreshToken}",
                    user = new { id = user.Id, name = user.Name, email = user.Email, role = user.Role.ToString() }
                });
            });
        }

        /// <summary>
        /// 🛡️ Round 16: Sign in with Apple — valida identityToken contra JWKS de Apple,
        /// crea/match User vía sub (AppleId), emite tokens.
        /// </summary>
        [HttpPost("apple-auth")]
        public async Task<IActionResult> AppleAuth([FromBody] AppleAuthRequestDto request)
        {
            if (request == null || string.IsNullOrEmpty(request.IdentityToken))
                return BadRequest(new { code = "invalid_request", message = "identityToken obligatorio." });

            var claims = await _appleAuth.ValidateIdentityTokenAsync(request.IdentityToken, request.Nonce);
            if (claims == null)
                return Unauthorized(new { code = "invalid_apple_token", message = "Token de Apple inválido." });

            var ip = GetClientIpAddress();

            // ─── Match por AppleId (sub) primero ────────────────────────────────────
            var user = await _context.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.AppleId == claims.Sub);

            // ─── Si no existe AppleId, intentar match por email (link de cuentas) ───
            // 🛡️ Guards: solo auto-linkamos si Apple confirma el email real (no proxy)
            // y si la cuenta existente NO tiene password (OAuth-only). Si tiene password,
            // forzamos al usuario a loguearse con ella primero — evita takeover por email.
            if (user == null
                && !string.IsNullOrEmpty(claims.Email)
                && claims.EmailVerified
                && !claims.IsPrivateEmail)
            {
                var emailLower = claims.Email.Trim().ToLowerInvariant();
                user = await _context.Users
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(u => u.Email.ToLower() == emailLower);

                if (user != null)
                {
                    if (user.IsBlocked) return Unauthorized(new { code = "account_blocked", message = "Cuenta bloqueada." });
                    if (user.IsDeleted) return Unauthorized(new { code = "account_deleted", message = "Cuenta eliminada." });
                    if (!string.IsNullOrEmpty(user.Password))
                    {
                        return Unauthorized(new
                        {
                            code = "account_has_password",
                            message = "Ya existe una cuenta con ese email. Inicia sesión con tu contraseña y vincula Apple desde tu perfil."
                        });
                    }
                    user.AppleId = claims.Sub;
                    user.EmailVerified = true; // Apple verifica
                    user.EmailVerifiedAt ??= DateTime.UtcNow;
                }
            }

            if (user != null)
            {
                if (user.IsBlocked) return Unauthorized(new { code = "account_blocked", message = "Cuenta bloqueada." });
                if (user.IsDeleted) return Unauthorized(new { code = "account_deleted", message = "Cuenta eliminada." });
            }
            else
            {
                // ─── Crear cuenta nueva ─────────────────────────────────────────────
                // El email puede ser proxy @privaterelay.appleid.com — guardarlo igual.
                // El name solo viene en el PRIMER login con Apple, vía request.FullName.
                var name = (request.FullName ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(name)) name = "Usuario Apple"; // fallback

                user = new User
                {
                    Name = name,
                    Email = (claims.Email ?? $"{claims.Sub}@appleid.local").Trim(),
                    AppleId = claims.Sub,
                    EmailVerified = claims.EmailVerified || true, // Apple siempre verifica
                    EmailVerifiedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    Role = UserRole.Client,
                };
                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                _context.UserSettings.Add(new UserSetting
                {
                    UserId = user.Id,
                    IsWhatsAppEnabled = true,
                    IsEmailEnabled = true,
                    Theme = "light",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            await _context.SaveChangesAsync();

            var accessToken = _userService.GenerateJwtToken(user);
            var refreshToken = await _userService.GenerateRefreshTokenAsync(user.Id, ip);

            return Ok(new
            {
                token = $"{accessToken}|{refreshToken}",
                user = new
                {
                    id = user.Id,
                    name = user.Name,
                    email = user.Email,
                    role = user.Role.ToString(),
                    emailVerified = user.EmailVerified
                }
            });
        }

        // ── Helpers / cache ─────────────────────────────────────────────────────────

        // Cache estática para datos pendientes de registro (TTL 15min, evita tabla extra).
        // Singleton process-wide. En multi-instancia, requeriría Redis — por ahora 1 instancia Render.
        private static readonly IMemoryCache _registrationCache =
            new MemoryCache(new MemoryCacheOptions());

        private record PendingRegistration(string Email, string Name, string PasswordHash);

        private static bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email && email.Length <= 320;
            }
            catch { return false; }
        }

        #endregion
    }

    // ── DTOs Round 16 ──────────────────────────────────────────────────────────────

    /// <summary>Registro con email/password.</summary>
    public class RegisterRequestDto
    {
        /// <summary>Email (se normaliza a lowercase).</summary>
        public string Email { get; set; } = string.Empty;
        /// <summary>Nombre completo del usuario.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Contraseña en plano (se hashea con BCrypt antes de guardar).</summary>
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>Verificación de OTP de email (final del registro o reset).</summary>
    public class VerifyEmailRequestDto
    {
        /// <summary>Token opaco devuelto por Register/ForgotPassword.</summary>
        public string VerificationToken { get; set; } = string.Empty;
        /// <summary>Código de 6 dígitos.</summary>
        public string Code { get; set; } = string.Empty;
    }

    /// <summary>Solicitud de reenvío de OTP.</summary>
    public class ResendOtpRequestDto
    {
        /// <summary>Token opaco del OTP previo.</summary>
        public string VerificationToken { get; set; } = string.Empty;
    }

    /// <summary>Login con email + password.</summary>
    public class LoginPasswordRequestDto
    {
        /// <summary>Email del usuario.</summary>
        public string Email { get; set; } = string.Empty;
        /// <summary>Contraseña en plano.</summary>
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>Inicio del flujo "olvidé contraseña".</summary>
    public class ForgotPasswordRequestDto
    {
        /// <summary>Email asociado a la cuenta.</summary>
        public string Email { get; set; } = string.Empty;
    }

    /// <summary>Reset de password con OTP + nueva contraseña.</summary>
    public class ResetPasswordRequestDto
    {
        /// <summary>Token opaco devuelto por ForgotPassword.</summary>
        public string VerificationToken { get; set; } = string.Empty;
        /// <summary>Código de 6 dígitos.</summary>
        public string Code { get; set; } = string.Empty;
        /// <summary>Nueva contraseña en plano.</summary>
        public string NewPassword { get; set; } = string.Empty;
    }

    /// <summary>Sign in with Apple — identityToken JWT firmado por Apple.</summary>
    public class AppleAuthRequestDto
    {
        /// <summary>JWT identityToken devuelto por Apple Sign In SDK.</summary>
        public string IdentityToken { get; set; } = string.Empty;
        /// <summary>authorizationCode (no usado en validación de identityToken pero recibido para futuro refresh con Apple).</summary>
        public string? AuthorizationCode { get; set; }
        /// <summary>Nonce literal que el cliente generó y envió en el auth request (anti-replay).</summary>
        public string? Nonce { get; set; }
        /// <summary>Nombre completo (solo viene en el PRIMER login con Apple — guardarlo en cuenta).</summary>
        public string? FullName { get; set; }
    }
}

