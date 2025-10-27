using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;
using newApi.Common;
using System.Globalization;

namespace newApi.Services
{
    public class AppointmentService : IAppointmentService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AppointmentService> _logger;
        private readonly SystemStatusService _systemStatusService;
        private readonly StripeRefundService _refundService;
        private readonly ILoggingService _loggingService;

        public AppointmentService(AppDbContext context, ILogger<AppointmentService> logger, SystemStatusService systemStatusService, StripeRefundService refundService, ILoggingService loggingService)
        {
            _context = context;
            _logger = logger;
            _systemStatusService = systemStatusService;
            _refundService = refundService;
            _loggingService = loggingService;
        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue
        /// </summary>
        private async Task<int> GetStatusIdByValueAsync(string statusValue)
        {
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == "SearchHireStatus");
            
            if (systemStatus == null)
            {
                _logger.LogWarning("SystemStatus not found for StatusValue: {StatusValue}", statusValue);
                // Default to "pending" (ID = 1)
                return 1;
            }
            
            return systemStatus.Id;
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
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
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
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
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
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
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

                // ✅ VALIDACIÓN: Verificar que la cita tenga al menos 24 horas de anticipación
                var proposedDateTime = DateTime.SpecifyKind(dto.ProposedDate, DateTimeKind.Utc).Date + dto.ProposedTime;
                var timeUntilAppointment = proposedDateTime - DateTime.UtcNow;
                
                if (timeUntilAppointment.TotalHours < 24)
                {
                    throw new InvalidOperationException(
                        $"Las citas deben crearse con al menos 24 horas de anticipación. " +
                        $"Tiempo restante: {timeUntilAppointment.TotalHours:F1} horas. " +
                        $"Fecha/hora propuesta: {proposedDateTime:dd/MM/yyyy HH:mm} UTC"
                    );
                }

                // ✅ VALIDACIÓN: Verificar que la ubicación propuesta esté dentro del rango del experto
                await ValidateAppointmentLocationAsync(searchHire, dto.Latitude, dto.Longitude);

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
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
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
                        .ThenInclude(sh => sh.Status)
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

                // ✅ VALIDACIÓN: Verificar que la cita tenga al menos 24 horas de anticipación
                var proposedDateTime = DateTime.SpecifyKind(dto.ProposedDate, DateTimeKind.Utc).Date + dto.ProposedTime;
                var timeUntilAppointment = proposedDateTime - DateTime.UtcNow;
                
                if (timeUntilAppointment.TotalHours < 24)
                {
                    throw new InvalidOperationException(
                        $"Las citas deben proponerse con al menos 24 horas de anticipación. " +
                        $"Tiempo restante: {timeUntilAppointment.TotalHours:F1} horas. " +
                        $"Fecha/hora propuesta: {proposedDateTime:dd/MM/yyyy HH:mm} UTC"
                    );
                }

                // ✅ VALIDACIÓN: Verificar que la ubicación propuesta esté dentro del rango del experto
                await ValidateAppointmentLocationAsync(appointment.SearchHire, dto.Latitude, dto.Longitude);

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
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
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
                        .ThenInclude(sh => sh.Status)
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
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
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
                _logger.LogInformation("🔍 REJECT APPOINTMENT STARTED - AppointmentId: {AppointmentId}, UserId: {UserId}", dto.AppointmentId, userId);
                
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(a => a.Status)
                    .FirstOrDefaultAsync(a => a.Id == dto.AppointmentId);

                if (appointment == null)
                {
                    _logger.LogError("🔍 APPOINTMENT NOT FOUND - AppointmentId: {AppointmentId}", dto.AppointmentId);
                    throw new ArgumentException("Appointment not found");
                }

                _logger.LogInformation("🔍 APPOINTMENT FOUND - Id: {Id}, SearchHireId: {SearchHireId}, ExpertId: {ExpertId}, RejectionCount: {RejectionCount}, ClientCancellationCount: {ClientCancellationCount}, ExpertCancellationCount: {ExpertCancellationCount}", 
                    appointment.Id, appointment.SearchHireId, appointment.SearchHire.ExpertId, appointment.RejectionCount, appointment.ClientCancellationCount, appointment.ExpertCancellationCount);

                // Verificar que el usuario es el experto
                if (appointment.SearchHire.ExpertId != userId)
                {
                    _logger.LogError("🔍 UNAUTHORIZED ACCESS - UserId: {UserId} is not the expert (ExpertId: {ExpertId})", userId, appointment.SearchHire.ExpertId);
                    throw new UnauthorizedAccessException("Only the expert can reject appointments");
                }

                _logger.LogInformation("🔍 AUTHORIZATION OK - UserId: {UserId} is the expert", userId);

                // 🔍 LOGS DETALLADOS: Analizar el estado actual
                _logger.LogInformation("🔍 REJECT APPOINTMENT ANALYSIS - AppointmentId: {AppointmentId}, Current RejectionCount: {RejectionCount}, ClientCancellationCount: {ClientCancellationCount}, ExpertCancellationCount: {ExpertCancellationCount}", 
                    appointment.Id, appointment.RejectionCount, appointment.ClientCancellationCount, appointment.ExpertCancellationCount);

                // Determinar el estado según el número de rechazos
                string statusValue;
                bool isSecondRejection = appointment.RejectionCount >= 1;
                
                _logger.LogInformation("🔍 REJECTION ANALYSIS - isSecondRejection: {IsSecondRejection} (RejectionCount >= 1: {RejectionCount} >= 1)", 
                    isSecondRejection, appointment.RejectionCount);
                
                if (isSecondRejection)
                {
                    // Segundo rechazo o más - cancelar por rechazos múltiples
                    // ✅ CORRECCIÓN: Usar el estado correcto para rechazo (no cancelación)
                    statusValue = "appointment_cancelled_by_expert_rejection";
                    _logger.LogInformation("🔍 SECOND REJECTION DETECTED - Using status: {StatusValue}", statusValue);
                }
                else
                {
                    // Primer rechazo
                    statusValue = "appointment_rejected";
                    _logger.LogInformation("🔍 FIRST REJECTION - Using status: {StatusValue}", statusValue);
                }

                var newStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                            s.StatusValue == statusValue);

