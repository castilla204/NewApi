using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ Round 16: emisión y verificación de códigos OTP por email.
    ///
    /// Flujo:
    ///  1. POST /auth/register o /auth/forgot-password → IssueAsync genera código + envía email
    ///     + devuelve VerificationToken opaco al cliente.
    ///  2. Cliente captura el código en el modal y hace POST /auth/verify-email con (code, token).
    ///  3. VerifyAsync chequea hash + expiración + intentos + marca consumed.
    ///  4. Backend procede con el siguiente paso (crear cuenta, resetear password, etc.)
    ///     usando el VerificationToken como prueba de email-ownership.
    ///
    /// Garantías:
    ///  - Anti-enumeración: IssueAsync no diferencia email-existe / no-existe (siempre devuelve token).
    ///  - Constant-time hash comparison: FixedTimeEquals.
    ///  - Rate-limit por email+purpose: máx N envíos / ventana (anti-flood + protección SMTP cost).
    ///  - Single-use: ConsumedAt set en primer match → siguientes intentos fallan.
    ///  - Brute-force: 5 intentos por OTP, luego inválido (forzar reenvío).
    /// </summary>
    public interface IEmailVerificationService
    {
        /// <summary>
        /// Emite un nuevo OTP. Devuelve el VerificationToken opaco (NUNCA el código en plano).
        /// Si shouldSendEmail=false, no envía email (útil para pre-registro anti-enum cuando el email no existe).
        /// </summary>
        Task<EmailVerificationIssueResult> IssueAsync(
            string email,
            EmailVerificationPurpose purpose,
            int? userId,
            string? requestIp,
            bool shouldSendEmail = true,
            CancellationToken ct = default);

        /// <summary>
        /// Verifica el código contra el OTP referenciado por VerificationToken.
        /// Marca el código como consumido si el match es correcto.
        /// </summary>
        Task<EmailVerificationVerifyResult> VerifyAsync(
            string verificationToken,
            string code,
            string? verifyIp,
            CancellationToken ct = default);

        /// <summary>
        /// Reissue de OTP basado en un VerificationToken activo (botón "reenviar").
        /// Reusa el mismo token; sustituye el código y resetea AttemptCount/ExpiresAt.
        /// </summary>
        Task<EmailVerificationIssueResult> ResendAsync(
            string verificationToken,
            string? requestIp,
            CancellationToken ct = default);
    }

    /// <summary>Resultado de IssueAsync. Token opaco que el cliente debe devolver en Verify.</summary>
    public record EmailVerificationIssueResult(
        bool Success,
        string VerificationToken,
        DateTime ExpiresAt,
        string? ErrorMessage = null,
        int? RetryAfterSeconds = null);

    /// <summary>Resultado de VerifyAsync. Incluye email confirmado para que el caller proceda al next step.</summary>
    public record EmailVerificationVerifyResult(
        bool Success,
        string? Email = null,
        EmailVerificationPurpose? Purpose = null,
        int? UserId = null,
        int AttemptsRemaining = 0,
        string? ErrorMessage = null);

    /// <inheritdoc />
    public class EmailVerificationService : IEmailVerificationService
    {
        private readonly AppDbContext _db;
        private readonly INotificationService _notifications;
        private readonly ILogger<EmailVerificationService> _logger;

        // Parámetros de seguridad (NIST SP 800-63B-4):
        private const int CodeLength = 6;             // 6 dígitos = 10^6 = 1M combinaciones (NIST mín 6)
        private const int SaltLength = 16;            // 16 bytes random por código
        private const int TokenLength = 32;           // 32 bytes → 64 chars hex (entropía 256 bits)
        private const int DefaultTtlMinutes = 10;     // NIST SHALL ≤ 10 min para OOB OTP
        private const int MaxIssuesPerEmailPerWindow = 5;  // Máx 5 envíos OTP por email+purpose
        private const int IssueRateLimitWindowSeconds = 600; // En ventana de 10 min
        private const int ResendCooldownSeconds = 30;       // Mín 30s entre reenvíos

        public EmailVerificationService(
            AppDbContext db,
            INotificationService notifications,
            ILogger<EmailVerificationService> logger)
        {
            _db = db;
            _notifications = notifications;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<EmailVerificationIssueResult> IssueAsync(
            string email,
            EmailVerificationPurpose purpose,
            int? userId,
            string? requestIp,
            bool shouldSendEmail = true,
            CancellationToken ct = default)
        {
            email = NormalizeEmail(email);

            // ─── Rate-limit por email+purpose ─────────────────────────────────────────
            var windowStart = DateTime.UtcNow.AddSeconds(-IssueRateLimitWindowSeconds);
            var recentCount = await _db.EmailVerificationCodes
                .Where(c => c.Email == email && c.Purpose == purpose && c.CreatedAt >= windowStart)
                .CountAsync(ct);

            if (recentCount >= MaxIssuesPerEmailPerWindow)
            {
                _logger.LogWarning("OTP issue rate-limit hit for email={Email} purpose={Purpose}", email, purpose);
                return new EmailVerificationIssueResult(
                    Success: false,
                    VerificationToken: GenerateOpaqueToken(), // token "fake" para no leakear que hubo límite
                    ExpiresAt: DateTime.UtcNow.AddMinutes(DefaultTtlMinutes),
                    ErrorMessage: "Has solicitado demasiados códigos. Espera unos minutos antes de intentarlo de nuevo.",
                    RetryAfterSeconds: IssueRateLimitWindowSeconds);
            }

            // ─── Generar código + salt + hash ─────────────────────────────────────────
            var code = GenerateNumericCode(CodeLength);
            var salt = RandomNumberGenerator.GetBytes(SaltLength);
            var hash = HashCode(code, salt);
            var token = GenerateOpaqueToken();
            var expiresAt = DateTime.UtcNow.AddMinutes(DefaultTtlMinutes);

            var entity = new EmailVerificationCode
            {
                Email = email,
                Purpose = purpose,
                UserId = userId,
                CodeHash = hash,
                Salt = salt,
                VerificationToken = token,
                ExpiresAt = expiresAt,
                RequestIp = requestIp,
            };
            _db.EmailVerificationCodes.Add(entity);
            await _db.SaveChangesAsync(ct);

            // ─── Enviar email (puede estar deshabilitado para anti-enum en pre-registro) ──
            if (shouldSendEmail)
            {
                try
                {
                    await _notifications.SendVerificationCodeEmailAsync(email, code, purpose, DefaultTtlMinutes);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fallo al enviar OTP a {Email}", email);
                    // Devolvemos error para que el endpoint mande 503. El código queda en BD pero sin email enviado.
                    return new EmailVerificationIssueResult(
                        Success: false,
                        VerificationToken: token,
                        ExpiresAt: expiresAt,
                        ErrorMessage: "No pudimos enviar el correo en este momento. Intenta de nuevo en unos segundos.");
                }
            }

            return new EmailVerificationIssueResult(Success: true, VerificationToken: token, ExpiresAt: expiresAt);
        }

        /// <inheritdoc />
        public async Task<EmailVerificationVerifyResult> VerifyAsync(
            string verificationToken,
            string code,
            string? verifyIp,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(verificationToken) || string.IsNullOrWhiteSpace(code))
            {
                return new EmailVerificationVerifyResult(false, ErrorMessage: "Datos inválidos.");
            }

            var entity = await _db.EmailVerificationCodes
                .FirstOrDefaultAsync(c => c.VerificationToken == verificationToken, ct);

            if (entity == null)
            {
                _logger.LogInformation("Verify: token desconocido (ip={Ip})", verifyIp);
                return new EmailVerificationVerifyResult(false, ErrorMessage: "Código o sesión no válidos.");
            }

            if (entity.ConsumedAt.HasValue)
            {
                _logger.LogWarning("Verify: token ya consumido (id={Id}, ip={Ip})", entity.Id, verifyIp);
                return new EmailVerificationVerifyResult(false, ErrorMessage: "Este código ya ha sido utilizado.");
            }

            if (entity.ExpiresAt < DateTime.UtcNow)
            {
                return new EmailVerificationVerifyResult(false, ErrorMessage: "El código ha caducado. Solicita uno nuevo.");
            }

            if (entity.AttemptCount >= entity.MaxAttempts)
            {
                return new EmailVerificationVerifyResult(false, ErrorMessage: "Has agotado los intentos. Solicita un código nuevo.");
            }

            // Comparación constant-time del hash.
            var candidateHash = HashCode(code.Trim(), entity.Salt);
            var matches = CryptographicOperations.FixedTimeEquals(candidateHash, entity.CodeHash);

            if (!matches)
            {
                entity.AttemptCount++;
                entity.VerifyIp = verifyIp;
                await _db.SaveChangesAsync(ct);
                var remaining = Math.Max(0, entity.MaxAttempts - entity.AttemptCount);
                return new EmailVerificationVerifyResult(
                    Success: false,
                    AttemptsRemaining: remaining,
                    ErrorMessage: remaining > 0
                        ? $"Código incorrecto. Te quedan {remaining} intentos."
                        : "Has agotado los intentos. Solicita un código nuevo.");
            }

            // ─── Éxito: marcar consumido ──────────────────────────────────────────────
            entity.ConsumedAt = DateTime.UtcNow;
            entity.VerifyIp = verifyIp;
            await _db.SaveChangesAsync(ct);

            return new EmailVerificationVerifyResult(
                Success: true,
                Email: entity.Email,
                Purpose: entity.Purpose,
                UserId: entity.UserId);
        }

        /// <inheritdoc />
        public async Task<EmailVerificationIssueResult> ResendAsync(
            string verificationToken,
            string? requestIp,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(verificationToken))
            {
                return new EmailVerificationIssueResult(false, "", DateTime.UtcNow, "Datos inválidos.");
            }

            var entity = await _db.EmailVerificationCodes
                .FirstOrDefaultAsync(c => c.VerificationToken == verificationToken, ct);

            if (entity == null)
            {
                // Anti-enum: devolvemos OK con token fake.
                return new EmailVerificationIssueResult(
                    Success: true,
                    VerificationToken: verificationToken,
                    ExpiresAt: DateTime.UtcNow.AddMinutes(DefaultTtlMinutes));
            }

            if (entity.ConsumedAt.HasValue)
            {
                return new EmailVerificationIssueResult(false, verificationToken, entity.ExpiresAt, "Este código ya se ha usado.");
            }

            // Cooldown: bloquear reenvíos demasiado seguidos.
            var secondsSinceLast = (DateTime.UtcNow - entity.CreatedAt).TotalSeconds;
            if (secondsSinceLast < ResendCooldownSeconds)
            {
                var wait = (int)Math.Ceiling(ResendCooldownSeconds - secondsSinceLast);
                return new EmailVerificationIssueResult(
                    Success: false,
                    VerificationToken: verificationToken,
                    ExpiresAt: entity.ExpiresAt,
                    ErrorMessage: $"Espera {wait}s antes de pedir otro código.",
                    RetryAfterSeconds: wait);
            }

            // Rate-limit por email+purpose (igual que en IssueAsync).
            var windowStart = DateTime.UtcNow.AddSeconds(-IssueRateLimitWindowSeconds);
            var recentCount = await _db.EmailVerificationCodes
                .Where(c => c.Email == entity.Email && c.Purpose == entity.Purpose && c.CreatedAt >= windowStart)
                .CountAsync(ct);
            if (recentCount >= MaxIssuesPerEmailPerWindow)
            {
                return new EmailVerificationIssueResult(
                    Success: false,
                    VerificationToken: verificationToken,
                    ExpiresAt: entity.ExpiresAt,
                    ErrorMessage: "Has solicitado demasiados códigos. Espera unos minutos.",
                    RetryAfterSeconds: IssueRateLimitWindowSeconds);
            }

            // Generar código nuevo en la misma fila (resetea attempts).
            var code = GenerateNumericCode(CodeLength);
            var newSalt = RandomNumberGenerator.GetBytes(SaltLength);
            entity.CodeHash = HashCode(code, newSalt);
            entity.Salt = newSalt;
            entity.AttemptCount = 0;
            entity.CreatedAt = DateTime.UtcNow;
            entity.ExpiresAt = DateTime.UtcNow.AddMinutes(DefaultTtlMinutes);
            entity.RequestIp = requestIp;
            await _db.SaveChangesAsync(ct);

            try
            {
                await _notifications.SendVerificationCodeEmailAsync(entity.Email, code, entity.Purpose, DefaultTtlMinutes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo reenvío OTP a {Email}", entity.Email);
                return new EmailVerificationIssueResult(false, verificationToken, entity.ExpiresAt, "No pudimos enviar el correo. Intenta de nuevo.");
            }

            return new EmailVerificationIssueResult(true, verificationToken, entity.ExpiresAt);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────

        private static string NormalizeEmail(string email)
            => (email ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>
        /// 6 dígitos uniformemente distribuidos. Usamos RandomNumberGenerator (CSPRNG).
        /// Generamos un int en [0, 1_000_000) y formateamos a 6 dígitos con padding.
        /// </summary>
        private static string GenerateNumericCode(int digits)
        {
            // Generamos 4 bytes random, los reducimos módulo 10^digits.
            // Hay sesgo despreciable (~2^32 / 10^6 = ~4295 valores por slot, sesgo < 0.001%).
            var bytes = RandomNumberGenerator.GetBytes(4);
            uint value = BitConverter.ToUInt32(bytes, 0);
            uint modulus = (uint)Math.Pow(10, digits);
            uint code = value % modulus;
            return code.ToString().PadLeft(digits, '0');
        }

        /// <summary>SHA-256(code + ':' + salt).</summary>
        private static byte[] HashCode(string code, byte[] salt)
        {
            using var sha = SHA256.Create();
            var codeBytes = Encoding.UTF8.GetBytes(code);
            var separator = new byte[] { (byte)':' };
            var combined = new byte[codeBytes.Length + separator.Length + salt.Length];
            Buffer.BlockCopy(codeBytes, 0, combined, 0, codeBytes.Length);
            Buffer.BlockCopy(separator, 0, combined, codeBytes.Length, separator.Length);
            Buffer.BlockCopy(salt, 0, combined, codeBytes.Length + separator.Length, salt.Length);
            return sha.ComputeHash(combined);
        }

        /// <summary>32 bytes random → 64 chars hex. Entropía 256 bits.</summary>
        private static string GenerateOpaqueToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(TokenLength);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
