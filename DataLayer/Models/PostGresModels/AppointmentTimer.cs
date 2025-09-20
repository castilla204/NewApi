namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Control de tiempos para citas
    /// </summary>
    public class AppointmentTimer
    {
        public int Id { get; set; }
        public int AppointmentId { get; set; }
        public string TimerType { get; set; } = string.Empty; // "proposal", "response", "modification", "auto_awaiting_client_decision", "reprogram"
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool IsExpired { get; set; } = false;
        public DateTime? ExpiredAt { get; set; }
        public string? Notes { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public virtual Appointment Appointment { get; set; } = null!;
    }
}




