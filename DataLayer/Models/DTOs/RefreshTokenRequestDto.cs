using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.DTOs
{
    public class RefreshTokenRequestDto
    {
        [Required(ErrorMessage = "Refresh token is required")]
        public string RefreshToken { get; set; } = string.Empty;
    }

    public class RefreshTokenResponseDto
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTime AccessTokenExpiresAt { get; set; }
        public DateTime RefreshTokenExpiresAt { get; set; }
    }

    public class LogoutRequestDto
    {
        public string? RefreshToken { get; set; }
    }
}

