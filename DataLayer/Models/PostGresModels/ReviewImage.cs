using System;
using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class ReviewImage
    {
        [Key]
        public int Id { get; set; }
        public int ReviewId { get; set; }
        public string ImageUrl { get; set; }
        public string ImageObjectName { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Propiedad de navegación
        public virtual Review? Review { get; set; }
    }
}