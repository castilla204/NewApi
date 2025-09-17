namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para mostrar información de una cita
    /// </summary>
    public class AppointmentDto
    {
        public int Id { get; set; }
        public int SearchHireId { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime ProposedDate { get; set; }
        public TimeSpan ProposedTime { get; set; }
        public string Location { get; set; } = string.Empty;
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public string? DisputeReason { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int? CompletedBy { get; set; }
        public int RejectionCount { get; set; }
        public int CancellationCount { get; set; }
        public DateTime? LastRejectionAt { get; set; }
        public DateTime? LastProposalAt { get; set; }
        public DateTime? LastResponseAt { get; set; }
        public bool IsLocked { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        
        // Información adicional
        public string? ClientName { get; set; }
        public string? ExpertName { get; set; }
        public decimal Amount { get; set; }
        public List<AppointmentTimerDto> Timers { get; set; } = new List<AppointmentTimerDto>();
    }

    /// <summary>
    /// DTO para crear una nueva cita
    /// </summary>
    public class CreateAppointmentDto
    {
        public int SearchHireId { get; set; }
        public DateTime ProposedDate { get; set; }
        public TimeSpan ProposedTime { get; set; }
        public string Location { get; set; } = string.Empty;
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
    }

    /// <summary>
    /// DTO para proponer una cita
    /// </summary>
    public class ProposeAppointmentDto
    {
        public DateTime ProposedDate { get; set; }
        public TimeSpan ProposedTime { get; set; }
        public string Location { get; set; } = string.Empty;
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
    }

    /// <summary>
    /// DTO para confirmar una cita
    /// </summary>
    public class ConfirmAppointmentDto
    {
        public int AppointmentId { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>
    /// DTO para rechazar una cita
    /// </summary>
    public class RejectAppointmentDto
    {
        public int AppointmentId { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// DTO para cancelar una cita
    /// </summary>
    public class CancelAppointmentDto
    {
        public int AppointmentId { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// DTO para marcar una cita como completada
    /// </summary>
    public class MarkCompletedDto
    {
        public int AppointmentId { get; set; }
        public string? Notes { get; set; }
    }


    /// <summary>
    /// DTO para mostrar información de un timer
    /// </summary>
    public class AppointmentTimerDto
    {
        public int Id { get; set; }
        public int AppointmentId { get; set; }
        public string TimerType { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool IsExpired { get; set; }
        public DateTime? ExpiredAt { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// DTO para mostrar métricas de citas (Admin)
    /// </summary>
    public class AppointmentMetricsDto
    {
        public int TotalAppointments { get; set; }
        public int PendingDisputes { get; set; }
        public int ClientNoShows { get; set; }
        public int ExpertNoShows { get; set; }
        public int SuccessfulAppointments { get; set; }
        public int CancelledAppointments { get; set; }
        public int AwaitingAppointment { get; set; }
        public int AppointmentProposed { get; set; }
        public int AppointmentConfirmed { get; set; }
        public int AppointmentRejected { get; set; }
    }
}
