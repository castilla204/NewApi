using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;

namespace newApi.Services
{
    public class AppointmentService : IAppointmentService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AppointmentService> _logger;
        private readonly SystemStatusService _systemStatusService;

        public AppointmentService(AppDbContext context, ILogger<AppointmentService> logger, SystemStatusService systemStatusService)
        {
            _context = context;
            _logger = logger;
            _systemStatusService = systemStatusService;
        }

        public async Task<AppointmentDto?> GetAppointmentAsync(int id)
        {
            try
            {
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.Status)
                    .Include(a => a.Timers)
                    .FirstOrDefaultAsync(a => a.Id == id);

                if (appointment == null)
                    return null;

                return MapToDto(appointment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting appointment with ID {AppointmentId}", id);
                throw;
            }
        }

        public async Task<AppointmentDto?> GetAppointmentBySearchHireIdAsync(int searchHireId)
        {
            try
            {
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.Status)
                    .Include(a => a.Timers)
                    .FirstOrDefaultAsync(a => a.SearchHireId == searchHireId);

                if (appointment == null)
                    return null;

                return MapToDto(appointment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting appointment for SearchHire ID {SearchHireId}", searchHireId);
                throw;
            }
        }

        public async Task<List<AppointmentDto>> GetUserAppointmentsAsync(int userId)
        {
            try
            {
                var appointments = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.Status)
                    .Include(a => a.Timers)
                    .Where(a => a.SearchHire.ClientId == userId || a.SearchHire.ExpertId == userId)
                    .OrderByDescending(a => a.CreatedAt)
                    .ToListAsync();

                return appointments.Select(MapToDto).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting appointments for user ID {UserId}", userId);
                throw;
            }
        }

        public async Task<AppointmentDto> CreateAppointmentAsync(CreateAppointmentDto dto)
        {
            try
            {
                // Verificar que el SearchHire existe y no tiene ya una cita
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Appointment)
                    .FirstOrDefaultAsync(sh => sh.Id == dto.SearchHireId);

                if (searchHire == null)
                    throw new ArgumentException("SearchHire not found");

                if (searchHire.Appointment != null)
                    throw new InvalidOperationException("SearchHire already has an appointment");

                // Obtener el estado "awaiting_appointment"
                var awaitingStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                            s.StatusValue == "awaiting_appointment");

                if (awaitingStatus == null)
                    throw new InvalidOperationException("Awaiting appointment status not found");

                var appointment = new Appointment
                {
                    SearchHireId = dto.SearchHireId,
                    StatusId = awaitingStatus.Id,
                    ProposedDate = DateTime.SpecifyKind(dto.ProposedDate, DateTimeKind.Utc),
                    ProposedTime = dto.ProposedTime,
                    Location = dto.Location,
                    Latitude = dto.Latitude,
                    Longitude = dto.Longitude,
                    DoorNumber = dto.DoorNumber,
                    OwnerPhone = dto.OwnerPhone,
                    SiteDetails = dto.SiteDetails,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.Appointments.Add(appointment);
                await _context.SaveChangesAsync();

                // Cargar la cita con todas las relaciones para devolver el DTO completo
                var createdAppointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.Status)
                    .Include(a => a.Timers)
                    .FirstAsync(a => a.Id == appointment.Id);

                return MapToDto(createdAppointment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating appointment for SearchHire ID {SearchHireId}", dto.SearchHireId);
                throw;
            }
        }

        public async Task<AppointmentDto> ProposeAppointmentAsync(int searchHireId, ProposeAppointmentDto dto, int userId)
        {
            try
            {
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                    .Include(a => a.Status)
                    .FirstOrDefaultAsync(a => a.SearchHireId == searchHireId);

                // Si no existe la cita, crearla automáticamente
                if (appointment == null)
                {
                    // Verificar que el SearchHire existe
                    var searchHire = await _context.SearchHires
                        .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                    if (searchHire == null)
                        throw new ArgumentException("SearchHire not found");

                    // Verificar que el usuario es el cliente
                    if (searchHire.ClientId != userId)
                        throw new UnauthorizedAccessException("Only the client can propose appointments");

                    // Obtener el estado "awaiting_appointment"
                    var awaitingStatus = await _context.SystemStatuses
                        .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                s.StatusValue == "awaiting_appointment");

                    if (awaitingStatus == null)
                        throw new InvalidOperationException("Awaiting appointment status not found");

                    // Crear la cita
                    appointment = new Appointment
                    {
                        SearchHireId = searchHireId,
                        StatusId = awaitingStatus.Id,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    _context.Appointments.Add(appointment);
                    await _context.SaveChangesAsync();

                    // Recargar la cita con las relaciones
                    appointment = await _context.Appointments
                        .Include(a => a.SearchHire)
                        .Include(a => a.Status)
                        .FirstAsync(a => a.Id == appointment.Id);
                }

                // Verificar que el usuario es el cliente
                if (appointment.SearchHire.ClientId != userId)
                    throw new UnauthorizedAccessException("Only the client can propose appointments");

                // Obtener el estado "appointment_proposed"
                var proposedStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                            s.StatusValue == "appointment_proposed");

                if (proposedStatus == null)
                    throw new InvalidOperationException("Appointment proposed status not found");

                // Actualizar la cita - asegurar que los DateTime tengan Kind=UTC
                appointment.ProposedDate = DateTime.SpecifyKind(dto.ProposedDate, DateTimeKind.Utc);
                appointment.ProposedTime = dto.ProposedTime;
                appointment.Location = dto.Location;
                appointment.Latitude = dto.Latitude;
                appointment.Longitude = dto.Longitude;
                appointment.DoorNumber = dto.DoorNumber;
                appointment.OwnerPhone = dto.OwnerPhone;
                appointment.SiteDetails = dto.SiteDetails;
                appointment.StatusId = proposedStatus.Id;
                appointment.LastProposalAt = DateTime.UtcNow;
                appointment.UpdatedAt = DateTime.UtcNow;

                // Crear timer para respuesta del cliente (24 horas)
                var responseTimer = new AppointmentTimer
                {
                    AppointmentId = appointment.Id,
                    TimerType = "response",
                    StartTime = DateTime.UtcNow,
                    EndTime = DateTime.UtcNow.AddHours(24),
                    IsExpired = false,
                    CreatedAt = DateTime.UtcNow
                };

                _context.AppointmentTimers.Add(responseTimer);
                await _context.SaveChangesAsync();

                // Cargar la cita actualizada con todas las relaciones
                var updatedAppointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.Status)
                    .Include(a => a.Timers)
                    .FirstAsync(a => a.Id == appointment.Id);

                return MapToDto(updatedAppointment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error proposing appointment for SearchHire ID {SearchHireId}", searchHireId);
                throw;
            }
        }

        public async Task<AppointmentDto> ConfirmAppointmentAsync(ConfirmAppointmentDto dto, int userId)
        {
            try
            {
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                    .Include(a => a.Status)
                    .FirstOrDefaultAsync(a => a.Id == dto.AppointmentId);

                if (appointment == null)
                    throw new ArgumentException("Appointment not found");

                // Verificar que el usuario es el experto
                if (appointment.SearchHire.ExpertId != userId)
                    throw new UnauthorizedAccessException("Only the expert can confirm appointments");

                // Obtener el estado "appointment_confirmed"
                var confirmedStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                            s.StatusValue == "appointment_confirmed");

                if (confirmedStatus == null)
                    throw new InvalidOperationException("Appointment confirmed status not found");

                // Actualizar la cita
                appointment.StatusId = confirmedStatus.Id;
                appointment.LastResponseAt = DateTime.UtcNow;
                appointment.UpdatedAt = DateTime.UtcNow;

                // Marcar timers de respuesta como expirados
                var responseTimers = await _context.AppointmentTimers
                    .Where(t => t.AppointmentId == appointment.Id && 
                               t.TimerType == "response" && 
                               !t.IsExpired)
                    .ToListAsync();

                foreach (var timer in responseTimers)
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();

                // Cargar la cita actualizada con todas las relaciones
                var updatedAppointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.Status)
                    .Include(a => a.Timers)
                    .FirstAsync(a => a.Id == appointment.Id);

                return MapToDto(updatedAppointment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error confirming appointment {AppointmentId}", dto.AppointmentId);
                throw;
            }
        }

        public async Task<AppointmentDto> RejectAppointmentAsync(RejectAppointmentDto dto, int userId)
        {
            try
            {
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                    .Include(a => a.Status)
                    .FirstOrDefaultAsync(a => a.Id == dto.AppointmentId);

                if (appointment == null)
                    throw new ArgumentException("Appointment not found");

                // Verificar que el usuario es el experto
                if (appointment.SearchHire.ExpertId != userId)
                    throw new UnauthorizedAccessException("Only the expert can reject appointments");

                // Determinar el estado según el número de rechazos
                string statusValue;
                if (appointment.RejectionCount >= 1)
                {
                    // Segundo rechazo o más - cancelar por rechazos múltiples
                    statusValue = "appointment_cancelled_by_expert_rejection";
                }
                else
                {
                    // Primer rechazo
                    statusValue = "appointment_rejected";
                }

                var newStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                            s.StatusValue == statusValue);

                if (newStatus == null)
                    throw new InvalidOperationException($"Appointment status '{statusValue}' not found");

                // Actualizar la cita
                appointment.StatusId = newStatus.Id;
                appointment.RejectionCount++;
                appointment.LastRejectionAt = DateTime.UtcNow;
                appointment.LastResponseAt = DateTime.UtcNow;
                appointment.UpdatedAt = DateTime.UtcNow;

                // Actualizar el SearchHire según el mapeo de estados
                var appointmentStatusEnum = statusValue switch
                {
                    "appointment_rejected" => AppointmentStatus.AppointmentRejected,
                    "appointment_cancelled_by_expert_rejection" => AppointmentStatus.AppointmentCancelledByExpertRejection,
                    _ => throw new InvalidOperationException($"Unknown appointment status: {statusValue}")
                };

                var targetSearchHireStatus = await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatusEnum);
                if (targetSearchHireStatus.HasValue)
                {
                    appointment.SearchHire.Status = targetSearchHireStatus.Value.ToString();
                    appointment.SearchHire.UpdatedAt = DateTime.UtcNow;
                }

                // Marcar timers de respuesta como expirados
                var responseTimers = await _context.AppointmentTimers
                    .Where(t => t.AppointmentId == appointment.Id && 
                               t.TimerType == "response" && 
                               !t.IsExpired)
                    .ToListAsync();

                foreach (var timer in responseTimers)
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();

                // Cargar la cita actualizada con todas las relaciones
                var updatedAppointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.Status)
                    .Include(a => a.Timers)
                    .FirstAsync(a => a.Id == appointment.Id);

                return MapToDto(updatedAppointment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rejecting appointment {AppointmentId}", dto.AppointmentId);
                throw;
            }
        }

        public async Task<AppointmentDto> CancelAppointmentAsync(CancelAppointmentDto dto, int userId)
        {
            try
            {
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                    .Include(a => a.Status)
                    .FirstOrDefaultAsync(a => a.Id == dto.AppointmentId);

                if (appointment == null)
                    throw new ArgumentException("Appointment not found");

                // Verificar que el usuario es el cliente o el experto
                if (appointment.SearchHire.ClientId != userId && appointment.SearchHire.ExpertId != userId)
                    throw new UnauthorizedAccessException("Only the client or expert can cancel appointments");

                // Determinar el estado de cancelación según quién cancela y el número de cancelaciones
                string statusValue;
                if (appointment.SearchHire.ClientId == userId)
                {
                    // Cliente cancela - verificar si es primera o segunda cancelación
                    if (appointment.CancellationCount >= 1)
                    {
                        statusValue = "appointment_cancelled_by_client_second";
                    }
                    else
                    {
                        statusValue = "appointment_cancelled_by_client";
                    }
                }
                else
                {
                    // Experto cancela
                    statusValue = "appointment_cancelled_by_expert";
                }

                var cancelledStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                            s.StatusValue == statusValue);

                if (cancelledStatus == null)
                    throw new InvalidOperationException($"Appointment cancelled status '{statusValue}' not found");

                // Actualizar la cita
                appointment.StatusId = cancelledStatus.Id;
                appointment.CancellationCount++;
                appointment.UpdatedAt = DateTime.UtcNow;

                // Actualizar el SearchHire según el mapeo de estados
                var appointmentStatusEnum = statusValue switch
                {
                    "appointment_cancelled_by_client" => AppointmentStatus.AppointmentCancelledByClient,
                    "appointment_cancelled_by_client_second" => AppointmentStatus.AppointmentCancelledByClientSecond,
                    "appointment_cancelled_by_expert" => AppointmentStatus.AppointmentCancelledByExpert,
                    _ => throw new InvalidOperationException($"Unknown appointment status: {statusValue}")
                };

                var targetSearchHireStatus = await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatusEnum);
                if (targetSearchHireStatus.HasValue)
                {
                    appointment.SearchHire.Status = targetSearchHireStatus.Value.ToString();
                    appointment.SearchHire.UpdatedAt = DateTime.UtcNow;
                }

                // Marcar todos los timers activos como expirados
                var activeTimers = await _context.AppointmentTimers
                    .Where(t => t.AppointmentId == appointment.Id && !t.IsExpired)
                    .ToListAsync();

                foreach (var timer in activeTimers)
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();

                // Cargar la cita actualizada con todas las relaciones
                var updatedAppointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.Status)
                    .Include(a => a.Timers)
                    .FirstAsync(a => a.Id == appointment.Id);

                return MapToDto(updatedAppointment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling appointment {AppointmentId}", dto.AppointmentId);
                throw;
            }
        }

        public async Task<object> GetAppointmentMetricsAsync()
        {
            try
            {
                var metrics = new AppointmentMetricsDto
                {
                    TotalAppointments = await _context.Appointments.CountAsync(),
                    PendingDisputes = await _context.Appointments
                        .Where(a => a.DisputeReason != null)
                        .CountAsync(),
                    ClientNoShows = await _context.Appointments
                        .Where(a => a.Status.StatusValue == "appointment_cancelled_by_client")
                        .CountAsync(),
                    ExpertNoShows = await _context.Appointments
                        .Where(a => a.Status.StatusValue == "appointment_cancelled_by_expert")
                        .CountAsync(),
                    SuccessfulAppointments = await _context.Appointments
                        .Where(a => a.Status.StatusValue == "appointment_awaiting_report")
                        .CountAsync(),
                    CancelledAppointments = await _context.Appointments
                        .Where(a => a.Status.StatusValue.Contains("cancelled"))
                        .CountAsync(),
                    AwaitingAppointment = await _context.Appointments
                        .Where(a => a.Status.StatusValue == "awaiting_appointment")
                        .CountAsync(),
                    AppointmentProposed = await _context.Appointments
                        .Where(a => a.Status.StatusValue == "appointment_proposed")
                        .CountAsync(),
                    AppointmentConfirmed = await _context.Appointments
                        .Where(a => a.Status.StatusValue == "appointment_confirmed")
                        .CountAsync(),
                    AppointmentRejected = await _context.Appointments
                        .Where(a => a.Status.StatusValue == "appointment_rejected")
                        .CountAsync()
                };

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting appointment metrics");
                throw;
            }
        }

        public async Task CheckAppointmentTimersAsync()
        {
            try
            {
                // 1. Procesar timers expirados
                var expiredTimers = await _context.AppointmentTimers
                    .Include(t => t.Appointment)
                        .ThenInclude(a => a.Status)
                    .Where(t => !t.IsExpired && t.EndTime <= DateTime.UtcNow)
                    .ToListAsync();

                foreach (var timer in expiredTimers)
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;

                    // Lógica específica según el tipo de timer
                    switch (timer.TimerType)
                    {
                        case "response":
                            // Si el cliente no responde en 24h, cancelar por no respuesta
                            var noResponseStatus = await _context.SystemStatuses
                                .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                        s.StatusValue == "appointment_cancelled_by_no_response");
                            
                            if (noResponseStatus != null)
                            {
                                timer.Appointment.StatusId = noResponseStatus.Id;
                                timer.Appointment.UpdatedAt = DateTime.UtcNow;
                            }
                            break;
                    }
                }

                // 2. Verificar citas confirmadas que deben cambiar a awaiting_report (3 horas después)
                var confirmedAppointments = await _context.Appointments
                    .Include(a => a.Status)
                    .Where(a => a.Status.StatusValue == "appointment_confirmed" &&
                               a.ProposedDate.Add(a.ProposedTime).AddHours(3) <= DateTime.UtcNow)
                    .ToListAsync();

                var awaitingReportStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                            s.StatusValue == "appointment_awaiting_report");

                if (awaitingReportStatus != null)
                {
                    foreach (var appointment in confirmedAppointments)
                    {
                        appointment.StatusId = awaitingReportStatus.Id;
                        appointment.UpdatedAt = DateTime.UtcNow;
                        
                        _logger.LogInformation("Appointment {AppointmentId} changed from confirmed to awaiting_report", appointment.Id);
                    }
                }

                if (expiredTimers.Any() || confirmedAppointments.Any())
                {
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Processed {TimerCount} expired timers and {AppointmentCount} appointments moved to awaiting_report", 
                        expiredTimers.Count, confirmedAppointments.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking appointment timers");
                throw;
            }
        }

        public async Task<AppointmentDto> SubmitExpertReportAsync(int appointmentId, int expertId, string? notes = null)
        {
            try
            {
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                    .Include(a => a.Status)
                    .FirstOrDefaultAsync(a => a.Id == appointmentId);

                if (appointment == null)
                    throw new ArgumentException("Appointment not found");

                // Verificar que el usuario es el experto
                if (appointment.SearchHire.ExpertId != expertId)
                    throw new UnauthorizedAccessException("Only the expert can submit reports");

                // Verificar que la cita está en estado awaiting_report
                if (appointment.Status.StatusValue != "appointment_awaiting_report")
                    throw new InvalidOperationException("Appointment must be in awaiting_report status to submit report");

                // Obtener el estado awaiting_client_decision
                var awaitingClientDecisionStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && 
                                            s.StatusValue == "awaiting_client_decision");

                if (awaitingClientDecisionStatus == null)
                    throw new InvalidOperationException("Awaiting client decision status not found");

                // Actualizar la cita
                appointment.StatusId = awaitingClientDecisionStatus.Id;
                appointment.UpdatedAt = DateTime.UtcNow;

                // Actualizar el SearchHire para que use el estado del sistema centralizado
                appointment.SearchHire.Status = "awaiting_client_decision";
                appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                // Cargar la cita actualizada con todas las relaciones
                var updatedAppointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.Status)
                    .Include(a => a.Timers)
                    .FirstAsync(a => a.Id == appointment.Id);

                _logger.LogInformation("Expert {ExpertId} submitted report for appointment {AppointmentId}", expertId, appointmentId);

                return MapToDto(updatedAppointment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error submitting expert report for appointment {AppointmentId}", appointmentId);
                throw;
            }
        }

        private AppointmentDto MapToDto(Appointment appointment)
        {
            return new AppointmentDto
            {
                Id = appointment.Id,
                SearchHireId = appointment.SearchHireId,
                Status = appointment.Status?.StatusValue ?? string.Empty,
                ProposedDate = appointment.ProposedDate,
                ProposedTime = appointment.ProposedTime,
                Location = appointment.Location,
                Latitude = appointment.Latitude,
                Longitude = appointment.Longitude,
                DoorNumber = appointment.DoorNumber,
                OwnerPhone = appointment.OwnerPhone,
                SiteDetails = appointment.SiteDetails,
                DisputeReason = appointment.DisputeReason,
                CompletedAt = appointment.CompletedAt,
                CompletedBy = appointment.CompletedBy,
                RejectionCount = appointment.RejectionCount,
                CancellationCount = appointment.CancellationCount,
                LastRejectionAt = appointment.LastRejectionAt,
                LastProposalAt = appointment.LastProposalAt,
                LastResponseAt = appointment.LastResponseAt,
                IsLocked = appointment.IsLocked,
                CreatedAt = appointment.CreatedAt,
                UpdatedAt = appointment.UpdatedAt,
                ClientName = appointment.SearchHire?.Client?.Name ?? string.Empty,
                ExpertName = appointment.SearchHire?.Expert?.Name ?? string.Empty,
                Amount = appointment.SearchHire?.Amount ?? 0,
                Timers = appointment.Timers?.Select(t => new AppointmentTimerDto
                {
                    Id = t.Id,
                    AppointmentId = t.AppointmentId,
                    TimerType = t.TimerType,
                    StartTime = t.StartTime,
                    EndTime = t.EndTime,
                    IsExpired = t.IsExpired,
                    ExpiredAt = t.ExpiredAt,
                    Notes = t.Notes,
                    CreatedAt = t.CreatedAt
                }).ToList() ?? new List<AppointmentTimerDto>()
            };
        }
    }
}
