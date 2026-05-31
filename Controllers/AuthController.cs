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
                var newAccessTokenExpiration = DateTime.UtcNow.AddHours(1);

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
                // 🛡️ Round 15 — R2 FIX: 1h uniforme con UserService.GenerateJwtToken.
                expires: DateTime.UtcNow.AddHours(1),
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

            // Siempre 200, anti-enum.
            return Ok(new
            {
                verificationToken = issue.VerificationToken,
                expiresAt = issue.ExpiresAt,
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

            var verify = await _emailVerifier.VerifyAsync(
                request.VerificationToken,
                request.Code,
                GetClientIpAddress());

            if (!verify.Success)
            {
                return BadRequest(new
                {
                    code = "verification_failed",
                    message = verify.ErrorMessage ?? "Código inválido.",
                    attemptsRemaining = verify.AttemptsRemaining
                });
            }

            // ─── Recuperar datos del registro pendiente ─────────────────────────────
            var cacheKey = $"reg:{request.VerificationToken}";
            if (!_registrationCache.TryGetValue<PendingRegistration>(cacheKey, out var pending) || pending == null)
            {
                // No hay registro pendiente — puede ser que el OTP fuera para password reset / step-up.
                // En ese caso devolvemos el email verificado y dejamos al frontend decidir qué hacer.
                return Ok(new { verifiedEmail = verify.Email, requiresProfile = false });
            }
            _registrationCache.Remove(cacheKey);

            // ─── Crear o actualizar User ─────────────────────────────────────────────
            // Si existe usuario con ese email (caso OAuth-only añadiendo password):
            var user = await _context.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Email.ToLower() == pending.Email);

            if (user != null)
            {
                if (user.IsBlocked) return Unauthorized(new { code = "account_blocked", message = "Cuenta bloqueada." });
                if (user.IsDeleted) return Unauthorized(new { code = "account_deleted", message = "Cuenta eliminada." });
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
                user = new User
                {
                    Name = pending.Name,
                    Email = pending.Email,
                    Password = pending.PasswordHash,
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

            // ─── Emitir tokens (login automático) ──────────────────────────────────
            var accessToken = _userService.GenerateJwtToken(user);
            var refreshToken = await _userService.GenerateRefreshTokenAsync(user.Id, GetClientIpAddress());

            await _logging.LogInfoAsync(
                message: "Registro email/password completado",
                details: $"User {user.Id} ({user.Email}) verificó email y se registró.",
                userId: user.Id,
                source: "AuthController.VerifyEmail");

            return Ok(new
            {
                token = $"{accessToken}|{refreshToken}",
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

            // shouldSend=true solo si user existe + tiene password + no eliminado/bloqueado.
            bool shouldSend = user != null && !user.IsBlocked && !user.IsDeleted && !string.IsNullOrEmpty(user.Password);
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
                message = "Si el email está registrado, te enviaremos un código de recuperación."
            });
        }

        /// <summary>
        /// Completa el flujo de reset: verifica OTP + cambia password + emite tokens (login automático).
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

            var verify = await _emailVerifier.VerifyAsync(
                request.VerificationToken,
                request.Code,
                GetClientIpAddress());

            if (!verify.Success || verify.Purpose != EmailVerificationPurpose.PasswordReset)
            {
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
                return Unauthorized(new { code = "verification_failed", message = "No se pudo completar la operación." });
            }

            user.Password = _passwordHasher.Hash(request.NewPassword);
            user.PasswordChangedAt = DateTime.UtcNow;
            user.FailedLoginAttempts = 0;
            user.LockedUntil = null;
            await _context.SaveChangesAsync();

            // Revocar TODOS los refresh tokens activos del usuario — buena práctica tras cambio de password.
            await RevokeAllUserTokensAsync(user.Id, "password_reset");

            await _logging.LogInfoAsync(
                message: "Password reset completado",
                details: $"User {user.Id} ({user.Email}) restableció su password",
                userId: user.Id,
                source: "AuthController.ResetPassword");

            // Emitir tokens nuevos (login automático).
            var accessToken = _userService.GenerateJwtToken(user);
            var refreshToken = await _userService.GenerateRefreshTokenAsync(user.Id, GetClientIpAddress());
            return Ok(new
            {
                token = $"{accessToken}|{refreshToken}",
                user = new { id = user.Id, name = user.Name, email = user.Email, role = user.Role.ToString() }
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
            if (user == null && !string.IsNullOrEmpty(claims.Email))
            {
                var emailLower = claims.Email.Trim().ToLowerInvariant();
                user = await _context.Users
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(u => u.Email.ToLower() == emailLower);

                if (user != null)
                {
                    if (user.IsBlocked) return Unauthorized(new { code = "account_blocked", message = "Cuenta bloqueada." });
                    if (user.IsDeleted) return Unauthorized(new { code = "account_deleted", message = "Cuenta eliminada." });
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

