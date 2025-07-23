namespace newApi.DataLayer.Models.DTOs
{
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
