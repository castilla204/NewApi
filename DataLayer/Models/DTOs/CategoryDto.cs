using System;

namespace newApi.DataLayer.Models.DTOs
{
    public class CategoryDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int? ParentId { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CategoryWithDetailsDto : CategoryDto
    {
        public bool IsParent { get; set; }
        public bool HasSubcategories { get; set; }
        public int SubcategoriesCount { get; set; }
    }

    public class ParentCategoryDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int SubcategoriesCount { get; set; }
    }

    public class CreateCategoryDto
    {
        public string Name { get; set; }
        public int? ParentId { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class UpdateCategoryDto
    {
        public string Name { get; set; }
        public int? ParentId { get; set; }
        public bool IsActive { get; set; }
    }
}