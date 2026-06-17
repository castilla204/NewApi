using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Regla de disponibilidad normalizada: UNA fila por (día, franja).
    /// Permite horas distintas por día y turnos partidos (varias filas mismo día).
    /// Sustituye funcionalmente al rango único de ExpertAvailability.
    /// ExpertId = ExpertProfile.Id (NO el User id).
    /// </summary>
    public class ExpertAvailabilityRule
    {
        public int Id { get; set; }

        [Required]
        public int ExpertId { get; set; } // FK -> ExpertProfiles.Id

        /// <summary>0=domingo … 6=sábado (System.DayOfWeek).</summary>
        [Required]
        public int DayOfWeek { get; set; }

        [Required]
        public TimeSpan StartLocal { get; set; }

        [Required]
        public TimeSpan EndLocal { get; set; }

        /// <summary>Snapshot IANA del timezone del experto al crear la regla.</summary>
        [MaxLength(64)]
        public string? Timezone { get; set; }

        [Required]
        public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
        public DateTime? EffectiveTo { get; set; }
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public virtual ExpertProfile Expert { get; set; } = null!;
    }
}
