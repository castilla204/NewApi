using System.ComponentModel.DataAnnotations;

namespace DataLayer.Models.PostGresModels
{
    public class PlatformCategoryMapping
    {
        [Key]
        public int Id { get; set; }
        public int PlatformId { get; set; }
        public int CategoryId { get; set; }
        public string UrlParameter { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation properties
        public virtual Platform Platform { get; set; }
        public virtual Category Category { get; set; }
    }
}