namespace newApi.DataLayer.Models.DTOs
{

    public class CreateSearchHireDto
    {
        public int SearchServiceId { get; set; }
        public string? SpecialRequirements { get; set; }
    }

    public class SearchHireResponseDto
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public int? ExpertId { get; set; }
        public int SearchServiceId { get; set; }
        public int? SearchId { get; set; }
        public string Status { get; set; }
        public string? ExpertTransferId { get; set; }
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public UserDto Client { get; set; }
        public UserDto? Expert { get; set; }
        public SearchServiceResponseDto Service { get; set; }
    }

    public class UpdateSearchHireStatusDto
    {
        public string Status { get; set; }
    }
}
