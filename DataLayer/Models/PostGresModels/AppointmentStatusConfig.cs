using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Configuración de porcentajes de distribución de dinero por estado de cita
    /// </summary>
    public class AppointmentStatusConfig
    {
        public int Id { get; set; }
        
        [Required]
        [MaxLength(50)]
        public string Status { get; set; } = string.Empty;
        
        [Required]
        [Range(0, 100)]
        public decimal ClientPercentage { get; set; }
        
        [Required]
        [Range(0, 100)]
        public decimal ExpertPercentage { get; set; }
        
        [Required]
        [Range(0, 100)]
        public decimal PlatformPercentage { get; set; }
        
        public bool IsActive { get; set; } = true;
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        // Validación: los porcentajes deben sumar 100%
        public bool IsValid => ClientPercentage + ExpertPercentage + PlatformPercentage == 100;
    }
}
