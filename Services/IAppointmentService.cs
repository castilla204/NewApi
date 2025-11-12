using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.enums;
using newApi.Common;

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
        Task CheckAppointmentTimersAsync();
        Task ProcessAppointmentTimerAsync(int timerId);
        Task ProcessAppointmentToAwaitingReportAsync(int appointmentId);
        Task<AppointmentDto> SubmitExpertReportAsync(int appointmentId, int expertId, string? notes = null);
    }
}