                if (newStatus == null)
                    throw new InvalidOperationException($"Appointment status '{statusValue}' not found");

                // Actualizar la cita
                appointment.StatusId = newStatus.Id;
                appointment.RejectionCount++;
                
                // ✅ CORRECCIÓN: Incrementar ExpertCancellationCount para segunda cancelación
                if (isSecondRejection)
                {
                    appointment.ExpertCancellationCount++;
                    _logger.LogInformation("🔍 EXPERT CANCELLATION COUNT INCREMENTED - New ExpertCancellationCount: {ExpertCancellationCount}", appointment.ExpertCancellationCount);
                }
                
                appointment.LastRejectionAt = DateTime.UtcNow;
                appointment.LastResponseAt = DateTime.UtcNow;
                appointment.UpdatedAt = DateTime.UtcNow;

                _logger.LogInformation("🔍 APPOINTMENT UPDATED - StatusId: {StatusId}, RejectionCount: {RejectionCount}, ClientCancellationCount: {ClientCancellationCount}, ExpertCancellationCount: {ExpertCancellationCount}", 
                    appointment.StatusId, appointment.RejectionCount, appointment.ClientCancellationCount, appointment.ExpertCancellationCount);

                // Actualizar el SearchHire según el mapeo de estados
                var appointmentStatusEnum = statusValue switch
                {
                    "appointment_rejected" => AppointmentStatus.AppointmentRejected,
                    "appointment_cancelled_by_expert_rejection" => AppointmentStatus.AppointmentCancelledByExpertRejection,
                    _ => throw new InvalidOperationException($"Unknown appointment status: {statusValue}")
                };

                _logger.LogInformation("🔍 MAPPING APPOINTMENT STATUS - appointmentStatusEnum: {AppointmentStatusEnum}", appointmentStatusEnum);
                
