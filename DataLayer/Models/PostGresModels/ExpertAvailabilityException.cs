using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Excepción de disponibilidad para una FECHA concreta; sobreescribe el horario semanal
    /// (ExpertAvailabilityRule) ese día. Patrón plano: UNA fila por franja (permite turnos partidos).
    /// - Cerrado (vacaciones/festivo): una fila con IsWorking=false y Start/End null.
    /// - Trabaja (abierto o horas especiales): N filas con IsWorking=true, una por franja.
    /// ExpertId = ExpertProfile.Id (NO el User id), igual que ExpertAvailabilityRule.
    /// </summary>
    public class ExpertAvailabilityException
    {
        public int Id { get; set; }

        [Required]
        public int ExpertId { get; set; } // FK -> ExpertProfiles.Id

        /// <summary>Fecha local del experto (columna `date`, sin hora).</summary>
        [Required]
        public DateOnly Date { get; set; }

        /// <summary>false = ese día no se trabaja (sin franjas).</summary>
        [Required]
        public bool IsWorking { get; set; }

        public TimeSpan? StartLocal { get; set; } // null si IsWorking=false
        public TimeSpan? EndLocal { get; set; }   // null si IsWorking=false

        /// <summary>Snapshot IANA del timezone del experto al crear la excepción.</summary>
        [MaxLength(64)]
        public string? Timezone { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public virtual ExpertProfile Expert { get; set; } = null!;
    }
}
