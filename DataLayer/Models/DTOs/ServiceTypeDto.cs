namespace newApi.DataLayer.Models.DTOs
{
    public class ServiceTypeDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int? ServiceTypeCategoryId { get; set; }
        public string? ServiceTypeCategoryName { get; set; }
        public int Position { get; set; }
        public bool IsActive { get; set; }
        public bool RequiresAppointment { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}


