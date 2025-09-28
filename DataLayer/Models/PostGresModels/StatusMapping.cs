using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Mapeo entre estados del sistema (ej: AppointmentStatus → SearchHireStatus)
    /// Permite configurar dinámicamente las transiciones de estado
    /// </summary>
    public class StatusMapping
    {
        public int Id { get; set; }
        
        [Required]
        public int SourceStatusId { get; set; } // Estado origen (ej: AppointmentStatus)
        
        [Required]
        public int TargetStatusId { get; set; } // Estado destino (ej: SearchHireStatus)
        
        public bool IsActive { get; set; } = true;
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public virtual SystemStatus SourceStatus { get; set; } = null!;
        public virtual SystemStatus TargetStatus { get; set; } = null!;
    }
}
