using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using newApi.DataLayer.Models;

using newApi.DataLayer.Models.DTOs;

using newApi.DataLayer.Models.PostGresModels;

using newApi.DataLayer.Models.enums;

using newApi.Common;

using System.Globalization;

using Hangfire;
using System.Text.Json;


namespace newApi.Services

{

    public class AppointmentService : IAppointmentService

    {

        private readonly AppDbContext _context;

        private readonly SystemStatusService _systemStatusService;

        private readonly StripeRefundService _refundService;

        private readonly ILoggingService _loggingService;

        private readonly IStripeValidationService _stripeValidationService;
        
        private readonly ITimezoneService _timezoneService;
        private readonly INotificationService _notificationService;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        // ✅ MEJORA: Cache de estados para evitar consultas repetidas a la BD
        // Usa una clave compuesta: "StatusType|StatusValue" -> StatusId
        private static readonly Dictionary<string, int> _statusCache = new Dictionary<string, int>();
        private static readonly object _cacheLock = new object();
        private static DateTime _cacheLastRefresh = DateTime.MinValue;
        private static readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(30); // Cache válido por 30 minutos

        /// <summary>
        /// Constructor
        /// </summary>
        public AppointmentService(AppDbContext context, SystemStatusService systemStatusService, StripeRefundService refundService, ILoggingService loggingService, IStripeValidationService stripeValidationService, ITimezoneService timezoneService, INotificationService notificationService, IServiceScopeFactory serviceScopeFactory)
        {

            _context = context;

            _systemStatusService = systemStatusService;

            _refundService = refundService;

            _loggingService = loggingService;

            _stripeValidationService = stripeValidationService;
            
            _timezoneService = timezoneService;
            
            _serviceScopeFactory = serviceScopeFactory;
            
            _notificationService = notificationService;

        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue with caching
        /// ✅ MEJORA: Cache de estados para mejorar performance
        /// Soporta tanto AppointmentStatus como SearchHireStatus
        /// </summary>
        private async Task<int> GetStatusIdByValueAsync(string statusValue, string statusType = "SearchHireStatus", CancellationToken cancellationToken = default)
        {
            // Crear clave de cache: "StatusType|StatusValue"
            string cacheKey = $"{statusType}|{statusValue}";

            // Verificar si el cache está expirado
            bool cacheExpired = DateTime.UtcNow - _cacheLastRefresh > _cacheExpiration;
            
            lock (_cacheLock)
            {
                // Si el cache está expirado, limpiarlo
                if (cacheExpired)
                {
                    _statusCache.Clear();
                    _cacheLastRefresh = DateTime.UtcNow;
                }

                // Intentar obtener del cache
                if (_statusCache.TryGetValue(cacheKey, out int cachedId))
                {
                    return cachedId;
                }
            }

            // Si no está en cache, consultar BD
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == statusType, cancellationToken);
            
            int statusId;
            if (systemStatus == null)
            {
                // Default to "pending" (ID = 1) solo para SearchHireStatus
                // Para AppointmentStatus, lanzar excepción si no se encuentra
                if (statusType == "SearchHireStatus")
                {
                    statusId = 1;
                }
                else
                {
                    throw new InvalidOperationException($"Status '{statusValue}' of type '{statusType}' not found in SystemStatuses");
                }
            }
            else
            {
                statusId = systemStatus.Id;
            }

            // Guardar en cache
            lock (_cacheLock)
            {
                _statusCache[cacheKey] = statusId;
            }
            
            return statusId;
        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue (legacy method, mantiene compatibilidad)
        /// Usa SearchHireStatus por defecto
        /// </summary>
        private async Task<int> GetStatusIdByValueAsync(string statusValue)
        {
            return await GetStatusIdByValueAsync(statusValue, "SearchHireStatus");
        }

        /// <summary>
        /// Helper method to get SystemStatus entity by value and type with caching
        /// ✅ MEJORA: Cache para obtener la entidad completa cuando se necesite
        /// </summary>
        private async Task<SystemStatus?> GetStatusByValueAndTypeAsync(string statusValue, string statusType, CancellationToken cancellationToken = default)
        {
            // Primero intentar obtener el ID del cache
            string cacheKey = $"{statusType}|{statusValue}";
            
            bool cacheExpired = DateTime.UtcNow - _cacheLastRefresh > _cacheExpiration;
            
            int? cachedId = null;
            lock (_cacheLock)
            {
                if (!cacheExpired && _statusCache.TryGetValue(cacheKey, out int id))
                {
                    cachedId = id;
                }
            }

            // Si tenemos el ID en cache, cargar la entidad
            if (cachedId.HasValue)
            {
                var cachedStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.Id == cachedId.Value, cancellationToken);
                
                if (cachedStatus != null && cachedStatus.StatusValue == statusValue && cachedStatus.StatusType == statusType)
                {
                    return cachedStatus;
                }
            }

            // Si no está en cache o no coincide, consultar BD
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == statusType, cancellationToken);
            
            // Guardar en cache si se encontró
            if (systemStatus != null)
            {
                lock (_cacheLock)
                {
                    _statusCache[cacheKey] = systemStatus.Id;
                    if (cacheExpired)
                    {
                        _cacheLastRefresh = DateTime.UtcNow;
                    }
                }
            }
            
