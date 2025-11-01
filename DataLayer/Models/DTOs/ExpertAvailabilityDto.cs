using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para la disponibilidad de un experto
    /// </summary>
    public class ExpertAvailabilityDto
    {
        public int Id { get; set; }
        public int ExpertId { get; set; }
        public List<string> DaysOfWeek { get; set; } = new List<string>(); // ["Monday", "Tuesday", etc.]
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
    
    /// <summary>
    /// DTO para crear o actualizar disponibilidad
    /// </summary>
    public class CreateOrUpdateExpertAvailabilityDto
    {
        /// <summary>
        /// Días de la semana en los que el experto trabaja
        /// Valores permitidos: "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
        /// </summary>
        [Required]
        public List<string> DaysOfWeek { get; set; } = new List<string>();
        
        /// <summary>
        /// Hora de inicio de la franja horaria (misma para todos los días)
        /// Formato: "HH:mm" (ej: "09:00")
        /// </summary>
        [Required]
        public TimeSpan StartTime { get; set; }
        
        /// <summary>
        /// Hora de fin de la franja horaria (misma para todos los días)
        /// Formato: "HH:mm" (ej: "18:00")
        /// </summary>
        [Required]
        public TimeSpan EndTime { get; set; }
    }
    
    /// <summary>
    /// DTO para obtener solo la disponibilidad actual activa
    /// </summary>
    public class CurrentExpertAvailabilityDto
    {
        public int Id { get; set; }
        public List<string> DaysOfWeek { get; set; } = new List<string>();
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public DateTime EffectiveFrom { get; set; }
    }
}
