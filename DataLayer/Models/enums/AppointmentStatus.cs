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
        AppointmentCancelledByClientNoProposal, // Cliente no propuso cita en 24h (timer "proposal")
        AppointmentCancelledByExpert,           // Primera cancelación del experto
        AppointmentCancelledByExpertSecond,     // Segunda cancelación del experto
        AppointmentCancelledByExpertNoResponse, // Experto no respondió a propuesta en 24h (timer "response")
        AppointmentCancelledByNoResponse,       // [DEPRECATED] Cliente no propuso en tiempo - usar AppointmentCancelledByClientNoProposal
        AppointmentCancelledByExpertRejection,  // Experto rechazó 2 veces (cancelación por rechazos)
        AppointmentAwaitingReport,              // Esperando reporte/archivos del experto (3h después de la cita)
        AppointmentCancelledByNoReport,          // Experto no envió reporte en 24h
        AppointmentReportSent,                 // Experto envió el reporte
        AppointmentCompletedWithoutClientApproval, // Cliente no decidió (aprobó/disputó) en 24h (timer "client_decision")
        AppointmentCancelledByClientAccountDelete, // Cliente eliminó su cuenta
        AppointmentCancelledByExpertAccountDelete  // Experto eliminó su cuenta
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
                AppointmentStatus.AppointmentCancelledByClientNoProposal => "appointment_cancelled_by_client_no_proposal",
                AppointmentStatus.AppointmentCancelledByExpert => "appointment_cancelled_by_expert",
                AppointmentStatus.AppointmentCancelledByExpertSecond => "appointment_cancelled_by_expert_second",
                AppointmentStatus.AppointmentCancelledByExpertNoResponse => "appointment_cancelled_by_expert_no_response",
                AppointmentStatus.AppointmentCancelledByNoResponse => "appointment_cancelled_by_no_response",
                AppointmentStatus.AppointmentCancelledByExpertRejection => "appointment_cancelled_by_expert_rejection",
                AppointmentStatus.AppointmentAwaitingReport => "appointment_awaiting_report",
                AppointmentStatus.AppointmentCancelledByNoReport => "appointment_cancelled_by_no_report",
                AppointmentStatus.AppointmentReportSent => "appointment_report_sent",
                AppointmentStatus.AppointmentCompletedWithoutClientApproval => "appointment_completed_without_client_approval",
                AppointmentStatus.AppointmentCancelledByClientAccountDelete => "appointment_cancelled_by_client_account_delete",
                AppointmentStatus.AppointmentCancelledByExpertAccountDelete => "appointment_cancelled_by_expert_account_delete",
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
                "appointment_cancelled_by_client_no_proposal" => AppointmentStatus.AppointmentCancelledByClientNoProposal,
                "appointment_cancelled_by_expert" => AppointmentStatus.AppointmentCancelledByExpert,
                "appointment_cancelled_by_expert_second" => AppointmentStatus.AppointmentCancelledByExpertSecond,
                "appointment_cancelled_by_expert_no_response" => AppointmentStatus.AppointmentCancelledByExpertNoResponse,
                "appointment_cancelled_by_no_response" => AppointmentStatus.AppointmentCancelledByNoResponse,
                "appointment_cancelled_by_expert_rejection" => AppointmentStatus.AppointmentCancelledByExpertRejection,
                "appointment_awaiting_report" => AppointmentStatus.AppointmentAwaitingReport,
                "appointment_cancelled_by_no_report" => AppointmentStatus.AppointmentCancelledByNoReport,
                "appointment_report_sent" => AppointmentStatus.AppointmentReportSent,
                "appointment_completed_without_client_approval" => AppointmentStatus.AppointmentCompletedWithoutClientApproval,
                "appointment_cancelled_by_client_account_delete" => AppointmentStatus.AppointmentCancelledByClientAccountDelete,
                "appointment_cancelled_by_expert_account_delete" => AppointmentStatus.AppointmentCancelledByExpertAccountDelete,
                _ => throw new ArgumentException($"Invalid appointment status: {value}")
            };
        }
    }
}
