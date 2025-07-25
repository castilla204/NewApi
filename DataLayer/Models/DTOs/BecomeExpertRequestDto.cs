using Microsoft.AspNetCore.Http;

namespace newApi.ScrapperGateway.DataLayer.Models.DTOs
{
    public class BecomeExpertRequestDto
    {
        public IFormFile ProfilePicture { get; set; }
        public string Description { get; set; }
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
    }
}