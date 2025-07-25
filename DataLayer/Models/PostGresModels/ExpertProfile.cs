namespace newApi.DataLayer.Models.PostGresModels
{
    public class ExpertProfile
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string ProfilePictureUrl { get; set; }
        public string ProfilePictureObjectName { get; set; }
        public string Description { get; set; }
        public string? StripeAccountId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public virtual User User { get; set; }
        public virtual ICollection<newApi.DataLayer.Models.PostGresModels.SearchService> SearchServices { get; set; }
    }
}