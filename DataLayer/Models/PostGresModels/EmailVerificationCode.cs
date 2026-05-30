using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace newApi.DataLayer.Models.PostGresModels
{
    public enum EmailVerificationPurpose
    {
        /// <summary>OTP enviado al registrar cuenta nueva (activación de email).</summary>
        EmailVerification = 1,
        /// <summary>OTP enviado en flujo "olvidé mi contraseña".</summary>
        PasswordReset = 2,
        /// <summary>OTP step-up para confirmar acciones sensibles (cambio email, etc.).</summary>
        StepUp = 3
    }

    /// <summary>
    /// 🛡️ Round 16: códigos OTP de un solo uso enviados por email para verificar la
    /// titularidad de la dirección de correo en flujos de registro / reset / step-up.
    ///
    /// Diseño (NIST SP 800-63B-4 + OWASP 2025):
    ///  - Código de 6 dígitos generado con RandomNumberGenerator (CSPRNG).
    ///  - Hash SHA-256(code + ':' + Salt) — NUNCA almacenar el código en plano.
    ///  - TTL 10 minutos (NIST SHALL ≤ 10 min para OOB OTP).
    ///  - Single-use: ConsumedAt se marca al primer match correcto.
    ///  - Máximo 5 intentos fallidos antes de invalidar y exigir reenvío.
    ///  - VerificationToken opaco devuelto al cliente para encadenar el siguiente paso
    ///    sin exponer el UserId ni necesitar email en el body (evita leakage).
    ///
    /// Anti-enumeración: el endpoint que envía OTP NUNCA revela si el email existe o no.
    /// La respuesta es siempre 200 con un VerificationToken aunque internamente no se
    /// haya enviado nada (cuenta inexistente, etc.).
    /// </summary>
    [Table("EmailVerificationCodes")]
    public class EmailVerificationCode
    {
        public int Id { get; set; }

        /// <summary>FK opcional. Null si el OTP se emitió para un email que aún no es User (pre-registro).</summary>
        public int? UserId { get; set; }
        public virtual User? User { get; set; }

        /// <summary>Email destinatario (lowercase, normalizado). Indexado.</summary>
        [Required]
        [MaxLength(320)] // RFC 5321: 64 local + @ + 255 domain
        public string Email { get; set; } = string.Empty;

        public EmailVerificationPurpose Purpose { get; set; }

        /// <summary>SHA-256(code + ':' + Salt). 32 bytes.</summary>
        [Required]
        public byte[] CodeHash { get; set; } = Array.Empty<byte>();

        /// <summary>16 bytes random por fila para evitar rainbow tables.</summary>
        [Required]
        public byte[] Salt { get; set; } = Array.Empty<byte>();

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>CreatedAt + 10 minutos por defecto.</summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>Contador de verificaciones fallidas sobre ESTE código.</summary>
        public int AttemptCount { get; set; } = 0;

        public int MaxAttempts { get; set; } = 5;

        /// <summary>NOT NULL = ya consumido (single-use). Bloquea reuso.</summary>
        public DateTime? ConsumedAt { get; set; }

        /// <summary>IP que solicitó el envío (auditoría/rate limit).</summary>
        [MaxLength(45)] // soporta IPv6
        public string? RequestIp { get; set; }

        /// <summary>IP que verificó el código (puede diferir de RequestIp = sospechoso).</summary>
        [MaxLength(45)]
        public string? VerifyIp { get; set; }

        /// <summary>
        /// Token opaco devuelto al cliente para encadenar al siguiente paso
        /// (verify-email → register, o reset → set new password) sin exponer UserId
        /// y sin requerir reenvío del email/code en el body.
        /// </summary>
        [Required]
        [MaxLength(64)]
        public string VerificationToken { get; set; } = string.Empty;
    }
}
