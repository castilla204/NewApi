using System.ComponentModel.DataAnnotations;

namespace DataLayer.Models.PostGresModels
{
    public class Category
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; }
        public int? ParentId { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation properties
        public virtual Category Parent { get; set; }
        public virtual ICollection<Category> Subcategories { get; set; }
        public virtual ICollection<PlatformCategoryMapping> PlatformCategoryMappings { get; set; }
    }
}