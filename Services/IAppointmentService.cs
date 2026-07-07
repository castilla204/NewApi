using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.enums;
using newApi.Common;
using Hangfire;

namespace newApi.Services
{
    public interface IAppointmentService
    {
        Task<AppointmentDto?> GetAppointmentAsync(int id);
        Task<AppointmentDto?> GetAppointmentBySearchHireIdAsync(int searchHireId);
        Task<List<AppointmentDto>> GetUserAppointmentsAsync(int userId);
#if false // ═══ SISTEMA ANTIGUO: crear cita "pagar primero y luego proponer" (DESACTIVADO; impl en AppointmentService bajo #if false. NO BORRAR) ═══
        Task<AppointmentDto> CreateAppointmentAsync(CreateAppointmentDto dto);
#endif
#if false // ═══ SISTEMA ANTIGUO: proponer/aceptar/rechazar cita (sin uso en prod, conservado, NO BORRAR) ═══
        Task<AppointmentDto> ProposeAppointmentAsync(int searchHireId, ProposeAppointmentDto dto, int userId);
        Task<AppointmentDto> ConfirmAppointmentAsync(ConfirmAppointmentDto dto, int userId);
        Task<AppointmentDto> RejectAppointmentAsync(RejectAppointmentDto dto, int userId);
#endif
        Task<AppointmentDto> CancelAppointmentAsync(CancelAppointmentDto dto, int userId);
        Task<object> GetAppointmentMetricsAsync();
        Task ProcessOverdueTimersAsync();
        Task ProcessAppointmentTimerAsync(int timerId);

        // ⚓ Tarea 5: timer de confirmación del experto (auto-cancela si no aprueba/rechaza a tiempo).
        Task CreateExpertConfirmationTimerAsync(int appointmentId, int searchHireId);
        Task CancelExpertConfirmationTimerAsync(int appointmentId);
        // Timer PRIMARIO de transición confirmed→awaiting_report (cita+3h). Programado al aprobar el experto;
        // el watchdog A-iii (ProcessOverdueTimersAsync) sigue siendo la red de seguridad si este se pierde.
        Task CreateAwaitingReportTransitionTimerAsync(int appointmentId);

        // ⚓ Tarea 6: notificaciones + recordatorios de la confirmación del experto. TODAS best-effort
        // (try/catch internos): un fallo de notificación NUNCA debe romper el flujo de dinero/estado.
        Task NotifyExpertConfirmationRequestedAsync(int searchHireId);
        Task NotifyExpertConfirmationResolvedAsync(int searchHireId, string outcome);
        Task ScheduleExpertConfirmationRemindersAsync(int timerId, int searchHireId, int appointmentId, DateTime start, DateTime end);

        [JobDisplayName("🔔 Recordatorio Confirmación Experto - SearchHire #{0}")]
        Task SendExpertConfirmationReminderAsync(int searchHireId, int appointmentId);

        // 🔔 NOTIF-FIX [S4]: recordatorio al VENDEDOR (tercero sin cuenta) a mitad del plazo de
        // coordinación. Re-valida (token vivo, sin cita, plazo no vencido) → no-op si ya reservó.
        [JobDisplayName("🔔 Recordatorio Reserva Vendedor - SearchHire #{0}")]
        Task SendSellerBookingReminderAsync(int searchHireId);

        [JobDisplayName("🚨 Aviso Admin: Confirmación Experto a punto de caducar - SearchHire #{0}")]
        Task AlertAdminExpertConfirmationExpiringAsync(int searchHireId, int appointmentId);

        Task CancelExpertConfirmationRemindersAsync(int appointmentId);

        [JobDisplayName("⏰ Timer Propuesta Cliente (Penaliza Cliente) - Timer #{0}")]
        Task ProcessProposalTimerAsync(int timerId);
        
        [JobDisplayName("⏰ Timer Respuesta Experto (Penaliza Experto) - Timer #{0}")]
        Task ProcessResponseTimerAsync(int timerId);
        
        [JobDisplayName("⏰ Timer Reporte Experto (24h para enviar reporte) - Timer #{0}")]
        Task ProcessExpertReportTimerAsync(int timerId);
        
        [JobDisplayName("⏰ Timer Decisión Cliente (24h para aprobar/disputar) - Timer #{0}")]
        Task ProcessClientDecisionTimerAsync(int timerId);
        
        [JobDisplayName("⏰ Timer Transición a Awaiting Report (3h después de cita) - Timer #{0}")]
        Task ProcessAwaitingReportTransitionTimerAsync(int timerId);
        
        [JobDisplayName("⏰ Timer Transición a Awaiting Report (3h después de cita) - Appointment #{0}")]
        Task ProcessAppointmentToAwaitingReportAsync(int appointmentId);
        
        Task<AppointmentDto> SubmitExpertReportAsync(int appointmentId, int expertId, string? notes = null);
    }
}
