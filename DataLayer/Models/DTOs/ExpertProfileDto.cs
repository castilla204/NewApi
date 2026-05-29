namespace newApi.DataLayer.Models.DTOs
{
    // 🛡️ NOTA: clase legacy con typo en el nombre (ExpertProfileDtoooo). NO usada por los flujos
    // principales — el DTO real es ExpertProfileDto en SearchHireDto.cs que SÍ tiene StripeStatus
    // y OnboardingCompleted. Mantener esta clase intacta para no romper consumers legacy.
    public class ExpertProfileDtoooo
    {
        public int Id { get; set; }
        public string ProfilePictureUrl { get; set; }
        public string Description { get; set; }
        public string? StripeAccountId { get; set; }
        public DateTime CreatedAt { get; set; }
        public UserDto User { get; set; }
    }
}
