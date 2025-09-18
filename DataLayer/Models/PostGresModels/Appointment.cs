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
        public string Status { get; set; } = AppointmentStatus.AwaitingAppointment.ToStringValue();
        public DateTime ProposedDate { get; set; }
        public TimeSpan ProposedTime { get; set; }
        public string Location { get; set; } = string.Empty;
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public string? DisputeReason { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int? CompletedBy { get; set; }
        
        // Campos para restricciones y control
        public int RejectionCount { get; set; } = 0;
        public int CancellationCount { get; set; } = 0;
        public DateTime? LastRejectionAt { get; set; }
        public DateTime? LastProposalAt { get; set; }
        public DateTime? LastResponseAt { get; set; }
        public bool IsLocked { get; set; } = false; // Bloqueado por 12h antes de la cita
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public virtual SearchHire SearchHire { get; set; } = null!;
        public virtual ICollection<AppointmentTimer> Timers { get; set; } = new List<AppointmentTimer>();
    }
}



