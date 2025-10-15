namespace newApi.DataLayer.Models.DTOs
{
    public class ExpertMapDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string ProfilePictureUrl { get; set; }
        public double AverageRating { get; set; }
        public int TotalReviews { get; set; }
        public int CompletedSearches { get; set; }
        public DateTime RegisteredSince { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
    }

    public class ExpertMapResponseDto
    {
        public List<ExpertMapDto> Experts { get; set; } = new List<ExpertMapDto>();
        public int TotalCount { get; set; }
    }
}
