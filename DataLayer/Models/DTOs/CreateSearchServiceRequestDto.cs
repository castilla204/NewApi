namespace newApi.DataLayer.Models.DTOs
{
    public class CreateSearchServiceRequestDto
    {
        public int ExpertProfileId { get; set; }
        public int CategoryId { get; set; }
        public int ServiceTypeId { get; set; } 
        public decimal Price { get; set; }
        public string Conditions { get; set; }
        public int DurationInHours { get; set; }
        public List<IFormFile> Images { get; set; } = new List<IFormFile>();
    }
}
