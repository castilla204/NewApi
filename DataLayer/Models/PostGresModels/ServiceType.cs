namespace newApi.DataLayer.Models.PostGresModels
{
    public class ServiceType
    {
        public int Id { get; set; }
        public string Name { get; set; } // e.g., "Web Search", "In-Person Review", "Web Search + In-Person Review"
        public string Description { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}