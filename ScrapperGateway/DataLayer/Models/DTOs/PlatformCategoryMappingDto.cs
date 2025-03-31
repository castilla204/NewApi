using System;

namespace DataLayer.Models.DTOs
{
    public class PlatformCategoryMappingDto
    {
        public int Id { get; set; }
        public int PlatformId { get; set; }
        public int CategoryId { get; set; }
        public string UrlParameter { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CreatePlatformCategoryMappingDto
    {
        public int PlatformId { get; set; }
        public int CategoryId { get; set; }
        public string UrlParameter { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class UpdatePlatformCategoryMappingDto
    {
        public int PlatformId { get; set; }
        public int CategoryId { get; set; }
        public string UrlParameter { get; set; }
        public bool IsActive { get; set; }
    }
}