using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace newApi.ScrapperGateway.DataLayer.Models.DTOs
{

    namespace newApi.ScrapperGateway.DataLayer.Models.DTOs
    {
        public class BecomeExpertRequestDto
        {
            public IFormFile ProfilePicture { get; set; }
            public string Description { get; set; }
            public string StripeAccountId { get; set; }
        }
    }
}
