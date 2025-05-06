namespace newApi.DataLayer.Models.DTOs
{
    public class SearchServiceDetailDto : SearchServiceResponseDto
    {
        public string CategoryName { get; set; }
        public int CompletedSearches { get; set; }
        public double AverageRating { get; set; }
    }
}
