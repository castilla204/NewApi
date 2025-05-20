namespace newApi.DataLayer.Models.DTOs
{
    public class CreateReviewDto
    {
        public int Score { get; set; }
        public string Description { get; set; } = string.Empty;
        public IFormFile[]? Images { get; set; } // Explicitly nullable
    }

    public class ReviewResponseDto
    {
        public int Id { get; set; }
        public int ReviewerId { get; set; }
        public int ExpertId { get; set; }
        public int SearchHireId { get; set; }
        public int Score { get; set; }
        public string Description { get; set; } = string.Empty;
        public List<string> ImageUrls { get; set; } = new List<string>();
        public DateTime CreatedAt { get; set; }
    }
}
