using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Disponibilidad de trabajo de los expertos
    /// Guarda el historial completo: cuando se actualiza, se marca la anterior como inactiva
    /// </summary>
    public class ExpertAvailability
    {
        public int Id { get; set; }
        
        [Required]
        public int ExpertId { get; set; }
        
        /// <summary>
        /// Días de la semana en formato JSON array: ["Monday", "Tuesday", "Wednesday", etc.]
        /// </summary>
        [Required]
        public string DaysOfWeek { get; set; } = "[]"; // JSON array
        
        /// <summary>
        /// Hora de inicio de la franja horaria (misma para todos los días)
        /// </summary>
        [Required]
        public TimeSpan StartTime { get; set; }
        
        /// <summary>
        /// Hora de fin de la franja horaria (misma para todos los días)
        /// </summary>
        [Required]
        public TimeSpan EndTime { get; set; }

        /// <summary>
        /// 🛡️ Round 28 MUD-A: snapshot del timezone IANA al crear esta disponibilidad
        /// (p.ej. "America/New_York", "Europe/Madrid"). Antes el frontend leía
        /// `expertProfile.Timezone` actual para renderizar, lo que causaba que
        /// post-mudanza un "09:00-18:00" creado en Aiken (US) se mostrara como
        /// "09:00-18:00 Madrid" sin moverse 6h — interpretación silenciosamente errónea.
        /// Con este snapshot, el frontend puede mostrar la zona horaria original o
        /// pedir al experto que actualice las horas tras una mudanza.
        /// Nullable para retro-compatibilidad con filas legacy creadas pre-R28.
        /// </summary>
        [MaxLength(50)]
        public string? Timezone { get; set; }

        /// <summary>
        /// Fecha desde la cual esta disponibilidad está vigente
        /// </summary>
        [Required]
        public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Fecha hasta la cual esta disponibilidad está vigente
        /// Null = disponibilidad actual/activa
        /// Cuando se crea una nueva disponibilidad, se establece este campo en la anterior
        /// </summary>
        public DateTime? EffectiveTo { get; set; }
        
        /// <summary>
        /// Indica si esta es la disponibilidad actual activa
        /// </summary>
        public bool IsActive { get; set; } = true;
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public virtual ExpertProfile Expert { get; set; } = null!;
    }
}



