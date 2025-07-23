namespace newApi.DataLayer.Models.DTOs
{
    public class SearchHireDto
    {
        public int Id { get; set; }
        public int? ExpertId { get; set; }
        public string Status { get; set; }
        public UserDto? Expert { get; set; }
    }
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

    public class SearchServiceResponseDto
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public int ServiceTypeId { get; set; }
        public string ServiceTypeName { get; set; }
        public decimal Price { get; set; }
        public string Conditions { get; set; }
        public int DurationInHours { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<string> ImageUrls { get; set; }
        public ExpertProfileDto? Expert { get; set; }
    }

    public class SearchServiceDetailDto : SearchServiceResponseDto
    {
        public string CategoryName { get; set; }
        public int CompletedSearches { get; set; }
        public double AverageRating { get; set; }
    }

    public class ExpertProfileDto
    {
        public int Id { get; set; }
        public string ProfilePictureUrl { get; set; }
        public string Description { get; set; }
        public string? StripeAccountId { get; set; }
        public DateTime CreatedAt { get; set; }
        public UserDto User { get; set; }
        public List<ReviewDto> Reviews { get; set; }
    }

    public class UserDto
    {
        public string Email { get; set; }
        public string Name { get; set; }
        public string? ProfilePictureUrl { get; set; }
    }

    public class ReviewDto
    {
        public int Id { get; set; }
        public int Score { get; set; }
        public string Description { get; set; } // Changed from Comment to Description
        public DateTime CreatedAt { get; set; }
    }

    public class CreateSearchServiceRequestDto
    {
        public int ExpertProfileId { get; set; }
        public int CategoryId { get; set; }
        public int ServiceTypeId { get; set; }
        public decimal Price { get; set; }
        public string Conditions { get; set; }
        public int DurationInHours { get; set; }
        public List<IFormFile> Images { get; set; }
    }



}