            return systemStatus;
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
                // ✅ LOG: Error al obtener cita por ID
                await _loggingService.LogErrorAsync(
                    message: "Error al obtener cita por ID",
                    details: $"Error al obtener cita con Id {id}. Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: null,
                    source: "AppointmentService.GetAppointmentByIdAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: id,
                    additionalData: new { 
                        AppointmentId = id,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

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
                // ✅ LOG: Error al obtener cita por SearchHireId
                await _loggingService.LogErrorAsync(
                    message: "Error al obtener cita por SearchHireId",
                    details: $"Error al obtener cita para SearchHire {searchHireId}. Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: null,
                    source: "AppointmentService.GetAppointmentBySearchHireIdAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: null,
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

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
                // ✅ LOG: Error al obtener citas del usuario
                await _loggingService.LogErrorAsync(
                    message: "Error al obtener citas del usuario",
                    details: $"Error al obtener citas para usuario {userId}. Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "AppointmentService.GetUserAppointmentsAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: null,
                    additionalData: new { 
                        UserId = userId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                throw;
            }

        }



        public async Task<AppointmentDto> CreateAppointmentAsync(CreateAppointmentDto dto)

        {

            try

            {

                // ✅ FIX CRÍTICO: NO usar ExecutionStrategy con transacciones manuales en PgBouncer
                // PgBouncer Transaction Pooler no admite savepoints automáticos que EF Core intenta crear
                // ✅ PROTECCIÓN: Abrir transacción ANTES de cualquier operación para evitar race conditions

                using var transaction = await _context.Database.BeginTransactionAsync();

                try

                {

                    // ✅ PROTECCIÓN: Usar row-level locking dentro de la transacción para evitar race conditions

                    // Bloquear el SearchHire con FOR UPDATE para evitar que dos usuarios creen citas simultáneamente

                    var searchHire = await _context.SearchHires

                        .FromSqlInterpolated($"SELECT * FROM \"SearchHires\" WHERE \"Id\" = {dto.SearchHireId} FOR UPDATE")

                        .Include(sh => sh.Appointment)

                        .Include(sh => sh.Status)

                        .Include(sh => sh.SearchService)

                            .ThenInclude(ss => ss.ExpertProfile)

                        .FirstOrDefaultAsync();



                    if (searchHire == null)

                        throw new ArgumentException("SearchHire not found");



                    // ✅ VALIDACIÓN CRÍTICA: Verificar que el SearchHire NO esté finalizado

                    if (searchHire.Status?.IsFinalizationStatus == true)

                    {

                        var searchHireStatus = searchHire.Status?.StatusValue ?? "unknown";

                        throw new InvalidOperationException(

                            $"No se puede crear una cita cuando el servicio está en estado de finalización '{searchHireStatus}'. " +

                            $"El servicio debe estar activo para poder crear citas."

                        );

                    }



                    // ✅ VALIDACIÓN: Verificar que no tenga ya una cita (con el bloqueo activo para evitar race conditions)

                    if (searchHire.Appointment != null)

                        throw new InvalidOperationException("SearchHire already has an appointment");



                    // ✅ MEJORA: Obtener el estado "awaiting_appointment" usando cache
                    var awaitingStatusId = await GetStatusIdByValueAsync(
                        AppointmentStatus.AwaitingAppointment.ToStringValue(), 
                        "AppointmentStatus"
                    );

                    // ✅ INTERNACIONALIZACIÓN: Obtener timezone efectivo y convertir fecha/hora local a UTC
                    // Prioridad: DTO > SearchHire.ExpertTimezone > ExpertProfile.Timezone > UTC
                    var expertTimezone = !string.IsNullOrWhiteSpace(dto.Timezone) && _timezoneService.IsValidTimezone(dto.Timezone)
                        ? dto.Timezone
                        : _timezoneService.GetEffectiveTimezone(
                            searchHire.ExpertTimezone,
                            searchHire.SearchService?.ExpertProfile?.Timezone
                        );
                    
                    // Construir DateTime local (asumiendo que viene en hora local del experto)
                    var proposedDateTimeLocal = dto.ProposedDate.Date + dto.ProposedTime;
                    
                    // Convertir de hora local a UTC
                    var proposedDateTimeUtc = _timezoneService.ConvertToUtc(proposedDateTimeLocal, expertTimezone);
                    
                    // Separar fecha y hora en UTC para guardar
                    var proposedDateUtc = proposedDateTimeUtc.Date;
                    var proposedTimeUtc = proposedDateTimeUtc.TimeOfDay;

                    // ✅ VALIDACIÓN: Verificar que la cita tenga al menos 24 horas de anticipación
                    var timeUntilAppointment = proposedDateTimeUtc - DateTime.UtcNow;

                    

                    if (timeUntilAppointment.TotalHours < 24)

                    {

                        throw new InvalidOperationException(

                            $"Las citas deben crearse con al menos 24 horas de anticipación. " +

                            $"Tiempo restante: {timeUntilAppointment.TotalHours:F1} horas. " +

                            $"Fecha/hora propuesta: {proposedDateTimeUtc:dd/MM/yyyy HH:mm} UTC ({proposedDateTimeLocal:dd/MM/yyyy HH:mm} {expertTimezone})"

                        );

                    }



                    // ✅ VALIDACIÓN: Verificar que la ubicación propuesta esté dentro del rango del experto

                    await ValidateAppointmentLocationAsync(searchHire, dto.Latitude, dto.Longitude);



                    // ✅ VALIDACIÓN: Verificar que la fecha/hora propuesta esté dentro del horario de disponibilidad del experto
                    // Usar la fecha/hora en UTC para la validación
                    await ValidateAppointmentAvailabilityAsync(searchHire, proposedDateTimeUtc);



                    // Crear la cita dentro de la transacción
                    // ✅ INTERNACIONALIZACIÓN: Guardar fecha/hora en UTC (convertida desde hora local)

                    var appointment = new Appointment

                    {

                        SearchHireId = dto.SearchHireId,

                        StatusId = awaitingStatusId,

                        ProposedDate = DateTime.SpecifyKind(proposedDateUtc, DateTimeKind.Utc),

                        ProposedTime = proposedTimeUtc,

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

                    try
                    {
                        await _context.SaveChangesAsync();
                    }
                    catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                        await transaction.RollbackAsync();
                        using var recoveryScope = _serviceScopeFactory.CreateScope();
                        var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var recoverySearchHire = await recoveryContext.SearchHires
                            .FromSqlInterpolated($"SELECT * FROM \"SearchHires\" WHERE \"Id\" = {dto.SearchHireId} FOR UPDATE")
                            .Include(sh => sh.Appointment)
                            .Include(sh => sh.Status)
                            .Include(sh => sh.SearchService)
                                .ThenInclude(ss => ss.ExpertProfile)
                            .FirstOrDefaultAsync();
                        
                        if (recoverySearchHire == null || recoverySearchHire.Appointment != null)
                        {
                            throw; // Re-lanzar si no se puede recuperar
                        }
                        
                        await using var recoveryTransaction = await recoveryContext.Database.BeginTransactionAsync();
                        try
                        {
                            var recoveryAppointment = new Appointment
                            {
                                SearchHireId = dto.SearchHireId,
                                StatusId = awaitingStatusId,
                                ProposedDate = DateTime.SpecifyKind(proposedDateUtc, DateTimeKind.Utc),
                                ProposedTime = proposedTimeUtc,
                                Location = dto.Location,
                                Latitude = dto.Latitude,
                                Longitude = dto.Longitude,
                                DoorNumber = dto.DoorNumber,
                                OwnerPhone = dto.OwnerPhone,
                                SiteDetails = dto.SiteDetails,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };
                            
                            recoveryContext.Appointments.Add(recoveryAppointment);
                            await recoveryContext.SaveChangesAsync();
                            
                            var recoveryProposalTimer = new AppointmentTimer
                            {
                                AppointmentId = recoveryAppointment.Id,
                                TimerType = "proposal",
                                StartTime = DateTime.UtcNow,
                                EndTime = DateTime.UtcNow.AddHours(24),
                                IsExpired = false,
                                CreatedAt = DateTime.UtcNow
                            };
                            
                            recoveryContext.AppointmentTimers.Add(recoveryProposalTimer);
                            await recoveryContext.SaveChangesAsync();
                            
                            var recoveryJobId = BackgroundJob.Schedule<IAppointmentService>(
                                service => service.ProcessAppointmentTimerAsync(recoveryProposalTimer.Id),
                                recoveryProposalTimer.EndTime - DateTime.UtcNow
                            );
                            
                            recoveryProposalTimer.HangfireJobId = recoveryJobId;
                            await recoveryContext.SaveChangesAsync();
                            
                            await recoveryTransaction.CommitAsync();
                            
                            var recoveryCreatedAppointment = await recoveryContext.Appointments
                                .Include(a => a.SearchHire)
                                    .ThenInclude(sh => sh.Client)
                                .Include(a => a.SearchHire)
                                    .ThenInclude(sh => sh.Expert)
                                .Include(a => a.SearchHire)
                                    .ThenInclude(sh => sh.Status)
                                .Include(a => a.Status)
                                .Include(a => a.Timers)
                                .FirstAsync(a => a.Id == recoveryAppointment.Id);
                            
                            return MapToDto(recoveryCreatedAppointment);
                        }
                        catch
                        {
                            await recoveryTransaction.RollbackAsync();
                            throw;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }
                        
                        using var recoveryScope = _serviceScopeFactory.CreateScope();
                        var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var recoverySearchHire = await recoveryContext.SearchHires
                            .FromSqlInterpolated($"SELECT * FROM \"SearchHires\" WHERE \"Id\" = {dto.SearchHireId} FOR UPDATE")
                            .Include(sh => sh.Appointment)
                            .Include(sh => sh.Status)
                            .Include(sh => sh.SearchService)
                                .ThenInclude(ss => ss.ExpertProfile)
                            .FirstOrDefaultAsync();
                        
                        if (recoverySearchHire == null || recoverySearchHire.Appointment != null)
                        {
                            throw; // Re-lanzar si no se puede recuperar
                        }
                        
                        await using var recoveryTransaction = await recoveryContext.Database.BeginTransactionAsync();
                        try
                        {
                            var recoveryAppointment = new Appointment
                            {
                                SearchHireId = dto.SearchHireId,
                                StatusId = awaitingStatusId,
                                ProposedDate = DateTime.SpecifyKind(proposedDateUtc, DateTimeKind.Utc),
                                ProposedTime = proposedTimeUtc,
                                Location = dto.Location,
                                Latitude = dto.Latitude,
                                Longitude = dto.Longitude,
                                DoorNumber = dto.DoorNumber,
                                OwnerPhone = dto.OwnerPhone,
                                SiteDetails = dto.SiteDetails,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };
                            
                            recoveryContext.Appointments.Add(recoveryAppointment);
                            await recoveryContext.SaveChangesAsync();
                            
                            var recoveryProposalTimer = new AppointmentTimer
                            {
                                AppointmentId = recoveryAppointment.Id,
                                TimerType = "proposal",
                                StartTime = DateTime.UtcNow,
                                EndTime = DateTime.UtcNow.AddHours(24),
                                IsExpired = false,
                                CreatedAt = DateTime.UtcNow
                            };
                            
                            recoveryContext.AppointmentTimers.Add(recoveryProposalTimer);
                            await recoveryContext.SaveChangesAsync();
                            
                            var recoveryJobId = BackgroundJob.Schedule<IAppointmentService>(
                                service => service.ProcessAppointmentTimerAsync(recoveryProposalTimer.Id),
                                recoveryProposalTimer.EndTime - DateTime.UtcNow
                            );
                            
                            recoveryProposalTimer.HangfireJobId = recoveryJobId;
                            await recoveryContext.SaveChangesAsync();
                            
                            await recoveryTransaction.CommitAsync();
                            
                            var recoveryCreatedAppointment = await recoveryContext.Appointments
                                .Include(a => a.SearchHire)
                                    .ThenInclude(sh => sh.Client)
                                .Include(a => a.SearchHire)
                                    .ThenInclude(sh => sh.Expert)
                                .Include(a => a.SearchHire)
                                    .ThenInclude(sh => sh.Status)
                                .Include(a => a.Status)
                                .Include(a => a.Timers)
                                .FirstAsync(a => a.Id == recoveryAppointment.Id);
                            
                            return MapToDto(recoveryCreatedAppointment);
                        }
                        catch
                        {
                            await recoveryTransaction.RollbackAsync();
                            throw;
                        }
                    }

                    // ✅ Crear timer para propuesta del cliente (24 horas)
                    // Cuando se crea la cita, el estado es "awaiting_appointment", 
                    // por lo que el cliente tiene 24 horas para proponer una fecha/hora
                    var proposalTimer = new AppointmentTimer
                    {
                        AppointmentId = appointment.Id,
                        TimerType = "proposal",
                        StartTime = DateTime.UtcNow,
                        EndTime = DateTime.UtcNow.AddHours(24),
                        IsExpired = false,
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.AppointmentTimers.Add(proposalTimer);
                    await _context.SaveChangesAsync();

                    // Programar scheduled job para cuando expire el timer (24 horas)
                    var jobId = BackgroundJob.Schedule<IAppointmentService>(
                        service => service.ProcessAppointmentTimerAsync(proposalTimer.Id),
                        proposalTimer.EndTime - DateTime.UtcNow
                    );

                    // Guardar el JobId en el timer
                    proposalTimer.HangfireJobId = jobId;
                    await _context.SaveChangesAsync();

                    // Commit de la transacción

                    await transaction.CommitAsync();



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

                catch (Exception innerEx)

                {

                    // Rollback en caso de error
                    try
                    {
                        await transaction.RollbackAsync();
                    }
                    catch { }

                    // ✅ LOG: Error en transacción al crear cita
                    await _loggingService.LogErrorAsync(
                        message: "Error en transacción al crear cita",
                        details: $"Error al crear cita para SearchHire {dto.SearchHireId} dentro de la transacción. Error: {innerEx.GetType().Name} - {innerEx.Message}, StackTrace: {innerEx.StackTrace}",
                        userId: null,
                        source: "AppointmentService.CreateAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: null,
                        additionalData: new { 
                            SearchHireId = dto.SearchHireId,
                            ErrorType = innerEx.GetType().Name,
                            ErrorMessage = innerEx.Message,
                            StackTrace = innerEx.StackTrace,
                            InnerException = innerEx.InnerException?.Message
                        }
                    );

                    throw;

                }

            }

            catch (Exception ex)

            {
                // ✅ LOG: Error general al crear cita
                await _loggingService.LogErrorAsync(
                    message: "Error al crear cita",
                    details: $"Error general al crear cita para SearchHire {dto.SearchHireId}. Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: null,
                    source: "AppointmentService.CreateAppointmentAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: null,
                    additionalData: new { 
                        SearchHireId = dto.SearchHireId,
                        ProposedDate = dto.ProposedDate,
                        ProposedTime = dto.ProposedTime,
                        Location = dto.Location,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                throw;
            }

        }



        public async Task<AppointmentDto> ProposeAppointmentAsync(int searchHireId, ProposeAppointmentDto dto, int userId)

        {

            try

            {

                // ✅ FIX CRÍTICO: NO usar ExecutionStrategy con transacciones manuales en PgBouncer
                // PgBouncer Transaction Pooler no admite savepoints automáticos que EF Core intenta crear
                // ✅ PROTECCIÓN: Abrir transacción ANTES de cualquier operación para evitar race conditions

                using var transaction = await _context.Database.BeginTransactionAsync();

                    try

                    {

                        // ✅ PROTECCIÓN: Usar row-level locking dentro de la transacción

                        // Intentar obtener la cita con FOR UPDATE (si existe)

                var appointment = await _context.Appointments

                            .FromSqlInterpolated($"SELECT * FROM \"Appointments\" WHERE \"SearchHireId\" = {searchHireId} FOR UPDATE")

                    .Include(a => a.SearchHire)

                        .ThenInclude(sh => sh.Status)

                    .Include(a => a.Status)

                            .FirstOrDefaultAsync();

                        // ✅ VALIDACIÓN CRÍTICA: Si la cita existe, verificar que el SearchHire NO esté finalizado
                        if (appointment != null && appointment.SearchHire?.Status?.IsFinalizationStatus == true)
                        {
                            var searchHireStatus = appointment.SearchHire.Status?.StatusValue ?? "unknown";
                            throw new InvalidOperationException(
                                $"No se puede proponer una cita cuando el servicio está en estado de finalización '{searchHireStatus}'. " +
                                $"El servicio debe estar activo para poder proponer citas."
                            );
                        }

                        // Si no existe la cita, crearla automáticamente dentro de la misma transacción

                if (appointment == null)

                {

                    // ✅ CORRECCIÓN: Cargar SearchHire con FOR UPDATE para mantener consistencia en la transacción
                    var searchHire = await _context.SearchHires
                        .FromSqlInterpolated($"SELECT * FROM \"SearchHires\" WHERE \"Id\" = {searchHireId} FOR UPDATE")
                        .Include(sh => sh.SearchService)
                            .ThenInclude(ss => ss.ExpertProfile)
                        .Include(sh => sh.Status)
                        .FirstOrDefaultAsync();



                    if (searchHire == null)

                        throw new ArgumentException("SearchHire not found");



                    // Verificar que el usuario es el cliente

                    if (searchHire.ClientId != userId)

                        throw new UnauthorizedAccessException("Only the client can propose appointments");

                    // ✅ VALIDACIÓN CRÍTICA: Verificar que el SearchHire NO esté finalizado
                    if (searchHire.Status?.IsFinalizationStatus == true)
                    {
                        var searchHireStatus = searchHire.Status?.StatusValue ?? "unknown";
                        throw new InvalidOperationException(
                            $"No se puede proponer una cita cuando el servicio está en estado de finalización '{searchHireStatus}'. " +
                            $"El servicio debe estar activo para poder proponer citas."
                        );
                    }

                    // ✅ VALIDACIÓN REMOVIDA: Permitir continuar el flujo incluso si la cuenta cambia a Deauthorized

                    // La validación de Stripe solo se aplica al CREAR contrataciones, no al continuar el flujo



                    // ✅ MEJORA: Obtener el estado "awaiting_appointment" usando cache
                    var awaitingStatusId = await GetStatusIdByValueAsync(
                        AppointmentStatus.AwaitingAppointment.ToStringValue(), 
                        "AppointmentStatus"
                    );



                            // Crear la cita dentro de la transacción
                            // ✅ Crear Appointment sin fecha/hora/ubicación - se asignarán cuando el cliente proponga

                    appointment = new Appointment

                    {

                        SearchHireId = searchHireId,

                        StatusId = awaitingStatusId,

                        // ProposedDate, ProposedTime, Location son nullable - se asignarán en ProposeAppointment

                        CreatedAt = DateTime.UtcNow,

                        UpdatedAt = DateTime.UtcNow

                    };



                    _context.Appointments.Add(appointment);
                    // ✅ CORRECCIÓN: Hacer SaveChanges para obtener el Id de la cita antes de recargarla
                    await _context.SaveChangesAsync();

                    // ✅ Crear timer para propuesta del cliente (24 horas)
                    // Cuando se crea la cita automáticamente, el estado es "awaiting_appointment", 
                    // por lo que el cliente tiene 24 horas para proponer una fecha/hora
                    var proposalTimer = new AppointmentTimer
                    {
                        AppointmentId = appointment.Id,
                        TimerType = "proposal",
                        StartTime = DateTime.UtcNow,
                        EndTime = DateTime.UtcNow.AddHours(24),
                        IsExpired = false,
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.AppointmentTimers.Add(proposalTimer);
                    await _context.SaveChangesAsync();

                    // Programar scheduled job para cuando expire el timer (24 horas)
                    var jobId = BackgroundJob.Schedule<IAppointmentService>(
                        service => service.ProcessAppointmentTimerAsync(proposalTimer.Id),
                        proposalTimer.EndTime - DateTime.UtcNow
                    );

                    // Guardar el JobId en el timer
                    proposalTimer.HangfireJobId = jobId;
                    await _context.SaveChangesAsync();

                    // ✅ Recargar la cita con las relaciones usando FOR UPDATE para mantener el bloqueo
                    // Esto asegura que el estado se carga correctamente y se mantiene el bloqueo de fila
                    appointment = await _context.Appointments
                        .FromSqlInterpolated($"SELECT * FROM \"Appointments\" WHERE \"Id\" = {appointment.Id} FOR UPDATE")
                        .Include(a => a.SearchHire)
                            .ThenInclude(sh => sh.Status)
                        .Include(a => a.Status)
                        .FirstAsync();

                }



                // Verificar que el usuario es el cliente

                if (appointment.SearchHire.ClientId != userId)

                    throw new UnauthorizedAccessException("Only the client can propose appointments");



                        // ✅ VALIDACIÓN CRÍTICA: Solo se puede proponer si está en "awaiting_appointment", "appointment_rejected" o estados de cancelación (primera cancelación)

                        // No se puede proponer si ya está propuesta, confirmada o cancelada (segunda cancelación)

                        var currentStatus = appointment.Status?.StatusValue ?? string.Empty;

                        var validStatesForPropose = new[] { 
                            AppointmentStatus.AwaitingAppointment.ToStringValue(), 
                            AppointmentStatus.AppointmentRejected.ToStringValue(),
                            AppointmentStatus.AppointmentCancelledByClient.ToStringValue(),      // Primera cancelación del cliente
                            AppointmentStatus.AppointmentCancelledByExpert.ToStringValue()        // Primera cancelación del experto
                        };

                        // ✅ PROTECCIÓN: Verificar que no se haya procesado ya (evitar doble click/race condition)
                        var invalidStatesForPropose = new[] { 
                            "appointment_proposed",
                            AppointmentStatus.AppointmentConfirmed.ToStringValue(),
                            AppointmentStatus.AppointmentCancelledByClientSecond.ToStringValue(),
                            AppointmentStatus.AppointmentCancelledByExpertSecond.ToStringValue()
                        };

                        if (invalidStatesForPropose.Contains(currentStatus))
                        {
                            throw new InvalidOperationException(
                                $"La cita ya ha sido procesada (estado: '{currentStatus}'). " +
                                $"No se puede proponer nuevamente."
                            );
                        }

                        if (!validStatesForPropose.Contains(currentStatus))

                        {

                            throw new InvalidOperationException(

                                $"No se puede proponer una cita en estado '{currentStatus}'. " +

                                $"Solo se pueden proponer citas en estados: {string.Join(", ", validStatesForPropose)}."

                            );

                        }



                // ✅ OPTIMIZACIÓN: Obtener el estado "appointment_proposed" usando cache (más eficiente)
                var proposedStatusId = await GetStatusIdByValueAsync(
                    AppointmentStatus.AppointmentProposed.ToStringValue(), 
                    "AppointmentStatus"
                );

                if (proposedStatusId == 0)
                    throw new InvalidOperationException("Appointment proposed status not found");

                // ✅ INTERNACIONALIZACIÓN: Obtener timezone efectivo y convertir fecha/hora local a UTC
                // Prioridad: DTO > SearchHire.ExpertTimezone > ExpertProfile.Timezone > UTC
                // Asegurar que SearchHire tenga las relaciones cargadas
                if (appointment.SearchHire != null && appointment.SearchHire.SearchService == null)
                {
                    await _context.Entry(appointment.SearchHire)
                        .Reference(sh => sh.SearchService)
                        .LoadAsync();
                    
                    if (appointment.SearchHire.SearchService != null)
                    {
                        await _context.Entry(appointment.SearchHire.SearchService)
                            .Reference(ss => ss.ExpertProfile)
                            .LoadAsync();
                    }
                }
                
                var expertTimezone = !string.IsNullOrWhiteSpace(dto.Timezone) && _timezoneService.IsValidTimezone(dto.Timezone)
                    ? dto.Timezone
                    : _timezoneService.GetEffectiveTimezone(
                        appointment.SearchHire?.ExpertTimezone,
                        appointment.SearchHire?.SearchService?.ExpertProfile?.Timezone
                    );
                
                // Construir DateTime local (asumiendo que viene en hora local del experto)
                var proposedDateTimeLocal = dto.ProposedDate.Date + dto.ProposedTime;
                
                // Convertir de hora local a UTC
                var proposedDateTimeUtc = _timezoneService.ConvertToUtc(proposedDateTimeLocal, expertTimezone);
                
                // Separar fecha y hora en UTC para guardar
                var proposedDateUtc = proposedDateTimeUtc.Date;
                var proposedTimeUtc = proposedDateTimeUtc.TimeOfDay;

                // ✅ VALIDACIÓN: Verificar que la cita tenga al menos 24 horas de anticipación
                var timeUntilAppointment = proposedDateTimeUtc - DateTime.UtcNow;

                

                if (timeUntilAppointment.TotalHours < 24)

                {

                    throw new InvalidOperationException(

                        $"Las citas deben proponerse con al menos 24 horas de anticipación. " +

                        $"Tiempo restante: {timeUntilAppointment.TotalHours:F1} horas. " +

                        $"Fecha/hora propuesta: {proposedDateTimeUtc:dd/MM/yyyy HH:mm} UTC ({proposedDateTimeLocal:dd/MM/yyyy HH:mm} {expertTimezone})"

                    );

                }



                // ✅ VALIDACIÓN: Verificar que SearchHire esté cargado antes de validar
                if (appointment.SearchHire == null)
                {
                    throw new InvalidOperationException("SearchHire no está cargado en el Appointment");
                }

                // ✅ VALIDACIÓN: Verificar que la ubicación propuesta esté dentro del rango del experto
                await ValidateAppointmentLocationAsync(appointment.SearchHire, dto.Latitude, dto.Longitude);

                // ✅ VALIDACIÓN: Verificar que la fecha/hora propuesta esté dentro del horario de disponibilidad del experto
                // Usar la fecha/hora en UTC para la validación
                await ValidateAppointmentAvailabilityAsync(appointment.SearchHire, proposedDateTimeUtc);



                // Actualizar la cita - ✅ INTERNACIONALIZACIÓN: Guardar fecha/hora en UTC (convertida desde hora local)
                appointment.ProposedDate = DateTime.SpecifyKind(proposedDateUtc, DateTimeKind.Utc);
                appointment.ProposedTime = proposedTimeUtc;

                appointment.Location = dto.Location;

                appointment.Latitude = dto.Latitude;

                appointment.Longitude = dto.Longitude;

                appointment.DoorNumber = dto.DoorNumber;

                appointment.OwnerPhone = dto.OwnerPhone;

                appointment.SiteDetails = dto.SiteDetails;

                appointment.StatusId = proposedStatusId;

                appointment.LastProposalAt = DateTime.UtcNow;

                appointment.UpdatedAt = DateTime.UtcNow;

                // ✅ CRÍTICO: Marcar la entidad como Modified explícitamente porque se cargó con FromSqlInterpolated
                // FromSqlInterpolated puede no detectar cambios automáticamente, necesitamos forzar el estado
                _context.Entry(appointment).State = EntityState.Modified;

                // ✅ DEBUG: Verificar que los valores se asignaron correctamente antes de SaveChanges
                await _loggingService.LogInfoAsync(
                    message: "Valores asignados al Appointment antes de SaveChanges",
                    details: $"AppointmentId: {appointment.Id}, ProposedDate: {appointment.ProposedDate}, ProposedTime: {appointment.ProposedTime}, Location: {appointment.Location}, StatusId: {appointment.StatusId}, EntityState: {_context.Entry(appointment).State}",
                    userId: userId,
                    source: "AppointmentService.ProposeAppointmentAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: appointment.Id,
                    notifyUser: false
                );



                // ✅ Cancelar timers de propuesta activos antes de crear el timer de respuesta
                var proposalTimers = await _context.AppointmentTimers
                    .Where(t => t.AppointmentId == appointment.Id && 
                               t.TimerType == "proposal" && 
                               !t.IsExpired)
                    .ToListAsync();

                // ✅ OPTIMIZACIÓN: Almacenar JobIds de Hangfire para cancelarlos después del commit
                var hangfireJobIdsToCancel = new List<string>();
                foreach (var timer in proposalTimers)
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    
                    // ✅ Almacenar JobId para cancelarlo después del commit (evitar operaciones Hangfire dentro de transacción)
                    if (!string.IsNullOrEmpty(timer.HangfireJobId))
                    {
                        hangfireJobIdsToCancel.Add(timer.HangfireJobId);
                        timer.HangfireJobId = null; // Limpiar referencia
                    }
                }

                // Crear timer para respuesta del experto (24 horas)
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

                // ✅ OPTIMIZACIÓN: Un solo SaveChangesAsync para todas las operaciones de BD
                var saveChangesResult = await _context.SaveChangesAsync();
                
                // ✅ DEBUG: Verificar que SaveChanges se ejecutó correctamente
                await _loggingService.LogInfoAsync(
                    message: "SaveChanges ejecutado en ProposeAppointment",
                    details: $"SaveChangesResult: {saveChangesResult} entidades modificadas. AppointmentId: {appointment.Id}, ProposedDate: {appointment.ProposedDate}, ProposedTime: {appointment.ProposedTime}, Location: {appointment.Location}",
                    userId: userId,
                    source: "AppointmentService.ProposeAppointmentAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: appointment.Id,
                    notifyUser: false
                );

                        // ✅ COMMIT: Confirmar la transacción
                        await transaction.CommitAsync();

                        // ✅ CRÍTICO: Detach el appointment del contexto para forzar recarga desde BD
                        _context.Entry(appointment).State = EntityState.Detached;

                        // ✅ CANCELAR jobs de Hangfire DESPUÉS del commit (mejor práctica: operaciones externas fuera de transacción)
                        foreach (var jobId in hangfireJobIdsToCancel)
                        {
                            try
                            {
                                BackgroundJob.Delete(jobId);
                            }
                            catch
                            {
                                // Si el job ya no existe o fue procesado, continuar sin error
                            }
                        }

                        // ✅ Programar scheduled job para cuando expire el timer de respuesta (24 horas) - DESPUÉS del commit
                        var responseJobId = BackgroundJob.Schedule<IAppointmentService>(
                            service => service.ProcessAppointmentTimerAsync(responseTimer.Id),
                            responseTimer.EndTime - DateTime.UtcNow
                        );

                        // Guardar el JobId en el timer (fuera de la transacción)
                        responseTimer.HangfireJobId = responseJobId;
                        await _context.SaveChangesAsync();

                // ✅ CRÍTICO: Cargar la cita actualizada con todas las relaciones usando AsNoTracking para evitar problemas de caché
                // O usar una nueva query para forzar la recarga desde la base de datos
                var updatedAppointment = await _context.Appointments
                    .AsNoTracking()
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(a => a.Status)
                    .Include(a => a.Timers)
                    .FirstAsync(a => a.Id == appointment.Id);

                // ✅ DEBUG: Verificar que los valores se cargaron correctamente después del commit
                await _loggingService.LogInfoAsync(
                    message: "Appointment recargado después del commit",
                    details: $"AppointmentId: {updatedAppointment.Id}, ProposedDate: {updatedAppointment.ProposedDate}, ProposedTime: {updatedAppointment.ProposedTime}, Location: {updatedAppointment.Location}, StatusId: {updatedAppointment.StatusId}, UpdatedAt: {updatedAppointment.UpdatedAt}",
                    userId: userId,
                    source: "AppointmentService.ProposeAppointmentAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: updatedAppointment.Id,
                    notifyUser: false
                );



                // ✅ Enviar mensaje al chat con el cambio de estado (después del commit)

                await SendAppointmentStatusChangeMessageAsync(

                    appointment.SearchHireId, 

                    AppointmentStatus.AppointmentProposed.ToStringValue(), 

                    userId

                );

                // ✅ Notificar al experto sobre la nueva propuesta de cita
                if (updatedAppointment.SearchHire?.ExpertId.HasValue == true && updatedAppointment.SearchHire.ExpertId.Value > 0)
                {
                    if (updatedAppointment.ProposedDate.HasValue && updatedAppointment.ProposedTime.HasValue)
                    {
                        var appointmentDateTime = updatedAppointment.ProposedDate.Value.Date + updatedAppointment.ProposedTime.Value;
                        var formattedDate = appointmentDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
                        var locationText = !string.IsNullOrEmpty(updatedAppointment.Location) ? updatedAppointment.Location : "ubicación pendiente";
                        
                        await _loggingService.LogInfoAsync(
                            message: "Nueva propuesta de cita recibida",
                            details: $"El cliente ha propuesto una cita para el {formattedDate} en {locationText}. Tienes 24 horas para aceptar o rechazar.",
                            userId: updatedAppointment.SearchHire.ExpertId.Value,
                            source: "AppointmentService.ProposeAppointmentAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: updatedAppointment.Id,
                            notifyUser: false // Desactivar notificación genérica
                        );

                        // ✅ EMAIL: Notificar al experto de la nueva propuesta
                        if (updatedAppointment.SearchHire.Expert != null && !string.IsNullOrEmpty(updatedAppointment.SearchHire.Expert.Email))
                        {
                            await _notificationService.SendGeneralNotificationEmailAsync(
                                updatedAppointment.SearchHire.Expert.Email,
                                updatedAppointment.SearchHire.Expert.Name,
                                "📅 Nueva Propuesta de Cita",
                                $"El cliente ha propuesto una cita para el <strong>{formattedDate}</strong> en <strong>{locationText}</strong>.<br><br>Tienes 24 horas para aceptar o rechazar esta propuesta.",
                                "Ver Propuesta",
                                "https://www.inspecciono.com/appointments"
                            );
                        }
                    }
                }

                // ✅ Notificar al cliente confirmando el envío de la propuesta (Confirmación para el remitente)
                var dateText = updatedAppointment.ProposedDate.HasValue 
                    ? updatedAppointment.ProposedDate.Value.ToString("dd/MM/yyyy") 
                    : "fecha pendiente";
                var timeText = updatedAppointment.ProposedTime.HasValue 
                    ? updatedAppointment.ProposedTime.Value.ToString(@"hh\:mm") 
                    : "hora pendiente";
                
                await _loggingService.LogInfoAsync(
                    message: "Propuesta de cita enviada correctamente",
                    details: $"Has propuesto una cita para el {dateText} a las {timeText}. El experto tiene 24 horas para responder. Si no responde, la cita se cancelará y recibirás un reembolso completo.",
                    userId: userId,
                    source: "AppointmentService.ProposeAppointmentAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: updatedAppointment.Id,
                    notifyUser: false // Desactivar notificación genérica
                );

                // ✅ EMAIL: Confirmar al cliente que su propuesta fue enviada
                if (updatedAppointment.SearchHire.Client != null && !string.IsNullOrEmpty(updatedAppointment.SearchHire.Client.Email))
                {
                    await _notificationService.SendGeneralNotificationEmailAsync(
                        updatedAppointment.SearchHire.Client.Email,
                        updatedAppointment.SearchHire.Client.Name,
                        "Propuesta Enviada",
                        $"Has propuesto una cita para el <strong>{updatedAppointment.ProposedDate:dd/MM/yyyy}</strong> a las <strong>{updatedAppointment.ProposedTime:hh\\:mm}</strong>.<br><br>El experto tiene 24 horas para responder.",
                        "Ver Estado",
                        "https://www.inspecciono.com/appointments"
                    );
                }

                return MapToDto(updatedAppointment);

                    }

                    catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, hacer rollback seguro
                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }
                        throw; // Re-lanzar para que el usuario pueda reintentar
                    }
                    catch (ObjectDisposedException)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, hacer rollback seguro
                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }
                        throw; // Re-lanzar para que el usuario pueda reintentar
                    }
                    catch (Exception innerEx)

                    {

                        // ✅ ROLLBACK: Revertir la transacción en caso de error

                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }

                        throw;

                    }

            }

            catch (Exception ex)

            {

                // ⚠️ LOG WARNING: Error general proponiendo cita (no afecta dinero, usuario puede reintentar)

                await _loggingService.LogWarningAsync(

                    message: "Error proposing appointment",

                    details: $"An unexpected exception occurred while proposing appointment for SearchHire {searchHireId}. " +

                            $"User {userId} attempted to propose appointment. " +

                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +

                            $"Stack Trace: {ex.StackTrace}. " +

                            $"User may need to retry the operation.",

                    userId: userId,

                    source: "AppointmentService.ProposeAppointmentAsync",

                    relatedEntityType: "Appointment",

                    relatedEntityId: 0, // No appointment created yet

                    additionalData: new { 

                        SearchHireId = searchHireId,

                        UserId = userId,

                        ErrorType = ex.GetType().Name,

                        ErrorMessage = ex.Message,

                        StackTrace = ex.StackTrace,

                        InnerException = ex.InnerException?.Message

                    }

                );

                throw;

            }

        }



        public async Task<AppointmentDto> ConfirmAppointmentAsync(ConfirmAppointmentDto dto, int userId)
        {
            try
            {
                // ✅ LOG: Inicio del proceso de confirmación
                await _loggingService.LogInfoAsync(
                    message: "Iniciando confirmación de cita",
                    details: $"Usuario {userId} intentando confirmar cita {dto.AppointmentId}",
                    userId: userId,
                    source: "AppointmentService.ConfirmAppointmentAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId,
                    notifyUser: false
                );

                // ✅ FIX CRÍTICO: NO usar ExecutionStrategy con transacciones manuales en PgBouncer
                // PgBouncer Transaction Pooler no admite savepoints automáticos que EF Core intenta crear
                // ✅ PROTECCIÓN: Abrir transacción ANTES del FOR UPDATE para que el bloqueo funcione
                using (var transaction = await _context.Database.BeginTransactionAsync())
                    {
                        try
                        {
                            // ✅ PROTECCIÓN: Usar row-level locking DENTRO de la transacción para evitar doble procesamiento
                            var appointment = await _context.Appointments
                                .FromSqlInterpolated($"SELECT * FROM \"Appointments\" WHERE \"Id\" = {dto.AppointmentId} FOR UPDATE")
                                .Include(a => a.SearchHire)
                                    .ThenInclude(sh => sh.Status)
                                .Include(a => a.Status)
                                .FirstOrDefaultAsync();

                            if (appointment == null)
                                throw new ArgumentException("Appointment not found");

                            var currentStatus = appointment.Status?.StatusValue ?? string.Empty;

                            // ✅ LOG: Cita cargada
                            await _loggingService.LogInfoAsync(
                                message: "Cita cargada para confirmación",
                                details: $"Cita {dto.AppointmentId} cargada. Estado actual: {currentStatus}, SearchHireId: {appointment.SearchHireId}, ExpertId: {appointment.SearchHire.ExpertId}",
                                userId: userId,
                                source: "AppointmentService.ConfirmAppointmentAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: dto.AppointmentId,
                                notifyUser: false
                            );

                            // ✅ VALIDACIÓN: Verificar que el usuario es el experto
                            if (appointment.SearchHire.ExpertId != userId)
                                throw new UnauthorizedAccessException("Only the expert can confirm appointments");

                            // ✅ VALIDACIÓN CRÍTICA: Verificar que el SearchHire NO esté finalizado
                            if (appointment.SearchHire.Status?.IsFinalizationStatus == true)
                            {
                                var searchHireStatus = appointment.SearchHire.Status?.StatusValue ?? "unknown";
                                throw new InvalidOperationException(
                                    $"No se puede confirmar una cita cuando el servicio está en estado de finalización '{searchHireStatus}'. " +
                                    $"El servicio debe estar activo para poder confirmar citas."
                                );
                            }

                            // ✅ VALIDACIÓN CRÍTICA: Solo se puede confirmar si la cita está en estado "appointment_proposed"
                            if (currentStatus != "appointment_proposed")
                            {
                                throw new InvalidOperationException(
                                    $"No se puede confirmar una cita en estado '{currentStatus}'. " +
                                    $"Solo se pueden confirmar citas en estado 'appointment_proposed' (cita propuesta por el cliente)."
                                );
                            }

                            // ✅ PROTECCIÓN: Verificar que no se haya procesado ya (evitar doble click)
                            var invalidStatesForConfirm = new[] { 
                                "appointment_confirmed",
                                AppointmentStatus.AppointmentRejected.ToStringValue(), 
                                AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue(),
                                AppointmentStatus.AppointmentCancelledByClient.ToStringValue(),
                                AppointmentStatus.AppointmentCancelledByClientSecond.ToStringValue(),
                                AppointmentStatus.AppointmentCancelledByExpert.ToStringValue(),
                                AppointmentStatus.AppointmentCancelledByExpertSecond.ToStringValue()
                            };

                            if (invalidStatesForConfirm.Contains(currentStatus))
                            {
                                throw new InvalidOperationException(
                                    $"La cita ya ha sido procesada (estado: '{currentStatus}'). " +
                                    $"No se puede confirmar nuevamente."
                                );
                            }

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

                            // ✅ CRÍTICO: Marcar la entidad como Modified explícitamente porque se cargó con FromSqlInterpolated
                            _context.Entry(appointment).State = EntityState.Modified;

                            // Marcar timers de respuesta como expirados y cancelar jobs de Hangfire
                            var responseTimers = await _context.AppointmentTimers
                                .Where(t => t.AppointmentId == appointment.Id && 
                                           t.TimerType == "response" && 
                                           !t.IsExpired)
                                .ToListAsync();

                            foreach (var timer in responseTimers)
                            {
                                timer.IsExpired = true;
                                timer.ExpiredAt = DateTime.UtcNow;
                                
                                // ✅ CANCELAR job de Hangfire si existe
                                if (!string.IsNullOrEmpty(timer.HangfireJobId))
                                {
                                    try
                                    {
                                        BackgroundJob.Delete(timer.HangfireJobId);
                                        timer.HangfireJobId = null; // Limpiar referencia
                                    }
                                    catch (Exception ex)
                                    {
                                        // Si el job ya no existe o fue procesado, continuar sin error
                                        timer.HangfireJobId = null;
                                    }
                                }
                            }

                            await _context.SaveChangesAsync();

                            // ✅ Programar job para cambiar a awaiting_report 3 horas después de la hora de la cita
                            if (appointment.ProposedDate.HasValue && appointment.ProposedTime.HasValue)
                            {
                                var appointmentDateTime = appointment.ProposedDate.Value.Date + appointment.ProposedTime.Value;
                                var timeUntil3HoursAfter = appointmentDateTime.AddHours(3) - DateTime.UtcNow;
                                
                                if (timeUntil3HoursAfter.TotalSeconds > 0) // Solo programar si aún no han pasado las 3 horas
                                {
                                // Crear timer para la transición a awaiting_report (3 horas después de la cita)
                                var awaitingReportTransitionTimer = new AppointmentTimer
                                {
                                    AppointmentId = appointment.Id,
                                    TimerType = "awaiting_report_transition",
                                    StartTime = DateTime.UtcNow,
                                    EndTime = appointmentDateTime.AddHours(3),
                                    IsExpired = false,
                                    CreatedAt = DateTime.UtcNow
                                };

                                _context.AppointmentTimers.Add(awaitingReportTransitionTimer);
                                await _context.SaveChangesAsync();

                                // Programar scheduled job para cuando expire el timer (3 horas después de la cita)
                                var jobId = BackgroundJob.Schedule<IAppointmentService>(
                                    service => service.ProcessAppointmentToAwaitingReportAsync(appointment.Id),
                                    timeUntil3HoursAfter
                                );

                                // Guardar el JobId en el timer
                                awaitingReportTransitionTimer.HangfireJobId = jobId;
                                await _context.SaveChangesAsync();
                                }
                            }

                            // ✅ LOG: Antes del commit
                            await _loggingService.LogInfoAsync(
                                message: "Preparando commit de confirmación de cita",
                                details: $"Cita {dto.AppointmentId} lista para commit. Nuevo estado: appointment_confirmed",
                                userId: userId,
                                source: "AppointmentService.ConfirmAppointmentAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: dto.AppointmentId,
                                notifyUser: false
                            );

                            // ✅ COMMIT: Confirmar la transacción
                            await transaction.CommitAsync();

                            // ✅ LOG: Commit exitoso
                            await _loggingService.LogInfoAsync(
                                message: "Commit de confirmación de cita exitoso",
                                details: $"Cita {dto.AppointmentId} confirmada exitosamente en la base de datos",
                                userId: userId,
                                source: "AppointmentService.ConfirmAppointmentAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: dto.AppointmentId,
                                notifyUser: false
                            );
                        }
                        catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                        {
                            // ✅ FIX CRÍTICO: Si la conexión está disposed, hacer rollback seguro
                            try
                            {
                                await transaction.RollbackAsync();
                            }
                            catch { }
                            throw; // Re-lanzar para que el usuario pueda reintentar
                        }
                        catch (ObjectDisposedException)
                        {
                            // ✅ FIX CRÍTICO: Si la conexión está disposed, hacer rollback seguro
                            try
                            {
                                await transaction.RollbackAsync();
                            }
                            catch { }
                            throw; // Re-lanzar para que el usuario pueda reintentar
                        }
                        catch (Exception innerEx)
                        {
                            // ✅ LOG: Error en transacción
                            await _loggingService.LogErrorAsync(
                                message: "Error en transacción al confirmar cita",
                                details: $"Error al confirmar cita {dto.AppointmentId} dentro de la transacción. Error: {innerEx.GetType().Name} - {innerEx.Message}",
                                userId: userId,
                                source: "AppointmentService.ConfirmAppointmentAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: dto.AppointmentId,
                                notifyUser: false
                            );

                            // ✅ ROLLBACK: Revertir la transacción en caso de error
                            try
                            {
                                await transaction.RollbackAsync();
                            }
                            catch { }
                            throw;
                        }
                    } // Cierre del using var transaction

                // ✅ CÓDIGO POST-COMMIT: Ejecutar fuera de la transacción para evitar errores de NpgsqlTransaction
                // ⚠️ IMPORTANTE: Si estas operaciones fallan, no deben afectar la respuesta ya que la transacción principal ya se completó
                AppointmentDto result;
                try
                {
                    // ✅ LOG: Iniciando operaciones post-commit
                    await _loggingService.LogInfoAsync(
                        message: "Iniciando operaciones post-commit",
                        details: $"Cargando cita {dto.AppointmentId} actualizada para operaciones post-commit",
                        userId: userId,
                        source: "AppointmentService.ConfirmAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: dto.AppointmentId,
                        notifyUser: false
                    );

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
                        .FirstAsync(a => a.Id == dto.AppointmentId);

                    // ✅ LOG: Enviando mensaje al chat
                    await _loggingService.LogInfoAsync(
                        message: "Enviando mensaje al chat",
                        details: $"Enviando mensaje de cambio de estado al chat para SearchHire {updatedAppointment.SearchHireId}",
                        userId: userId,
                        source: "AppointmentService.ConfirmAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: dto.AppointmentId,
                        notifyUser: false
                    );

                    // ✅ Enviar mensaje al chat con el cambio de estado (después del commit)
                    await SendAppointmentStatusChangeMessageAsync(
                        updatedAppointment.SearchHireId, 
                        AppointmentStatus.AppointmentConfirmed.ToStringValue(), 
                        userId
                    );

                    // ✅ LOG: Mensaje al chat enviado
                    await _loggingService.LogInfoAsync(
                        message: "Mensaje al chat enviado",
                        details: $"Mensaje de cambio de estado enviado exitosamente al chat",
                        userId: userId,
                        source: "AppointmentService.ConfirmAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: dto.AppointmentId,
                        notifyUser: false
                    );

                    // ✅ Notificar al cliente que la cita fue confirmada por el experto
                    if (updatedAppointment.SearchHire?.ClientId != null
                        && updatedAppointment.ProposedDate.HasValue && updatedAppointment.ProposedTime.HasValue)
                    {
                        // Formatear fecha y hora correctamente (combinar Date + TimeSpan para obtener DateTime)
                        var appointmentDateTime = updatedAppointment.ProposedDate.Value.Date + updatedAppointment.ProposedTime.Value;
                        var formattedDateTime = appointmentDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
                        
                        // ✅ LOG: Enviando notificación al cliente
                        await _loggingService.LogInfoAsync(
                            message: "Enviando notificación al cliente",
                            details: $"Preparando notificación para cliente {updatedAppointment.SearchHire.ClientId}. Fecha formateada: {formattedDateTime}",
                            userId: userId,
                            source: "AppointmentService.ConfirmAppointmentAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: dto.AppointmentId,
                            notifyUser: false
                        );

                        await _loggingService.LogInfoAsync(
                            message: "Cita confirmada por el experto",
                            details: $"El experto confirmó la cita para el {formattedDateTime} en {updatedAppointment.Location}.",
                            userId: updatedAppointment.SearchHire.ClientId,
                            source: "AppointmentService.ConfirmAppointmentAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: updatedAppointment.Id,
                            notifyUser: false
                        );

                        // ✅ EMAIL: Enviar email personalizado de confirmación
                        if (updatedAppointment.SearchHire.Client != null && !string.IsNullOrEmpty(updatedAppointment.SearchHire.Client.Email))
                        {
                            await _notificationService.SendAppointmentConfirmationEmailAsync(
                                updatedAppointment.SearchHire.Client.Email, 
                                updatedAppointment.SearchHire.Client.Name, 
                                formattedDateTime, 
                                updatedAppointment.Location, 
                                isExpert: false,
                                searchHireId: updatedAppointment.SearchHireId
                            );
                        }

                        // ✅ LOG: Notificación al cliente enviada
                        await _loggingService.LogInfoAsync(
                            message: "Notificación al cliente enviada",
                            details: $"Notificación enviada exitosamente al cliente {updatedAppointment.SearchHire.ClientId}",
                            userId: userId,
                            source: "AppointmentService.ConfirmAppointmentAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: dto.AppointmentId,
                            notifyUser: false
                        );
                    }

                    // ✅ Notificar al experto confirmando la cita (Confirmación para el remitente)
                    if (updatedAppointment.SearchHire?.ExpertId.HasValue == true && updatedAppointment.SearchHire.ExpertId.Value > 0
                        && updatedAppointment.ProposedDate.HasValue && updatedAppointment.ProposedTime.HasValue)
                    {
                        var appointmentDateTime = updatedAppointment.ProposedDate.Value.Date + updatedAppointment.ProposedTime.Value;
                        var formattedDate = appointmentDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
                        
                        await _loggingService.LogInfoAsync(
                            message: "Cita confirmada correctamente",
                            details: $"Has confirmado la cita para el {formattedDate}. Recuerda que solo puedes cancelar hasta 12 horas antes de la cita.",
                            userId: updatedAppointment.SearchHire.ExpertId.Value,
                            source: "AppointmentService.ConfirmAppointmentAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: updatedAppointment.Id,
                            notifyUser: false
                        );

                        // ✅ EMAIL: Enviar email personalizado de confirmación al experto
                        if (updatedAppointment.SearchHire.Expert != null && !string.IsNullOrEmpty(updatedAppointment.SearchHire.Expert.Email))
                        {
                            await _notificationService.SendAppointmentConfirmationEmailAsync(
                                updatedAppointment.SearchHire.Expert.Email, 
                                updatedAppointment.SearchHire.Expert.Name, 
                                formattedDate, 
                                updatedAppointment.Location, 
                                isExpert: true,
                                searchHireId: updatedAppointment.SearchHireId
                            );
                        }
                    }

                    // ✅ LOG: Mapeando a DTO
                    await _loggingService.LogInfoAsync(
                        message: "Mapeando cita a DTO",
                        details: $"Convirtiendo cita {dto.AppointmentId} a AppointmentDto",
                        userId: userId,
                        source: "AppointmentService.ConfirmAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: dto.AppointmentId,
                        notifyUser: false
                    );

                    result = MapToDto(updatedAppointment);

                    // ✅ LOG: Operaciones post-commit completadas
                    await _loggingService.LogInfoAsync(
                        message: "Operaciones post-commit completadas",
                        details: $"Todas las operaciones post-commit completadas exitosamente para cita {dto.AppointmentId}",
                        userId: userId,
                        source: "AppointmentService.ConfirmAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: dto.AppointmentId,
                        notifyUser: false
                    );
                }
                catch (Exception postCommitEx)
                {
                    // ✅ LOG: Error en operaciones post-commit
                    await _loggingService.LogWarningAsync(
                        message: "Error en operaciones post-commit",
                        details: $"Error en operaciones post-commit para cita {dto.AppointmentId}. Error: {postCommitEx.GetType().Name} - {postCommitEx.Message}. StackTrace: {postCommitEx.StackTrace}",
                        userId: userId,
                        source: "AppointmentService.ConfirmAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: dto.AppointmentId,
                        notifyUser: false
                    );

                    // ⚠️ LOG WARNING: Error en operaciones post-commit (la transacción principal ya se completó)
                    // Intentar cargar la cita de forma más simple para devolver el resultado
                    try
                    {
                        // ✅ LOG: Intentando fallback
                        await _loggingService.LogInfoAsync(
                            message: "Intentando fallback de carga de cita",
                            details: $"Intentando cargar cita {dto.AppointmentId} con relaciones mínimas",
                            userId: userId,
                            source: "AppointmentService.ConfirmAppointmentAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: dto.AppointmentId,
                            notifyUser: false
                        );

                        var fallbackAppointment = await _context.Appointments
                            .Include(a => a.SearchHire)
                                .ThenInclude(sh => sh.Client)
                            .Include(a => a.SearchHire)
                                .ThenInclude(sh => sh.Expert)
                            .Include(a => a.SearchHire)
                                .ThenInclude(sh => sh.Status)
                            .Include(a => a.Status)
                            .FirstAsync(a => a.Id == dto.AppointmentId);

                        await _loggingService.LogWarningAsync(
                            message: "Error en operaciones post-commit al confirmar cita",
                            details: $"La cita {dto.AppointmentId} se confirmó exitosamente, pero hubo un error en operaciones post-commit (mensajes/notificaciones). " +
                                    $"Error Type: {postCommitEx.GetType().Name}, Error Message: {postCommitEx.Message}. " +
                                    $"La cita fue confirmada correctamente en la base de datos.",
                            userId: userId,
                            source: "AppointmentService.ConfirmAppointmentAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: dto.AppointmentId,
                            notifyUser: false
                        );

                        result = MapToDto(fallbackAppointment);
                    }
                    catch (Exception fallbackEx)
                    {
                        // Si incluso el fallback falla, intentar una carga mínima
                        try
                        {
                            var minimalAppointment = await _context.Appointments
                                .AsNoTracking()
                                .FirstAsync(a => a.Id == dto.AppointmentId);

                            await _loggingService.LogWarningAsync(
                                message: "Error en operaciones post-commit al confirmar cita - usando carga mínima",
                                details: $"La cita {dto.AppointmentId} se confirmó exitosamente, pero hubo errores en operaciones post-commit. " +
                                        $"Error original: {postCommitEx.GetType().Name} - {postCommitEx.Message}. " +
                                        $"Error fallback: {fallbackEx.GetType().Name} - {fallbackEx.Message}. " +
                                        $"Se devuelve resultado con carga mínima. La cita fue confirmada correctamente en la base de datos.",
                                userId: userId,
                                source: "AppointmentService.ConfirmAppointmentAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: dto.AppointmentId,
                                notifyUser: false
                            );

                            // Construir un DTO básico con la información mínima disponible
                            result = new AppointmentDto
                            {
                                Id = minimalAppointment.Id,
                                SearchHireId = minimalAppointment.SearchHireId,
                                ProposedDate = minimalAppointment.ProposedDate,
                                ProposedTime = minimalAppointment.ProposedTime,
                                Location = minimalAppointment.Location,
                                Latitude = minimalAppointment.Latitude,
                                Longitude = minimalAppointment.Longitude,
                                DoorNumber = minimalAppointment.DoorNumber,
                                OwnerPhone = minimalAppointment.OwnerPhone,
                                SiteDetails = minimalAppointment.SiteDetails,
                                RejectionCount = minimalAppointment.RejectionCount,
                                ClientCancellationCount = minimalAppointment.ClientCancellationCount,
                                ExpertCancellationCount = minimalAppointment.ExpertCancellationCount,
                                LastRejectionAt = minimalAppointment.LastRejectionAt,
                                LastClientCancellationAt = minimalAppointment.LastClientCancellationAt,
                                LastExpertCancellationAt = minimalAppointment.LastExpertCancellationAt,
                                LastProposalAt = minimalAppointment.LastProposalAt,
                                LastResponseAt = minimalAppointment.LastResponseAt,
                                Status = "appointment_confirmed", // Sabemos que se confirmó
                                CreatedAt = minimalAppointment.CreatedAt,
                                UpdatedAt = minimalAppointment.UpdatedAt,
                                Timers = new List<AppointmentTimerDto>() // Lista vacía ya que no cargamos relaciones
                            };
                        }
                        catch (Exception minimalEx)
                        {
                            // Solo en este caso extremo lanzar la excepción
                            await _loggingService.LogErrorAsync(
                                message: "Error crítico al confirmar cita - no se pudo cargar ni mínimamente",
                                details: $"La cita {dto.AppointmentId} se confirmó exitosamente en la BD, pero no se pudo cargar para devolver el resultado. " +
                                        $"Error original: {postCommitEx.GetType().Name} - {postCommitEx.Message}. " +
                                        $"Error fallback: {fallbackEx.GetType().Name} - {fallbackEx.Message}. " +
                                        $"Error mínimo: {minimalEx.GetType().Name} - {minimalEx.Message}.",
                                userId: userId,
                                source: "AppointmentService.ConfirmAppointmentAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: dto.AppointmentId,
                                notifyUser: false
                            );
                            throw postCommitEx; // Lanzar la excepción original para que el controller la maneje
                        }
                    }
                }

                return result;

            }

            catch (Exception ex)

            {

                // ⚠️ LOG WARNING: Error general confirmando cita (no afecta dinero, usuario puede reintentar)

                await _loggingService.LogWarningAsync(

                    message: "Error confirming appointment",

                    details: $"An unexpected exception occurred while confirming appointment {dto.AppointmentId}. " +

                            $"User {userId} attempted to confirm appointment. " +

                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +

                            $"Stack Trace: {ex.StackTrace}. " +

                            $"User may need to retry the operation.",

                    userId: userId,

                    source: "AppointmentService.ConfirmAppointmentAsync",

                    relatedEntityType: "Appointment",

                    relatedEntityId: dto.AppointmentId,

                    additionalData: new { 

                        AppointmentId = dto.AppointmentId,

                        UserId = userId,

                        ErrorType = ex.GetType().Name,

                        ErrorMessage = ex.Message,

                        StackTrace = ex.StackTrace,

                        InnerException = ex.InnerException?.Message

                    }

                );

                throw;

            }

        }



        public async Task<AppointmentDto> RejectAppointmentAsync(RejectAppointmentDto dto, int userId)

        {

            try

            {

                // ✅ FIX CRÍTICO: NO usar ExecutionStrategy con transacciones manuales en PgBouncer
                // PgBouncer Transaction Pooler no admite savepoints automáticos que EF Core intenta crear
                // ✅ PROTECCIÓN: Abrir transacción ANTES del FOR UPDATE para que el bloqueo funcione

                using var transaction = await _context.Database.BeginTransactionAsync();

                    try

                    {

                        // ✅ PROTECCIÓN: Usar row-level locking DENTRO de la transacción para evitar doble procesamiento

                var appointment = await _context.Appointments

                            .FromSqlInterpolated($"SELECT * FROM \"Appointments\" WHERE \"Id\" = {dto.AppointmentId} FOR UPDATE")

                    .Include(a => a.SearchHire)

                        .ThenInclude(sh => sh.Status)

                    .Include(a => a.Status)

                            .FirstOrDefaultAsync();



                if (appointment == null)

                {

                    throw new ArgumentException("Appointment not found");

                }

                        // ✅ VALIDACIÓN: Verificar que el usuario es el experto

                if (appointment.SearchHire.ExpertId != userId)

                {

                    throw new UnauthorizedAccessException("Only the expert can reject appointments");

                }

                        // ✅ VALIDACIÓN CRÍTICA: Verificar que el SearchHire NO esté finalizado
                        if (appointment.SearchHire.Status?.IsFinalizationStatus == true)
                        {
                            var searchHireStatus = appointment.SearchHire.Status?.StatusValue ?? "unknown";
                            throw new InvalidOperationException(
                                $"No se puede rechazar una cita cuando el servicio está en estado de finalización '{searchHireStatus}'. " +
                                $"El servicio debe estar activo para poder rechazar citas."
                            );
                        }

                        // ✅ VALIDACIÓN CRÍTICA: Solo se puede rechazar si la cita está en estado "appointment_proposed"

                        // No se puede rechazar si está en "awaiting_appointment" (no hay propuesta aún) o en otros estados finales

                        var currentStatus = appointment.Status?.StatusValue ?? string.Empty;

                        if (currentStatus != "appointment_proposed")

                        {

                            throw new InvalidOperationException(

                                $"No se puede rechazar una cita en estado '{currentStatus}'. " +

                                $"Solo se pueden rechazar citas en estado 'appointment_proposed' (cita propuesta por el cliente)."

                            );

                        }



                        // ✅ PROTECCIÓN: Verificar que no se haya procesado ya (evitar doble click)

                        // Si ya está en un estado de rechazo o cancelación, no permitir otra operación

                        var invalidStatesForReject = new[] { 

                            AppointmentStatus.AppointmentRejected.ToStringValue(), 

                            AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByClient.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByClientSecond.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByExpert.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByExpertSecond.ToStringValue(),

                            "appointment_confirmed"

                        };

                        

                        if (invalidStatesForReject.Contains(currentStatus))

                        {

                            throw new InvalidOperationException(

                                $"La cita ya ha sido procesada (estado: '{currentStatus}'). " +

                                $"No se puede rechazar nuevamente."

                            );

                        }

                // 🔍 LOGS DETALLADOS: Analizar el estado actual

                // Determinar el estado según el número de rechazos

                string statusValue;

                bool isSecondRejection = appointment.RejectionCount >= 1;

                

                

                if (isSecondRejection)

                {

                    // Segundo rechazo o más - cancelar por rechazos múltiples

                    // ✅ CORRECCIÓN: Usar el estado correcto para rechazo (no cancelación)

                    statusValue = AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue();

                }

                else

                {

                    // Primer rechazo

                    statusValue = AppointmentStatus.AppointmentRejected.ToStringValue();

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

                }

                

                appointment.LastRejectionAt = DateTime.UtcNow;

                appointment.LastResponseAt = DateTime.UtcNow;

                appointment.UpdatedAt = DateTime.UtcNow;

                // ✅ CRÍTICO: Marcar la entidad como Modified explícitamente porque se cargó con FromSqlInterpolated
                _context.Entry(appointment).State = EntityState.Modified;

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

                    var statusId = await GetStatusIdByValueAsync(targetSearchHireStatus.Value.ToStringValue());

                    appointment.SearchHire.StatusId = statusId;

                    appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

                }

                else

                {
                    // ✅ MEJORA: Si NO hay mapeo, loguear pero NO bloquear el cambio de estado
                    // El Appointment.StatusId YA cambió (línea 1478), esto es correcto
                    // El SearchHire NO cambia porque no hay mapeo (comportamiento esperado para estados no finales)
                    await _loggingService.LogWarningAsync(
                        message: "No mapping found for AppointmentStatus to SearchHireStatus",
                        details: $"AppointmentStatus '{statusValue}' does not have a mapping to SearchHireStatus. " +
                                $"Appointment {appointment.Id} status was updated, but SearchHire {appointment.SearchHireId} status was not changed. " +
                                $"This is expected for non-finalization states (e.g., first rejection/cancellation).",
                        userId: userId,
                        source: "AppointmentService.RejectAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: appointment.Id,
                        additionalData: new { 
                            AppointmentStatus = statusValue,
                            AppointmentId = appointment.Id,
                            SearchHireId = appointment.SearchHireId
                        }
                    );
                }



                // Marcar timers de respuesta como expirados y cancelar jobs de Hangfire

                var responseTimers = await _context.AppointmentTimers

                    .Where(t => t.AppointmentId == appointment.Id && 

                               t.TimerType == "response" && 

                               !t.IsExpired)

                    .ToListAsync();



                foreach (var timer in responseTimers)

                {

                    timer.IsExpired = true;

                    timer.ExpiredAt = DateTime.UtcNow;
                    
                    // ✅ CANCELAR job de Hangfire si existe
                    if (!string.IsNullOrEmpty(timer.HangfireJobId))
                    {
                        try
                        {
                            BackgroundJob.Delete(timer.HangfireJobId);
                            timer.HangfireJobId = null; // Limpiar referencia
                        }
                        catch (Exception ex)
                        {
                            // Si el job ya no existe o fue procesado, continuar sin error
                            timer.HangfireJobId = null;
                        }
                    }
                }



                var saveChangesResult = await _context.SaveChangesAsync();

                // ✅ DEBUG: Verificar que SaveChanges se ejecutó correctamente
                await _loggingService.LogInfoAsync(
                    message: "SaveChanges ejecutado en RejectAppointment",
                    details: $"SaveChangesResult: {saveChangesResult} entidades modificadas. AppointmentId: {appointment.Id}, StatusId: {appointment.StatusId}, RejectionCount: {appointment.RejectionCount}",
                    userId: userId,
                    source: "AppointmentService.RejectAppointmentAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: appointment.Id,
                    notifyUser: false
                );

                

                // ✅ CORRECCIÓN: Procesar refund automático para segunda cancelación DENTRO de la transacción
                // RefundService ahora detecta transacciones existentes y las usa en lugar de crear nuevas
                if (isSecondRejection)
                {
                    try
                    {
                        // 🔍 LOG: Verificar configuración de dinero antes del refund
                        var moneyConfig = await _systemStatusService.GetMoneyDistributionConfigAsync(
                            AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue(), 
                            appointment.SearchHire.SearchService?.CategoryId, 
                            appointment.SearchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);

                        // Orquestar refund+transfer según configuración del subestado de finalización
                        // ✅ OPTIMIZACIÓN: updateState: false porque ya cambiamos el estado arriba
                        var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                            appointment.SearchHireId,
                            AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue(),
                            "Segundo rechazo del experto - penalización máxima",
                            userId,
                            updateState: false);

                        if (!refundSuccess)
                        {
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
                        // 🚨 LOG CRÍTICO: Error procesando refund automático (una sola vez, con información completa)
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Error processing automatic refund during appointment rejection",
                            details: $"Automatic refund failed during appointment rejection for Appointment {appointment.Id} (SearchHire {appointment.SearchHireId}). " +
                                    $"This occurred on second rejection by expert {userId}. " +
                                    $"Error Type: {refundEx.GetType().Name}, Error Message: {refundEx.Message}. " +
                                    $"SearchHire Amount: {appointment.SearchHire?.Amount}€, ClientId: {appointment.SearchHire?.ClientId}, ExpertId: {appointment.SearchHire?.ExpertId}. " +
                                    $"Stack Trace: {refundEx.StackTrace}. " +
                                    $"ACTION REQUIRED: Review refund error and manually process refund if needed. Appointment rejection completed but refund failed.",
                            userId: appointment.SearchHire?.ClientId,
                            source: "AppointmentService.RejectAppointmentAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: appointment.Id,
                            additionalData: new { 
                                AppointmentId = appointment.Id,
                                SearchHireId = appointment.SearchHireId,
                                Amount = appointment.SearchHire?.Amount,
                                ClientId = appointment.SearchHire?.ClientId,
                                ExpertId = appointment.SearchHire?.ExpertId,
                                ExpertUserId = userId,
                                ErrorType = refundEx.GetType().Name,
                                ErrorMessage = refundEx.Message,
                                StackTrace = refundEx.StackTrace,
                                InnerException = refundEx.InnerException?.Message
                            }
                        );
                        
                        // No lanzar la excepción para no afectar el flujo principal
                    }
                }

                if (!isSecondRejection)

                {
                    // ✅ Si es primer rechazo, restaurar timer de 24h para que el cliente proponga otra vez
                    // NO cambiar el estado - se mantiene como "appointment_rejected"
                    // El cliente puede proponer desde "appointment_rejected"
                    
                    // Crear nuevo timer para propuesta del cliente (24 horas)
                    var proposalTimer = new AppointmentTimer
                    {
                        AppointmentId = appointment.Id,
                        TimerType = "proposal",
                        StartTime = DateTime.UtcNow,
                        EndTime = DateTime.UtcNow.AddHours(24),
                        IsExpired = false,
                        CreatedAt = DateTime.UtcNow
                    };
                    
                    _context.AppointmentTimers.Add(proposalTimer);
                    await _context.SaveChangesAsync();
                    
                    // Programar scheduled job para cuando expire el timer (24 horas)
                    var jobId = BackgroundJob.Schedule<IAppointmentService>(
                        service => service.ProcessAppointmentTimerAsync(proposalTimer.Id),
                        proposalTimer.EndTime - DateTime.UtcNow
                    );
                    
                    // Guardar el JobId en el timer
                    proposalTimer.HangfireJobId = jobId;
                    await _context.SaveChangesAsync();
                }



                        // ✅ COMMIT: Confirmar la transacción

                        await transaction.CommitAsync();

                        // ✅ CRÍTICO: Detach el appointment del contexto para forzar recarga desde BD
                        _context.Entry(appointment).State = EntityState.Detached;

                // ✅ CRÍTICO: Cargar la cita actualizada con todas las relaciones usando AsNoTracking para evitar problemas de caché
                var updatedAppointment = await _context.Appointments
                    .AsNoTracking()
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(a => a.Status)
                    .Include(a => a.Timers)
                    .FirstAsync(a => a.Id == appointment.Id);

                // ✅ DEBUG: Verificar que los valores se cargaron correctamente después del commit
                await _loggingService.LogInfoAsync(
                    message: "Appointment recargado después del commit en RejectAppointment",
                    details: $"AppointmentId: {updatedAppointment.Id}, StatusId: {updatedAppointment.StatusId}, StatusValue: {updatedAppointment.Status?.StatusValue}, RejectionCount: {updatedAppointment.RejectionCount}, UpdatedAt: {updatedAppointment.UpdatedAt}",
                    userId: userId,
                    source: "AppointmentService.RejectAppointmentAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: updatedAppointment.Id,
                    notifyUser: false
                );

                

                // ✅ Enviar mensaje al chat con el cambio de estado (después del commit)

                // El statusValue se determina según si es primera o segunda cancelación

                var statusValueToSend = isSecondRejection

                    ? AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue()

                    : AppointmentStatus.AppointmentRejected.ToStringValue();

                

                await SendAppointmentStatusChangeMessageAsync(

                    appointment.SearchHireId, 

                    statusValueToSend, 

                    userId

                );



                // ✅ Notificar al cliente sobre el rechazo
                if (isSecondRejection)
                {
                    // Segunda cancelación - notificar sobre refund automático
                    await _loggingService.LogWarningAsync(
                        message: "Cita rechazada por segunda vez",
                        details: $"El experto rechazó la propuesta de cita por segunda vez. Se procesará tu reembolso automáticamente.",
                        userId: appointment.SearchHire.ClientId,
                        source: "AppointmentService.RejectAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: appointment.Id,
                        notifyUser: false
                    );

                    // ✅ EMAIL: Notificar cancelación por rechazos múltiples
                    if (updatedAppointment.SearchHire.Client != null && !string.IsNullOrEmpty(updatedAppointment.SearchHire.Client.Email))
                    {
                        var expertName = updatedAppointment.SearchHire.Expert?.Name ?? "El experto";
                        await _notificationService.SendGeneralNotificationEmailAsync(
                            updatedAppointment.SearchHire.Client.Email,
                            updatedAppointment.SearchHire.Client.Name,
                            "❌ Cita Cancelada",
                            $"{expertName} ha rechazado la propuesta de cita por segunda vez. Lamentamos los inconvenientes. Hemos procesado el reembolso completo a tu favor.",
                            "Ver Reembolso",
                            "https://www.inspecciono.com/appointments"
                        );
                    }

                    // ✅ Notificar al experto (Confirmación de rechazo final)
                    await _loggingService.LogWarningAsync(
                        message: "Has rechazado la cita por segunda vez",
                        details: $"Has rechazado la propuesta por segunda vez. El servicio ha sido cancelado y se ha procesado el reembolso al cliente.",
                        userId: userId,
                        source: "AppointmentService.RejectAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: appointment.Id,
                        notifyUser: false
                    );

                    // ✅ EMAIL: Confirmar al experto que se canceló por segundo rechazo
                    if (updatedAppointment.SearchHire.Expert != null && !string.IsNullOrEmpty(updatedAppointment.SearchHire.Expert.Email))
                    {
                        await _notificationService.SendGeneralNotificationEmailAsync(
                            updatedAppointment.SearchHire.Expert.Email,
                            updatedAppointment.SearchHire.Expert.Name,
                            "Cita Cancelada (2do rechazo)",
                            "Has rechazado la cita por segunda vez. La contratación ha sido cancelada y el cliente reembolsado."
                        );
                    }
                }
                else
                {
                    // Primera cancelación - notificar que puede proponer otra
                    await _loggingService.LogInfoAsync(
                        message: "Cita rechazada",
                        details: $"El experto rechazó la propuesta de cita. Puedes proponer otra fecha y hora. Tienes 24 horas para hacerlo.",
                        userId: appointment.SearchHire.ClientId,
                        source: "AppointmentService.RejectAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: appointment.Id,
                        notifyUser: false
                    );

                    // ✅ EMAIL: Notificar rechazo y pedir nueva propuesta
                    if (updatedAppointment.SearchHire.Client != null && !string.IsNullOrEmpty(updatedAppointment.SearchHire.Client.Email))
                    {
                        var expertName = updatedAppointment.SearchHire.Expert?.Name ?? "El experto";
                        await _notificationService.SendGeneralNotificationEmailAsync(
                            updatedAppointment.SearchHire.Client.Email,
                            updatedAppointment.SearchHire.Client.Name,
                            "📅 Cita Rechazada",
                            $"{expertName} no puede asistir en la fecha propuesta. Por favor, propón una nueva fecha y hora para la cita.<br><br>Tienes 24 horas para enviar una nueva propuesta.",
                            "Proponer Nueva Fecha",
                            "https://www.inspecciono.com/appointments"
                        );
                    }

                    // ✅ Notificar al experto (Confirmación de rechazo)
                    await _loggingService.LogInfoAsync(
                        message: "Has rechazado la cita",
                        details: $"Has rechazado la propuesta de cita. El cliente tiene 24 horas para proponer una nueva fecha.",
                        userId: userId,
                        source: "AppointmentService.RejectAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: appointment.Id,
                        notifyUser: false
                    );

                    // ✅ EMAIL: Confirmar al experto que rechazó la cita
                    if (updatedAppointment.SearchHire.Expert != null && !string.IsNullOrEmpty(updatedAppointment.SearchHire.Expert.Email))
                    {
                        await _notificationService.SendGeneralNotificationEmailAsync(
                            updatedAppointment.SearchHire.Expert.Email,
                            updatedAppointment.SearchHire.Expert.Name,
                            "Cita Rechazada",
                            "Has rechazado la propuesta de cita. Hemos notificado al cliente para que proponga una nueva fecha."
                        );
                    }
                }



                return MapToDto(updatedAppointment);

                    }

                    catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, hacer rollback seguro
                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }
                        throw; // Re-lanzar para que el usuario pueda reintentar
                    }
                    catch (ObjectDisposedException)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, hacer rollback seguro
                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }
                        throw; // Re-lanzar para que el usuario pueda reintentar
                    }
                    catch (Exception innerEx)

                    {

                        // ✅ ROLLBACK: Revertir la transacción en caso de error

                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }

                        throw;

                    }

            }

            catch (Exception ex)

            {

                // ⚠️ LOG WARNING: Error general rechazando cita (el refund tiene su propio CRITICAL si falla, usuario puede reintentar)

                await _loggingService.LogWarningAsync(

                    message: "Error rejecting appointment",

                    details: $"An unexpected exception occurred while rejecting appointment {dto.AppointmentId}. " +

                            $"Expert {userId} attempted to reject appointment. " +

                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +

                            $"Stack Trace: {ex.StackTrace}. " +

                            $"Expert may need to retry the operation. Note: If refund processing fails, it will be logged separately as CRITICAL.",

                    userId: userId,

                    source: "AppointmentService.RejectAppointmentAsync",

                    relatedEntityType: "Appointment",

                    relatedEntityId: dto.AppointmentId,

                    additionalData: new { 

                        AppointmentId = dto.AppointmentId,

                        UserId = userId,

                        ErrorType = ex.GetType().Name,

                        ErrorMessage = ex.Message,

                        StackTrace = ex.StackTrace,

                        InnerException = ex.InnerException?.Message

                    }

                );

                throw;

            }

        }



        public async Task<AppointmentDto> CancelAppointmentAsync(CancelAppointmentDto dto, int userId)

        {

            try

            {

                // ✅ FIX CRÍTICO: NO usar ExecutionStrategy con transacciones manuales en PgBouncer
                // PgBouncer Transaction Pooler no admite savepoints automáticos que EF Core intenta crear
                // ✅ PROTECCIÓN: Abrir transacción ANTES del FOR UPDATE para que el bloqueo funcione

                using var transaction = await _context.Database.BeginTransactionAsync();

                    try

                    {

                        // ✅ PROTECCIÓN: Usar row-level locking DENTRO de la transacción para evitar doble procesamiento

                var appointment = await _context.Appointments

                            .FromSqlInterpolated($"SELECT * FROM \"Appointments\" WHERE \"Id\" = {dto.AppointmentId} FOR UPDATE")

                    .Include(a => a.SearchHire)

                        .ThenInclude(sh => sh.Status)

                    .Include(a => a.Status)

                            .FirstOrDefaultAsync();



                if (appointment == null)

                    throw new ArgumentException("Appointment not found");



                        var currentStatus = appointment.Status?.StatusValue ?? string.Empty;

                        // ✅ VALIDACIÓN: Verificar que el usuario es el cliente o el experto

                if (appointment.SearchHire.ClientId != userId && appointment.SearchHire.ExpertId != userId)

                    throw new UnauthorizedAccessException("Only the client or expert can cancel appointments");

                        // ✅ VALIDACIÓN CRÍTICA: Verificar que el SearchHire NO esté finalizado
                        if (appointment.SearchHire.Status?.IsFinalizationStatus == true)
                        {
                            var searchHireStatus = appointment.SearchHire.Status?.StatusValue ?? "unknown";
                            throw new InvalidOperationException(
                                $"No se puede cancelar una cita cuando el servicio está en estado de finalización '{searchHireStatus}'. " +
                                $"El servicio debe estar activo para poder cancelar citas."
                            );
                        }

                        // ✅ VALIDACIÓN CRÍTICA: No se puede cancelar si está en "awaiting_appointment" (no hay propuesta aún)

                        // Solo se puede cancelar si hay una propuesta o cita confirmada

                        if (currentStatus == AppointmentStatus.AwaitingAppointment.ToStringValue())

                        {

                            throw new InvalidOperationException(

                                "No se puede cancelar una cita en estado 'awaiting_appointment'. " +

                                "En este estado no hay ninguna propuesta de cita vigente. " +

                                "Solo se pueden cancelar citas que ya han sido propuestas o confirmadas."

                            );

                        }



                        // ✅ PROTECCIÓN: Verificar que no se haya procesado ya (evitar doble click)

                        // Estados finales donde no se puede cancelar

                        var finalStates = new[] { 

                            AppointmentStatus.AppointmentCancelledByClient.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByClientSecond.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByExpert.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByExpertSecond.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),

                            AppointmentStatus.AppointmentCompletedWithoutClientApproval.ToStringValue(),

                            AppointmentStatus.AppointmentReportSent.ToStringValue()

                        };

                        

                        if (finalStates.Contains(currentStatus))

                        {

                            throw new InvalidOperationException(

                                $"La cita ya está cancelada o finalizada (estado: '{currentStatus}'). " +

                                $"No se puede cancelar nuevamente."

                            );

                        }



                        // ✅ VALIDACIÓN: Solo se pueden cancelar citas en estados válidos

                        // Estados válidos: SOLO appointment_confirmed (cuando la cita ya está confirmada)
                        // - appointment_proposed: El experto puede rechazar/aprobar, no necesita cancelar
                        // - appointment_rejected: El cliente puede proponer nueva cita, no necesita cancelar
                        // - appointment_confirmed: No hay otra acción disponible, cancelar es la única opción

                        var validStatesForCancel = new[] { 

                            "appointment_confirmed" // Solo cuando está confirmada

                        };

                        

                        if (!validStatesForCancel.Contains(currentStatus))

                        {

                            throw new InvalidOperationException(

                                $"No se puede cancelar una cita en estado '{currentStatus}'. " +

                                $"Solo se pueden cancelar citas en estados: {string.Join(", ", validStatesForCancel)}."

                            );

                        }

                        // ✅ VALIDACIÓN: No se puede cancelar si quedan menos de 12 horas antes de la cita
                        // Solo aplicar si la cita está confirmada (appointment_confirmed)
                        if (currentStatus == "appointment_confirmed")
                        {
                            // Verificar que la fecha propuesta sea válida (no sea null, DateTime.MinValue o default)
                            if (appointment.ProposedDate.HasValue && appointment.ProposedTime.HasValue &&
                                appointment.ProposedDate.Value != default(DateTime) && appointment.ProposedDate.Value > DateTime.MinValue)
                            {
                                var appointmentDateTime = appointment.ProposedDate.Value.Date + appointment.ProposedTime.Value;
                                var timeUntilAppointment = appointmentDateTime - DateTime.UtcNow;
                                
                                if (timeUntilAppointment.TotalHours < 12)
                                {
                                    string errorMessage;
                                    if (timeUntilAppointment.TotalHours < 0)
                                    {
                                        // La cita ya pasó
                                        errorMessage = $"No se puede cancelar una cita que ya ha pasado. " +
                                                      $"La cita era el {appointmentDateTime:dd/MM/yyyy HH:mm} UTC y ya ha transcurrido.";
                                    }
                                    else
                                    {
                                        // La cita está muy cerca (menos de 12h)
                                        var hoursRemaining = (int)Math.Ceiling(timeUntilAppointment.TotalHours);
                                        errorMessage = $"No se puede cancelar una cita con menos de 12 horas de antelación. " +
                                                      $"Quedan aproximadamente {hoursRemaining} horas hasta la cita " +
                                                      $"(programada para el {appointmentDateTime:dd/MM/yyyy HH:mm} UTC).";
                                    }
                                    
                                    throw new InvalidOperationException(errorMessage);
                                }
                            }
                        }



                // Determinar el estado de cancelación según quién cancela y el número de cancelaciones específicas

                string statusValue;

                if (appointment.SearchHire.ClientId == userId)

                {

                    // Cliente cancela - verificar si es primera o segunda cancelación del cliente

                    if (appointment.ClientCancellationCount >= 1)

                    {

                        statusValue = AppointmentStatus.AppointmentCancelledByClientSecond.ToStringValue();

                    }

                    else

                    {

                        statusValue = AppointmentStatus.AppointmentCancelledByClient.ToStringValue();

                    }

                }

                else

                {

                    // Experto cancela - verificar si es primera o segunda cancelación del experto

                    if (appointment.ExpertCancellationCount >= 1)

                    {

                        statusValue = AppointmentStatus.AppointmentCancelledByExpertSecond.ToStringValue();

                    }

                    else

                    {

                        statusValue = AppointmentStatus.AppointmentCancelledByExpert.ToStringValue();

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

                // ✅ CRÍTICO: Marcar la entidad como Modified explícitamente porque se cargó con FromSqlInterpolated
                _context.Entry(appointment).State = EntityState.Modified;

                

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

                else

                {
                    // ✅ MEJORA: Si NO hay mapeo, loguear pero NO bloquear el cambio de estado
                    // El Appointment.StatusId YA cambió (línea 2208), esto es correcto
                    // El SearchHire NO cambia porque no hay mapeo (comportamiento esperado para estados no finales)
                    await _loggingService.LogWarningAsync(
                        message: "No mapping found for AppointmentStatus to SearchHireStatus",
                        details: $"AppointmentStatus '{statusValue}' does not have a mapping to SearchHireStatus. " +
                                $"Appointment {appointment.Id} status was updated, but SearchHire {appointment.SearchHireId} status was not changed. " +
                                $"This is expected for non-finalization states (e.g., first cancellation).",
                        userId: userId,
                        source: "AppointmentService.CancelAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: appointment.Id,
                        additionalData: new { 
                            AppointmentStatus = statusValue,
                            AppointmentId = appointment.Id,
                            SearchHireId = appointment.SearchHireId
                        }
                    );
                }

                // ✅ CRÍTICO: Si la cancelación NO es final (primera cancelación), crear un timer de propuesta
                // Esto evita que la app quede bloqueada si nadie toma acción
                if (!cancelledStatus.IsFinalizationStatus)
                {
                    // Cancelar timers existentes (ej: response timer si existía)
                    var activeTimersForCancel = await _context.AppointmentTimers
                        .Where(t => t.AppointmentId == appointment.Id && !t.IsExpired)
                        .ToListAsync();
                    
                    foreach (var timer in activeTimersForCancel)
                    {
                        timer.IsExpired = true;
                        timer.ExpiredAt = DateTime.UtcNow;
                        if (!string.IsNullOrEmpty(timer.HangfireJobId))
                        {
                            try { BackgroundJob.Delete(timer.HangfireJobId); } catch { }
                            timer.HangfireJobId = null;
                        }
                    }

                    // Crear nuevo timer de "proposal" (24h)
                    // Si expira sin acción, se cancelará definitivamente
                    var proposalTimer = new AppointmentTimer
                    {
                        AppointmentId = appointment.Id,
                        TimerType = "proposal",
                        StartTime = DateTime.UtcNow,
                        EndTime = DateTime.UtcNow.AddHours(24),
                        IsExpired = false,
                        CreatedAt = DateTime.UtcNow
                    };
                    
                    _context.AppointmentTimers.Add(proposalTimer);
                    await _context.SaveChangesAsync();
                    
                    var jobId = BackgroundJob.Schedule<IAppointmentService>(
                        service => service.ProcessAppointmentTimerAsync(proposalTimer.Id),
                        proposalTimer.EndTime - DateTime.UtcNow
                    );
                    
                    proposalTimer.HangfireJobId = jobId;
                }

                // ✅ CRÍTICO: Guardar estados ANTES de procesar dinero
                // El estado debe cambiar SIEMPRE, incluso si falla el procesamiento de dinero
                await _context.SaveChangesAsync();

                // Si el subestado NO es de finalización, no invocar orquestador (primera cancelación, reprogramable)

                if (cancelledStatus.IsFinalizationStatus)

                {

                    // Orquestar movimientos de dinero según el estado determinado (subestado → fallback final), respetando granularidad
                    // ✅ OPTIMIZACIÓN: updateState: false porque ya cambiamos el estado arriba (líneas 2225, 2285)
                    // ✅ CRÍTICO: SaveChanges ya se hizo arriba, estados ya están guardados

                    try

                    {

                        var distributionOk = await _refundService.ProcessMoneyDistributionAsync(

                            appointment.SearchHireId,

                            statusValue,

                            "Cancellation flow from CancelAppointmentAsync",

                            userId,
                            updateState: false);

                        if (!distributionOk)

                        {

                        }

                    }

                    catch (Exception distEx)

                    {

                    }

                }



                // Marcar todos los timers activos como expirados y cancelar jobs de Hangfire

                var activeTimers = await _context.AppointmentTimers

                    .Where(t => t.AppointmentId == appointment.Id && !t.IsExpired)

                    .ToListAsync();



                foreach (var timer in activeTimers)

                {

                    timer.IsExpired = true;

                    timer.ExpiredAt = DateTime.UtcNow;
                    
                    // ✅ CANCELAR job de Hangfire si existe
                    if (!string.IsNullOrEmpty(timer.HangfireJobId))
                    {
                        try
                        {
                            BackgroundJob.Delete(timer.HangfireJobId);
                            timer.HangfireJobId = null; // Limpiar referencia
                        }
                        catch (Exception ex)
                        {
                            // Si el job ya no existe o fue procesado, continuar sin error
                            timer.HangfireJobId = null;
                        }
                    }
                }
                
                // ✅ CANCELAR explícitamente el timer de transición a awaiting_report si existe
                // Esto es necesario porque el job ProcessAppointmentToAwaitingReportAsync se programa directamente
                var transitionTimers = await _context.AppointmentTimers
                    .Where(t => t.AppointmentId == appointment.Id && 
                               t.TimerType == "awaiting_report_transition" && 
                               !t.IsExpired)
                    .ToListAsync();
                
                foreach (var timer in transitionTimers)
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    
                    if (!string.IsNullOrEmpty(timer.HangfireJobId))
                    {
                        try
                        {
                            BackgroundJob.Delete(timer.HangfireJobId);
                            timer.HangfireJobId = null;
                        }
                        catch (Exception ex)
                        {
                            timer.HangfireJobId = null;
                        }
                    }
                }



                await _context.SaveChangesAsync();
                
                // ✅ Si NO es cancelación final (primera cancelación), restaurar timer de 24h para que el cliente proponga otra vez
                // NO cambiar el estado - se mantiene como "appointment_cancelled_by_client" o "appointment_cancelled_by_expert"
                if (!cancelledStatus.IsFinalizationStatus)
                {
                    // Crear nuevo timer para propuesta del cliente (24 horas)
                    var proposalTimer = new AppointmentTimer
                    {
                        AppointmentId = appointment.Id,
                        TimerType = "proposal",
                        StartTime = DateTime.UtcNow,
                        EndTime = DateTime.UtcNow.AddHours(24),
                        IsExpired = false,
                        CreatedAt = DateTime.UtcNow
                    };
                    
                    _context.AppointmentTimers.Add(proposalTimer);
                    await _context.SaveChangesAsync();
                    
                    // Programar scheduled job para cuando expire el timer (24 horas)
                    var jobId = BackgroundJob.Schedule<IAppointmentService>(
                        service => service.ProcessAppointmentTimerAsync(proposalTimer.Id),
                        proposalTimer.EndTime - DateTime.UtcNow
                    );
                    
                    // Guardar el JobId en el timer
                    proposalTimer.HangfireJobId = jobId;
                    await _context.SaveChangesAsync();
                }



                        // ✅ COMMIT: Confirmar la transacción

                        await transaction.CommitAsync();

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



                // ✅ Enviar mensaje al chat con el cambio de estado (después del commit)

                await SendAppointmentStatusChangeMessageAsync(

                    appointment.SearchHireId, 

                    statusValue, 

                    userId

                );



                // ✅ Notificar a la otra parte sobre la cancelación
                if (appointment.SearchHire.ClientId == userId)
                {
                    // Cliente canceló - notificar al experto
                    if (appointment.SearchHire.ExpertId.HasValue)
                    {
                        var refundMessage = cancelledStatus.IsFinalizationStatus 
                            ? " Se procesará el reembolso al cliente." 
                            : "";
                        await _loggingService.LogWarningAsync(
                            message: "Cita cancelada por el cliente",
                            details: $"El cliente canceló la cita #{appointment.Id}.{refundMessage}",
                            userId: appointment.SearchHire.ExpertId.Value,
                            source: "AppointmentService.CancelAppointmentAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: appointment.Id,
                            notifyUser: true
                        );
                    }

                    // ✅ Notificar al cliente (Confirmación de cancelación)
                    var refundInfo = cancelledStatus.IsFinalizationStatus 
                        ? "Se procesará el reembolso a tu favor (menos penalización si aplica)." 
                        : "Se ha creado un nuevo timer de propuesta. Tienes 24 horas para proponer una nueva cita.";
                        
                    await _loggingService.LogInfoAsync(
                        message: "Has cancelado la cita",
                        details: $"Has cancelado la cita #{appointment.Id} correctamente. {refundInfo}",
                        userId: userId,
                        source: "AppointmentService.CancelAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: appointment.Id,
                        notifyUser: false // Desactivar notificación genérica
                    );

                    // ✅ EMAIL: Notificar cancelación a ambas partes
                    // 1. Al que canceló
                    var actorEmail = userId == appointment.SearchHire.ClientId ? appointment.SearchHire.Client?.Email : appointment.SearchHire.Expert?.Email;
                    var actorName = userId == appointment.SearchHire.ClientId ? appointment.SearchHire.Client?.Name : appointment.SearchHire.Expert?.Name;
                    
                    if (!string.IsNullOrEmpty(actorEmail))
                    {
                        await _notificationService.SendGeneralNotificationEmailAsync(
                            actorEmail,
                            actorName ?? "Usuario",
                            "Cita Cancelada",
                            $"Has cancelado la cita del {(appointment.ProposedDate?.ToString("dd/MM/yyyy") ?? "fecha no especificada")}. {refundInfo}",
                            "Ver Estado",
                            "https://www.inspecciono.com/appointments"
                        );
                    }

                    // 2. A la contraparte
                    var otherPartyId = userId == appointment.SearchHire.ClientId ? appointment.SearchHire.ExpertId : appointment.SearchHire.ClientId;
                    var otherPartyEmail = userId == appointment.SearchHire.ClientId ? appointment.SearchHire.Expert?.Email : appointment.SearchHire.Client?.Email;
                    var otherPartyName = userId == appointment.SearchHire.ClientId ? appointment.SearchHire.Expert?.Name : appointment.SearchHire.Client?.Name;

                    if (otherPartyId.HasValue && !string.IsNullOrEmpty(otherPartyEmail))
                    {
                        var cancelledBy = userId == appointment.SearchHire.ClientId ? "el cliente" : "el experto";
                        await _notificationService.SendGeneralNotificationEmailAsync(
                            otherPartyEmail,
                            otherPartyName ?? "Usuario",
                            "⚠️ Cita Cancelada",
                            $"La cita programada para el {(appointment.ProposedDate?.ToString("dd/MM/yyyy") ?? "fecha no especificada")} ha sido cancelada por {cancelledBy}.",
                            "Ver Detalles",
                            "https://www.inspecciono.com/appointments"
                        );
                    }
                }
                else
                {
                    // Experto canceló - notificar al cliente
                    var refundMessage = cancelledStatus.IsFinalizationStatus 
                        ? " Se procesará tu reembolso." 
                        : "";
                    await _loggingService.LogWarningAsync(
                        message: "Cita cancelada por el experto",
                        details: $"El experto canceló la cita #{appointment.Id}.{refundMessage}",
                        userId: appointment.SearchHire.ClientId,
                        source: "AppointmentService.CancelAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: appointment.Id,
                        notifyUser: true
                    );

                    // ✅ Notificar al experto (Confirmación de cancelación)
                    var refundInfo = cancelledStatus.IsFinalizationStatus 
                        ? "Se procesará el reembolso completo al cliente." 
                        : "El cliente ha sido notificado y podrá proponer una nueva cita.";

                    await _loggingService.LogInfoAsync(
                        message: "Has cancelado la cita",
                        details: $"Has cancelado la cita #{appointment.Id} correctamente. {refundInfo}",
                        userId: userId,
                        source: "AppointmentService.CancelAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: appointment.Id,
                        notifyUser: false // Desactivar notificación genérica
                    );

                    // ✅ EMAIL: Notificar cancelación a ambas partes
                    // 1. Al que canceló
                    var actorEmail = userId == appointment.SearchHire.ClientId ? appointment.SearchHire.Client?.Email : appointment.SearchHire.Expert?.Email;
                    var actorName = userId == appointment.SearchHire.ClientId ? appointment.SearchHire.Client?.Name : appointment.SearchHire.Expert?.Name;
                    
                    if (!string.IsNullOrEmpty(actorEmail))
                    {
                        await _notificationService.SendGeneralNotificationEmailAsync(
                            actorEmail,
                            actorName ?? "Usuario",
                            "Cita Cancelada",
                            $"Has cancelado la cita del {(appointment.ProposedDate?.ToString("dd/MM/yyyy") ?? "fecha no especificada")}. {refundInfo}",
                            "Ver Estado",
                            "https://www.inspecciono.com/appointments"
                        );
                    }

                    // 2. A la contraparte
                    var otherPartyId = userId == appointment.SearchHire.ClientId ? appointment.SearchHire.ExpertId : appointment.SearchHire.ClientId;
                    var otherPartyEmail = userId == appointment.SearchHire.ClientId ? appointment.SearchHire.Expert?.Email : appointment.SearchHire.Client?.Email;
                    var otherPartyName = userId == appointment.SearchHire.ClientId ? appointment.SearchHire.Expert?.Name : appointment.SearchHire.Client?.Name;

                    if (otherPartyId.HasValue && !string.IsNullOrEmpty(otherPartyEmail))
                    {
                        var cancelledBy = userId == appointment.SearchHire.ClientId ? "el cliente" : "el experto";
                        await _notificationService.SendGeneralNotificationEmailAsync(
                            otherPartyEmail,
                            otherPartyName ?? "Usuario",
                            "⚠️ Cita Cancelada",
                            $"La cita programada para el {(appointment.ProposedDate?.ToString("dd/MM/yyyy") ?? "fecha no especificada")} ha sido cancelada por {cancelledBy}.",
                            "Ver Detalles",
                            "https://www.inspecciono.com/appointments"
                        );
                    }
                }



                return MapToDto(updatedAppointment);

                    }

                    catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, hacer rollback seguro
                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }
                        throw; // Re-lanzar para que el usuario pueda reintentar
                    }
                    catch (ObjectDisposedException)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, hacer rollback seguro
                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }
                        throw; // Re-lanzar para que el usuario pueda reintentar
                    }
                    catch (Exception innerEx)

                    {

                        // ✅ ROLLBACK: Revertir la transacción en caso de error

                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }

                        throw;

                    }

            }

            catch (Exception ex)

            {

                // ⚠️ LOG WARNING: Error general cancelando cita (el refund tiene su propio CRITICAL si falla, usuario puede reintentar)

                await _loggingService.LogWarningAsync(

                    message: "Error cancelling appointment",

                    details: $"An unexpected exception occurred while cancelling appointment {dto.AppointmentId}. " +

                            $"User {userId} attempted to cancel appointment. " +

                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +

                            $"Stack Trace: {ex.StackTrace}. " +

                            $"User may need to retry the operation. Note: If refund processing fails, it will be logged separately as CRITICAL.",

                    userId: userId,

                    source: "AppointmentService.CancelAppointmentAsync",

                    relatedEntityType: "Appointment",

                    relatedEntityId: dto.AppointmentId,

                    additionalData: new { 

                        AppointmentId = dto.AppointmentId,

                        UserId = userId,

                        ErrorType = ex.GetType().Name,

                        ErrorMessage = ex.Message,

                        StackTrace = ex.StackTrace,

                        InnerException = ex.InnerException?.Message

                    }

                );

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

                    PendingDisputes = 0, // ✅ REMOVED: DisputeReason field eliminated

                    ClientNoShows = await _context.Appointments

                        .Where(a => a.Status.StatusValue == AppointmentStatus.AppointmentCancelledByClient.ToStringValue())

                        .CountAsync(),

                    ExpertNoShows = await _context.Appointments

                        .Where(a => a.Status.StatusValue == AppointmentStatus.AppointmentCancelledByExpert.ToStringValue())

                        .CountAsync(),

                    SuccessfulAppointments = await _context.Appointments

                        .Where(a => a.Status.StatusValue == "appointment_awaiting_report")

                        .CountAsync(),

                    CancelledAppointments = await _context.Appointments

                        .Where(a => a.Status.StatusValue.Contains("cancelled"))

                        .CountAsync(),

                    AwaitingAppointment = await _context.Appointments

                        .Where(a => a.Status.StatusValue == AppointmentStatus.AwaitingAppointment.ToStringValue())

                        .CountAsync(),

                    AppointmentProposed = await _context.Appointments

                        .Where(a => a.Status.StatusValue == "appointment_proposed")

                        .CountAsync(),

                    AppointmentConfirmed = await _context.Appointments

                        .Where(a => a.Status.StatusValue == "appointment_confirmed")

                        .CountAsync(),

                    AppointmentRejected = await _context.Appointments

                        .Where(a => a.Status.StatusValue == AppointmentStatus.AppointmentRejected.ToStringValue())

                        .CountAsync()

                };



                return metrics;

            }

            catch (Exception ex)

            {

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

                            // Si el experto no responde en 24h, cancelar por no respuesta

                            var noResponseStatus = await _context.SystemStatuses

                                .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 

                                                        s.StatusValue == AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue());

                            

                            if (noResponseStatus != null)

                            {

                                timer.Appointment.StatusId = noResponseStatus.Id;

                                timer.Appointment.UpdatedAt = DateTime.UtcNow;

                                // 🎯 PROCESAR DINERO AUTOMÁTICAMENTE
                                // ✅ MEJORA: Usar lógica automática de mapeo - ProcessMoneyDistributionAsync mapea automáticamente
                                // appointment_cancelled_by_expert_no_response → cancelled (genérico)
                                // Usa los % del AppointmentStatus (100/0/0) porque tiene configuración
                                try

                                {
                                        var moneySuccess = await _refundService.ProcessMoneyDistributionAsync(

                                        timer.Appointment.SearchHireId,

                                        AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),

                                        "Expert did not respond within 24h - automatic cancellation",

                                        null,
                                        updateState: true); // ✅ updateState: true para que haga el mapeo automático

                                    

                                    if (moneySuccess)

                                    {

                                        // ✅ LOG INFO: Timer expirado correctamente - experto no respondió (comportamiento esperado)

                                        await _loggingService.LogInfoAsync(

                                            message: "Appointment timer expired - expert no response, auto-cancelled",

                                            details: $"Appointment {timer.Appointment.Id} cancelled automatically due to expert not responding within 24h. Money distribution processed successfully.",

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

                                                Status = AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),

                                                MoneyDistributionSuccess = true

                                            }

                                        );



                                        // ✅ Notificar a cliente y experto sobre cancelación automática

                                        if (timer.Appointment.SearchHire?.ClientId > 0)

                                        {

                                            await _loggingService.LogInfoAsync(

                                                message: "Cita cancelada - experto no respondió",

                                                details: $"El experto no respondió a tu propuesta de cita en 24 horas. La cita fue cancelada automáticamente. Se procesará tu reembolso completo.",

                                                userId: timer.Appointment.SearchHire.ClientId,

                                                source: "AppointmentService.CheckAppointmentTimersAsync",

                                                relatedEntityType: "Appointment",

                                                relatedEntityId: timer.Appointment.Id,

                                                notifyUser: true

                                            );

                                        }



                                        if (timer.Appointment.SearchHire?.ExpertId.HasValue == true)

                                        {

                                            await _loggingService.LogWarningAsync(

                                                message: "Cita cancelada automáticamente",

                                                details: $"No respondiste a la propuesta de cita en 24 horas. La cita fue cancelada automáticamente.",

                                                userId: timer.Appointment.SearchHire.ExpertId.Value,

                                                source: "AppointmentService.CheckAppointmentTimersAsync",

                                                relatedEntityType: "Appointment",

                                                relatedEntityId: timer.Appointment.Id,

                                                notifyUser: true

                                            );

                                        }

                                    }

                                    else

                                    {

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

                                                Status = AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),

                                                MoneyDistributionSuccess = false

                                            }

                                        );

                                    }

                                }

                                catch (Exception ex)

                                {

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

                                            Status = AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),

                                            Exception = ex.Message,

                                            StackTrace = ex.StackTrace

                                        }

                                    );

                                }

                                

                                // ✅ Enviar mensaje al chat con el cambio de estado automático

                                // Para cambios automáticos, el senderId es el ExpertId del SearchHire

                                var expertIdForMessage = timer.Appointment.SearchHire?.ExpertId ?? 0;

                                if (expertIdForMessage > 0)

                                {

                                    await SendAppointmentStatusChangeMessageAsync(

                                        timer.Appointment.SearchHireId,

                                        AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),

                                        expertIdForMessage

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

                                    // ✅ Enviar mensaje al chat con el cambio de estado automático

                                    // Para cambios automáticos, el senderId es el ExpertId del SearchHire

                                    var expertIdForMessage = timer.Appointment.SearchHire?.ExpertId ?? 0;

                                    if (expertIdForMessage > 0)

                                    {

                                        await SendAppointmentStatusChangeMessageAsync(

                                            timer.Appointment.SearchHireId,

                                            AppointmentStatus.AppointmentReportSent.ToStringValue(),

                                            expertIdForMessage

                                        );

                                    }

                                }

                            }

                            else

                            {

                                // Si faltan archivos, cancelar por no reporte

                                var noReportStatus = await _context.SystemStatuses

                                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 

                                                            s.StatusValue == AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue());

                                

                                if (noReportStatus != null)

                                {

                                    timer.Appointment.StatusId = noReportStatus.Id;

                                    timer.Appointment.UpdatedAt = DateTime.UtcNow;

                                    

                                    // También actualizar el SearchHire para que use el estado del sistema centralizado

                                    var cancelledStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());

                                    timer.Appointment.SearchHire.StatusId = cancelledStatusId;

                                    timer.Appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

                                    

                                    // 🎯 PROCESAR DINERO AUTOMÁTICAMENTE
                                    // ✅ OPTIMIZACIÓN: updateState: false porque ya cambiamos el estado arriba

                                    try

                                    {

                                        var moneySuccess = await _refundService.ProcessMoneyDistributionAsync(

                                            timer.Appointment.SearchHireId,

                                            AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue(),

                                            "Expert did not submit report within 24h - automatic cancellation",

                                            null,
                                            updateState: false);

                                        

                                        if (moneySuccess)

                                        {

                                            // ✅ Notificar a cliente y experto sobre cancelación por falta de reporte

                                            if (timer.Appointment.SearchHire?.ClientId > 0)

                                            {

                                                await _loggingService.LogWarningAsync(

                                                    message: "Cita cancelada - experto no envió reporte",

                                                    details: $"El experto no envió el reporte a tiempo. La cita fue cancelada automáticamente. Se procesará tu reembolso.",

                                                    userId: timer.Appointment.SearchHire.ClientId,

                                                    source: "AppointmentService.CheckAppointmentTimersAsync",

                                                    relatedEntityType: "Appointment",

                                                    relatedEntityId: timer.Appointment.Id,

                                                    notifyUser: true

                                                );

                                            }



                                            if (timer.Appointment.SearchHire?.ExpertId.HasValue == true)

                                            {

                                                await _loggingService.LogWarningAsync(

                                                    message: "Cita cancelada - no enviaste el reporte",

                                                    details: $"No enviaste el reporte de la cita #{timer.Appointment.Id} en 24 horas. La cita fue cancelada automáticamente.",

                                                    userId: timer.Appointment.SearchHire.ExpertId.Value,

                                                    source: "AppointmentService.CheckAppointmentTimersAsync",

                                                    relatedEntityType: "Appointment",

                                                    relatedEntityId: timer.Appointment.Id,

                                                    notifyUser: true

                                                );

                                            }

                                        }

                                        else

                                        {

                                            // 🚨 LOG CRÍTICO: Fallo en distribución de dinero por falta de reporte (una sola vez, con información completa)

                                            await _loggingService.LogCriticalAsync(

                                                message: "CRITICAL: Money distribution failed for expired report timer",

                                                details: $"Appointment {timer.Appointment.Id} timer expired (expert did not submit report within 24h) but money distribution failed. " +

                                                        $"Timer Type: expert_report, AppointmentId: {timer.Appointment.Id}, SearchHireId: {timer.Appointment.SearchHireId}. " +

                                                        $"ClientId: {timer.Appointment.SearchHire?.ClientId}, ExpertId: {timer.Appointment.SearchHire?.ExpertId}, Amount: {timer.Appointment.SearchHire?.Amount}€. " +

                                                        $"ACTION REQUIRED: Review ProcessMoneyDistributionAsync error logs and manually process money distribution if needed.",

                                                userId: timer.Appointment.SearchHire?.ClientId,

                                                source: "AppointmentService.CheckAppointmentTimersAsync",

                                                relatedEntityType: "Appointment",

                                                relatedEntityId: timer.Appointment.Id,

                                                additionalData: new { 

                                                    Action = "TimerExpired",

                                                    TimerType = "expert_report",

                                                    AppointmentId = timer.Appointment.Id,

                                                    SearchHireId = timer.Appointment.SearchHireId,

                                                    ClientId = timer.Appointment.SearchHire?.ClientId,

                                                    ExpertId = timer.Appointment.SearchHire?.ExpertId,

                                                    Amount = timer.Appointment.SearchHire?.Amount,

                                                    Status = AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue(),

                                                    MoneyDistributionSuccess = false

                                                }

                                            );

                                        }

                                    }

                                    catch (Exception ex)

                                    {

                                        // 🚨 LOG CRÍTICO: Excepción procesando distribución por falta de reporte (una sola vez, con información completa)

                                        await _loggingService.LogCriticalAsync(

                                            message: "CRITICAL: Exception during money distribution for expired report timer",

                                            details: $"Exception occurred while processing money distribution for Appointment {timer.Appointment.Id} due to expired report timer. " +

                                                    $"Timer Type: expert_report, AppointmentId: {timer.Appointment.Id}, SearchHireId: {timer.Appointment.SearchHireId}. " +

                                                    $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +

                                                    $"ClientId: {timer.Appointment.SearchHire?.ClientId}, ExpertId: {timer.Appointment.SearchHire?.ExpertId}, Amount: {timer.Appointment.SearchHire?.Amount}€. " +

                                                    $"Stack Trace: {ex.StackTrace}. " +

                                                    $"ACTION REQUIRED: Review exception and manually process money distribution if needed.",

                                            userId: timer.Appointment.SearchHire?.ClientId,

                                            source: "AppointmentService.CheckAppointmentTimersAsync",

                                            relatedEntityType: "Appointment",

                                            relatedEntityId: timer.Appointment.Id,

                                            additionalData: new { 

                                                Action = "TimerExpired",

                                                TimerType = "expert_report",

                                                AppointmentId = timer.Appointment.Id,

                                                SearchHireId = timer.Appointment.SearchHireId,

                                                ClientId = timer.Appointment.SearchHire?.ClientId,

                                                ExpertId = timer.Appointment.SearchHire?.ExpertId,

                                                Amount = timer.Appointment.SearchHire?.Amount,

                                                Status = AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue(),

                                                ErrorType = ex.GetType().Name,

                                                ErrorMessage = ex.Message,

                                                StackTrace = ex.StackTrace,

                                                InnerException = ex.InnerException?.Message

                                            }

                                        );

                                    }

                                    // ✅ Enviar mensaje al chat con el cambio de estado automático

                                    // Para cambios automáticos, el senderId es el ExpertId del SearchHire

                                    var expertIdForMessage = timer.Appointment.SearchHire?.ExpertId ?? 0;

                                    if (expertIdForMessage > 0)

                                    {

                                    await SendAppointmentStatusChangeMessageAsync(

                                        timer.Appointment.SearchHireId,

                                        AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue(),

                                        expertIdForMessage

                                    );

                                    }

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

                    .Where(a => a.ProposedDate.HasValue && a.ProposedTime.HasValue && 
                                a.ProposedDate.Value.Add(a.ProposedTime.Value) <= cutoffTime)

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
                        await _context.SaveChangesAsync();

                        // Programar scheduled job para cuando expire el timer (24 horas)
                        var jobId = BackgroundJob.Schedule<IAppointmentService>(
                            service => service.ProcessAppointmentTimerAsync(expertReportTimer.Id),
                            expertReportTimer.EndTime - DateTime.UtcNow
                        );

                        // Guardar el JobId en el timer
                        expertReportTimer.HangfireJobId = jobId;
                        await _context.SaveChangesAsync();

                        // ✅ Enviar mensaje al chat con el cambio de estado automático

                        // Para cambios automáticos, el senderId es el ExpertId del SearchHire

                        var appointmentWithHire = await _context.Appointments

                            .Include(a => a.SearchHire)

                            .FirstOrDefaultAsync(a => a.Id == appointment.Id);

                        

                        if (appointmentWithHire?.SearchHire?.ExpertId.HasValue == true)

                        {

                            await SendAppointmentStatusChangeMessageAsync(

                                appointmentWithHire.SearchHireId,

                                AppointmentStatus.AppointmentAwaitingReport.ToStringValue(),

                                appointmentWithHire.SearchHire.ExpertId.Value

                            );

                        }

                    }

                }



                if (expiredTimers.Any() || confirmedAppointments.Any())

                {

                    await _context.SaveChangesAsync();

                }

            }

            catch (Exception ex)

            {

                throw;

            }

        }

        /// <summary>
        /// Procesa un timer de cita expirado. Hangfire reintenta automáticamente hasta 5 veces con delays progresivos
        /// (1m, 5m, 10m, 15m, 20m) para cubrir fallos transitorios de Stripe/BD/red.
        /// </summary>
        [AutomaticRetry(
            Attempts = 5, 
            DelaysInSeconds = new[] { 60, 300, 600, 900, 1200 },  // 1m, 5m, 10m, 15m, 20m
            OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        public async Task ProcessAppointmentTimerAsync(int timerId)
        {
            try
            {
                var timer = await _context.AppointmentTimers
                    .Include(t => t.Appointment)
                        .ThenInclude(a => a.Status)
                    .Include(t => t.Appointment)
                        .ThenInclude(a => a.SearchHire)
                            .ThenInclude(sh => sh.Status)
                    .Include(t => t.Appointment)
                        .ThenInclude(a => a.SearchHire)
                            .ThenInclude(sh => sh.Client)
                    .Include(t => t.Appointment)
                        .ThenInclude(a => a.SearchHire)
                            .ThenInclude(sh => sh.Expert)
                    .FirstOrDefaultAsync(t => t.Id == timerId);

                // #region agent log
                File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                    JsonSerializer.Serialize(new
                    {
                        sessionId = "debug-session",
                        runId = "run1",
                        hypothesisId = "H1",
                        location = "AppointmentService.ProcessAppointmentTimerAsync:entry",
                        message = "Timer loaded",
                        data = new
                        {
                            timerId,
                            timerType = timer?.TimerType,
                            isExpired = timer?.IsExpired,
                            appointmentId = timer?.AppointmentId
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }) + Environment.NewLine);
                // #endregion

                if (timer == null)
                {
                    // ✅ LOG: Timer no encontrado
                    await _loggingService.LogWarningAsync(
                        message: "Timer no encontrado",
                        details: $"Timer con Id {timerId} no existe en la base de datos.",
                        userId: null,
                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                        relatedEntityType: "AppointmentTimer",
                        relatedEntityId: timerId
                    );
                    return; // Timer no encontrado
                }

                // Verificar si el timer ya está expirado (puede haber sido cancelado)
                if (timer.IsExpired)
                {
                    // ✅ MEJORA: Verificar si la cita fue procesada correctamente
                    // Si el timer está expirado pero la cita no fue procesada, reprocesar
                    var currentAppointmentStatus = timer.Appointment?.Status?.StatusValue ?? string.Empty;
                    var currentSearchHireStatus = timer.Appointment?.SearchHire?.Status?.StatusValue ?? string.Empty;
                    
                    // Para timer "response", si la cita sigue en "appointment_proposed", significa que NO se procesó
                    bool shouldReprocess = false;
                    if (timer.TimerType == "response" && currentAppointmentStatus == "appointment_proposed" && currentSearchHireStatus == "pending")
                    {
                        shouldReprocess = true;
                    }
                    else if (timer.TimerType == "proposal" && currentAppointmentStatus == AppointmentStatus.AwaitingAppointment.ToStringValue() && currentSearchHireStatus == "pending")
                    {
                        shouldReprocess = true;
                    }
                    else if (timer.TimerType == "expert_report" && currentAppointmentStatus == "appointment_awaiting_report" && currentSearchHireStatus == "pending")
                    {
                        shouldReprocess = true;
                    }
                    else if (timer.TimerType == "client_decision" && currentAppointmentStatus == "appointment_report_sent" && currentSearchHireStatus == "awaiting_client_decision")
                    {
                        shouldReprocess = true;
                    }
                    
                    if (shouldReprocess)
                    {
                        // #region agent log
                        File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                            JsonSerializer.Serialize(new
                            {
                                sessionId = "debug-session",
                                runId = "run1",
                                hypothesisId = "H2",
                                location = "AppointmentService.ProcessAppointmentTimerAsync:isExpired-reprocess",
                                message = "Reprocessing expired timer",
                                data = new
                                {
                                    timerId,
                                    timerType = timer.TimerType,
                                    appointmentStatus = currentAppointmentStatus,
                                    searchHireStatus = currentSearchHireStatus
                                },
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            }) + Environment.NewLine);
                        // #endregion
                        // ⚠️ Timer expirado pero cita no procesada - reprocesar
                        await _loggingService.LogWarningAsync(
                            message: "Timer expirado pero cita no procesada - reprocesando",
                            details: $"Timer {timerId} está marcado como expirado (IsExpired=true), pero la cita no fue procesada. " +
                                    $"TimerType: {timer.TimerType}, AppointmentId: {timer.AppointmentId}, " +
                                    $"AppointmentStatus: {currentAppointmentStatus}, SearchHireStatus: {currentSearchHireStatus}. " +
                                    $"Reprocesando para asegurar que la cita se actualice correctamente.",
                            userId: timer.Appointment?.SearchHire?.ClientId,
                            source: "AppointmentService.ProcessAppointmentTimerAsync",
                            relatedEntityType: "AppointmentTimer",
                            relatedEntityId: timerId
                        );
                        // Continuar con el procesamiento (no retornar)
                    }
                    else
                    {
                        // ✅ LOG: Timer ya procesado correctamente
                        await _loggingService.LogInfoAsync(
                            message: "Timer ya expirado - retornando sin procesar",
                            details: $"Timer {timerId} ya está marcado como expirado (IsExpired=true) y la cita fue procesada correctamente. " +
                                    $"AppointmentId: {timer.AppointmentId}, TimerType: {timer.TimerType}, " +
                                    $"AppointmentStatus: {currentAppointmentStatus}, SearchHireStatus: {currentSearchHireStatus}",
                            userId: timer.Appointment?.SearchHire?.ClientId,
                            source: "AppointmentService.ProcessAppointmentTimerAsync",
                            relatedEntityType: "AppointmentTimer",
                            relatedEntityId: timerId
                        );
                        return; // Timer ya procesado correctamente
                    }
                }

                // ✅ VALIDACIÓN CRÍTICA: Verificar que el SearchHire y Appointment existan
                if (timer.Appointment == null || timer.Appointment.SearchHire == null)
                {
                    // #region agent log
                    File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                        JsonSerializer.Serialize(new
                        {
                            sessionId = "debug-session",
                            runId = "run1",
                            hypothesisId = "H4",
                            location = "AppointmentService.ProcessAppointmentTimerAsync:null-check",
                            message = "Appointment or SearchHire null",
                            data = new { timerId, appointmentNull = timer.Appointment == null, searchHireNull = timer.Appointment?.SearchHire == null },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }) + Environment.NewLine);
                    // #endregion
                    // ✅ LOG: Appointment o SearchHire eliminados
                    await _loggingService.LogWarningAsync(
                        message: "Appointment o SearchHire eliminados",
                        details: $"Timer {timerId}. Appointment es null: {timer.Appointment == null}, " +
                                $"SearchHire es null: {timer.Appointment?.SearchHire == null}",
                        userId: null,
                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                        relatedEntityType: "AppointmentTimer",
                        relatedEntityId: timerId
                    );
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Appointment o SearchHire eliminados
                }

                var searchHire = timer.Appointment.SearchHire;
                var appointment = timer.Appointment;

                // ✅ VALIDACIÓN CRÍTICA: Verificar que el SearchHire NO esté finalizado
                if (searchHire.Status?.IsFinalizationStatus == true)
                {
                    // #region agent log
                    File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                        JsonSerializer.Serialize(new
                        {
                            sessionId = "debug-session",
                            runId = "run1",
                            hypothesisId = "H4",
                            location = "AppointmentService.ProcessAppointmentTimerAsync:finalized-searchhire",
                            message = "SearchHire already finalized",
                            data = new { timerId, searchHireId = searchHire.Id, status = searchHire.Status?.StatusValue },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }) + Environment.NewLine);
                    // #endregion
                    // ✅ LOG: SearchHire ya finalizado
                    await _loggingService.LogInfoAsync(
                        message: "SearchHire ya finalizado - retornando sin procesar",
                        details: $"Timer {timerId}. SearchHireId: {searchHire.Id}, Status: {searchHire.Status?.StatusValue}, " +
                                $"IsFinalizationStatus: {searchHire.Status?.IsFinalizationStatus}",
                        userId: null,
                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                        relatedEntityType: "AppointmentTimer",
                        relatedEntityId: timerId
                    );
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // SearchHire ya finalizado, no procesar
                }

                // ✅ VALIDACIÓN CRÍTICA: Verificar que los usuarios existan y no estén bloqueados
                if (searchHire.Client == null || searchHire.Client.IsBlocked)
                {
                    // ✅ LOG: Cliente eliminado o bloqueado
                    await _loggingService.LogWarningAsync(
                        message: "Timer cancelado - Cliente bloqueado o eliminado",
                        details: $"Timer {timerId} cancelado porque el cliente {searchHire.ClientId} está {(searchHire.Client == null ? "eliminado" : "bloqueado")}. Timer expirado sin procesar.",
                        userId: searchHire.ClientId,
                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                        relatedEntityType: "AppointmentTimer",
                        relatedEntityId: timerId
                    );

                    // #region agent log
                    File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                        JsonSerializer.Serialize(new
                        {
                            sessionId = "debug-session",
                            runId = "run1",
                            hypothesisId = "H4",
                            location = "AppointmentService.ProcessAppointmentTimerAsync:client-blocked-null",
                            message = "Client null or blocked",
                            data = new { timerId, clientNull = searchHire.Client == null, clientBlocked = searchHire.Client?.IsBlocked },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }) + Environment.NewLine);
                    // #endregion
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Cliente eliminado o bloqueado
                }

                if (searchHire.ExpertId.HasValue && (searchHire.Expert == null || searchHire.Expert.IsBlocked))
                {
                    // ✅ LOG: Experto eliminado o bloqueado
                    await _loggingService.LogWarningAsync(
                        message: "Timer cancelado - Experto bloqueado o eliminado",
                        details: $"Timer {timerId} cancelado porque el experto {searchHire.ExpertId} está {(searchHire.Expert == null ? "eliminado" : "bloqueado")}. Timer expirado sin procesar.",
                        userId: searchHire.ClientId,
                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                        relatedEntityType: "AppointmentTimer",
                        relatedEntityId: timerId
                    );

                    // #region agent log
                    File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                        JsonSerializer.Serialize(new
                        {
                            sessionId = "debug-session",
                            runId = "run1",
                            hypothesisId = "H4",
                            location = "AppointmentService.ProcessAppointmentTimerAsync:expert-blocked-null",
                            message = "Expert null or blocked",
                            data = new { timerId, expertNull = searchHire.Expert == null, expertBlocked = searchHire.Expert?.IsBlocked },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }) + Environment.NewLine);
                    // #endregion
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Experto eliminado o bloqueado
                }

                // ✅ VALIDACIÓN CRÍTICA: Verificar estado del SearchHire según el tipo de timer
                var searchHireStatus = searchHire.Status?.StatusValue ?? string.Empty;
                
                // Para timers de "proposal" y "response", solo procesar si SearchHire está en "pending"
                if (timer.TimerType == "proposal" || timer.TimerType == "response")
                {
                    if (searchHireStatus != "pending")
                    {
                        // #region agent log
                        File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                            JsonSerializer.Serialize(new
                            {
                                sessionId = "debug-session",
                                runId = "run1",
                                hypothesisId = "H4",
                                location = "AppointmentService.ProcessAppointmentTimerAsync:pending-check",
                                message = "SearchHire not pending",
                                data = new { timerId, timerType = timer.TimerType, searchHireId = searchHire.Id, searchHireStatus },
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            }) + Environment.NewLine);
                        // #endregion
                        // ✅ LOG: SearchHire no está en pending
                        await _loggingService.LogInfoAsync(
                            message: "SearchHire no está en pending - retornando sin procesar",
                            details: $"Timer {timerId}, TimerType: {timer.TimerType}. SearchHireId: {searchHire.Id}, " +
                                    $"SearchHireStatus actual: '{searchHireStatus}', esperado: 'pending'",
                            userId: null,
                            source: "AppointmentService.ProcessAppointmentTimerAsync",
                            relatedEntityType: "AppointmentTimer",
                            relatedEntityId: timerId
                        );
                        timer.IsExpired = true;
                        timer.ExpiredAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                        return; // SearchHire no está en pending, no procesar
                    }
                }

                // Para timer de "expert_report", solo procesar si está en "pending"
                if (timer.TimerType == "expert_report")
                {
                    if (searchHireStatus != "pending")
                    {
                        timer.IsExpired = true;
                        timer.ExpiredAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                        return; // SearchHire no está en pending, no procesar
                    }
                }

                // ✅ VALIDACIÓN CRÍTICA: Verificar estado de la cita antes de procesar
                var appointmentStatus = appointment.Status?.StatusValue ?? string.Empty;
                
                if (timer.TimerType == "proposal" && appointmentStatus != AppointmentStatus.AwaitingAppointment.ToStringValue() && appointmentStatus != AppointmentStatus.AppointmentRejected.ToStringValue() && 
                    appointmentStatus != AppointmentStatus.AppointmentCancelledByClient.ToStringValue() && appointmentStatus != AppointmentStatus.AppointmentCancelledByExpert.ToStringValue())
                {
                    // ✅ LOG: Estado de cita no válido para proposal
                    await _loggingService.LogWarningAsync(
                        message: "Timer proposal cancelado - Estado de cita inválido",
                        details: $"Timer {timerId} cancelado. Estado actual: '{appointmentStatus}'. Estados esperados: awaiting_appointment, rejected, cancelled_by_client/expert.",
                        userId: searchHire.ClientId,
                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                        relatedEntityType: "AppointmentTimer",
                        relatedEntityId: timerId
                    );

                    // #region agent log
                    File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                        JsonSerializer.Serialize(new
                        {
                            sessionId = "debug-session",
                            runId = "run1",
                            hypothesisId = "H4",
                            location = "AppointmentService.ProcessAppointmentTimerAsync:proposal-invalid-appointment-status",
                            message = "Invalid appointment status for proposal",
                            data = new { timerId, appointmentStatus },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }) + Environment.NewLine);
                    // #endregion
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Estado de cita no válido para timer de proposal
                }

                if (timer.TimerType == "response" && appointmentStatus != "appointment_proposed")
                {
                    // #region agent log
                    File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                        JsonSerializer.Serialize(new
                        {
                            sessionId = "debug-session",
                            runId = "run1",
                            hypothesisId = "H4",
                            location = "AppointmentService.ProcessAppointmentTimerAsync:response-invalid-appointment-status",
                            message = "Invalid appointment status for response",
                            data = new { timerId, appointmentStatus },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }) + Environment.NewLine);
                    // #endregion
                    // ✅ LOG: Estado de cita no válido
                    await _loggingService.LogInfoAsync(
                        message: "Estado de cita no válido para timer de response - retornando sin procesar",
                        details: $"Timer {timerId}, TimerType: response. AppointmentId: {timer.AppointmentId}, " +
                                $"AppointmentStatus actual: '{appointmentStatus}', esperado: 'appointment_proposed'",
                        userId: timer.Appointment?.SearchHire?.ClientId,
                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                        relatedEntityType: "AppointmentTimer",
                        relatedEntityId: timerId
                    );
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Estado de cita no válido para timer de response
                }

                if (timer.TimerType == "expert_report" && appointmentStatus != "appointment_awaiting_report")
                {
                    // ✅ LOG: Estado de cita no válido para expert_report
                    await _loggingService.LogWarningAsync(
                        message: "Timer expert_report cancelado - Estado de cita inválido",
                        details: $"Timer {timerId} cancelado. Estado actual: '{appointmentStatus}'. Estado esperado: appointment_awaiting_report.",
                        userId: searchHire.ClientId,
                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                        relatedEntityType: "AppointmentTimer",
                        relatedEntityId: timerId
                    );

                    // #region agent log
                    File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                        JsonSerializer.Serialize(new
                        {
                            sessionId = "debug-session",
                            runId = "run1",
                            hypothesisId = "H4",
                            location = "AppointmentService.ProcessAppointmentTimerAsync:expert-report-invalid-appointment-status",
                            message = "Invalid appointment status for expert_report",
                            data = new { timerId, appointmentStatus },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }) + Environment.NewLine);
                    // #endregion
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Estado de cita no válido para timer de expert_report
                }
                
                // Para timer de "client_decision", solo procesar si SearchHire está en "awaiting_client_decision"
                if (timer.TimerType == "client_decision")
                {
                    if (searchHireStatus != "awaiting_client_decision")
                    {
                        // ✅ LOG: Estado SearchHire no válido para client_decision
                        await _loggingService.LogWarningAsync(
                            message: "Timer client_decision cancelado - Estado SearchHire inválido",
                            details: $"Timer {timerId} cancelado. Estado SearchHire actual: '{searchHireStatus}'. Estado esperado: awaiting_client_decision.",
                            userId: searchHire.ClientId,
                            source: "AppointmentService.ProcessAppointmentTimerAsync",
                            relatedEntityType: "AppointmentTimer",
                            relatedEntityId: timerId
                        );

                        // #region agent log
                        File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                            JsonSerializer.Serialize(new
                            {
                                sessionId = "debug-session",
                                runId = "run1",
                                hypothesisId = "H4",
                                location = "AppointmentService.ProcessAppointmentTimerAsync:client-decision-searchhire-invalid",
                                message = "SearchHire not awaiting_client_decision",
                                data = new { timerId, searchHireStatus },
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            }) + Environment.NewLine);
                        // #endregion
                        timer.IsExpired = true;
                        timer.ExpiredAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                        return; // SearchHire no está en awaiting_client_decision, no procesar
                    }
                }
                
                if (timer.TimerType == "client_decision" && appointmentStatus != "appointment_report_sent")
                {
                    // ✅ LOG: Estado de cita no válido para client_decision
                    await _loggingService.LogWarningAsync(
                        message: "Timer client_decision cancelado - Estado de cita inválido",
                        details: $"Timer {timerId} cancelado. Estado Appointment actual: '{appointmentStatus}'. Estado esperado: appointment_report_sent.",
                        userId: searchHire.ClientId,
                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                        relatedEntityType: "AppointmentTimer",
                        relatedEntityId: timerId
                    );

                    // #region agent log
                    File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                        JsonSerializer.Serialize(new
                        {
                            sessionId = "debug-session",
                            runId = "run1",
                            hypothesisId = "H4",
                            location = "AppointmentService.ProcessAppointmentTimerAsync:client-decision-appointment-invalid",
                            message = "Appointment status invalid for client_decision",
                            data = new { timerId, appointmentStatus },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }) + Environment.NewLine);
                    // #endregion
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Estado de cita no válido para timer de client_decision
                }

                // #region agent log
                File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                    JsonSerializer.Serialize(new
                    {
                        sessionId = "debug-session",
                        runId = "run2",
                        hypothesisId = "H5",
                        location = "AppointmentService.ProcessAppointmentTimerAsync:after-validations",
                        message = "Passed validations, proceeding to process timer",
                        data = new
                        {
                            timerId,
                            timerType = timer.TimerType,
                            appointmentStatus,
                            searchHireStatus,
                            isExpired = timer.IsExpired
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }) + Environment.NewLine);
                // #endregion

                // ✅ LOG: Inicio de procesamiento del timer
                await _loggingService.LogInfoAsync(
                    message: "Iniciando procesamiento de timer",
                    details: $"Timer {timerId}, TimerType: {timer.TimerType}, AppointmentId: {timer.AppointmentId}, " +
                            $"AppointmentStatus: {appointmentStatus}, SearchHireId: {searchHire.Id}, " +
                            $"SearchHireStatus: {searchHireStatus}, EndTime: {timer.EndTime}, Now: {DateTime.UtcNow}",
                    userId: searchHire.ClientId,
                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                    relatedEntityType: "AppointmentTimer",
                    relatedEntityId: timerId
                );

                // Marcar timer como expirado
                timer.IsExpired = true;
                timer.ExpiredAt = DateTime.UtcNow;

                // Procesar según el tipo de timer
                switch (timer.TimerType)
                {
                    case "proposal":
                        // Si el cliente no propone en 24h, cancelar
                        var noProposalStatus = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                    s.StatusValue == AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue());

                        if (noProposalStatus != null && timer.Appointment != null)
                        {
                            // #region agent log
                            File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                JsonSerializer.Serialize(new
                                {
                                    sessionId = "debug-session",
                                    runId = "run2",
                                    hypothesisId = "H6",
                                    location = "AppointmentService.ProcessAppointmentTimerAsync:proposal-branch",
                                    message = "Entering proposal branch",
                                    data = new
                                    {
                                        timerId,
                                        appointmentId = timer.AppointmentId,
                                        searchHireId = timer.Appointment.SearchHireId,
                                        appointmentStatus,
                                        searchHireStatus
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                }) + Environment.NewLine);
                            // #endregion

                            timer.Appointment.StatusId = noProposalStatus.Id;
                            timer.Appointment.UpdatedAt = DateTime.UtcNow;

                            // ✅ CRÍTICO: Guardar estado INMEDIATAMENTE antes de procesar dinero
                            await _context.SaveChangesAsync();

                            // #region agent log
                            File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                JsonSerializer.Serialize(new
                                {
                                    sessionId = "debug-session",
                                    runId = "run3",
                                    hypothesisId = "H9",
                                    location = "AppointmentService.ProcessAppointmentTimerAsync:proposal-after-save",
                                    message = "Proposal branch saved state before money",
                                    data = new
                                    {
                                        timerId,
                                        appointmentId = timer.AppointmentId,
                                        searchHireId = timer.Appointment.SearchHireId,
                                        statusId = timer.Appointment.StatusId
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                }) + Environment.NewLine);
                            // #endregion

                            // Procesar dinero automáticamente
                            // ✅ MEJORA: Usar lógica automática de mapeo - ProcessMoneyDistributionAsync mapea automáticamente
                            // appointment_cancelled_by_client_no_proposal → cancelled (genérico)
                            // Usa los % del AppointmentStatus (0/100/0) porque tiene configuración
                            try
                            {
                                // #region agent log
                                File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                    JsonSerializer.Serialize(new
                                    {
                                        sessionId = "debug-session",
                                        runId = "run1",
                                        hypothesisId = "H3",
                                        location = "AppointmentService.ProcessAppointmentTimerAsync:proposal-money",
                                        message = "Calling money distribution",
                                        data = new
                                        {
                                            timerId,
                                            appointmentId = timer.AppointmentId,
                                            searchHireId = timer.Appointment.SearchHireId,
                                            status = AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue()
                                        },
                                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                    }) + Environment.NewLine);
                                // #endregion

                                await _refundService.ProcessMoneyDistributionAsync(
                                    timer.Appointment.SearchHireId,
                                    AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue(),
                                    "Client did not propose within 24h - automatic cancellation",
                                    null,
                                    updateState: true); // ✅ updateState: true para que haga el mapeo automático

                                // ✅ EMAIL: Notificar cancelación automática por falta de propuesta
                                if (timer.Appointment.SearchHire.Client != null && !string.IsNullOrEmpty(timer.Appointment.SearchHire.Client.Email))
                                {
                                    await _notificationService.SendGeneralNotificationEmailAsync(
                                        timer.Appointment.SearchHire.Client.Email,
                                        timer.Appointment.SearchHire.Client.Name,
                                        "❌ Contratación Cancelada",
                                        "La contratación ha sido cancelada porque no se recibió una propuesta de cita en el plazo de 24 horas.",
                                        "Ver Detalles",
                                        "https://www.inspecciono.com/appointments"
                                    );
                                }

                                if (timer.Appointment.SearchHire.Expert != null && !string.IsNullOrEmpty(timer.Appointment.SearchHire.Expert.Email))
                                {
                                    await _notificationService.SendGeneralNotificationEmailAsync(
                                        timer.Appointment.SearchHire.Expert.Email,
                                        timer.Appointment.SearchHire.Expert.Name,
                                        "Contratación Cancelada",
                                        "La contratación ha sido cancelada porque el cliente no propuso una fecha en el plazo establecido."
                                    );
                                }
                            }
                            catch (Exception moneyEx)
                            {
                                // ✅ NOTIFICAR ADMIN: El dinero falló pero el estado ya está guardado
                                await _loggingService.LogCriticalAsync(
                                    message: "Money distribution failed after auto-cancel (proposal timer)",
                                    details: $"Timer {timerId}, AppointmentId {timer.AppointmentId}, SearchHireId {timer.Appointment.SearchHireId}. " +
                                             $"State is already updated to appointment_cancelled_by_client_no_proposal. Error: {moneyEx.Message}",
                                    userId: timer.Appointment.SearchHire?.ClientId,
                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                    relatedEntityType: "Appointment",
                                    relatedEntityId: timer.Appointment.Id,
                                    additionalData: new
                                    {
                                        TimerType = "proposal",
                                        TimerId = timer.Id,
                                        AppointmentStatus = AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue(),
                                        Exception = moneyEx.Message,
                                        StackTrace = moneyEx.StackTrace
                                    },
                                    notifyUser: true
                                );
                            }

                            // #region agent log
                            File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                JsonSerializer.Serialize(new
                                {
                                    sessionId = "debug-session",
                                    runId = "run2",
                                    hypothesisId = "H7",
                                    location = "AppointmentService.ProcessAppointmentTimerAsync:proposal-complete",
                                    message = "Proposal timer processed (after money try/catch)",
                                    data = new
                                    {
                                        timerId,
                                        appointmentId = timer.AppointmentId,
                                        searchHireId = timer.Appointment.SearchHireId,
                                        appointmentStatus = timer.Appointment.StatusId,
                                        searchHireStatus = timer.Appointment.SearchHire?.StatusId
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                }) + Environment.NewLine);
                            // #endregion
                        }
                        break;

                    case "response":
                        // Si el experto no responde en 24h, cancelar
                        var noResponseStatus = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                    s.StatusValue == AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue());

                        if (noResponseStatus != null && timer.Appointment != null)
                        {
                            // #region agent log
                            File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                JsonSerializer.Serialize(new
                                {
                                    sessionId = "debug-session",
                                    runId = "run2",
                                    hypothesisId = "H6",
                                    location = "AppointmentService.ProcessAppointmentTimerAsync:response-branch",
                                    message = "Entering response branch",
                                    data = new
                                    {
                                        timerId,
                                        appointmentId = timer.AppointmentId,
                                        searchHireId = timer.Appointment.SearchHireId,
                                        appointmentStatus,
                                        searchHireStatus
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                }) + Environment.NewLine);
                            // #endregion

                            // ✅ LOG: Inicio de procesamiento
                            await _loggingService.LogInfoAsync(
                                message: "Iniciando cancelación de cita por falta de respuesta",
                                details: $"Timer {timerId} expirado. TimerType: response, AppointmentId: {timer.AppointmentId}, " +
                                        $"AppointmentStatus actual: {appointmentStatus}, SearchHireId: {timer.Appointment.SearchHireId}, " +
                                        $"SearchHireStatus: {searchHireStatus}",
                                userId: timer.Appointment.SearchHire?.ClientId,
                                source: "AppointmentService.ProcessAppointmentTimerAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: timer.Appointment.Id
                            );

                            timer.Appointment.StatusId = noResponseStatus.Id;
                            timer.Appointment.UpdatedAt = DateTime.UtcNow;

                            // ✅ LOG: Estado cambiado
                            await _loggingService.LogInfoAsync(
                                message: "Estado de cita cambiado",
                                details: $"Cambiando estado de cita {timer.AppointmentId} a 'appointment_cancelled_by_expert_no_response'. " +
                                        $"StatusId: {noResponseStatus.Id}",
                                userId: timer.Appointment.SearchHire?.ClientId,
                                source: "AppointmentService.ProcessAppointmentTimerAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: timer.Appointment.Id
                            );

                            // ✅ CRÍTICO: Guardar estado INMEDIATAMENTE antes de procesar dinero
                            // Esto asegura que el estado se persista incluso si ProcessMoneyDistributionAsync falla
                            await _context.SaveChangesAsync();
                            
                            // #region agent log
                            File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                JsonSerializer.Serialize(new
                                {
                                    sessionId = "debug-session",
                                    runId = "run2",
                                    hypothesisId = "H9",
                                    location = "AppointmentService.ProcessAppointmentTimerAsync:response-after-save",
                                    message = "Response branch saved state before money",
                                    data = new
                                    {
                                        timerId,
                                        appointmentId = timer.AppointmentId,
                                        searchHireId = timer.Appointment.SearchHireId,
                                        statusId = timer.Appointment.StatusId
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                }) + Environment.NewLine);
                            // #endregion

                            await _loggingService.LogInfoAsync(
                                message: "Estado de cita guardado en base de datos",
                                details: $"Estado de cita {timer.AppointmentId} guardado exitosamente. StatusId: {noResponseStatus.Id}",
                                userId: timer.Appointment.SearchHire?.ClientId,
                                source: "AppointmentService.ProcessAppointmentTimerAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: timer.Appointment.Id
                            );

                            // Procesar dinero automáticamente
                            // ✅ MEJORA: Usar lógica automática de mapeo - ProcessMoneyDistributionAsync mapea automáticamente
                            // appointment_cancelled_by_expert_no_response → cancelled (genérico)
                            // Usa los % del AppointmentStatus (100/0/0) porque tiene configuración
                            try
                            {
                                // #region agent log
                                File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                    JsonSerializer.Serialize(new
                                    {
                                        sessionId = "debug-session",
                                        runId = "run1",
                                        hypothesisId = "H3",
                                        location = "AppointmentService.ProcessAppointmentTimerAsync:response-money",
                                        message = "Calling money distribution",
                                        data = new
                                        {
                                            timerId,
                                            appointmentId = timer.AppointmentId,
                                            searchHireId = timer.Appointment.SearchHireId,
                                            status = AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue()
                                        },
                                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                    }) + Environment.NewLine);
                                // #endregion

                                // ✅ LOG: Iniciando procesamiento de dinero
                                await _loggingService.LogInfoAsync(
                                    message: "Iniciando procesamiento de distribución de dinero",
                                    details: $"Llamando a ProcessMoneyDistributionAsync para SearchHireId: {timer.Appointment.SearchHireId}, " +
                                            $"StatusValue: {AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue()}",
                                    userId: timer.Appointment.SearchHire?.ClientId,
                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                    relatedEntityType: "Appointment",
                                    relatedEntityId: timer.Appointment.Id
                                );

                                await _refundService.ProcessMoneyDistributionAsync(
                                    timer.Appointment.SearchHireId,
                                    AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),
                                    "Expert did not respond within 24h - automatic cancellation",
                                    null,
                                    updateState: true); // ✅ updateState: true para que haga el mapeo automático

                                // ✅ EMAIL: Notificar cancelación automática por falta de respuesta del experto
                                if (timer.Appointment.SearchHire.Client != null && !string.IsNullOrEmpty(timer.Appointment.SearchHire.Client.Email))
                                {
                                    var expertName = timer.Appointment.SearchHire.Expert?.Name ?? "El experto";
                                    await _notificationService.SendGeneralNotificationEmailAsync(
                                        timer.Appointment.SearchHire.Client.Email,
                                        timer.Appointment.SearchHire.Client.Name,
                                        "❌ Cita Cancelada",
                                        $"{expertName} no respondió a tu propuesta en el plazo de 24 horas. Hemos cancelado la cita y procesado tu reembolso completo.",
                                        "Ver Reembolso",
                                        "https://www.inspecciono.com/appointments"
                                    );
                                }

                                if (timer.Appointment.SearchHire.Expert != null && !string.IsNullOrEmpty(timer.Appointment.SearchHire.Expert.Email))
                                {
                                    await _notificationService.SendGeneralNotificationEmailAsync(
                                        timer.Appointment.SearchHire.Expert.Email,
                                        timer.Appointment.SearchHire.Expert.Name,
                                        "Cita Cancelada (Sin respuesta)",
                                        "La cita ha sido cancelada porque no respondiste a la propuesta del cliente en el plazo de 24 horas."
                                    );
                                }
                                
                                // ✅ LOG: Procesamiento de dinero completado
                                await _loggingService.LogInfoAsync(
                                    message: "Distribución de dinero procesada",
                                    details: $"ProcessMoneyDistributionAsync completado para SearchHireId: {timer.Appointment.SearchHireId}",
                                    userId: timer.Appointment.SearchHire?.ClientId,
                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                    relatedEntityType: "Appointment",
                                    relatedEntityId: timer.Appointment.Id
                                );
                            }
                            catch (Exception ex)
                            {
                                // ✅ LOG ERROR: Registrar el error completo para debugging
                                await _loggingService.LogCriticalAsync(
                                    message: "Money distribution failed after auto-cancel (response timer)",
                                    details: $"Error al procesar distribución de dinero para cita {timer.AppointmentId}, SearchHireId: {timer.Appointment.SearchHireId}. " +
                                            $"Estado ya guardado como appointment_cancelled_by_expert_no_response. Error: {ex.Message}. StackTrace: {ex.StackTrace}. InnerException: {ex.InnerException?.Message}",
                                    userId: timer.Appointment.SearchHire?.ClientId,
                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                    relatedEntityType: "Appointment",
                                    relatedEntityId: timer.Appointment.Id,
                                    additionalData: new
                                    {
                                        TimerType = "response",
                                        TimerId = timer.Id,
                                        AppointmentStatus = AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),
                                        Exception = ex.Message,
                                        StackTrace = ex.StackTrace
                                    },
                                    notifyUser: true
                                );
                            }
                        }
                        else
                        {
                            // ✅ LOG WARNING: Estado no encontrado o Appointment null
                            await _loggingService.LogWarningAsync(
                                message: "No se pudo procesar cancelación - estado no encontrado o appointment null",
                                details: $"Timer {timerId}. noResponseStatus es null: {noResponseStatus == null}, " +
                                        $"timer.Appointment es null: {timer.Appointment == null}",
                                userId: null,
                                source: "AppointmentService.ProcessAppointmentTimerAsync",
                                relatedEntityType: "AppointmentTimer",
                                relatedEntityId: timerId
                            );
                        }
                        break;

                    case "expert_report":
                        // Si el experto no envía reporte en 24h, cancelar
                        var noReportStatus = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                    s.StatusValue == AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue());

                        if (noReportStatus != null && timer.Appointment != null)
                        {
                            // #region agent log
                            File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                JsonSerializer.Serialize(new
                                {
                                    sessionId = "debug-session",
                                    runId = "run2",
                                    hypothesisId = "H6",
                                    location = "AppointmentService.ProcessAppointmentTimerAsync:expert-report-branch",
                                    message = "Entering expert_report branch",
                                    data = new
                                    {
                                        timerId,
                                        appointmentId = timer.AppointmentId,
                                        searchHireId = timer.Appointment.SearchHireId,
                                        appointmentStatus,
                                        searchHireStatus
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                }) + Environment.NewLine);
                            // #endregion

                            timer.Appointment.StatusId = noReportStatus.Id;
                            timer.Appointment.UpdatedAt = DateTime.UtcNow;

                            // ✅ CRÍTICO: Guardar estado INMEDIATAMENTE antes de procesar dinero
                            await _context.SaveChangesAsync();

                            // #region agent log
                            File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                JsonSerializer.Serialize(new
                                {
                                    sessionId = "debug-session",
                                    runId = "run2",
                                    hypothesisId = "H9",
                                    location = "AppointmentService.ProcessAppointmentTimerAsync:expert-report-after-save",
                                    message = "Expert_report branch saved state before money",
                                    data = new
                                    {
                                        timerId,
                                        appointmentId = timer.AppointmentId,
                                        searchHireId = timer.Appointment.SearchHireId,
                                        statusId = timer.Appointment.StatusId
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                }) + Environment.NewLine);
                            // #endregion

                            // Procesar dinero automáticamente
                            // ✅ MEJORA: Usar lógica automática de mapeo - ProcessMoneyDistributionAsync mapea automáticamente
                            // appointment_cancelled_by_no_report → cancelled (genérico)
                            // Usa los % del AppointmentStatus (95/0/5) porque tiene configuración
                            try
                            {
                                // #region agent log
                                File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                    JsonSerializer.Serialize(new
                                    {
                                        sessionId = "debug-session",
                                        runId = "run1",
                                        hypothesisId = "H3",
                                        location = "AppointmentService.ProcessAppointmentTimerAsync:expert-report-money",
                                        message = "Calling money distribution",
                                        data = new
                                        {
                                            timerId,
                                            appointmentId = timer.AppointmentId,
                                            searchHireId = timer.Appointment.SearchHireId,
                                            status = AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue()
                                        },
                                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                    }) + Environment.NewLine);
                                // #endregion

                                await _refundService.ProcessMoneyDistributionAsync(
                                    timer.Appointment.SearchHireId,
                                    AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue(),
                                    "Expert did not submit report within 24h - automatic cancellation",
                                    null,
                                    updateState: true); // ✅ updateState: true para que haga el mapeo automático
                            }
                            catch (Exception moneyEx)
                            {
                                await _loggingService.LogCriticalAsync(
                                    message: "Money distribution failed after auto-cancel (expert_report timer)",
                                    details: $"Timer {timerId}, AppointmentId {timer.AppointmentId}, SearchHireId: {timer.Appointment.SearchHireId}. " +
                                             $"Estado ya guardado como appointment_cancelled_by_no_report. Error: {moneyEx.Message}",
                                    userId: timer.Appointment.SearchHire?.ClientId,
                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                    relatedEntityType: "Appointment",
                                    relatedEntityId: timer.Appointment.Id,
                                    additionalData: new
                                    {
                                        TimerType = "expert_report",
                                        TimerId = timer.Id,
                                        AppointmentStatus = AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue(),
                                        Exception = moneyEx.Message,
                                        StackTrace = moneyEx.StackTrace
                                    },
                                    notifyUser: true
                                );
                            }
                        }
                        break;

                    case "client_decision":
                        // Si el cliente no aprueba/disputa en 24h, completar automáticamente a favor del experto
                        try
                        {
                            // Cambiar AppointmentStatus a estado específico
                            var completedWithoutApprovalAppointmentStatus = await _context.SystemStatuses
                                .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                         s.StatusValue == AppointmentStatus.AppointmentCompletedWithoutClientApproval.ToStringValue());
                            
                            if (completedWithoutApprovalAppointmentStatus != null && timer.Appointment != null)
                            {
                                // #region agent log
                                File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                    JsonSerializer.Serialize(new
                                    {
                                        sessionId = "debug-session",
                                        runId = "run2",
                                        hypothesisId = "H6",
                                        location = "AppointmentService.ProcessAppointmentTimerAsync:client-decision-branch",
                                        message = "Entering client_decision branch",
                                        data = new
                                        {
                                            timerId,
                                            appointmentId = timer.AppointmentId,
                                            searchHireId = timer.Appointment.SearchHireId,
                                            appointmentStatus,
                                            searchHireStatus
                                        },
                                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                    }) + Environment.NewLine);
                                // #endregion

                                timer.Appointment.StatusId = completedWithoutApprovalAppointmentStatus.Id;
                                timer.Appointment.UpdatedAt = DateTime.UtcNow;
                                
                                // ✅ CRÍTICO: Guardar estado INMEDIATAMENTE antes de procesar dinero
                                await _context.SaveChangesAsync();

                                // #region agent log
                                File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                    JsonSerializer.Serialize(new
                                    {
                                        sessionId = "debug-session",
                                        runId = "run2",
                                        hypothesisId = "H9",
                                        location = "AppointmentService.ProcessAppointmentTimerAsync:client-decision-after-save",
                                        message = "Client_decision branch saved state before money",
                                        data = new
                                        {
                                            timerId,
                                            appointmentId = timer.AppointmentId,
                                            searchHireId = timer.Appointment.SearchHireId,
                                            statusId = timer.Appointment.StatusId
                                        },
                                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                    }) + Environment.NewLine);
                                // #endregion
                            }

                            // Procesar dinero automáticamente
                            // ✅ MEJORA: Usar lógica automática de mapeo - ProcessMoneyDistributionAsync mapea automáticamente
                            // appointment_completed_without_client_approval → completed (genérico)
                            // Usa los % del AppointmentStatus (0/100/0) porque tiene configuración
                            bool moneySuccess = false;
                            try
                            {
                                // #region agent log
                                File.AppendAllText("c:\\Users\\Diego\\Downloads\\App\\App\\NewApi\\.cursor\\debug.log",
                                    JsonSerializer.Serialize(new
                                    {
                                        sessionId = "debug-session",
                                        runId = "run1",
                                        hypothesisId = "H3",
                                        location = "AppointmentService.ProcessAppointmentTimerAsync:client-decision-money",
                                        message = "Calling money distribution",
                                        data = new
                                        {
                                            timerId,
                                            appointmentId = timer.AppointmentId,
                                            searchHireId = timer.Appointment.SearchHireId,
                                            status = AppointmentStatus.AppointmentCompletedWithoutClientApproval.ToStringValue()
                                        },
                                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                    }) + Environment.NewLine);
                                // #endregion

                                moneySuccess = await _refundService.ProcessMoneyDistributionAsync(
                                timer.Appointment.SearchHireId,
                                AppointmentStatus.AppointmentCompletedWithoutClientApproval.ToStringValue(),
                                "Client did not respond within 24h - automatic completion in favor of expert",
                                null,
                                updateState: true); // ✅ updateState: true para que haga el mapeo automático
                            }
                            catch (Exception ex)
                            {
                                await _loggingService.LogCriticalAsync(
                                    message: "Money distribution failed after auto-complete (client_decision timer)",
                                    details: $"Timer {timerId}, AppointmentId {timer.AppointmentId}, SearchHireId: {timer.Appointment.SearchHireId}. " +
                                             $"Estado ya guardado como appointment_completed_without_client_approval. Error: {ex.Message}",
                                    userId: timer.Appointment.SearchHire?.ClientId,
                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                    relatedEntityType: "Appointment",
                                    relatedEntityId: timer.Appointment.Id,
                                    additionalData: new
                                    {
                                        TimerType = "client_decision",
                                        TimerId = timer.Id,
                                        AppointmentStatus = AppointmentStatus.AppointmentCompletedWithoutClientApproval.ToStringValue(),
                                        Exception = ex.Message,
                                        StackTrace = ex.StackTrace
                                    },
                                    notifyUser: true
                                );
                            }

                            if (!moneySuccess)
                            {
                                // ✅ NOTIFICAR ADMIN: Dinero no procesado aunque estado ya fue guardado
                                await _loggingService.LogCriticalAsync(
                                    message: "Money distribution returned false after auto-complete (client_decision timer)",
                                    details: $"Timer {timerId}, AppointmentId {timer.AppointmentId}, SearchHireId: {timer.Appointment.SearchHireId}. " +
                                             $"Estado ya guardado como appointment_completed_without_client_approval. Requiere revisión manual.",
                                    userId: timer.Appointment.SearchHire?.ClientId,
                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                    relatedEntityType: "Appointment",
                                    relatedEntityId: timer.Appointment.Id,
                                    additionalData: new
                                    {
                                        TimerType = "client_decision",
                                        TimerId = timer.Id,
                                        AppointmentStatus = AppointmentStatus.AppointmentCompletedWithoutClientApproval.ToStringValue(),
                                        MoneySuccess = false
                                    },
                                    notifyUser: true
                                );

                                // ✅ FALLBACK: Verificar si el estado se cambió (puede haber fallado en Fase 1 o 2)
                                // Si NO se cambió, cambiarlo manualmente para evitar que el sistema quede bloqueado
                                var currentSearchHire = await _context.SearchHires
                                    .Include(sh => sh.Status)
                                    .Include(sh => sh.Appointment)
                                        .ThenInclude(a => a.Status)
                                    .FirstOrDefaultAsync(sh => sh.Id == timer.Appointment.SearchHireId);
                                
                                bool stateWasChanged = false;
                                if (currentSearchHire != null)
                                {
                                    // Verificar si el estado ya está en "completed" (cambió en Fase 2)
                                    var isCompleted = currentSearchHire.Status?.StatusValue == "completed" ||
                                                    currentSearchHire.Status?.IsFinalizationStatus == true;
                                    
                                    if (!isCompleted)
                                    {
                                        // Estado NO cambió (falló en Fase 1 o 2) → Cambiarlo manualmente como fallback
                                        try
                                        {
                                            // Cambiar SearchHire a "completed"
                                            var completedStatus = await _context.SystemStatuses
                                                .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && 
                                                                         s.StatusValue == "completed");
                                            if (completedStatus != null)
                                            {
                                                currentSearchHire.StatusId = completedStatus.Id;
                                                currentSearchHire.UpdatedAt = DateTime.UtcNow;
                                            }
                                            
                                            // Cambiar Appointment si existe
                                            if (currentSearchHire.Appointment != null)
                                            {
                                                var appointmentCompletedStatus = await _context.SystemStatuses
                                                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                                             s.StatusValue == "appointment_completed_auto");
                                                if (appointmentCompletedStatus == null)
                                                {
                                                    // Fallback: buscar cualquier estado de finalización de Appointment
                                                    appointmentCompletedStatus = await _context.SystemStatuses
                                                        .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                                                 s.IsFinalizationStatus == true);
                                                }
                                                if (appointmentCompletedStatus != null)
                                                {
                                                    currentSearchHire.Appointment.StatusId = appointmentCompletedStatus.Id;
                                                    currentSearchHire.Appointment.UpdatedAt = DateTime.UtcNow;
                                                }
                                            }
                                            
                                            await _context.SaveChangesAsync();
                                            stateWasChanged = true;
                                            
                                            // Log del fallback
                                            await _loggingService.LogWarningAsync(
                                                message: "State updated manually after ProcessMoneyDistributionAsync failure",
                                                details: $"SearchHire {timer.Appointment.SearchHireId} state was manually updated to 'completed' because ProcessMoneyDistributionAsync failed in Phase 1 or 2 (before state change). " +
                                                        $"This prevents the system from being blocked. Money distribution still needs manual processing.",
                                                userId: currentSearchHire.ClientId,
                                                source: "AppointmentService.ProcessAppointmentTimerAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: timer.Appointment.SearchHireId,
                                                additionalData: new { 
                                                    TimerType = "client_decision",
                                                    TimerId = timer.Id,
                                                    AppointmentId = timer.Appointment.Id,
                                                    FallbackStateChange = true
                                                }
                                            );
                                        }
                                        catch (Exception fallbackEx)
                                        {
                                            // Si el fallback también falla, log crítico
                                            await _loggingService.LogCriticalAsync(
                                                message: "CRITICAL: Failed to update state in fallback after ProcessMoneyDistributionAsync failure",
                                                details: $"SearchHire {timer.Appointment.SearchHireId} timer expired but both ProcessMoneyDistributionAsync and fallback state update failed. " +
                                                        $"System is BLOCKED. Fallback error: {fallbackEx.Message}",
                                                userId: currentSearchHire?.ClientId,
                                                source: "AppointmentService.ProcessAppointmentTimerAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: timer.Appointment.SearchHireId,
                                                additionalData: new { 
                                                    TimerType = "client_decision",
                                                    TimerId = timer.Id,
                                                    AppointmentId = timer.Appointment.Id,
                                                    FallbackError = fallbackEx.Message
                                                }
                                            );
                                        }
                                    }
                                    else
                                    {
                                        // Estado YA cambió (falló en Fase 3 - Stripe) → Correcto, solo falta dinero
                                        stateWasChanged = true;
                                    }
                                }
                                
                                // 🚨 LOG CRÍTICO: Fallo en distribución de dinero por timer expirado (client_decision)
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Money distribution failed for expired client_decision timer",
                                    details: $"Appointment {timer.Appointment.Id} timer expired (client did not respond within 24h) but money distribution failed. " +
                                            $"Timer Type: client_decision, AppointmentId: {timer.Appointment.Id}, SearchHireId: {timer.Appointment.SearchHireId}. " +
                                            $"ClientId: {timer.Appointment.SearchHire?.ClientId}, ExpertId: {timer.Appointment.SearchHire?.ExpertId}, Amount: {timer.Appointment.SearchHire?.Amount}€. " +
                                            $"State was {(stateWasChanged ? "updated" : "NOT updated - system may be blocked")}. " +
                                            $"ACTION REQUIRED: Review ProcessMoneyDistributionAsync error logs and manually process money distribution if needed.",
                                    userId: timer.Appointment.SearchHire?.ClientId,
                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                    relatedEntityType: "Appointment",
                                    relatedEntityId: timer.Appointment.Id,
                                    additionalData: new { 
                                        Action = "TimerExpired",
                                        TimerType = "client_decision",
                                        TimerId = timer.Id,
                                        AppointmentId = timer.Appointment.Id,
                                        SearchHireId = timer.Appointment.SearchHireId,
                                        ClientId = timer.Appointment.SearchHire?.ClientId,
                                        ExpertId = timer.Appointment.SearchHire?.ExpertId,
                                        Status = AppointmentStatus.AppointmentCompletedWithoutClientApproval.ToStringValue(),
                                        MoneyDistributionSuccess = false,
                                        StateWasChanged = stateWasChanged
                                    }
                                );
                            }
                            else
                            {
                                // ✅ LOG INFO: Timer expirado - cliente no respondió, completado automáticamente
                                await _loggingService.LogInfoAsync(
                                    message: "Appointment timer expired - client no response, auto-completed",
                                    details: $"Appointment {timer.Appointment.Id} completed automatically in favor of expert due to client not responding within 24h",
                                    userId: timer.Appointment.SearchHire?.ClientId,
                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                    relatedEntityType: "Appointment",
                                    relatedEntityId: timer.Appointment.Id,
                                    additionalData: new { 
                                        Action = "TimerExpired",
                                        TimerType = "client_decision",
                                        TimerId = timer.Id,
                                        AppointmentId = timer.Appointment.Id,
                                        SearchHireId = timer.Appointment.SearchHireId,
                                        ClientId = timer.Appointment.SearchHire?.ClientId,
                                        ExpertId = timer.Appointment.SearchHire?.ExpertId,
                                        Status = AppointmentStatus.AppointmentCompletedWithoutClientApproval.ToStringValue(),
                                        MoneyDistributionSuccess = true
                                    },
                                    notifyUser: false // Usar emails específicos
                                );
                                
                                // ✅ EMAIL: Notificar al cliente que el servicio se completó (y pedir reseña)
                                if (timer.Appointment.SearchHire?.Client != null && !string.IsNullOrEmpty(timer.Appointment.SearchHire.Client.Email))
                                {
                                    var serviceName = timer.Appointment.SearchHire.SearchService?.ServiceType?.Name ?? "Servicio de Inspección";
                                    var expertName = timer.Appointment.SearchHire.Expert?.Name ?? "El experto";
                                    
                                    // Usar el template de finalización que invita a reseñar
                                    await _notificationService.SendServiceCompletionEmailAsync(
                                        timer.Appointment.SearchHire.Client.Email,
                                        timer.Appointment.SearchHire.Client.Name,
                                        serviceName,
                                        expertName,
                                        timer.Appointment.SearchHireId
                                    );
                                    
                                    // Log adicional para el cliente explicando por qué se completó
                                    await _loggingService.LogInfoAsync(
                                        message: "Servicio finalizado automáticamente",
                                        details: "El plazo de 24 horas para revisar el reporte ha finalizado sin acciones. El servicio se ha marcado como completado automáticamente.",
                                        userId: timer.Appointment.SearchHire.ClientId,
                                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                                        relatedEntityType: "Appointment",
                                        relatedEntityId: timer.Appointment.Id,
                                        notifyUser: true
                                    );
                                }
                                
                                // ✅ Notificar al experto que el servicio se completó automáticamente a su favor
                                if (timer.Appointment.SearchHire?.ExpertId.HasValue == true)
                                {
                                    await _loggingService.LogInfoAsync(
                                        message: "Servicio completado automáticamente a tu favor",
                                        details: $"El cliente no respondió en 24 horas. El servicio #{timer.Appointment.SearchHireId} se completó automáticamente a tu favor y se procesó tu pago.",
                                        userId: timer.Appointment.SearchHire.ExpertId.Value,
                                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                                        relatedEntityType: "Appointment",
                                        relatedEntityId: timer.Appointment.Id,
                                        notifyUser: false // Usar email específico
                                    );

                                    if (timer.Appointment.SearchHire.Expert != null && !string.IsNullOrEmpty(timer.Appointment.SearchHire.Expert.Email))
                                    {
                                        await _notificationService.SendGeneralNotificationEmailAsync(
                                            timer.Appointment.SearchHire.Expert.Email,
                                            timer.Appointment.SearchHire.Expert.Name,
                                            "🎉 Servicio Completado",
                                            $"¡Enhorabuena! El servicio #{timer.Appointment.SearchHireId} se ha completado automáticamente porque el cliente no presentó objeciones en 24 horas. El pago ha sido liberado a tu cuenta."
                                        );
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // ✅ FALLBACK: Si hay excepción, intentar cambiar estado manualmente
                            try
                            {
                                var currentSearchHire = await _context.SearchHires
                                    .Include(sh => sh.Status)
                                    .Include(sh => sh.Appointment)
                                        .ThenInclude(a => a.Status)
                                    .FirstOrDefaultAsync(sh => sh.Id == timer.Appointment.SearchHireId);
                                
                                if (currentSearchHire != null && 
                                    currentSearchHire.Status?.StatusValue != "completed" &&
                                    currentSearchHire.Status?.IsFinalizationStatus != true)
                                {
                                    // ✅ MEJORA: Usar cache para obtener el estado "completed"
                                    try
                                    {
                                        var completedStatusId = await GetStatusIdByValueAsync("completed", "SearchHireStatus");
                                        currentSearchHire.StatusId = completedStatusId;
                                        currentSearchHire.UpdatedAt = DateTime.UtcNow;
                                        
                                        if (currentSearchHire.Appointment != null)
                                        {
                                            // Intentar obtener appointment_completed_auto primero
                                            try
                                            {
                                                var appointmentCompletedStatusId = await GetStatusIdByValueAsync(
                                                    "appointment_completed_auto", 
                                                    "AppointmentStatus"
                                                );
                                                currentSearchHire.Appointment.StatusId = appointmentCompletedStatusId;
                                                currentSearchHire.Appointment.UpdatedAt = DateTime.UtcNow;
                                            }
                                            catch
                                            {
                                                // Fallback: buscar cualquier estado de finalización de AppointmentStatus
                                                var appointmentCompletedStatus = await GetStatusByValueAndTypeAsync(
                                                    "appointment_completed_auto", 
                                                    "AppointmentStatus"
                                                );
                                                if (appointmentCompletedStatus == null)
                                                {
                                                    // Buscar cualquier estado de finalización
                                                    appointmentCompletedStatus = await _context.SystemStatuses
                                                        .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                                                 s.IsFinalizationStatus == true);
                                                }
                                                if (appointmentCompletedStatus != null)
                                                {
                                                    currentSearchHire.Appointment.StatusId = appointmentCompletedStatus.Id;
                                                    currentSearchHire.Appointment.UpdatedAt = DateTime.UtcNow;
                                                }
                                            }
                                        }
                                    }
                                    catch
                                    {
                                        // Si falla el cache, usar consulta directa como fallback
                                        var completedStatus = await _context.SystemStatuses
                                            .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && 
                                                                     s.StatusValue == "completed");
                                        if (completedStatus != null)
                                        {
                                            currentSearchHire.StatusId = completedStatus.Id;
                                            currentSearchHire.UpdatedAt = DateTime.UtcNow;
                                        }
                                    }
                                    
                                    await _context.SaveChangesAsync();
                                }
                            }
                            catch (Exception fallbackEx)
                            {
                                // Si el fallback también falla, continuar con el log crítico
                            }
                            
                            // 🚨 LOG CRÍTICO: Excepción procesando distribución por falta de decisión del cliente
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Exception during money distribution for expired client_decision timer",
                                details: $"Exception occurred while processing money distribution for Appointment {timer.Appointment.Id} due to expired client_decision timer. " +
                                        $"Timer Type: client_decision, AppointmentId: {timer.Appointment.Id}, SearchHireId: {timer.Appointment.SearchHireId}. " +
                                        $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                                        $"ClientId: {timer.Appointment.SearchHire?.ClientId}, ExpertId: {timer.Appointment.SearchHire?.ExpertId}, Amount: {timer.Appointment.SearchHire?.Amount}€. " +
                                        $"Stack Trace: {ex.StackTrace}. " +
                                        $"ACTION REQUIRED: Review exception and manually process money distribution if needed.",
                                userId: timer.Appointment.SearchHire?.ClientId,
                                source: "AppointmentService.ProcessAppointmentTimerAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: timer.Appointment.Id,
                                additionalData: new { 
                                    Action = "TimerExpired",
                                    TimerType = "client_decision",
                                    TimerId = timer.Id,
                                    AppointmentId = timer.Appointment.Id,
                                    SearchHireId = timer.Appointment.SearchHireId,
                                    ClientId = timer.Appointment.SearchHire?.ClientId,
                                    ExpertId = timer.Appointment.SearchHire?.ExpertId,
                                    Status = "completed_without_client_approval",
                                    Exception = ex.Message,
                                    StackTrace = ex.StackTrace
                                }
                            );
                        }
                        break;
                }

                // ✅ LOG: Guardando cambios en base de datos
                await _loggingService.LogInfoAsync(
                    message: "Guardando cambios en base de datos",
                    details: $"Timer {timerId} procesado. Guardando cambios: IsExpired=true, AppointmentStatusId={timer.Appointment?.StatusId}, " +
                            $"AppointmentId={timer.AppointmentId}",
                    userId: timer.Appointment?.SearchHire?.ClientId,
                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                    relatedEntityType: "AppointmentTimer",
                    relatedEntityId: timerId
                );

                await _context.SaveChangesAsync();

                // ✅ LOG: Cambios guardados exitosamente
                await _loggingService.LogInfoAsync(
                    message: "Timer procesado exitosamente",
                    details: $"Timer {timerId} procesado y cambios guardados en base de datos. " +
                            $"AppointmentId: {timer.AppointmentId}, AppointmentStatus: {timer.Appointment?.Status?.StatusValue ?? "null"}",
                    userId: timer.Appointment?.SearchHire?.ClientId,
                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                    relatedEntityType: "AppointmentTimer",
                    relatedEntityId: timerId
                );
            }
            catch (Exception ex)
            {
                // 🚨 LOG CRÍTICO: Excepción general procesando timer
                // Intentar obtener información del timer si es posible
                AppointmentTimer? timer = null;
                try
                {
                    timer = await _context.AppointmentTimers
                        .Include(t => t.Appointment)
                            .ThenInclude(a => a.SearchHire)
                        .FirstOrDefaultAsync(t => t.Id == timerId);
                }
                catch
                {
                    // Si no podemos obtener el timer, continuar sin esa información
                }

                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Exception during appointment timer processing",
                    details: $"Exception occurred while processing AppointmentTimer {timerId}. " +
                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                            $"Stack Trace: {ex.StackTrace}. " +
                            (timer != null ? 
                                $"Timer Type: {timer.TimerType}, AppointmentId: {timer.AppointmentId}, SearchHireId: {timer.Appointment?.SearchHireId}. " +
                                $"ClientId: {timer.Appointment?.SearchHire?.ClientId}, ExpertId: {timer.Appointment?.SearchHire?.ExpertId}. " : 
                                "Could not retrieve timer information. ") +
                            $"ACTION REQUIRED: Review exception and manually process timer if needed.",
                    userId: timer?.Appointment?.SearchHire?.ClientId,
                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                    relatedEntityType: "AppointmentTimer",
                    relatedEntityId: timerId,
                    additionalData: new { 
                        Action = "TimerProcessingException",
                        TimerId = timerId,
                        TimerType = timer?.TimerType,
                        AppointmentId = timer?.AppointmentId,
                        SearchHireId = timer?.Appointment?.SearchHireId,
                        ClientId = timer?.Appointment?.SearchHire?.ClientId,
                        ExpertId = timer?.Appointment?.SearchHire?.ExpertId,
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );

                // No lanzar excepción para evitar que Hangfire reintente indefinidamente
                // El timer se procesará en el próximo CheckAppointmentTimersAsync si es necesario
            }
        }

        /// <summary>
        /// Cambia el estado de una cita confirmada a "awaiting_report" 3 horas después de la hora de la cita.
        /// Hangfire reintenta automáticamente hasta 5 veces con delays progresivos
        /// (1m, 5m, 10m, 15m, 20m) para cubrir fallos transitorios de BD/red.
        /// </summary>
        [AutomaticRetry(
            Attempts = 5, 
            DelaysInSeconds = new[] { 60, 300, 600, 900, 1200 },  // 1m, 5m, 10m, 15m, 20m
            OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        public async Task ProcessAppointmentToAwaitingReportAsync(int appointmentId)
        {
            try
            {
                var appointment = await _context.Appointments
                    .Include(a => a.Status)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .FirstOrDefaultAsync(a => a.Id == appointmentId);

                if (appointment == null || appointment.Status?.StatusValue != "appointment_confirmed")
                {
                    return; // Cita no encontrada o no está confirmada
                }

                // ✅ VALIDACIÓN CRÍTICA: Verificar que el SearchHire exista
                if (appointment.SearchHire == null)
                {
                    return; // SearchHire eliminado
                }

                var searchHire = appointment.SearchHire;

                // ✅ VALIDACIÓN CRÍTICA: Verificar que el SearchHire NO esté finalizado
                if (searchHire.Status?.IsFinalizationStatus == true)
                {
                    return; // SearchHire ya finalizado, no procesar
                }

                // ✅ VALIDACIÓN CRÍTICA: Verificar estado del SearchHire (debe estar en "pending" o "awaiting_client_decision")
                var searchHireStatus = searchHire.Status?.StatusValue ?? string.Empty;
                if (searchHireStatus != "pending" && searchHireStatus != "awaiting_client_decision")
                {
                    return; // SearchHire no está en estado válido
                }

                // ✅ VALIDACIÓN CRÍTICA: Verificar que los usuarios existan y no estén bloqueados
                if (searchHire.Client == null || searchHire.Client.IsBlocked)
                {
                    return; // Cliente eliminado o bloqueado
                }

                if (searchHire.ExpertId.HasValue && (searchHire.Expert == null || searchHire.Expert.IsBlocked))
                {
                    return; // Experto eliminado o bloqueado
                }

                var awaitingReportStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                            s.StatusValue == "appointment_awaiting_report");

                if (awaitingReportStatus != null)
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
                    await _context.SaveChangesAsync();

                    // Programar scheduled job para cuando expire el timer (24 horas)
                    var jobId = BackgroundJob.Schedule<IAppointmentService>(
                        service => service.ProcessAppointmentTimerAsync(expertReportTimer.Id),
                        expertReportTimer.EndTime - DateTime.UtcNow
                    );

                    // Guardar el JobId en el timer
                    expertReportTimer.HangfireJobId = jobId;
                    await _context.SaveChangesAsync();
                    
                    // ✅ Enviar mensaje al chat con el cambio de estado (después del commit)
                    // Para cambios automáticos, el senderId es el ExpertId del SearchHire
                    var expertIdForMessage = searchHire.ExpertId ?? 0;
                    if (expertIdForMessage > 0)
                    {
                        await SendAppointmentStatusChangeMessageAsync(
                            searchHire.Id,
                            "appointment_awaiting_report",
                            expertIdForMessage
                        );
                    }
                    
                    // ✅ Notificar al experto que debe enviar el reporte en 24 horas
                    if (searchHire.ExpertId.HasValue)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Debes enviar el reporte de la cita",
                            details: $"Han pasado 3 horas desde la cita. Tienes 24 horas para enviar el reporte del servicio #{searchHire.Id}. Si no lo envías a tiempo, la cita será cancelada automáticamente.",
                            userId: searchHire.ExpertId.Value,
                            source: "AppointmentService.ProcessAppointmentToAwaitingReportAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: appointment.Id,
                            notifyUser: false // Usar email específico
                        );

                        if (searchHire.Expert != null && !string.IsNullOrEmpty(searchHire.Expert.Email))
                        {
                            await _notificationService.SendGeneralNotificationEmailAsync(
                                searchHire.Expert.Email,
                                searchHire.Expert.Name,
                                "📝 Hora de Enviar el Reporte",
                                $"Han pasado 3 horas desde la cita #{appointment.Id}. Tienes 24 horas para enviar el reporte del servicio. Si no lo envías a tiempo, la cita será cancelada automáticamente.",
                                "Enviar Reporte",
                                "https://www.inspecciono.com/appointments"
                            );
                        }
                    }
                    
                    // ✅ Notificar al cliente que se está esperando el reporte
                    if (searchHire.ClientId > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Esperando reporte del experto",
                            details: $"Han pasado 3 horas desde la cita. El experto tiene 24 horas para enviar el reporte del servicio. Te notificaremos cuando esté listo.",
                            userId: searchHire.ClientId,
                            source: "AppointmentService.ProcessAppointmentToAwaitingReportAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: appointment.Id,
                            notifyUser: false // Usar email específico
                        );

                        if (searchHire.Client != null && !string.IsNullOrEmpty(searchHire.Client.Email))
                        {
                            await _notificationService.SendGeneralNotificationEmailAsync(
                                searchHire.Client.Email,
                                searchHire.Client.Name,
                                "⏳ Esperando Reporte",
                                $"Han pasado 3 horas desde la cita #{appointment.Id}. El experto tiene 24 horas para enviar el reporte. Te notificaremos en cuanto esté disponible."
                            );
                        }
                    }
                    
                    // ✅ Marcar el timer de transición como expirado ya que el job se ejecutó exitosamente
                    var transitionTimers = await _context.AppointmentTimers
                        .Where(t => t.AppointmentId == appointment.Id && 
                                   t.TimerType == "awaiting_report_transition" && 
                                   !t.IsExpired)
                        .ToListAsync();
                    
                    foreach (var timer in transitionTimers)
                    {
                        timer.IsExpired = true;
                        timer.ExpiredAt = DateTime.UtcNow;
                        if (!string.IsNullOrEmpty(timer.HangfireJobId))
                        {
                            timer.HangfireJobId = null; // Limpiar referencia
                        }
                    }
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                // 🚨 LOG CRÍTICO: Error en job de transición a awaiting_report
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Exception in ProcessAppointmentToAwaitingReportAsync",
                    details: $"Failed to transition appointment {appointmentId} to awaiting_report status. " +
                            $"Error: {ex.Message}. StackTrace: {ex.StackTrace}",
                    userId: null,
                    source: "AppointmentService.ProcessAppointmentToAwaitingReportAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: appointmentId,
                    additionalData: new { 
                        Action = "ProcessAppointmentToAwaitingReport",
                        AppointmentId = appointmentId,
                        Exception = ex.Message
                    }
                );
                throw; // Rethrow para que Hangfire reintente
            }
        }



        public async Task<AppointmentDto> SubmitExpertReportAsync(int appointmentId, int expertId, string? notes = null)

        {

            try

            {

                // ✅ FIX CRÍTICO: NO usar ExecutionStrategy con transacciones manuales en PgBouncer
                // PgBouncer Transaction Pooler no admite savepoints automáticos que EF Core intenta crear
                // ✅ PROTECCIÓN: Abrir transacción ANTES del FOR UPDATE para que el bloqueo funcione

                using var transaction = await _context.Database.BeginTransactionAsync();

                    try

                    {

                        // ✅ PROTECCIÓN: Usar row-level locking DENTRO de la transacción para evitar doble procesamiento

                var appointment = await _context.Appointments

                            .FromSqlInterpolated($"SELECT * FROM \"Appointments\" WHERE \"Id\" = {appointmentId} FOR UPDATE")

                    .Include(a => a.SearchHire)

                        .ThenInclude(sh => sh.Status)

                    .Include(a => a.Status)

                            .FirstOrDefaultAsync();



                if (appointment == null)

                    throw new ArgumentException("Appointment not found");



                        var currentStatus = appointment.Status?.StatusValue ?? string.Empty;

                        // ✅ VALIDACIÓN: Verificar que el usuario es el experto

                if (appointment.SearchHire.ExpertId != expertId)

                    throw new UnauthorizedAccessException("Only the expert can submit reports");

                        // ✅ VALIDACIÓN CRÍTICA: Verificar que el SearchHire NO esté finalizado
                        if (appointment.SearchHire.Status?.IsFinalizationStatus == true)
                        {
                            var searchHireStatus = appointment.SearchHire.Status?.StatusValue ?? "unknown";
                            throw new InvalidOperationException(
                                $"No se puede enviar el reporte cuando el servicio está en estado de finalización '{searchHireStatus}'. " +
                                $"El servicio debe estar activo para poder enviar reportes."
                            );
                        }

                        // ✅ VALIDACIÓN CRÍTICA: Solo se puede enviar reporte si está en estado "appointment_awaiting_report"

                        if (currentStatus != "appointment_awaiting_report")

                        {

                            throw new InvalidOperationException(

                                $"No se puede enviar el reporte en estado '{currentStatus}'. " +

                                $"Solo se pueden enviar reportes cuando la cita está en estado 'appointment_awaiting_report'."

                            );

                        }



                        // ✅ PROTECCIÓN: Verificar que no se haya procesado ya (evitar doble click)

                        var invalidStatesForReport = new[] { 

                            AppointmentStatus.AppointmentReportSent.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByClient.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByClientSecond.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByExpert.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByExpertSecond.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue(),

                            AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue()

                        };

                        

                        if (invalidStatesForReport.Contains(currentStatus))

                        {

                            throw new InvalidOperationException(

                                $"La cita ya ha sido procesada (estado: '{currentStatus}'). " +

                                $"No se puede enviar el reporte nuevamente."

                            );

                        }



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

                // ✅ CRÍTICO: Marcar la entidad como Modified explícitamente porque se cargó con FromSqlInterpolated
                _context.Entry(appointment).State = EntityState.Modified;



                // Actualizar el SearchHire según el mapeo de estados

                var appointmentStatusEnum = AppointmentStatus.AppointmentReportSent;

                var targetSearchHireStatus = await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatusEnum);

                if (targetSearchHireStatus.HasValue)

                {

                    var oldStatusValue = appointment.SearchHire.Status?.StatusValue ?? "unknown";

                    var statusId = await GetStatusIdByValueAsync(targetSearchHireStatus.Value.ToStringValue());

                    appointment.SearchHire.StatusId = statusId;

                    appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

                }

                else

                {

                }

                // ✅ CANCELAR TODOS los timers activos (expert_report, response, proposal, etc.) antes de crear el timer de client_decision
                // Esto asegura que no queden timers antiguos activos cuando se envía el reporte
                var activeTimers = await _context.AppointmentTimers

                    .Where(t => t.AppointmentId == appointment.Id && 

                               !t.IsExpired)

                    .ToListAsync();



                foreach (var timer in activeTimers)

                {

                    timer.IsExpired = true;

                    timer.ExpiredAt = DateTime.UtcNow;
                    
                    // ✅ CANCELAR job de Hangfire si existe
                    if (!string.IsNullOrEmpty(timer.HangfireJobId))
                    {
                        try
                        {
                            BackgroundJob.Delete(timer.HangfireJobId);
                            timer.HangfireJobId = null; // Limpiar referencia
                        }
                        catch (Exception ex)
                        {
                            // Si el job ya no existe o fue procesado, continuar sin error
                            timer.HangfireJobId = null;
                        }
                    }
                }

                await _context.SaveChangesAsync();
                
                // ✅ Crear timer para decisión del cliente (24 horas)
                // Si el cliente no aprueba/disputa en 24h, se completa automáticamente a favor del experto
                var clientDecisionTimer = new AppointmentTimer
                {
                    AppointmentId = appointment.Id,
                    TimerType = "client_decision",
                    StartTime = DateTime.UtcNow,
                    EndTime = DateTime.UtcNow.AddHours(24),
                    IsExpired = false,
                    CreatedAt = DateTime.UtcNow
                };

                _context.AppointmentTimers.Add(clientDecisionTimer);
                await _context.SaveChangesAsync();

                // Programar scheduled job para cuando expire el timer (24 horas)
                var jobId = BackgroundJob.Schedule<IAppointmentService>(
                    service => service.ProcessAppointmentTimerAsync(clientDecisionTimer.Id),
                    clientDecisionTimer.EndTime - DateTime.UtcNow
                );

                // Guardar el JobId en el timer
                clientDecisionTimer.HangfireJobId = jobId;
                await _context.SaveChangesAsync();

                        // ✅ COMMIT: Confirmar la transacción

                        await transaction.CommitAsync();
                        
                // ✅ Notificar al cliente que el experto envió el reporte
                if (appointment.SearchHire?.ClientId != null)
                {
                    await _loggingService.LogInfoAsync(
                        message: "Reporte del experto recibido",
                        details: $"El experto envió el reporte del servicio #{appointment.SearchHireId}. Tienes 24 horas para aprobar o disputar el servicio. Si no actúas en 24 horas, se aprobará automáticamente.",
                        userId: appointment.SearchHire.ClientId,
                        source: "AppointmentService.SubmitExpertReportAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: appointment.Id,
                        notifyUser: false // Usar email específico
                    );

                    if (appointment.SearchHire.Client != null && !string.IsNullOrEmpty(appointment.SearchHire.Client.Email))
                    {
                        await _notificationService.SendGeneralNotificationEmailAsync(
                            appointment.SearchHire.Client.Email,
                            appointment.SearchHire.Client.Name,
                            "📄 Reporte Recibido",
                            $"El experto ha enviado el reporte del servicio #{appointment.SearchHireId}.<br><br>Tienes 24 horas para revisar el reporte y aprobarlo o abrir una disputa. Si no realizas ninguna acción, el servicio se aprobará automáticamente.",
                            "Revisar Reporte",
                            "https://www.inspecciono.com/appointments"
                        );
                    }
                }

                // ✅ Notificar al experto que el reporte se envió correctamente
                if (appointment.SearchHire?.ExpertId.HasValue == true)
                {
                    await _loggingService.LogInfoAsync(
                        message: "Reporte enviado correctamente",
                        details: $"Has enviado el reporte del servicio #{appointment.SearchHireId}. El cliente tiene 24 horas para aprobarlo o disputarlo.",
                        userId: appointment.SearchHire.ExpertId.Value,
                        source: "AppointmentService.SubmitExpertReportAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: appointment.Id,
                        notifyUser: false // Usar email específico
                    );

                    if (appointment.SearchHire.Expert != null && !string.IsNullOrEmpty(appointment.SearchHire.Expert.Email))
                    {
                        await _notificationService.SendGeneralNotificationEmailAsync(
                            appointment.SearchHire.Expert.Email,
                            appointment.SearchHire.Expert.Name,
                            "✅ Reporte Enviado",
                            $"Has enviado correctamente el reporte del servicio #{appointment.SearchHireId}. El cliente ha sido notificado y tiene 24 horas para revisar tu trabajo."
                        );
                    }
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

                // ✅ Enviar mensaje al chat con el cambio de estado (después del commit)

                await SendAppointmentStatusChangeMessageAsync(

                    appointment.SearchHireId, 

                    AppointmentStatus.AppointmentReportSent.ToStringValue(), 

                    expertId

                );



                return MapToDto(updatedAppointment);

                    }

                    catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, hacer rollback seguro
                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }
                        throw; // Re-lanzar para que el usuario pueda reintentar
                    }
                    catch (ObjectDisposedException)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, hacer rollback seguro
                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }
                        throw; // Re-lanzar para que el usuario pueda reintentar
                    }
                    catch (Exception innerEx)

                    {

                        // ✅ ROLLBACK: Revertir la transacción en caso de error

                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch { }

                        throw;

                    }

            }

            catch (Exception ex)

            {

                // ⚠️ LOG WARNING: Error general enviando reporte de experto (no afecta dinero, usuario puede reintentar)

                await _loggingService.LogWarningAsync(

                    message: "Error submitting expert report",

                    details: $"An unexpected exception occurred while submitting expert report for appointment {appointmentId}. " +

                            $"Expert {expertId} attempted to submit report. " +

                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +

                            $"Stack Trace: {ex.StackTrace}. " +

                            $"Expert may need to retry the operation.",

                    userId: expertId,

                    source: "AppointmentService.SubmitExpertReportAsync",

                    relatedEntityType: "Appointment",

                    relatedEntityId: appointmentId,

                    additionalData: new { 

                        AppointmentId = appointmentId,

                        ExpertId = expertId,

                        ErrorType = ex.GetType().Name,

                        ErrorMessage = ex.Message,

                        StackTrace = ex.StackTrace,

                        InnerException = ex.InnerException?.Message

                    }

                );

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

                var missingFiles = new List<string>();



                // Verificar PDF obligatorio

                var pdfType = requiredDeliverableTypes.FirstOrDefault(dt => dt.Name == "PDF");

                if (pdfType != null)

                {

                    var hasPdf = uploadedDeliverables.Any(d => d.Type == "pdf");

                    if (!hasPdf)

                    {

                        missingFiles.Add("PDF");

                    }

                }



                // Verificar video si está configurado

                var videoType = requiredDeliverableTypes.FirstOrDefault(dt => dt.Name == "Video");

                if (videoType != null)

                {

                    var hasVideo = uploadedDeliverables.Any(d => d.Type == "video");

                    if (!hasVideo)

                    {

                        missingFiles.Add("MP4");

                    }

                }



                // Si faltan archivos, devolver mensaje específico

                if (missingFiles.Any())

                {

                    var missingFilesText = string.Join(" y ", missingFiles);

                    return (false, $"Faltan archivos obligatorios: {missingFilesText}. Debes subir estos archivos antes de enviar el reporte.");

                }

                return (true, string.Empty);

            }

            catch (Exception ex)

            {

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

                    searchLocationRange = 50; // Rango por defecto

                }



                // Calcular la distancia entre la ubicación del experto y la ubicación propuesta para la cita

                var distance = CalculateDistance(expertLatitude, expertLongitude, appointmentLatitude.Value, appointmentLongitude.Value);





                // Verificar que la distancia esté dentro del rango permitido

                if (distance > searchLocationRange)

                {

                    throw new InvalidOperationException(

                        $"La ubicación propuesta para la cita está fuera del rango del experto. " +

                        $"Distancia: {distance:F1} km, Rango máximo: {searchLocationRange} km. " +

                        $"El experto solo puede realizar citas dentro de su rango de servicio original."

                    );

                }

            }

            catch (Exception ex)

            {

                throw;

            }

        }



        /// <summary>

        /// Valida que la fecha/hora propuesta para la cita esté dentro del horario de disponibilidad del experto
        /// ✅ INTERNACIONALIZACIÓN: proposedDateTime viene en UTC, se convierte a hora local del experto para validar
        /// </summary>

        private async Task ValidateAppointmentAvailabilityAsync(SearchHire searchHire, DateTime proposedDateTime)

        {

            try

            {

                // ✅ INTERNACIONALIZACIÓN: proposedDateTime está en UTC, necesitamos convertir a hora local del experto
                // para comparar con las horas de disponibilidad que están en hora local

                // Cargar el SearchHire con el ExpertProfile

                var hire = await _context.SearchHires

                    .Include(sh => sh.SearchService)

                        .ThenInclude(ss => ss.ExpertProfile)

                    .FirstOrDefaultAsync(sh => sh.Id == searchHire.Id);



                if (hire == null)

                {

                    throw new ArgumentException("SearchHire not found");

                }



                // Verificar que existe el ExpertProfile

                if (hire.SearchService?.ExpertProfile == null)

                {

                    throw new InvalidOperationException("Expert profile not found for the service");

                }



                var expertProfileId = hire.SearchService.ExpertProfile.Id;

                // ✅ INTERNACIONALIZACIÓN: Obtener timezone del experto (prioridad: SearchHire.ExpertTimezone > ExpertProfile.Timezone)
                var expertTimezone = _timezoneService.GetEffectiveTimezone(
                    hire.ExpertTimezone,
                    hire.SearchService.ExpertProfile.Timezone
                );

                // ✅ INTERNACIONALIZACIÓN: Convertir fecha/hora UTC a hora local del experto
                var proposedDateTimeLocal = _timezoneService.ConvertFromUtc(proposedDateTime, expertTimezone);

                // Obtener la disponibilidad activa del experto

                var availability = await _context.ExpertAvailabilities

                    .Where(ea => ea.ExpertId == expertProfileId && ea.IsActive && ea.EffectiveTo == null)

                    .OrderByDescending(ea => ea.EffectiveFrom)

                    .FirstOrDefaultAsync();



                // Si el experto no tiene horarios configurados, no permitir la cita

                if (availability == null)

                {

                    throw new InvalidOperationException(

                        "El experto no tiene horarios de disponibilidad configurados. " +

                        "No se puede crear una cita sin horarios establecidos."

                    );

                }



                // Deserializar los días de la semana

                var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(availability.DaysOfWeek) ?? new List<string>();



                if (daysOfWeek.Count == 0)

                {

                    throw new InvalidOperationException(

                        "El experto no tiene días de disponibilidad configurados."

                    );

                }



                // ✅ INTERNACIONALIZACIÓN: Obtener el día de la semana de la fecha propuesta en hora LOCAL del experto
                var dayOfWeek = proposedDateTimeLocal.DayOfWeek.ToString(); // "Monday", "Tuesday", etc.



                // Verificar que el día esté en los días disponibles

                if (!daysOfWeek.Contains(dayOfWeek))

                {

                    var availableDaysSpanish = string.Join(", ", daysOfWeek.Select(d =>

                    {

                        return d switch

                        {

                            "Monday" => "Lunes",

                            "Tuesday" => "Martes",

                            "Wednesday" => "Miércoles",

                            "Thursday" => "Jueves",

                            "Friday" => "Viernes",

                            "Saturday" => "Sábado",

                            "Sunday" => "Domingo",

                            _ => d

                        };

                    }));



                    var daySpanish = dayOfWeek switch

                    {

                        "Monday" => "Lunes",

                        "Tuesday" => "Martes",

                        "Wednesday" => "Miércoles",

                        "Thursday" => "Jueves",

                        "Friday" => "Viernes",

                        "Saturday" => "Sábado",

                        "Sunday" => "Domingo",

                        _ => dayOfWeek

                    };



                    throw new InvalidOperationException(

                        $"El día propuesto ({daySpanish}) no está dentro de los horarios de disponibilidad del experto. " +

                        $"Días disponibles: {availableDaysSpanish}. " +

                        $"Fecha propuesta: {proposedDateTimeLocal:dd/MM/yyyy} ({expertTimezone})"

                    );

                }



                // ✅ INTERNACIONALIZACIÓN: Obtener la hora propuesta en hora LOCAL del experto (solo horas y minutos, sin segundos)
                var proposedTime = proposedDateTimeLocal.TimeOfDay;

                var proposedTimeOnly = new TimeSpan(proposedTime.Hours, proposedTime.Minutes, 0);



                // ✅ INTERNACIONALIZACIÓN: Verificar que la hora LOCAL esté dentro del rango de disponibilidad LOCAL
                if (proposedTimeOnly < availability.StartTime || proposedTimeOnly > availability.EndTime)

                {

                    var startTimeFormatted = $"{availability.StartTime.Hours:D2}:{availability.StartTime.Minutes:D2}";

                    var endTimeFormatted = $"{availability.EndTime.Hours:D2}:{availability.EndTime.Minutes:D2}";

                    var proposedTimeFormatted = $"{proposedTimeOnly.Hours:D2}:{proposedTimeOnly.Minutes:D2}";



                    throw new InvalidOperationException(

                        $"La hora propuesta ({proposedTimeFormatted} {expertTimezone}) está fuera del horario de disponibilidad del experto. " +

                        $"Horario disponible: {startTimeFormatted} - {endTimeFormatted} ({expertTimezone}). " +

                        $"Fecha/hora propuesta: {proposedDateTimeLocal:dd/MM/yyyy HH:mm} {expertTimezone} (UTC: {proposedDateTime:dd/MM/yyyy HH:mm})"

                    );

                }

            }

            catch (Exception ex)

            {

                throw;

            }

        }

        /// <summary>
        /// Envía un mensaje automático al chat cuando cambia el estado de una cita.
        /// Formato: "APPointmentStatusChange:{status_value}"
        /// </summary>
        private async Task SendAppointmentStatusChangeMessageAsync(int searchHireId, string statusValue, int senderId)

        {

            try

            {

                // Buscar la conversación activa del SearchHire

                var conversation = await _context.Conversations

                    .FirstOrDefaultAsync(c => c.SearchHireId == searchHireId && c.IsActive);



                if (conversation == null)

                {

                    return;

                }



                // Crear el mensaje con el formato esperado por el frontend

                var message = new Message

                {

                    ConversationId = conversation.Id,

                    SenderId = senderId,

                    Content = $"APPointmentStatusChange:{statusValue}",

                    SentAt = DateTime.UtcNow,

                    IsRead = false

                };



                _context.Messages.Add(message);

                conversation.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

            }

            catch (Exception ex)

            {

                // No lanzar excepción - el envío del mensaje no debe afectar el flujo principal

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
            // ✅ INTERNACIONALIZACIÓN: Convertir fecha/hora UTC a hora local del experto
            // Obtener timezone efectivo (prioridad: SearchHire.ExpertTimezone > ExpertProfile.Timezone > UTC)
            string? expertTimezone = null;
            DateTime? proposedDateLocal = null;
            TimeSpan? proposedTimeLocal = null;
            
            // Solo convertir si hay fecha/hora propuesta
            if (appointment.ProposedDate.HasValue && appointment.ProposedTime.HasValue
                && appointment.ProposedDate.Value != default(DateTime) && appointment.ProposedTime.Value != default(TimeSpan))
            {
                // Asegurar que SearchHire y sus relaciones estén cargadas
                if (appointment.SearchHire != null)
                {
                    if (appointment.SearchHire.SearchService == null)
                    {
                        _context.Entry(appointment.SearchHire)
                            .Reference(sh => sh.SearchService)
                            .Load();
                    }
                    
                    if (appointment.SearchHire.SearchService?.ExpertProfile == null && 
                        appointment.SearchHire.SearchService != null)
                    {
                        _context.Entry(appointment.SearchHire.SearchService)
                            .Reference(ss => ss.ExpertProfile)
                            .Load();
                    }
                }
                
                // Obtener timezone efectivo
                expertTimezone = _timezoneService.GetEffectiveTimezone(
                    appointment.SearchHire?.ExpertTimezone,
                    appointment.SearchHire?.SearchService?.ExpertProfile?.Timezone
                );
                
                // Construir DateTime UTC desde fecha y hora guardadas
                if (appointment.ProposedDate.HasValue && appointment.ProposedTime.HasValue)
                {
                    var proposedDateTimeUtc = DateTime.SpecifyKind(
                        appointment.ProposedDate.Value.Date + appointment.ProposedTime.Value,
                        DateTimeKind.Utc
                    );
                    
                    // Convertir de UTC a hora local
                    var proposedDateTimeLocal = _timezoneService.ConvertFromUtc(proposedDateTimeUtc, expertTimezone);
                    proposedDateLocal = proposedDateTimeLocal.Date;
                    proposedTimeLocal = proposedDateTimeLocal.TimeOfDay;
                }
            }

            return new AppointmentDto

            {

                Id = appointment.Id,

                SearchHireId = appointment.SearchHireId,

                Status = appointment.Status?.StatusValue ?? string.Empty,

                ProposedDate = appointment.ProposedDate, // ✅ Nullable: UTC (guardada en BD)

                ProposedTime = appointment.ProposedTime, // ✅ Nullable: UTC (guardada en BD)
                
                // ✅ INTERNACIONALIZACIÓN: Fecha/hora en hora local para el frontend
                ProposedDateLocal = proposedDateLocal,
                ProposedTimeLocal = proposedTimeLocal,
                Timezone = expertTimezone,
                Country = appointment.SearchHire?.ExpertCountry, // ✅ INTERNACIONALIZACIÓN: País del experto al momento de la contratación

                Location = appointment.Location,

                Latitude = appointment.Latitude,

                Longitude = appointment.Longitude,

                DoorNumber = appointment.DoorNumber,

                OwnerPhone = appointment.OwnerPhone,

                SiteDetails = appointment.SiteDetails,

                RejectionCount = appointment.RejectionCount,

                ClientCancellationCount = appointment.ClientCancellationCount,

                ExpertCancellationCount = appointment.ExpertCancellationCount,

                LastRejectionAt = appointment.LastRejectionAt,

                LastClientCancellationAt = appointment.LastClientCancellationAt,

                LastExpertCancellationAt = appointment.LastExpertCancellationAt,

                LastProposalAt = appointment.LastProposalAt,

                LastResponseAt = appointment.LastResponseAt,

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

