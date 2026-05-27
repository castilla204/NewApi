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
        Task<AppointmentDto> CreateAppointmentAsync(CreateAppointmentDto dto);
        Task<AppointmentDto> ProposeAppointmentAsync(int searchHireId, ProposeAppointmentDto dto, int userId);
        Task<AppointmentDto> ConfirmAppointmentAsync(ConfirmAppointmentDto dto, int userId);
        Task<AppointmentDto> RejectAppointmentAsync(RejectAppointmentDto dto, int userId);
        Task<AppointmentDto> CancelAppointmentAsync(CancelAppointmentDto dto, int userId);
        Task<object> GetAppointmentMetricsAsync();
        Task ProcessOverdueTimersAsync();
        Task ProcessAppointmentTimerAsync(int timerId);
        
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
