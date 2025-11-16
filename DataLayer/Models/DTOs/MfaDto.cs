using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para habilitar MFA
    /// </summary>
    public class EnableMfaRequestDto
    {
        [Required]
        public string TotpCode { get; set; } = string.Empty;
    }

    /// <summary>
    /// DTO para respuesta de habilitación de MFA
    /// </summary>
    public class EnableMfaResponseDto
    {
        public string QrCodeBase64 { get; set; } = string.Empty;
        public string ManualEntryKey { get; set; } = string.Empty;
        public List<string> RecoveryCodes { get; set; } = new();
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// DTO para verificar código MFA
    /// </summary>
    public class VerifyMfaRequestDto
    {
        [Required]
        [StringLength(6, MinimumLength = 6, ErrorMessage = "El código debe tener 6 dígitos")]
        public string Code { get; set; } = string.Empty;

        public bool IsRecoveryCode { get; set; } = false;
    }

    /// <summary>
    /// DTO para respuesta de verificación MFA
    /// </summary>
    public class VerifyMfaResponseDto
    {
        public bool IsValid { get; set; }
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public int? RemainingRecoveryCodes { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// DTO para deshabilitar MFA
    /// </summary>
    public class DisableMfaRequestDto
    {
        [Required]
        public string Password { get; set; } = string.Empty;

        [Required]
        public string TotpCode { get; set; } = string.Empty;
    }

    /// <summary>
    /// DTO para estado de MFA
    /// </summary>
    public class MfaStatusDto
    {
        public bool IsEnabled { get; set; }
        public bool IsRequired { get; set; }
        public DateTime? EnabledAt { get; set; }
        public DateTime? LastVerifiedAt { get; set; }
        public int RemainingRecoveryCodes { get; set; }
        public bool IsLocked { get; set; }
        public DateTime? LockedUntil { get; set; }
    }
}

