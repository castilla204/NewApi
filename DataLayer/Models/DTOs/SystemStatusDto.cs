namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para mostrar información de un estado del sistema
    /// </summary>
    public class SystemStatusDto
    {
        public int Id { get; set; }
        public string StatusType { get; set; } = string.Empty;
        public string StatusName { get; set; } = string.Empty;
        public string StatusValue { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Color { get; set; }
        public bool IsActive { get; set; }
        public bool IsFinalizationStatus { get; set; }
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}



