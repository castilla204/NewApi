using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Tabla centralizada para todos los estados del sistema
    /// Reemplaza los enums hardcodeados por una solución flexible y configurable
    /// </summary>
    public class SystemStatus
    {
        public int Id { get; set; }
        
        [Required]
        [MaxLength(50)]
        public string StatusType { get; set; } = string.Empty; // "SearchHireStatus", "AppointmentStatus", etc.
        
        [Required]
        [MaxLength(50)]
        public string StatusName { get; set; } = string.Empty; // "Completed", "Cancelled", etc.
        
        [Required]
        [MaxLength(50)]
        public string StatusValue { get; set; } = string.Empty; // "completed", "cancelled", etc.
        
        [Required]
        [MaxLength(100)]
        public string DisplayName { get; set; } = string.Empty; // "Completado", "Cancelado", etc.
        
        [MaxLength(500)]
        public string? Description { get; set; } // Descripción del estado
        
        public bool IsActive { get; set; } = true;
        
        public bool IsFinalizationStatus { get; set; } = false; // Indica si es un estado de finalización donde se procesa dinero
        
        public int SortOrder { get; set; } = 0; // Para ordenar en UI
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public virtual ICollection<StatusMapping> SourceMappings { get; set; } = new List<StatusMapping>();
        public virtual ICollection<StatusMapping> TargetMappings { get; set; } = new List<StatusMapping>();
        public virtual ICollection<StatusConfiguration> StatusConfigurations { get; set; } = new List<StatusConfiguration>();
    }
}

