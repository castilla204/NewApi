using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;
using newApi.Common;

namespace newApi.Services
{
    public interface IAppointmentService
    {
        Task<AppointmentDto?> GetAppointmentAsync(int appointmentId);
        Task<AppointmentDto?> GetAppointmentBySearchHireIdAsync(int searchHireId);
        Task<List<AppointmentDto>> GetUserAppointmentsAsync(int userId);
        Task<AppointmentDto> CreateAppointmentAsync(CreateAppointmentDto dto);
        Task<AppointmentDto> ProposeAppointmentAsync(int searchHireId, ProposeAppointmentDto dto, int userId);
        Task<AppointmentDto> ConfirmAppointmentAsync(ConfirmAppointmentDto dto, int userId);
        Task<AppointmentDto> RejectAppointmentAsync(RejectAppointmentDto dto, int userId);
        Task<AppointmentDto> CancelAppointmentAsync(CancelAppointmentDto dto, int userId);
        Task<AppointmentDto> MarkCompletedAsync(MarkCompletedDto dto, int userId);
        Task<AppointmentDto> CreateDisputeAsync(CreateAppointmentDisputeDto dto, int userId);
        Task CheckAppointmentTimersAsync();
        Task<AppointmentMetricsDto> GetAppointmentMetricsAsync();
        Task<List<AppointmentDto>> GetAppointmentDisputesAsync();
        Task<bool> ResolveDisputeAsync(ResolveAppointmentDisputeDto dto, int adminId);
    }

    public class AppointmentService : IAppointmentService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AppointmentService> _logger;
        private readonly ICheckingClientDecisionService _checkingClientDecisionService;

        public AppointmentService(
            AppDbContext context, 
            ILogger<AppointmentService> logger,
            ICheckingClientDecisionService checkingClientDecisionService)
        {
            _context = context;
            _logger = logger;
            _checkingClientDecisionService = checkingClientDecisionService;
        }

        public async Task<AppointmentDto?> GetAppointmentAsync(int appointmentId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .ThenInclude(sh => sh.Client)
                .Include(a => a.SearchHire)
                .ThenInclude(sh => sh.Expert)
                .Include(a => a.Timers)
                .FirstOrDefaultAsync(a => a.Id == appointmentId);

            return appointment != null ? MapToDto(appointment) : null;
        }

        public async Task<AppointmentDto?> GetAppointmentBySearchHireIdAsync(int searchHireId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .ThenInclude(sh => sh.Client)
                .Include(a => a.SearchHire)
                .ThenInclude(sh => sh.Expert)
                .Include(a => a.Timers)
                .FirstOrDefaultAsync(a => a.SearchHireId == searchHireId);

            return appointment != null ? MapToDto(appointment) : null;
        }

        public async Task<List<AppointmentDto>> GetUserAppointmentsAsync(int userId)
        {
            var appointments = await _context.Appointments
                .Include(a => a.SearchHire)
                .ThenInclude(sh => sh.Client)
                .Include(a => a.SearchHire)
                .ThenInclude(sh => sh.Expert)
                .Include(a => a.Timers)
                .Where(a => a.SearchHire.ClientId == userId || a.SearchHire.ExpertId == userId)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();

            return appointments.Select(MapToDto).ToList();
        }

