using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Refresh Token para renovación segura de Access Tokens
    /// Best Practice 2025: Rotación de tokens para máxima seguridad
    /// </summary>
    [Table("RefreshTokens")]
    public class RefreshToken
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [Required]
        [MaxLength(500)]
        public string Token { get; set; } = string.Empty;

        [Required]
        public DateTime ExpiresAt { get; set; }

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public bool IsRevoked { get; set; } = false;

        public DateTime? RevokedAt { get; set; }

        [MaxLength(100)]
        public string? RevokedByIp { get; set; }

        [Required]
        [MaxLength(100)]
        public string CreatedByIp { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? ReplacedByToken { get; set; }

        [MaxLength(500)]
        public string? DeviceInfo { get; set; }

        // Navigation property
        public virtual User User { get; set; } = null!;

        // Computed properties
        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
        public bool IsActive => !IsRevoked && !IsExpired;
    }
}

