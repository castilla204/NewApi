using newApi.DataLayer.Models.PostGresModels;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para la respuesta de BecomeExpert
    /// </summary>
    public class BecomeExpertResponseDto
    {
        public string Message { get; set; }
        public string Token { get; set; }
        public UserInfoDto User { get; set; }
    }

    /// <summary>
    /// DTO para información del usuario en BecomeExpert
    /// </summary>
    public class UserInfoDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public bool PhoneVerified { get; set; }
        public string Role { get; set; }
        public ExpertProfileInfoDto ExpertProfile { get; set; }
    }

    /// <summary>
    /// DTO para información del perfil de experto en BecomeExpert
    /// </summary>
    public class ExpertProfileInfoDto
    {
        public int Id { get; set; }
        public string ProfilePictureUrl { get; set; }
        public string Description { get; set; }
        public string? StripeAccountId { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
        public StripeStatus StripeStatus { get; set; }
        public string? StripeStatusDetails { get; set; }
        public bool OnboardingCompleted { get; set; }
    }
}


