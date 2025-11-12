namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Control de tiempos para citas
    /// </summary>
    public class AppointmentTimer
    {
        public int Id { get; set; }
        public int AppointmentId { get; set; }
        public string TimerType { get; set; } = string.Empty; // "proposal", "response", "modification", "auto_awaiting_client_decision", "reprogram", "awaiting_report_transition", "expert_report", "client_decision"
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool IsExpired { get; set; } = false;
        public DateTime? ExpiredAt { get; set; }
        public string? Notes { get; set; }
        public string? HangfireJobId { get; set; } // ID del job de Hangfire programado para este timer
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public virtual Appointment Appointment { get; set; } = null!;
    }
}





