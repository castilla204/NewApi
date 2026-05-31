using Microsoft.AspNetCore.Http;

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
        public DateTime? ProposedDate { get; set; } // ✅ Nullable: Fecha en UTC (guardada en BD) - se asigna cuando el cliente propone
        public TimeSpan? ProposedTime { get; set; } // ✅ Nullable: Hora en UTC (guardada en BD) - se asigna cuando el cliente propone
        
        // ✅ NUEVOS CAMPOS: Fecha/hora en hora local para el frontend
        /// <summary>
        /// Fecha propuesta en hora local del experto (convertida desde UTC)
        /// </summary>
        public DateTime? ProposedDateLocal { get; set; }
        
        /// <summary>
        /// Hora propuesta en hora local del experto (convertida desde UTC)
        /// </summary>
        public TimeSpan? ProposedTimeLocal { get; set; }
        
        /// <summary>
        /// Timezone IANA usado para la conversión (ej: "Europe/Madrid", "America/Mexico_City")
        /// </summary>
        public string? Timezone { get; set; }

        /// <summary>
        /// 🌍 Round 21: Timezone IANA del experto capturado en el momento de creación de la cita.
        /// Inmutable: si el experto cambia su timezone después, este campo conserva el original
        /// para que el dashboard muestre la hora histórica correctamente.
        /// Null para citas legacy creadas antes de Round 21 (asumir Europe/Madrid).
        /// </summary>
        public string? ProposerTimezone { get; set; }

        /// <summary>
        /// País del experto al momento de la contratación (ISO 3166-1 alpha-2, ej: "ES", "MX")
        /// </summary>
        public string? Country { get; set; }
        public string? Location { get; set; } // ✅ Nullable: Se asigna cuando el cliente propone la cita
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        
        // ✅ NUEVOS CAMPOS: Información adicional del sitio
        public string? DoorNumber { get; set; } // Número de puerta, garaje, etc.
        public string? OwnerPhone { get; set; } // Teléfono del propietario del objeto
        public string? SiteDetails { get; set; } // Detalles específicos del sitio (opcional)
        
        public int RejectionCount { get; set; }
        public int ClientCancellationCount { get; set; }
        public int ExpertCancellationCount { get; set; }
        public DateTime? LastRejectionAt { get; set; }
        public DateTime? LastClientCancellationAt { get; set; }
        public DateTime? LastExpertCancellationAt { get; set; }
        public DateTime? LastProposalAt { get; set; }
        public DateTime? LastResponseAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        
        // Información adicional
        public string? ClientName { get; set; }
        public string? ExpertName { get; set; }
        public decimal Amount { get; set; }
        public List<AppointmentTimerDto> Timers { get; set; } = new List<AppointmentTimerDto>();
        
        // ✅ NUEVOS CAMPOS: Información de ubicación del experto para validación
        public string? ExpertLatitude { get; set; } // Coordenadas del experto al momento de la contratación
        public string? ExpertLongitude { get; set; } // Coordenadas del experto al momento de la contratación
        public int? LocationRange { get; set; } // Rango máximo permitido en km
        public SystemStatusDto? StatusInfo { get; set; } // ✅ NUEVO: Información completa del estado
    }

    /// <summary>
    /// DTO para crear una nueva cita
    /// </summary>
    public class CreateAppointmentDto
    {
        public int SearchHireId { get; set; }
        public DateTime ProposedDate { get; set; } // Fecha en hora LOCAL del experto
        public TimeSpan ProposedTime { get; set; } // Hora en hora LOCAL del experto
        public string Location { get; set; } = string.Empty;
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        
        // ✅ NUEVOS CAMPOS: Información adicional del sitio
        public string? DoorNumber { get; set; } // Número de puerta, garaje, etc.
        public string? OwnerPhone { get; set; } // Teléfono del propietario del objeto
        public string? SiteDetails { get; set; } // Detalles específicos del sitio (opcional)
        
        /// <summary>
        /// Timezone IANA opcional (ej: "Europe/Madrid", "America/Mexico_City")
        /// Si no se proporciona, se usa el timezone del experto desde SearchHire.ExpertTimezone
        /// </summary>
        public string? Timezone { get; set; }
    }

    /// <summary>
    /// DTO para proponer una cita
    /// </summary>
    public class ProposeAppointmentDto
    {
        public DateTime ProposedDate { get; set; } // Fecha en hora LOCAL del experto
        public TimeSpan ProposedTime { get; set; } // Hora en hora LOCAL del experto
        public string Location { get; set; } = string.Empty;
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        
        // ✅ NUEVOS CAMPOS: Información adicional del sitio
        public string? DoorNumber { get; set; } // Número de puerta, garaje, etc.
        public string? OwnerPhone { get; set; } // Teléfono del propietario del objeto
        public string? SiteDetails { get; set; } // Detalles específicos del sitio (opcional)
        
        /// <summary>
        /// Timezone IANA opcional (ej: "Europe/Madrid", "America/Mexico_City")
        /// Si no se proporciona, se usa el timezone del experto desde SearchHire.ExpertTimezone
        /// </summary>
        public string? Timezone { get; set; }
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
    /// DTO para subir reporte del experto
    /// </summary>
    public class SubmitExpertReportDto
    {
        public string? Notes { get; set; }
    }

    /// <summary>
    /// DTO para subir archivos y enviar reporte del experto en una sola operación
    /// </summary>
    public class SubmitExpertReportWithFilesDto
    {
        public string? Notes { get; set; }
        public List<IFormFile>? Files { get; set; }
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
