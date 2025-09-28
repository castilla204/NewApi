using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Configuración de distribución de dinero por estado, categoría y tipo de servicio
    /// Reemplaza CategoryServiceTypeConfigs con mayor flexibilidad
    /// </summary>
    public class StatusConfiguration
    {
        public int Id { get; set; }
        
        [Required]
        public int StatusId { get; set; } // Referencia a SystemStatus
        
        public int? CategoryId { get; set; } // NULL = aplica a todas las categorías
        
        public int? ServiceTypeCategoryId { get; set; } // NULL = aplica a todos los tipos
        
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
        
        // Navigation properties
        public virtual SystemStatus Status { get; set; } = null!;
        public virtual Category? Category { get; set; }
        public virtual ServiceTypeCategory? ServiceTypeCategory { get; set; }
        
        // Validación: los porcentajes deben sumar 100%
        public bool IsValid => ClientPercentage + ExpertPercentage + PlatformPercentage == 100;
    }
}