        public async Task<AppointmentDto> CreateAppointmentAsync(CreateAppointmentDto dto)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var appointment = new Appointment
                {
                    SearchHireId = dto.SearchHireId,
                    Status = AppointmentStatus.AwaitingAppointment.ToStringValue(),
                    ProposedDate = dto.ProposedDate,
                    ProposedTime = dto.ProposedTime,
                    Location = dto.Location,
                    Latitude = dto.Latitude,
                    Longitude = dto.Longitude,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.Appointments.Add(appointment);
                await _context.SaveChangesAsync();

                // Crear timer de 48h para proponer cita
                await CreateTimerAsync(appointment.Id, "proposal", 48);

                await transaction.CommitAsync();

                _logger.LogInformation("Appointment created: {AppointmentId} for SearchHire: {SearchHireId}", 
                    appointment.Id, dto.SearchHireId);

                return await GetAppointmentAsync(appointment.Id) ?? throw new InvalidOperationException("Failed to retrieve created appointment");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to create appointment for SearchHire: {SearchHireId}", dto.SearchHireId);
                throw;
            }
        }

        public async Task<AppointmentDto> ProposeAppointmentAsync(int searchHireId, ProposeAppointmentDto dto, int userId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .FirstOrDefaultAsync(a => a.SearchHireId == searchHireId);

            if (appointment == null)
                throw new InvalidOperationException("Appointment not found");

            // Verificar que el usuario es el cliente
            if (appointment.SearchHire.ClientId != userId)
                throw new UnauthorizedAccessException("Only the client can propose appointments");

            // Verificar estado
            if (appointment.Status != AppointmentStatus.AwaitingAppointment.ToStringValue() && 
                appointment.Status != AppointmentStatus.AppointmentRejected.ToStringValue())
                throw new InvalidOperationException($"Cannot propose appointment in status: {appointment.Status}");

            // Verificar restricción de 12h
            var appointmentDateTime = dto.ProposedDate.Date.Add(dto.ProposedTime);
            if (appointmentDateTime <= DateTime.UtcNow.AddHours(12))
                throw new InvalidOperationException("Cannot propose appointment less than 12 hours in advance");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                appointment.Status = AppointmentStatus.AppointmentProposed.ToStringValue();
                appointment.ProposedDate = dto.ProposedDate;
                appointment.ProposedTime = dto.ProposedTime;
                appointment.Location = dto.Location;
                appointment.Latitude = dto.Latitude;
                appointment.Longitude = dto.Longitude;
                appointment.LastProposalAt = DateTime.UtcNow;
                appointment.UpdatedAt = DateTime.UtcNow;

                // Crear timer de 48h para respuesta del experto
                await CreateTimerAsync(appointment.Id, "response", 48);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Appointment proposed: {AppointmentId} by user: {UserId}", appointment.Id, userId);

                return await GetAppointmentAsync(appointment.Id) ?? throw new InvalidOperationException("Failed to retrieve updated appointment");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to propose appointment: {AppointmentId}", appointment.Id);
                throw;
            }
        }

        public async Task<AppointmentDto> ConfirmAppointmentAsync(ConfirmAppointmentDto dto, int userId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .FirstOrDefaultAsync(a => a.Id == dto.AppointmentId);

            if (appointment == null)
                throw new InvalidOperationException("Appointment not found");

            // Verificar que el usuario es el experto
            if (appointment.SearchHire.ExpertId != userId)
                throw new UnauthorizedAccessException("Only the expert can confirm appointments");

            // Verificar estado
            if (appointment.Status != AppointmentStatus.AppointmentProposed.ToStringValue())
                throw new InvalidOperationException($"Cannot confirm appointment in status: {appointment.Status}");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                appointment.Status = AppointmentStatus.AppointmentConfirmed.ToStringValue();
                appointment.LastResponseAt = DateTime.UtcNow;
                appointment.UpdatedAt = DateTime.UtcNow;

                // Crear timer de 3h para cambio automático a AwaitingClientDecision
                var appointmentDateTime = appointment.ProposedDate.Date.Add(appointment.ProposedTime);
                var timerEndTime = appointmentDateTime.AddHours(3);
                await CreateTimerAsync(appointment.Id, "auto_awaiting_client_decision", timerEndTime);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Appointment confirmed: {AppointmentId} by expert: {UserId}", appointment.Id, userId);

                return await GetAppointmentAsync(appointment.Id) ?? throw new InvalidOperationException("Failed to retrieve updated appointment");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to confirm appointment: {AppointmentId}", appointment.Id);
                throw;
            }
        }

        public async Task<AppointmentDto> RejectAppointmentAsync(RejectAppointmentDto dto, int userId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .FirstOrDefaultAsync(a => a.Id == dto.AppointmentId);

            if (appointment == null)
                throw new InvalidOperationException("Appointment not found");

            // Verificar que el usuario es el experto
            if (appointment.SearchHire.ExpertId != userId)
                throw new UnauthorizedAccessException("Only the expert can reject appointments");

            // Verificar estado
            if (appointment.Status != AppointmentStatus.AppointmentProposed.ToStringValue())
                throw new InvalidOperationException($"Cannot reject appointment in status: {appointment.Status}");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                appointment.RejectionCount++;
                appointment.LastRejectionAt = DateTime.UtcNow;
                appointment.LastResponseAt = DateTime.UtcNow;
                appointment.UpdatedAt = DateTime.UtcNow;

                // Verificar si es la tercera vez
                if (appointment.RejectionCount >= 3)
                {
                    appointment.Status = AppointmentStatus.AppointmentCancelledByNoResponse.ToStringValue();
                    appointment.SearchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
                    
                    // 100% al cliente
                    await ProcessCancellationRefundAsync(appointment.SearchHireId, 1.0m, 0.0m);
                }
                else
                {
                    appointment.Status = AppointmentStatus.AppointmentRejected.ToStringValue();
                    // Crear timer de 48h para nueva propuesta del cliente
                    await CreateTimerAsync(appointment.Id, "proposal", 48);
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Appointment rejected: {AppointmentId} by expert: {UserId}, rejection count: {Count}", 
                    appointment.Id, userId, appointment.RejectionCount);

                return await GetAppointmentAsync(appointment.Id) ?? throw new InvalidOperationException("Failed to retrieve updated appointment");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to reject appointment: {AppointmentId}", appointment.Id);
                throw;
            }
        }

        public async Task<AppointmentDto> CancelAppointmentAsync(CancelAppointmentDto dto, int userId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .FirstOrDefaultAsync(a => a.Id == dto.AppointmentId);

            if (appointment == null)
                throw new InvalidOperationException("Appointment not found");

            // Verificar que el usuario es parte de la cita
            if (appointment.SearchHire.ClientId != userId && appointment.SearchHire.ExpertId != userId)
                throw new UnauthorizedAccessException("Only the client or expert can cancel appointments");

            // Verificar estado
            if (appointment.Status != AppointmentStatus.AppointmentConfirmed.ToStringValue())
                throw new InvalidOperationException($"Cannot cancel appointment in status: {appointment.Status}");

            // Verificar restricción de 12h
            var appointmentDateTime = appointment.ProposedDate.Date.Add(appointment.ProposedTime);
            if (appointmentDateTime <= DateTime.UtcNow.AddHours(12))
                throw new InvalidOperationException("Cannot cancel appointment less than 12 hours in advance");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var isClient = appointment.SearchHire.ClientId == userId;
                appointment.CancellationCount++;
                appointment.UpdatedAt = DateTime.UtcNow;

                if (isClient)
                {
                    if (appointment.CancellationCount == 1)
                    {
                        appointment.Status = AppointmentStatus.AppointmentCancelledByClient.ToStringValue();
                        // Crear timer de 24h para reprogramar
                        await CreateTimerAsync(appointment.Id, "reprogram", 24);
                    }
                    else
                    {
                        appointment.Status = AppointmentStatus.AppointmentCancelledByClientSecond.ToStringValue();
                        appointment.SearchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
                        // 92% cliente, 8% experto
                        await ProcessCancellationRefundAsync(appointment.SearchHireId, 0.92m, 0.08m);
                    }
                }
                else
                {
                    appointment.Status = AppointmentStatus.AppointmentCancelledByExpert.ToStringValue();
                    appointment.SearchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
                    // 92% cliente, 8% experto
                    await ProcessCancellationRefundAsync(appointment.SearchHireId, 0.92m, 0.08m);
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Appointment cancelled: {AppointmentId} by user: {UserId}, cancellation count: {Count}", 
                    appointment.Id, userId, appointment.CancellationCount);

                return await GetAppointmentAsync(appointment.Id) ?? throw new InvalidOperationException("Failed to retrieve updated appointment");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to cancel appointment: {AppointmentId}", appointment.Id);
                throw;
            }
        }

        public async Task<AppointmentDto> MarkCompletedAsync(MarkCompletedDto dto, int userId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .FirstOrDefaultAsync(a => a.Id == dto.AppointmentId);

            if (appointment == null)
                throw new InvalidOperationException("Appointment not found");

            // Verificar que el usuario es parte de la cita
            if (appointment.SearchHire.ClientId != userId && appointment.SearchHire.ExpertId != userId)
                throw new UnauthorizedAccessException("Only the client or expert can mark appointments as completed");

            // Verificar estado
            if (appointment.Status != AppointmentStatus.AppointmentConfirmed.ToStringValue())
                throw new InvalidOperationException($"Cannot mark appointment as completed in status: {appointment.Status}");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                appointment.Status = AppointmentStatus.AppointmentCompleted.ToStringValue();
                appointment.CompletedAt = DateTime.UtcNow;
                appointment.CompletedBy = userId;
                appointment.UpdatedAt = DateTime.UtcNow;

                // Cambiar a AwaitingClientDecision para que el cliente pueda aprobar
                appointment.SearchHire.Status = SearchHireStatus.AwaitingClientDecision.ToStringValue();
                appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Appointment marked as completed: {AppointmentId} by user: {UserId}", appointment.Id, userId);

                return await GetAppointmentAsync(appointment.Id) ?? throw new InvalidOperationException("Failed to retrieve updated appointment");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to mark appointment as completed: {AppointmentId}", appointment.Id);
                throw;
            }
        }

        public async Task<AppointmentDto> CreateDisputeAsync(CreateAppointmentDisputeDto dto, int userId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .FirstOrDefaultAsync(a => a.Id == dto.AppointmentId);

            if (appointment == null)
                throw new InvalidOperationException("Appointment not found");

            // SOLO EL CLIENTE PUEDE CREAR DISPUTAS
            if (appointment.SearchHire.ClientId != userId)
                throw new UnauthorizedAccessException("Only the client can create disputes");

            // Verificar estado - SOLO cuando se llega a AwaitingClientDecision
            if (appointment.SearchHire.Status != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                throw new InvalidOperationException("Can only create disputes when status is AwaitingClientDecision");

            // Verificar que no han pasado más de 24h desde que cambió a AwaitingClientDecision
            if (appointment.SearchHire.UpdatedAt.HasValue && 
                appointment.SearchHire.UpdatedAt.Value.AddHours(24) < DateTime.UtcNow)
                throw new InvalidOperationException("Cannot create dispute after 24 hours");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                appointment.Status = AppointmentStatus.AppointmentDisputed.ToStringValue();
                appointment.DisputeReason = dto.DisputeReason;
                appointment.UpdatedAt = DateTime.UtcNow;

                appointment.SearchHire.Status = SearchHireStatus.Disputed.ToStringValue();
                appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

                // Crear disputa usando el sistema existente
                var dispute = new Dispute
                {
                    SearchHireId = appointment.SearchHireId,
                    ReporterId = userId,
                    Reason = dto.DisputeReason,
                    ResolutionComments = dto.EvidenceText, // Usar ResolutionComments para almacenar evidencia
                    Status = "pending",
                    CreatedAt = DateTime.UtcNow
                };

                _context.Disputes.Add(dispute);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Appointment dispute created: {AppointmentId} by user: {UserId}", appointment.Id, userId);

                return await GetAppointmentAsync(appointment.Id) ?? throw new InvalidOperationException("Failed to retrieve updated appointment");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to create appointment dispute: {AppointmentId}", appointment.Id);
                throw;
            }
        }

        public async Task CheckAppointmentTimersAsync()
        {
            var expiredTimers = await _context.AppointmentTimers
                .Where(t => t.EndTime <= DateTime.UtcNow && !t.IsExpired)
                .ToListAsync();

            foreach (var timer in expiredTimers)
            {
                try
                {
                    switch (timer.TimerType)
                    {
                        case "proposal":
                            await ProcessProposalTimeoutAsync(timer.AppointmentId);
                            break;
                        case "response":
                            await ProcessResponseTimeoutAsync(timer.AppointmentId);
                            break;
                        case "auto_awaiting_client_decision":
                            await ProcessAutoAwaitingClientDecisionAsync(timer.AppointmentId);
                            break;
                        case "reprogram":
                            await ProcessReprogramTimeoutAsync(timer.AppointmentId);
                            break;
                    }

                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing expired timer: {TimerId} for appointment: {AppointmentId}", 
                        timer.Id, timer.AppointmentId);
                }
            }

            if (expiredTimers.Any())
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("Processed {Count} expired appointment timers", expiredTimers.Count);
            }
        }

        public async Task<AppointmentMetricsDto> GetAppointmentMetricsAsync()
        {
            var metrics = new AppointmentMetricsDto
            {
                TotalAppointments = await _context.Appointments.CountAsync(),
                PendingDisputes = await _context.Appointments.CountAsync(a => a.Status == AppointmentStatus.AppointmentDisputed.ToStringValue()),
                ClientNoShows = await _context.Appointments.CountAsync(a => a.Status == AppointmentStatus.AppointmentCancelledByClientSecond.ToStringValue()),
                ExpertNoShows = await _context.Appointments.CountAsync(a => a.Status == AppointmentStatus.AppointmentCancelledByExpert.ToStringValue()),
                SuccessfulAppointments = await _context.Appointments.CountAsync(a => a.Status == AppointmentStatus.AppointmentCompleted.ToStringValue()),
                CancelledAppointments = await _context.Appointments.CountAsync(a => 
                    a.Status == AppointmentStatus.AppointmentCancelledByClient.ToStringValue() ||
                    a.Status == AppointmentStatus.AppointmentCancelledByClientSecond.ToStringValue() ||
                    a.Status == AppointmentStatus.AppointmentCancelledByExpert.ToStringValue() ||
                    a.Status == AppointmentStatus.AppointmentCancelledByNoResponse.ToStringValue()),
                AwaitingAppointment = await _context.Appointments.CountAsync(a => a.Status == AppointmentStatus.AwaitingAppointment.ToStringValue()),
                AppointmentProposed = await _context.Appointments.CountAsync(a => a.Status == AppointmentStatus.AppointmentProposed.ToStringValue()),
                AppointmentConfirmed = await _context.Appointments.CountAsync(a => a.Status == AppointmentStatus.AppointmentConfirmed.ToStringValue()),
                AppointmentRejected = await _context.Appointments.CountAsync(a => a.Status == AppointmentStatus.AppointmentRejected.ToStringValue())
            };

            return metrics;
        }

        public async Task<List<AppointmentDto>> GetAppointmentDisputesAsync()
        {
            var appointments = await _context.Appointments
                .Include(a => a.SearchHire)
                .ThenInclude(sh => sh.Client)
                .Include(a => a.SearchHire)
                .ThenInclude(sh => sh.Expert)
                .Include(a => a.Timers)
                .Where(a => a.Status == AppointmentStatus.AppointmentDisputed.ToStringValue())
                .OrderByDescending(a => a.UpdatedAt)
                .ToListAsync();

            return appointments.Select(MapToDto).ToList();
        }

        public async Task<bool> ResolveDisputeAsync(ResolveAppointmentDisputeDto dto, int adminId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .FirstOrDefaultAsync(a => a.Id == dto.AppointmentId);

            if (appointment == null)
                return false;

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                switch (dto.Resolution)
                {
                    case "client_no_show":
                        // Cliente no se presentó - 80% al experto
                        await ProcessNoShowRefundAsync(appointment.SearchHireId, 0.0m, 0.8m);
                        appointment.SearchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
                        break;
                        
                    case "expert_no_show":
                        // Experto no se presentó - 100% al cliente
                        await ProcessNoShowRefundAsync(appointment.SearchHireId, 1.0m, 0.0m);
                        appointment.SearchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
                        break;
                        
                    case "both_present":
                        // Ambos se presentaron - proceder normalmente
                        appointment.SearchHire.Status = SearchHireStatus.Completed.ToStringValue();
                        await _checkingClientDecisionService.ProcessTransferToExpert(appointment.SearchHireId);
                        break;
                        
                    case "technical_issue":
                        // Problema técnico - reprogramar
                        appointment.Status = AppointmentStatus.AwaitingAppointment.ToStringValue();
                        appointment.SearchHire.Status = SearchHireStatus.Pending.ToStringValue();
                        await CreateTimerAsync(appointment.Id, "proposal", 48);
                        break;
                }

                appointment.Status = AppointmentStatus.AppointmentCompleted.ToStringValue();
                appointment.UpdatedAt = DateTime.UtcNow;
                appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Appointment dispute resolved: {AppointmentId} by admin: {AdminId}, resolution: {Resolution}", 
                    appointment.Id, adminId, dto.Resolution);

                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to resolve appointment dispute: {AppointmentId}", appointment.Id);
                return false;
            }
        }

        #region Private Methods

        private async Task CreateTimerAsync(int appointmentId, string timerType, int hours)
        {
            var timer = new AppointmentTimer
            {
                AppointmentId = appointmentId,
                TimerType = timerType,
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow.AddHours(hours),
                CreatedAt = DateTime.UtcNow
            };

            _context.AppointmentTimers.Add(timer);
        }

        private async Task CreateTimerAsync(int appointmentId, string timerType, DateTime endTime)
        {
            var timer = new AppointmentTimer
            {
                AppointmentId = appointmentId,
                TimerType = timerType,
                StartTime = DateTime.UtcNow,
                EndTime = endTime,
                CreatedAt = DateTime.UtcNow
            };

            _context.AppointmentTimers.Add(timer);
        }

        private async Task ProcessCancellationRefundAsync(int searchHireId, decimal clientPercentage, decimal expertPercentage)
        {
            var searchHire = await _context.SearchHires.FindAsync(searchHireId);
            if (searchHire == null) return;

            var clientAmount = searchHire.Amount * clientPercentage;
            var expertAmount = searchHire.Amount * expertPercentage;

            // Procesar devoluciones (implementar según tu sistema de pagos)
            if (clientAmount > 0)
            {
                // Procesar devolución al cliente
                _logger.LogInformation("Processing client refund: {Amount} for SearchHire: {SearchHireId}", clientAmount, searchHireId);
            }

            if (expertAmount > 0)
            {
                // Procesar devolución al experto
                _logger.LogInformation("Processing expert refund: {Amount} for SearchHire: {SearchHireId}", expertAmount, searchHireId);
            }
        }

        private async Task ProcessNoShowRefundAsync(int searchHireId, decimal clientPercentage, decimal expertPercentage)
        {
            var searchHire = await _context.SearchHires.FindAsync(searchHireId);
            if (searchHire == null) return;

            var clientAmount = searchHire.Amount * clientPercentage;
            var expertAmount = searchHire.Amount * expertPercentage;

            // Procesar devoluciones (implementar según tu sistema de pagos)
            if (clientAmount > 0)
            {
                _logger.LogInformation("Processing client no-show refund: {Amount} for SearchHire: {SearchHireId}", clientAmount, searchHireId);
            }

            if (expertAmount > 0)
            {
                _logger.LogInformation("Processing expert no-show refund: {Amount} for SearchHire: {SearchHireId}", expertAmount, searchHireId);
            }
        }

        private async Task ProcessProposalTimeoutAsync(int appointmentId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .FirstOrDefaultAsync(a => a.Id == appointmentId);

            if (appointment == null) return;

            appointment.Status = AppointmentStatus.AppointmentCancelledByNoResponse.ToStringValue();
            appointment.SearchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
            appointment.UpdatedAt = DateTime.UtcNow;
            appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

            // 92% cliente, 8% experto
            await ProcessCancellationRefundAsync(appointment.SearchHireId, 0.92m, 0.08m);

            _logger.LogInformation("Appointment cancelled due to proposal timeout: {AppointmentId}", appointmentId);
        }

        private async Task ProcessResponseTimeoutAsync(int appointmentId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .FirstOrDefaultAsync(a => a.Id == appointmentId);

            if (appointment == null) return;

            appointment.Status = AppointmentStatus.AppointmentCancelledByNoResponse.ToStringValue();
            appointment.SearchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
            appointment.UpdatedAt = DateTime.UtcNow;
            appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

            // 92% cliente, 8% experto
            await ProcessCancellationRefundAsync(appointment.SearchHireId, 0.92m, 0.08m);

            _logger.LogInformation("Appointment cancelled due to response timeout: {AppointmentId}", appointmentId);
        }

        private async Task ProcessAutoAwaitingClientDecisionAsync(int appointmentId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .FirstOrDefaultAsync(a => a.Id == appointmentId);

            if (appointment == null) return;

            appointment.SearchHire.Status = SearchHireStatus.AwaitingClientDecision.ToStringValue();
            appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation("Appointment automatically moved to AwaitingClientDecision: {AppointmentId}", appointmentId);
        }

        private async Task ProcessReprogramTimeoutAsync(int appointmentId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .FirstOrDefaultAsync(a => a.Id == appointmentId);

            if (appointment == null) return;

            appointment.Status = AppointmentStatus.AppointmentCancelledByNoResponse.ToStringValue();
            appointment.SearchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
            appointment.UpdatedAt = DateTime.UtcNow;
            appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

            // 92% cliente, 8% experto
            await ProcessCancellationRefundAsync(appointment.SearchHireId, 0.92m, 0.08m);

            _logger.LogInformation("Appointment cancelled due to reprogram timeout: {AppointmentId}", appointmentId);
        }

        private AppointmentDto MapToDto(Appointment appointment)
        {
            return new AppointmentDto
            {
                Id = appointment.Id,
                SearchHireId = appointment.SearchHireId,
                Status = appointment.Status,
                ProposedDate = appointment.ProposedDate,
                ProposedTime = appointment.ProposedTime,
                Location = appointment.Location,
                Latitude = appointment.Latitude,
                Longitude = appointment.Longitude,
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
                ClientName = appointment.SearchHire.Client?.Name,
                ExpertName = appointment.SearchHire.Expert?.Name,
                Amount = appointment.SearchHire.Amount,
                Timers = appointment.Timers.Select(t => new AppointmentTimerDto
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
                }).ToList()
            };
        }

        #endregion
    }
}
