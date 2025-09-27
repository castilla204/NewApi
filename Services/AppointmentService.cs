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
        Task CheckAppointmentTimersAsync();
        Task<AppointmentMetricsDto> GetAppointmentMetricsAsync();
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

            // ✅ NUEVO: Si no existe Appointment, crear uno automáticamente
            if (appointment == null)
            {
                // Verificar que el SearchHire existe y el usuario es el cliente
                var searchHire = await _context.SearchHires
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId);
                
                if (searchHire == null)
                    throw new InvalidOperationException("SearchHire not found");
                
                if (searchHire.ClientId != userId)
                    throw new UnauthorizedAccessException("Only the client can propose appointments");
                
                // Verificar que el SearchHire está en estado válido para crear citas
                if (searchHire.Status != SearchHireStatus.Pending.ToStringValue())
                    throw new InvalidOperationException($"Cannot create appointment when SearchHire status is: {searchHire.Status}");
                
                // Crear el Appointment automáticamente
                appointment = new Appointment
                {
                    SearchHireId = searchHireId,
                    Status = AppointmentStatus.AwaitingAppointment.ToStringValue(),
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
                
                _logger.LogInformation("Appointment created automatically for SearchHire: {SearchHireId}", searchHireId);
            }
            else
            {
                // Verificar que el usuario es el cliente
                if (appointment.SearchHire.ClientId != userId)
                    throw new UnauthorizedAccessException("Only the client can propose appointments");
            }

            // Verificar estado - solo si el Appointment ya existía (no recién creado)
            if (appointment.Status != AppointmentStatus.AwaitingAppointment.ToStringValue() && 
                appointment.Status != AppointmentStatus.AppointmentRejected.ToStringValue() &&
                appointment.Status != AppointmentStatus.AppointmentCancelledByClient.ToStringValue())
                throw new InvalidOperationException($"Cannot propose appointment in status: {appointment.Status}");

            // Verificar restricción de 12h
            _logger.LogInformation("ProposedDate: {ProposedDate}, ProposedTime: {ProposedTime}", dto.ProposedDate, dto.ProposedTime);
            var appointmentDateTime = dto.ProposedDate.Date.Add(dto.ProposedTime);
            var now = DateTime.UtcNow;
            var minDateTime = now.AddHours(12);
            _logger.LogInformation("AppointmentDateTime: {AppointmentDateTime}, Now: {Now}, MinDateTime: {MinDateTime}", appointmentDateTime, now, minDateTime);
            
            if (appointmentDateTime <= minDateTime)
                throw new InvalidOperationException($"Cannot propose appointment less than 12 hours in advance. Appointment: {appointmentDateTime}, Minimum: {minDateTime}");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Si es reprogramación después de primera cancelación, cancelar timer de reprogramación
                if (appointment.Status == AppointmentStatus.AppointmentCancelledByClient.ToStringValue())
                {
                    await CancelReprogramTimerAsync(appointment.Id);
                    _logger.LogInformation("Cancelled reprogram timer for appointment: {AppointmentId}", appointment.Id);
                }

                appointment.Status = AppointmentStatus.AppointmentProposed.ToStringValue();
                appointment.ProposedDate = DateTime.SpecifyKind(dto.ProposedDate, DateTimeKind.Utc);
                appointment.ProposedTime = dto.ProposedTime;
                appointment.Location = dto.Location;
                appointment.Latitude = dto.Latitude;
                appointment.Longitude = dto.Longitude;
                appointment.DoorNumber = dto.DoorNumber;
                appointment.OwnerPhone = dto.OwnerPhone;
                appointment.SiteDetails = dto.SiteDetails;
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
                // Se crea un nuevo timer cada vez que se confirma (incluyendo reprogramaciones)
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

                // Verificar si es la segunda vez (cambiamos de 3 a 2 rechazos)
                if (appointment.RejectionCount >= 2)
                {
                    appointment.Status = AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue();
                    appointment.SearchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
                    
                    // Usar configuración de BD para distribución de dinero
                    await ProcessCancellationRefundAsync(appointment.SearchHireId, AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue());
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
                        // NO cambiar SearchHire.Status - dinero retenido
                        // Crear timer de 24h para reprogramar
                        await CreateTimerAsync(appointment.Id, "reprogram", 24);
                    }
                    else
                    {
                        appointment.Status = AppointmentStatus.AppointmentCancelledByClientSecond.ToStringValue();
                        appointment.SearchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
                        // Usar configuración de BD para distribución de dinero
                        await ProcessCancellationRefundAsync(appointment.SearchHireId, AppointmentStatus.AppointmentCancelledByClientSecond.ToStringValue());
                    }
                }
                else
                {
                    appointment.Status = AppointmentStatus.AppointmentCancelledByExpert.ToStringValue();
                    appointment.SearchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
                    // Usar configuración de BD para distribución de dinero
                    await ProcessCancellationRefundAsync(appointment.SearchHireId, AppointmentStatus.AppointmentCancelledByExpert.ToStringValue());
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
                PendingDisputes = 0, // Las disputas se manejan en el sistema general de Disputes
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

        private async Task CancelReprogramTimerAsync(int appointmentId)
        {
            var activeReprogramTimers = await _context.AppointmentTimers
                .Where(t => t.AppointmentId == appointmentId && 
                           t.TimerType == "reprogram" && 
                           !t.IsExpired)
                .ToListAsync();

            foreach (var timer in activeReprogramTimers)
            {
                timer.IsExpired = true;
                timer.ExpiredAt = DateTime.UtcNow;
            }
        }

        private async Task ProcessCancellationRefundAsync(int searchHireId, string status)
        {
            // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
            var searchHire = await _context.SearchHires
                .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", searchHireId)
                .Include(sh => sh.SearchService)
                .ThenInclude(ss => ss.ServiceType)
                .ThenInclude(st => st.ServiceTypeCategory)
                .Include(sh => sh.Client)
                .Include(sh => sh.Expert)
                .FirstOrDefaultAsync();
            
            if (searchHire == null) return;

            // Obtener configuración de distribución de dinero
            var config = await GetMoneyDistributionConfigAsync(status, searchHire.SearchService?.CategoryId, searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);
            
            if (config == null)
            {
                _logger.LogError("No money distribution configuration found for status: {Status} and ServiceTypeCategoryId: {ServiceTypeCategoryId}", 
                    status, searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);
                return;
            }

            var clientAmount = searchHire.Amount * (config.ClientPercentage / 100);
            var expertAmount = searchHire.Amount * (config.ExpertPercentage / 100);
            var platformAmount = searchHire.Amount * (config.PlatformPercentage / 100);

            // Procesar devoluciones reales
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                if (clientAmount > 0)
                {
                    // Reembolsar al cliente
                    searchHire.Client.Balance += clientAmount;
                    
                    // Crear transacción financiera de reembolso
                    var clientRefundTransaction = new FinancialTransaction
                    {
                        UserId = searchHire.ClientId,
                        Amount = clientAmount,
                        TransactionType = "Refund",
                        RelatedEntityType = "SearchHire",
                        RelatedEntityId = searchHireId,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.FinancialTransactions.Add(clientRefundTransaction);
                    
                    _logger.LogInformation("Processing client refund: {Amount} ({Percentage}%) for SearchHire: {SearchHireId} using config: {Source}", 
                        clientAmount, config.ClientPercentage, searchHireId, config.Source);
                }

                if (expertAmount > 0)
                {
                    // Reembolsar al experto
                    searchHire.Expert.Balance += expertAmount;
                    
                    // Crear transacción financiera de reembolso
                    var expertRefundTransaction = new FinancialTransaction
                    {
                        UserId = searchHire.ExpertId ?? 0,
                        Amount = expertAmount,
                        TransactionType = "Refund",
                        RelatedEntityType = "SearchHire",
                        RelatedEntityId = searchHireId,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.FinancialTransactions.Add(expertRefundTransaction);
                    
                    _logger.LogInformation("Processing expert refund: {Amount} ({Percentage}%) for SearchHire: {SearchHireId} using config: {Source}", 
                        expertAmount, config.ExpertPercentage, searchHireId, config.Source);
                }

                if (platformAmount > 0)
                {
                    _logger.LogInformation("Platform keeps: {Amount} ({Percentage}%) for SearchHire: {SearchHireId} using config: {Source}", 
                        platformAmount, config.PlatformPercentage, searchHireId, config.Source);
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                
                _logger.LogInformation("Successfully processed cancellation refunds for SearchHire: {SearchHireId}", searchHireId);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Error processing cancellation refunds for SearchHire: {SearchHireId}", searchHireId);
                throw;
            }
        }

        private async Task<MoneyDistributionConfigDto?> GetMoneyDistributionConfigAsync(string status, int? categoryId, int? serviceTypeCategoryId)
        {
            // 1. Buscar configuración específica por Category + ServiceTypeCategory
            if (categoryId.HasValue && serviceTypeCategoryId.HasValue)
            {
                var specificConfig = await _context.CategoryServiceTypeConfigs
                    .Include(cst => cst.Category)
                    .Include(cst => cst.ServiceTypeCategory)
                    .FirstOrDefaultAsync(cst => cst.CategoryId == categoryId.Value 
                                             && cst.ServiceTypeCategoryId == serviceTypeCategoryId.Value 
                                             && cst.Status == status 
                                             && cst.IsActive);

                if (specificConfig != null)
                {
                    return new MoneyDistributionConfigDto
                    {
                        ClientPercentage = specificConfig.ClientPercentage,
                        ExpertPercentage = specificConfig.ExpertPercentage,
                        PlatformPercentage = specificConfig.PlatformPercentage,
                        Source = "category_service_type",
                        CategoryName = specificConfig.Category?.Name,
                        ServiceTypeCategoryName = specificConfig.ServiceTypeCategory?.Name,
                        Status = status
                    };
                }
            }

            // 2. Buscar configuración específica por ServiceTypeCategory
            if (serviceTypeCategoryId.HasValue)
            {
                var categoryConfig = await _context.ServiceTypeCategoryConfigs
                    .Include(sc => sc.ServiceTypeCategory)
                    .FirstOrDefaultAsync(sc => sc.ServiceTypeCategoryId == serviceTypeCategoryId.Value 
                                             && sc.Status == status 
                                             && sc.IsActive);

                if (categoryConfig != null)
                {
                    return new MoneyDistributionConfigDto
                    {
                        ClientPercentage = categoryConfig.ClientPercentage,
                        ExpertPercentage = categoryConfig.ExpertPercentage,
                        PlatformPercentage = categoryConfig.PlatformPercentage,
                        Source = "service_type_category",
                        ServiceTypeCategoryName = categoryConfig.ServiceTypeCategory?.Name,
                        Status = status
                    };
                }
            }

            // 3. Buscar configuración por defecto por estado
            var defaultConfig = await _context.AppointmentStatusConfigs
                .FirstOrDefaultAsync(ac => ac.Status == status && ac.IsActive);

            if (defaultConfig != null)
            {
                return new MoneyDistributionConfigDto
                {
                    ClientPercentage = defaultConfig.ClientPercentage,
                    ExpertPercentage = defaultConfig.ExpertPercentage,
                    PlatformPercentage = defaultConfig.PlatformPercentage,
                    Source = "appointment_status",
                    Status = status
                };
            }

            // 4. NO HAY CONFIGURACIÓN - FALLAR EN LUGAR DE INVENTAR VALORES
            _logger.LogError("No money distribution configuration found for status: {Status}, categoryId: {CategoryId}, serviceTypeCategoryId: {ServiceTypeCategoryId}. Configuration must be created by admin.", 
                status, categoryId, serviceTypeCategoryId);
            return null;
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

            // Usar configuración de BD para distribución de dinero
            await ProcessCancellationRefundAsync(appointment.SearchHireId, AppointmentStatus.AppointmentCancelledByNoResponse.ToStringValue());

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

            // Usar configuración de BD para distribución de dinero
            await ProcessCancellationRefundAsync(appointment.SearchHireId, AppointmentStatus.AppointmentCancelledByNoResponse.ToStringValue());

            _logger.LogInformation("Appointment cancelled due to response timeout: {AppointmentId}", appointmentId);
        }

        private async Task ProcessAutoAwaitingClientDecisionAsync(int appointmentId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.SearchHire)
                .FirstOrDefaultAsync(a => a.Id == appointmentId);

            if (appointment == null) return;

            // Marcar la cita como completada automáticamente
            appointment.Status = AppointmentStatus.AppointmentCompleted.ToStringValue();
            appointment.CompletedAt = DateTime.UtcNow;
            appointment.CompletedBy = appointment.SearchHire.ExpertId; // El experto la completó automáticamente
            appointment.UpdatedAt = DateTime.UtcNow;

            // Cambiar SearchHire a AwaitingClientDecision para que el cliente pueda aprobar
            appointment.SearchHire.Status = SearchHireStatus.AwaitingClientDecision.ToStringValue();
            appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation("Appointment automatically completed and moved to AwaitingClientDecision: {AppointmentId}", appointmentId);
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

            // Usar configuración de BD para distribución de dinero
            await ProcessCancellationRefundAsync(appointment.SearchHireId, AppointmentStatus.AppointmentCancelledByNoResponse.ToStringValue());

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
