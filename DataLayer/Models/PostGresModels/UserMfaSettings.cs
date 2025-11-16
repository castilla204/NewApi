using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// ✅ SEGURIDAD 2025: Configuración de MFA (Autenticación Multifactor) para usuarios
    /// Soporta TOTP (Google Authenticator, Microsoft Authenticator, etc.)
    /// </summary>
    [Table("UserMfaSettings")]
    public class UserMfaSettings
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        /// <summary>
        /// Indica si MFA está habilitado para este usuario
        /// </summary>
        [Required]
        public bool IsEnabled { get; set; } = false;

        /// <summary>
        /// Secreto TOTP cifrado (base32)
        /// ⚠️ CRÍTICO: Este valor debe estar cifrado en BD
        /// </summary>
        [Required]
        [MaxLength(500)]
        public string TotpSecret { get; set; } = string.Empty;

        /// <summary>
        /// Códigos de recuperación cifrados (JSON array)
        /// Formato: ["code1", "code2", ..., "code10"]
        /// </summary>
        [MaxLength(2000)]
        public string? RecoveryCodesEncrypted { get; set; }

        /// <summary>
        /// Número de códigos de recuperación usados
        /// </summary>
        public int RecoveryCodesUsed { get; set; } = 0;

        /// <summary>
        /// Fecha de habilitación de MFA
        /// </summary>
        public DateTime? EnabledAt { get; set; }

        /// <summary>
        /// Fecha de última verificación exitosa con MFA
        /// </summary>
        public DateTime? LastVerifiedAt { get; set; }

        /// <summary>
        /// Número de intentos fallidos consecutivos
        /// </summary>
        public int FailedAttempts { get; set; } = 0;

        /// <summary>
        /// Fecha hasta la cual el usuario está bloqueado (si excedió intentos)
        /// </summary>
        public DateTime? LockedUntil { get; set; }

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        // Navigation property
        public virtual User User { get; set; } = null!;

        // Computed properties
        public bool IsLocked => LockedUntil.HasValue && LockedUntil.Value > DateTime.UtcNow;
        public bool HasRecoveryCodes => !string.IsNullOrEmpty(RecoveryCodesEncrypted);
    }
}