                var targetSearchHireStatus = await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatusEnum);
                _logger.LogInformation("🔍 TARGET SEARCH HIRE STATUS - targetSearchHireStatus: {TargetSearchHireStatus}", targetSearchHireStatus);
                
                if (targetSearchHireStatus.HasValue)
                {
                    var statusId = await GetStatusIdByValueAsync(targetSearchHireStatus.Value.ToStringValue());
                    appointment.SearchHire.StatusId = statusId;
                    appointment.SearchHire.UpdatedAt = DateTime.UtcNow;
                    _logger.LogInformation("🔍 SEARCH HIRE STATUS UPDATED - StatusId: {StatusId}, StatusValue: {StatusValue}", 
                        statusId, targetSearchHireStatus.Value.ToStringValue());
                }
                else
                {
                    _logger.LogWarning("🔍 NO TARGET SEARCH HIRE STATUS FOUND - appointmentStatusEnum: {AppointmentStatusEnum}", appointmentStatusEnum);
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

                // ✅ CORRECCIÓN: Procesar refund automático para segunda cancelación
                if (isSecondRejection)
                {
                    try
                    {
                        _logger.LogInformation("🔍 PROCESSING AUTOMATIC REFUND - AppointmentId: {AppointmentId}, SearchHireId: {SearchHireId}, Amount: {Amount}", 
                            appointment.Id, appointment.SearchHireId, appointment.SearchHire.Amount);
                        
                        // 🔍 LOG: Verificar configuración de dinero antes del refund
                        var moneyConfig = await _systemStatusService.GetMoneyDistributionConfigAsync(
                            "appointment_cancelled_by_expert_rejection", 
                            appointment.SearchHire.SearchService?.CategoryId, 
                            appointment.SearchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);
                        
                        _logger.LogInformation("🔍 MONEY DISTRIBUTION CONFIG - Status: appointment_cancelled_by_expert_rejection, CategoryId: {CategoryId}, ServiceTypeCategoryId: {ServiceTypeCategoryId}, Config: {Config}", 
                            appointment.SearchHire.SearchService?.CategoryId, 
                            appointment.SearchHire.SearchService?.ServiceType?.ServiceTypeCategoryId,
                            moneyConfig != null ? $"Client: {moneyConfig.ClientPercentage}%, Expert: {moneyConfig.ExpertPercentage}%, Platform: {moneyConfig.PlatformPercentage}%" : "NULL");
                        
                        // Orquestar refund+transfer según configuración del subestado de finalización
                        var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                            appointment.SearchHireId,
                            "appointment_cancelled_by_expert_rejection",
                            "Segundo rechazo del experto - penalización máxima",
                            userId);
                        
                        if (refundSuccess)
                        {
                            _logger.LogInformation("✅ AUTOMATIC REFUND SUCCESS - AppointmentId: {AppointmentId}, SearchHireId: {SearchHireId}", 
                                appointment.Id, appointment.SearchHireId);
                        }
                        else
                        {
                            _logger.LogError("❌ AUTOMATIC REFUND FAILED - AppointmentId: {AppointmentId}, SearchHireId: {SearchHireId}", 
                                appointment.Id, appointment.SearchHireId);
                            
                            // Log critical error for money transaction failure
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Automatic refund failed",
                                details: $"Automatic refund failed for Appointment {appointment.Id}",
                                userId: appointment.SearchHire?.ClientId,
                                source: "AppointmentService.RejectAppointmentAsync",
                                relatedEntityType: "Refund",
                                relatedEntityId: appointment.SearchHireId,
                                additionalData: new { 
                                    AppointmentId = appointment.Id,
                                    SearchHireId = appointment.SearchHireId,
                                    Amount = appointment.SearchHire?.Amount,
                                    ClientId = appointment.SearchHire?.ClientId,
                                    ExpertId = appointment.SearchHire?.ExpertId
                                }
                            );
                        }
                    }
                    catch (Exception refundEx)
                    {
                        _logger.LogError(refundEx, "❌ ERROR PROCESSING AUTOMATIC REFUND - AppointmentId: {AppointmentId}, SearchHireId: {SearchHireId}", 
                            appointment.Id, appointment.SearchHireId);
                        
                        // Log critical error for money transaction failure
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Error processing automatic refund",
                            details: refundEx.ToString(),
                            userId: appointment.SearchHire?.ClientId,
                            source: "AppointmentService.RejectAppointmentAsync",
                            relatedEntityType: "Refund",
                            relatedEntityId: appointment.SearchHireId,
                            additionalData: new { 
                                AppointmentId = appointment.Id,
                                SearchHireId = appointment.SearchHireId,
                                Amount = appointment.SearchHire?.Amount,
                                ClientId = appointment.SearchHire?.ClientId,
                                ExpertId = appointment.SearchHire?.ExpertId,
                                ErrorMessage = refundEx.Message
                            }
                        );
                        
                        // No lanzar la excepción para no afectar el flujo principal
                    }
                }
                else
                {
                    _logger.LogInformation("🔍 NO REFUND PROCESSING - First rejection, isSecondRejection: {IsSecondRejection}", isSecondRejection);
                }

                // Cargar la cita actualizada con todas las relaciones
                var updatedAppointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(a => a.Status)
                    .Include(a => a.Timers)
                    .FirstAsync(a => a.Id == appointment.Id);

                return MapToDto(updatedAppointment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔍 ERROR REJECTING APPOINTMENT - AppointmentId: {AppointmentId}, UserId: {UserId}, Error: {Error}", 
                    dto.AppointmentId, userId, ex.Message);
                throw;
            }
        }

        public async Task<AppointmentDto> CancelAppointmentAsync(CancelAppointmentDto dto, int userId)
        {
            try
            {
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(a => a.Status)
                    .FirstOrDefaultAsync(a => a.Id == dto.AppointmentId);

                if (appointment == null)
                    throw new ArgumentException("Appointment not found");

                // Verificar que el usuario es el cliente o el experto
                if (appointment.SearchHire.ClientId != userId && appointment.SearchHire.ExpertId != userId)
                    throw new UnauthorizedAccessException("Only the client or expert can cancel appointments");

                // Determinar el estado de cancelación según quién cancela y el número de cancelaciones específicas
                string statusValue;
                if (appointment.SearchHire.ClientId == userId)
                {
                    // Cliente cancela - verificar si es primera o segunda cancelación del cliente
                    if (appointment.ClientCancellationCount >= 1)
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
                    // Experto cancela - verificar si es primera o segunda cancelación del experto
                    if (appointment.ExpertCancellationCount >= 1)
                    {
                        statusValue = "appointment_cancelled_by_expert_second";
                    }
                    else
                    {
                        statusValue = "appointment_cancelled_by_expert";
                    }
                }

                var cancelledStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                            s.StatusValue == statusValue);

                if (cancelledStatus == null)
                    throw new InvalidOperationException($"Appointment cancelled status '{statusValue}' not found");

                // Actualizar la cita
                appointment.StatusId = cancelledStatus.Id;
                
                // Incrementar contadores específicos según quién cancela
                if (appointment.SearchHire.ClientId == userId)
                {
                    appointment.ClientCancellationCount++;
                    appointment.LastClientCancellationAt = DateTime.UtcNow;
                }
                else
                {
                    appointment.ExpertCancellationCount++;
                    appointment.LastExpertCancellationAt = DateTime.UtcNow;
                }
                
                appointment.UpdatedAt = DateTime.UtcNow;

                // Actualizar el SearchHire según el mapeo de estados
                var appointmentStatusEnum = statusValue switch
                {
                    "appointment_cancelled_by_client" => AppointmentStatus.AppointmentCancelledByClient,
                    "appointment_cancelled_by_client_second" => AppointmentStatus.AppointmentCancelledByClientSecond,
                    "appointment_cancelled_by_expert" => AppointmentStatus.AppointmentCancelledByExpert,
                    "appointment_cancelled_by_expert_second" => AppointmentStatus.AppointmentCancelledByExpertSecond,
                    _ => throw new InvalidOperationException($"Unknown appointment status: {statusValue}")
                };

                var targetSearchHireStatus = await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatusEnum);
                if (targetSearchHireStatus.HasValue)
                {
                    var statusId = await GetStatusIdByValueAsync(targetSearchHireStatus.Value.ToStringValue());
                    appointment.SearchHire.StatusId = statusId;
                    appointment.SearchHire.UpdatedAt = DateTime.UtcNow;
                }

                // Si el subestado NO es de finalización, no invocar orquestador (primera cancelación, reprogramable)
                if (cancelledStatus.IsFinalizationStatus)
                {
                    // Orquestar movimientos de dinero según el estado determinado (subestado → fallback final), respetando granularidad
                    try
                    {
                        var distributionOk = await _refundService.ProcessMoneyDistributionAsync(
                            appointment.SearchHireId,
                            statusValue,
                            "Cancellation flow from CancelAppointmentAsync",
                            userId);
                        if (!distributionOk)
                        {
                            _logger.LogWarning("Money distribution not applied for AppointmentId={AppointmentId}, SearchHireId={SearchHireId}, Status={Status}",
                                appointment.Id, appointment.SearchHireId, statusValue);
                        }
                    }
                    catch (Exception distEx)
                    {
                        _logger.LogError(distEx, "Error orchestrating money distribution for AppointmentId={AppointmentId}, SearchHireId={SearchHireId}", appointment.Id, appointment.SearchHireId);
                    }
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
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
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
                                
                                // 🎯 PROCESAR DINERO AUTOMÁTICAMENTE
                                try
                                {
                                    var moneySuccess = await _refundService.ProcessMoneyDistributionAsync(
                                        timer.Appointment.SearchHireId,
                                        "appointment_cancelled_by_no_response",
                                        "Client did not respond within 24h - automatic cancellation",
                                        null);
                                    
                                    if (moneySuccess)
                                    {
                                        _logger.LogInformation("✅ Money distribution processed for appointment {AppointmentId} due to no response", timer.Appointment.Id);
                                        
                                        // 🚨 LOG CRÍTICO: Timer expirado - cliente no respondió
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: Appointment timer expired - client no response",
                                            details: $"Appointment {timer.Appointment.Id} cancelled due to client not responding within 24h",
                                            userId: timer.Appointment.SearchHire?.ClientId,
                                            source: "AppointmentService.CheckAppointmentTimersAsync",
                                            relatedEntityType: "Appointment",
                                            relatedEntityId: timer.Appointment.Id,
                                            additionalData: new { 
                                                Action = "TimerExpired",
                                                TimerType = "response",
                                                AppointmentId = timer.Appointment.Id,
                                                SearchHireId = timer.Appointment.SearchHireId,
                                                ClientId = timer.Appointment.SearchHire?.ClientId,
                                                ExpertId = timer.Appointment.SearchHire?.ExpertId,
                                                Status = "appointment_cancelled_by_no_response",
                                                MoneyDistributionSuccess = true
                                            }
                                        );
                                    }
                                    else
                                    {
                                        _logger.LogError("❌ Failed to process money distribution for appointment {AppointmentId} due to no response", timer.Appointment.Id);
                                        
                                        // 🚨 LOG CRÍTICO: Fallo en distribución de dinero por timer expirado
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: Money distribution failed for expired timer",
                                            details: $"Appointment {timer.Appointment.Id} timer expired but money distribution failed",
                                            userId: timer.Appointment.SearchHire?.ClientId,
                                            source: "AppointmentService.CheckAppointmentTimersAsync",
                                            relatedEntityType: "Appointment",
                                            relatedEntityId: timer.Appointment.Id,
                                            additionalData: new { 
                                                Action = "TimerExpired",
                                                TimerType = "response",
                                                AppointmentId = timer.Appointment.Id,
                                                SearchHireId = timer.Appointment.SearchHireId,
                                                ClientId = timer.Appointment.SearchHire?.ClientId,
                                                ExpertId = timer.Appointment.SearchHire?.ExpertId,
                                                Status = "appointment_cancelled_by_no_response",
                                                MoneyDistributionSuccess = false
                                            }
                                        );
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "❌ Error processing money distribution for appointment {AppointmentId} due to no response", timer.Appointment.Id);
                                    
                                    // 🚨 LOG CRÍTICO: Excepción en timer expirado
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL: Exception during timer expiration processing",
                                        details: $"Appointment {timer.Appointment.Id} timer expired but exception occurred: {ex.Message}",
                                        userId: timer.Appointment.SearchHire?.ClientId,
                                        source: "AppointmentService.CheckAppointmentTimersAsync",
                                        relatedEntityType: "Appointment",
                                        relatedEntityId: timer.Appointment.Id,
                                        additionalData: new { 
                                            Action = "TimerExpired",
                                            TimerType = "response",
                                            AppointmentId = timer.Appointment.Id,
                                            SearchHireId = timer.Appointment.SearchHireId,
                                            ClientId = timer.Appointment.SearchHire?.ClientId,
                                            ExpertId = timer.Appointment.SearchHire?.ExpertId,
                                            Status = "appointment_cancelled_by_no_response",
                                            Exception = ex.Message,
                                            StackTrace = ex.StackTrace
                                        }
                                    );
                                }
                            }
                            break;
                            
                        case "expert_report":
                            // Verificar si se han subido todos los archivos requeridos
                            var validationResult = await ValidateRequiredDeliverablesAsync(timer.Appointment.SearchHire);
                            
                            if (validationResult.IsValid)
                            {
                                // Si todos los archivos están listos, enviar el reporte automáticamente
                                // La cita se marca como informe enviado
                                var appointmentReportSentStatus = await _context.SystemStatuses
                                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                            s.StatusValue == "appointment_report_sent");
                                
                                // El SearchHire pasa a esperar decisión del cliente
                                var awaitingClientDecisionStatus = await _context.SystemStatuses
                                    .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && 
                                                            s.StatusValue == "awaiting_client_decision");
                                
                                if (appointmentReportSentStatus != null && awaitingClientDecisionStatus != null)
                                {
                                    // Marcar la cita como informe enviado
                                    timer.Appointment.StatusId = appointmentReportSentStatus.Id;
                                    timer.Appointment.UpdatedAt = DateTime.UtcNow;
                                    
                                    // Actualizar el SearchHire para que use el estado del sistema centralizado
                                    var awaitingStatusId = await GetStatusIdByValueAsync(SearchHireStatus.AwaitingClientDecision.ToStringValue());
                                    timer.Appointment.SearchHire.StatusId = awaitingStatusId;
                                    timer.Appointment.SearchHire.UpdatedAt = DateTime.UtcNow;
                                    
                                    _logger.LogInformation("Appointment {AppointmentId} automatically completed and SearchHire moved to awaiting_client_decision - all required files were uploaded", timer.Appointment.Id);
                                }
                            }
                            else
                            {
                                // Si faltan archivos, cancelar por no reporte
                                var noReportStatus = await _context.SystemStatuses
                                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                            s.StatusValue == "appointment_cancelled_by_no_report");
                                
                                if (noReportStatus != null)
                                {
                                    timer.Appointment.StatusId = noReportStatus.Id;
                                    timer.Appointment.UpdatedAt = DateTime.UtcNow;
                                    
                                    // También actualizar el SearchHire para que use el estado del sistema centralizado
                                    var cancelledStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                                    timer.Appointment.SearchHire.StatusId = cancelledStatusId;
                                    timer.Appointment.SearchHire.UpdatedAt = DateTime.UtcNow;
                                    
                                    // 🎯 PROCESAR DINERO AUTOMÁTICAMENTE
                                    try
                                    {
                                        var moneySuccess = await _refundService.ProcessMoneyDistributionAsync(
                                            timer.Appointment.SearchHireId,
                                            "appointment_cancelled_by_no_report",
                                            "Expert did not submit report within 24h - automatic cancellation",
                                            null);
                                        
                                        if (moneySuccess)
                                        {
                                            _logger.LogInformation("✅ Money distribution processed for appointment {AppointmentId} due to no report", timer.Appointment.Id);
                                        }
                                        else
                                        {
                                            _logger.LogError("❌ Failed to process money distribution for appointment {AppointmentId} due to no report", timer.Appointment.Id);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "❌ Error processing money distribution for appointment {AppointmentId} due to no report", timer.Appointment.Id);
                                    }
                                    
                                    _logger.LogInformation("Appointment {AppointmentId} cancelled due to expert not submitting report within 24h - missing files: {MissingFiles}", 
                                        timer.Appointment.Id, validationResult.ErrorMessage);
                                }
                            }
                            break;
                    }
                }

                // 2. Verificar citas confirmadas que deben cambiar a awaiting_report (3 horas después)
                var cutoffTime = DateTime.UtcNow.AddHours(-3);
                var confirmedAppointments = await _context.Appointments
                    .Include(a => a.Status)
                    .Where(a => a.Status.StatusValue == "appointment_confirmed")
                    .ToListAsync();

                // Filtrar en memoria para evitar problemas de traducción de EF
                confirmedAppointments = confirmedAppointments
                    .Where(a => a.ProposedDate.Add(a.ProposedTime) <= cutoffTime)
                    .ToList();

                var awaitingReportStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                            s.StatusValue == "appointment_awaiting_report");

                if (awaitingReportStatus != null)
                {
                    foreach (var appointment in confirmedAppointments)
                    {
                        appointment.StatusId = awaitingReportStatus.Id;
                        appointment.UpdatedAt = DateTime.UtcNow;
                        
                        // Crear timer para reporte del experto (24 horas)
                        var expertReportTimer = new AppointmentTimer
                        {
                            AppointmentId = appointment.Id,
                            TimerType = "expert_report",
                            StartTime = DateTime.UtcNow,
                            EndTime = DateTime.UtcNow.AddHours(24),
                            IsExpired = false,
                            CreatedAt = DateTime.UtcNow
                        };

                        _context.AppointmentTimers.Add(expertReportTimer);
                        
                        _logger.LogInformation("Appointment {AppointmentId} changed from confirmed to awaiting_report with 24h timer for expert report", appointment.Id);
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
                        .ThenInclude(sh => sh.Status)
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

                // Validar que se hayan subido los archivos obligatorios
                var validationResult = await ValidateRequiredDeliverablesAsync(appointment.SearchHire);
                if (!validationResult.IsValid)
                {
                    throw new InvalidOperationException(validationResult.ErrorMessage);
                }

                // Obtener el estado appointment_report_sent para la cita
                var appointmentReportSentStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                            s.StatusValue == "appointment_report_sent");

                // Obtener el estado awaiting_client_decision para el SearchHire
                var awaitingClientDecisionStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && 
                                            s.StatusValue == "awaiting_client_decision");

                if (appointmentReportSentStatus == null)
                    throw new InvalidOperationException("Appointment report sent status not found");
                
                if (awaitingClientDecisionStatus == null)
                    throw new InvalidOperationException("Awaiting client decision status not found");

                // Actualizar la cita como informe enviado
                appointment.StatusId = appointmentReportSentStatus.Id;
                appointment.UpdatedAt = DateTime.UtcNow;

                // Actualizar el SearchHire según el mapeo de estados
                _logger.LogInformation("=== STARTING STATUS MAPPING ===");
                var appointmentStatusEnum = AppointmentStatus.AppointmentReportSent;
                _logger.LogInformation("🔍 Getting target SearchHire status for appointment status: {AppointmentStatus}", appointmentStatusEnum);
                
                var targetSearchHireStatus = await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatusEnum);
                _logger.LogInformation("🎯 Target SearchHire status result: {TargetStatus}", targetSearchHireStatus);
                
                if (targetSearchHireStatus.HasValue)
                {
                    var oldStatusValue = appointment.SearchHire.Status?.StatusValue ?? "unknown";
                    var statusId = await GetStatusIdByValueAsync(targetSearchHireStatus.Value.ToStringValue());
                    appointment.SearchHire.StatusId = statusId;
                    appointment.SearchHire.UpdatedAt = DateTime.UtcNow;
                    _logger.LogInformation("✅ Updated SearchHire {SearchHireId} status: {OldStatus} → {NewStatus}", 
                        appointment.SearchHire.Id, oldStatusValue, targetSearchHireStatus.Value.ToStringValue());
                }
                else
                {
                    _logger.LogWarning("⚠️ No target SearchHire status found for appointment status: {AppointmentStatus}", appointmentStatusEnum);
                }
                _logger.LogInformation("=== STATUS MAPPING COMPLETED ===");

                // Marcar timers de expert_report como expirados
                var expertReportTimers = await _context.AppointmentTimers
                    .Where(t => t.AppointmentId == appointment.Id && 
                               t.TimerType == "expert_report" && 
                               !t.IsExpired)
                    .ToListAsync();

                foreach (var timer in expertReportTimers)
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                }

                _logger.LogInformation("=== SAVING CHANGES TO DATABASE ===");
                await _context.SaveChangesAsync();
                _logger.LogInformation("=== DATABASE CHANGES SAVED ===");

                // Cargar la cita actualizada con todas las relaciones
                var updatedAppointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
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


        private async Task<(bool IsValid, string ErrorMessage)> ValidateRequiredDeliverablesAsync(SearchHire searchHire)
        {
            try
            {
                // Cargar el SearchHire con todas las relaciones necesarias
                var hire = await _context.SearchHires
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.SelectedDeliverableTypes)
                            .ThenInclude(ssdt => ssdt.DeliverableType)
                    .Include(sh => sh.Deliverables)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHire.Id);

                if (hire == null)
                {
                    return (false, "SearchHire not found");
                }

                // Obtener los tipos de entregables requeridos para este servicio
                var requiredDeliverableTypes = hire.SearchService.SelectedDeliverableTypes
                    .Where(ssdt => ssdt.IsSelected)
                    .Select(ssdt => ssdt.DeliverableType)
                    .ToList();

                if (!requiredDeliverableTypes.Any())
                {
                    return (true, string.Empty); // No hay entregables requeridos
                }

                // Obtener los archivos ya subidos
                var uploadedDeliverables = hire.Deliverables.ToList();

                // Verificar PDF obligatorio
                var pdfType = requiredDeliverableTypes.FirstOrDefault(dt => dt.Name == "PDF");
                if (pdfType != null)
                {
                    var hasPdf = uploadedDeliverables.Any(d => d.Type == "pdf");
                    if (!hasPdf)
                    {
                        return (false, "Es obligatorio subir un archivo PDF antes de enviar el reporte");
                    }
                }

                // Verificar video si está configurado
                var videoType = requiredDeliverableTypes.FirstOrDefault(dt => dt.Name == "Video");
                if (videoType != null)
                {
                    var hasVideo = uploadedDeliverables.Any(d => d.Type == "video");
                    if (!hasVideo)
                    {
                        return (false, "Es obligatorio subir un archivo de video antes de enviar el reporte");
                    }
                }

                _logger.LogInformation("All required deliverables validated for SearchHire {HireId}", hire.Id);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating required deliverables for SearchHire {HireId}", searchHire.Id);
                return (false, "Error validating required deliverables");
            }
        }

        /// <summary>
        /// Valida que la ubicación propuesta para la cita esté dentro del rango del experto
        /// definido cuando se contrató el servicio. Esto asegura que el experto no pueda
        /// cambiar su ubicación después de ser contratado para afectar las citas.
        /// </summary>
        private async Task ValidateAppointmentLocationAsync(SearchHire searchHire, decimal? appointmentLatitude, decimal? appointmentLongitude)
        {
            try
            {
                // Cargar el SearchHire con todas las relaciones necesarias
                var hire = await _context.SearchHires
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ExpertProfile)
                    .Include(sh => sh.Search)
                        .ThenInclude(s => s.SearchParameters)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHire.Id);

                if (hire == null)
                {
                    throw new ArgumentException("SearchHire not found");
                }

                // Si no se proporcionan coordenadas para la cita, no validar
                if (!appointmentLatitude.HasValue || !appointmentLongitude.HasValue)
                {
                    _logger.LogWarning("No appointment coordinates provided for SearchHire {SearchHireId}, skipping location validation", hire.Id);
                    return;
                }

                // Obtener las coordenadas del experto al momento de la contratación
                if (hire.SearchService?.ExpertProfile == null)
                {
                    throw new InvalidOperationException("Expert profile not found for the service");
                }

                // Parsear coordenadas del experto
                if (!decimal.TryParse(hire.SearchService.ExpertProfile.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLatitude))
                {
                    throw new InvalidOperationException("Invalid expert latitude in service");
                }

                if (!decimal.TryParse(hire.SearchService.ExpertProfile.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLongitude))
                {
                    throw new InvalidOperationException("Invalid expert longitude in service");
                }

                // Obtener el rango de ubicación del Search original desde SearchParameters
                var searchLocationRange = hire.Search.SearchParameters?.FirstOrDefault()?.LocationRange;
                if (searchLocationRange == null || searchLocationRange <= 0)
                {
                    _logger.LogWarning("No location range defined for Search {SearchId}, using default range of 50km", hire.SearchId);
                    searchLocationRange = 50; // Rango por defecto
                }

                // Calcular la distancia entre la ubicación del experto y la ubicación propuesta para la cita
                var distance = CalculateDistance(expertLatitude, expertLongitude, appointmentLatitude.Value, appointmentLongitude.Value);

                _logger.LogInformation("Validating appointment location for SearchHire {SearchHireId}: Expert at ({ExpertLat}, {ExpertLon}), Appointment at ({AppointmentLat}, {AppointmentLon}), Distance: {Distance}km, MaxRange: {MaxRange}km",
                    hire.Id, expertLatitude, expertLongitude, appointmentLatitude.Value, appointmentLongitude.Value, distance, searchLocationRange);

                // Verificar que la distancia esté dentro del rango permitido
                if (distance > searchLocationRange)
                {
                    throw new InvalidOperationException(
                        $"La ubicación propuesta para la cita está fuera del rango del experto. " +
                        $"Distancia: {distance:F1} km, Rango máximo: {searchLocationRange} km. " +
                        $"El experto solo puede realizar citas dentro de su rango de servicio original."
                    );
                }

                _logger.LogInformation("Appointment location validation passed for SearchHire {SearchHireId}", hire.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating appointment location for SearchHire {SearchHireId}", searchHire.Id);
                throw;
            }
        }

        /// <summary>
        /// Calcula la distancia entre dos puntos geográficos usando la fórmula de Haversine
        /// </summary>
        private static decimal CalculateDistance(decimal lat1, decimal lon1, decimal lat2, decimal lon2)
        {
            const double R = 6371; // Radio de la Tierra en km
            var dLat = (double)(lat2 - lat1) * Math.PI / 180;
            var dLon = (double)(lon2 - lon1) * Math.PI / 180;
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos((double)lat1 * Math.PI / 180) * Math.Cos((double)lat2 * Math.PI / 180) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return (decimal)(R * c);
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
                ClientCancellationCount = appointment.ClientCancellationCount,
                ExpertCancellationCount = appointment.ExpertCancellationCount,
                LastRejectionAt = appointment.LastRejectionAt,
                LastProposalAt = appointment.LastProposalAt,
                LastResponseAt = appointment.LastResponseAt,
                IsLocked = appointment.IsLocked,
                CreatedAt = appointment.CreatedAt,
                UpdatedAt = appointment.UpdatedAt,
                ClientName = appointment.SearchHire?.Client?.Name ?? string.Empty,
                ExpertName = appointment.SearchHire?.Expert?.Name ?? string.Empty,
                Amount = appointment.SearchHire?.Amount ?? 0,
                // ✅ NUEVOS CAMPOS: Información de ubicación del experto
                ExpertLatitude = appointment.SearchHire?.SearchService?.ExpertProfile?.Latitude,
                ExpertLongitude = appointment.SearchHire?.SearchService?.ExpertProfile?.Longitude,
                LocationRange = appointment.SearchHire?.Search?.SearchParameters?.FirstOrDefault()?.LocationRange ?? 50, // Rango por defecto de 50km
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
