namespace newApi.DataLayer.Models.DTOs
{
    public class SearchServiceResponseDto
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public decimal Price { get; set; }
        public string Conditions { get; set; }
        public int DurationInHours { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<string> ImageUrls { get; set; }
        public ExpertProfileDto Expert { get; set; }
    }
}
