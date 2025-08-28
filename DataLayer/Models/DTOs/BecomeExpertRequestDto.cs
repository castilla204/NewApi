using Microsoft.AspNetCore.Http;

namespace newApi.ScrapperGateway.DataLayer.Models.DTOs
{
    public class BecomeExpertRequestDto
    {
        public IFormFile ProfilePicture { get; set; }
        public string Description { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
    }

    public class UpdateExpertProfileRequestDto
    {
        public IFormFile? ProfilePicture { get; set; }
        public string Description { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
    }
}