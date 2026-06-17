using System.ComponentModel.DataAnnotations;
using newApi.DataLayer.Models.enums;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Citas para servicios que requieren encuentro presencial
    /// </summary>
    public class Appointment
    {
        public int Id { get; set; }
        public int SearchHireId { get; set; }
        public int StatusId { get; set; } // Referencia a SystemStatus
        public DateTime? ProposedDate { get; set; } // ✅ Nullable: Se asigna cuando el cliente propone la cita
        public TimeSpan? ProposedTime { get; set; } // ✅ Nullable: Se asigna cuando el cliente propone la cita
        public string? Location { get; set; } // ✅ Nullable: Se asigna cuando el cliente propone la cita
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }

        /// <summary>
        /// 🌍 Round 21: IANA timezone del experto al momento de creación de la cita (ej "Europe/Madrid").
        /// Inmutable — si el experto cambia su timezone después, esta cita conserva la original
        /// para que el dashboard del usuario muestre la hora correctamente.
        /// Nullable para backward-compat con citas existentes (que se asume Europe/Madrid).
        /// </summary>
        [MaxLength(64)]
        public string? ProposerTimezone { get; set; }
        
        // ✅ NUEVOS CAMPOS: Información adicional del sitio
        public string? DoorNumber { get; set; } // Número de puerta, garaje, etc.
        public string? OwnerPhone { get; set; } // Teléfono del propietario del objeto
        public string? SiteDetails { get; set; } // Detalles específicos del sitio (opcional)
        
        // Campos para restricciones y control
        public int RejectionCount { get; set; } = 0;
        public int ClientCancellationCount { get; set; } = 0;
        public int ExpertCancellationCount { get; set; } = 0;
        public DateTime? LastRejectionAt { get; set; }
        public DateTime? LastClientCancellationAt { get; set; }
        public DateTime? LastExpertCancellationAt { get; set; }
        public DateTime? LastProposalAt { get; set; }
        public DateTime? LastResponseAt { get; set; }

        // 🗓️ Reserva atómica (Fase A): intervalo UTC + bandera de bloqueo de agenda.
        // ExpertId se denormaliza desde SearchHire.ExpertId (User id) para que la query
        // de solape no haga JOIN y la exclusion constraint agrupe por experto.
        public int? ExpertId { get; set; }
        public DateTime? StartsAtUtc { get; set; }
        public DateTime? EndsAtUtc { get; set; }
        public bool BlocksCalendar { get; set; } = false;
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public virtual SearchHire SearchHire { get; set; } = null!;
        public virtual SystemStatus Status { get; set; } = null!;
        public virtual ICollection<AppointmentTimer> Timers { get; set; } = new List<AppointmentTimer>();
    }
}




