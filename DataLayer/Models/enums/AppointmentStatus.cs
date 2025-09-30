namespace newApi.DataLayer.Models.enums
{
    /// <summary>
    /// Estados específicos para citas (subestados de SearchHire)
    /// </summary>
    public enum AppointmentStatus
    {
        AwaitingAppointment,                    // Esperando propuesta del cliente (48h)
        AppointmentProposed,                    // Cliente propuso cita
        AppointmentConfirmed,                   // Experto confirmó
        AppointmentRejected,                    // Experto rechazó
        AppointmentCancelledByClient,           // Primera cancelación del cliente
        AppointmentCancelledByClientSecond,     // Segunda cancelación del cliente
        AppointmentCancelledByExpert,           // Experto cancela voluntariamente
        AppointmentCancelledByNoResponse,       // Cliente no propuso en tiempo
        AppointmentCancelledByExpertRejection,  // Experto rechazó 2 veces (cancelación por rechazos)
        AppointmentAwaitingReport               // Esperando reporte/archivos del experto (3h después de la cita)
    }

    public static class AppointmentStatusExtensions
    {
        public static string ToStringValue(this AppointmentStatus status)
        {
            return status switch
            {
                AppointmentStatus.AwaitingAppointment => "awaiting_appointment",
                AppointmentStatus.AppointmentProposed => "appointment_proposed",
                AppointmentStatus.AppointmentConfirmed => "appointment_confirmed",
                AppointmentStatus.AppointmentRejected => "appointment_rejected",
                AppointmentStatus.AppointmentCancelledByClient => "appointment_cancelled_by_client",
                AppointmentStatus.AppointmentCancelledByClientSecond => "appointment_cancelled_by_client_second",
                AppointmentStatus.AppointmentCancelledByExpert => "appointment_cancelled_by_expert",
                AppointmentStatus.AppointmentCancelledByNoResponse => "appointment_cancelled_by_no_response",
                AppointmentStatus.AppointmentCancelledByExpertRejection => "appointment_cancelled_by_expert_rejection",
                AppointmentStatus.AppointmentAwaitingReport => "appointment_awaiting_report",
                _ => status.ToString().ToLower()
            };
        }

        public static AppointmentStatus FromStringValue(string value)
        {
            return value switch
            {
                "awaiting_appointment" => AppointmentStatus.AwaitingAppointment,
                "appointment_proposed" => AppointmentStatus.AppointmentProposed,
                "appointment_confirmed" => AppointmentStatus.AppointmentConfirmed,
                "appointment_rejected" => AppointmentStatus.AppointmentRejected,
                "appointment_cancelled_by_client" => AppointmentStatus.AppointmentCancelledByClient,
                "appointment_cancelled_by_client_second" => AppointmentStatus.AppointmentCancelledByClientSecond,
                "appointment_cancelled_by_expert" => AppointmentStatus.AppointmentCancelledByExpert,
                "appointment_cancelled_by_no_response" => AppointmentStatus.AppointmentCancelledByNoResponse,
                "appointment_cancelled_by_expert_rejection" => AppointmentStatus.AppointmentCancelledByExpertRejection,
                "appointment_awaiting_report" => AppointmentStatus.AppointmentAwaitingReport,
                _ => throw new ArgumentException($"Invalid appointment status: {value}")
            };
        }
    }
}
