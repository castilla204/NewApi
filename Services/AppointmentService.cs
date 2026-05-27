using Microsoft.EntityFrameworkCore;

using newApi.DataLayer.Models;

using newApi.DataLayer.Models.DTOs;

using newApi.DataLayer.Models.PostGresModels;

using newApi.DataLayer.Models.enums;

using newApi.Common;

using System.Globalization;

using Hangfire;



namespace newApi.Services

{

    // TODO P3-8: envolver SaveChangesAsync con ConcurrencyRetryHelper.SaveChangesWithRetryAsync(_context, ...)
    // en los 5 métodos críticos: ProposeAppointmentAsync, ConfirmAppointmentAsync, RejectAppointmentAsync,
    // CancelAppointmentAsync, SubmitExpertReportAsync. Cada uno usa transacción manual con múltiples ramas
    // (Hangfire schedule + commit/rollback) y exige tests dedicados antes de tocar. Tras P2-4 (xmin como
    // concurrency token en Appointment/FinancialTransaction/SearchHire/User/ExpertProfile), un conflicto
    // lanza DbUpdateConcurrencyException → HTTP 500. Pendiente de iteración futura con tests E2E.
    public class AppointmentService : IAppointmentService

    {

        private readonly AppDbContext _context;

        private readonly SystemStatusService _systemStatusService;

        private readonly StripeRefundService _refundService;

        private readonly ILoggingService _loggingService;

        private readonly IStripeValidationService _stripeValidationService;
        private readonly ITimezoneService _timezoneService; // 🔧 FIX zona horaria

        // Ô£à MEJORA: Cache de estados para evitar consultas repetidas a la BD
        // Usa una clave compuesta: "StatusType|StatusValue" -> StatusId
        private static readonly Dictionary<string, int> _statusCache = new Dictionary<string, int>();
        private static readonly object _cacheLock = new object();
        private static DateTime _cacheLastRefresh = DateTime.MinValue;
        private static readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(30); // Cache v├ílido por 30 minutos

        public AppointmentService(AppDbContext context, SystemStatusService systemStatusService, StripeRefundService refundService, ILoggingService loggingService, IStripeValidationService stripeValidationService, ITimezoneService timezoneService)

        {

            _context = context;

            _systemStatusService = systemStatusService;

            _refundService = refundService;

            _loggingService = loggingService;

            _stripeValidationService = stripeValidationService;

            _timezoneService = timezoneService;

        }

        /// <summary>
        /// 🔧 FIX zona horaria: convierte la hora LOCAL de pared de la cita (ProposedDate.Date + ProposedTime)
        /// a UTC REAL usando el timezone IANA del experto (SearchHire.ExpertTimezone). Si el timezone es
        /// null/vacío, ConvertToUtc hace fallback a "tratar como UTC" (= comportamiento anterior), así que es
        /// seguro. Usar SOLO para comparar contra DateTime.UtcNow o programar timers Hangfire; el ALMACENAMIENTO
        /// de ProposedDate/ProposedTime sigue en hora local del experto (la validación de disponibilidad y el
        /// display dependen de ello).
        /// </summary>
        private DateTime GetAppointmentUtc(DateTime proposedDate, TimeSpan proposedTime, string? expertTimezone)
        {
            var localWall = DateTime.SpecifyKind(proposedDate.Date + proposedTime, DateTimeKind.Unspecified);
            return _timezoneService.ConvertToUtc(localWall, expertTimezone ?? string.Empty);
        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue with caching
        /// Ô£à MEJORA: Cache de estados para mejorar performance
        /// Soporta tanto AppointmentStatus como SearchHireStatus
        /// </summary>
        private async Task<int> GetStatusIdByValueAsync(string statusValue, string statusType = "SearchHireStatus", CancellationToken cancellationToken = default)
        {
            // Crear clave de cache: "StatusType|StatusValue"
            string cacheKey = $"{statusType}|{statusValue}";

            // Verificar si el cache est├í expirado
            bool cacheExpired = DateTime.UtcNow - _cacheLastRefresh > _cacheExpiration;
            
            lock (_cacheLock)
            {
                // Si el cache est├í expirado, limpiarlo
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

            // Si no est├í en cache, consultar BD
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == statusType, cancellationToken);
            
            int statusId;
            if (systemStatus == null)
            {
                // Default to "pending" (ID = 1) solo para SearchHireStatus
                // Para AppointmentStatus, lanzar excepci├│n si no se encuentra
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
        /// Ô£à MEJORA: Cache para obtener la entidad completa cuando se necesite
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

            // Si no est├í en cache o no coincide, consultar BD
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == statusType, cancellationToken);
            
            // Guardar en cache si se encontr├│
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

                throw;

            }

        }



        public async Task<AppointmentDto> CreateAppointmentAsync(CreateAppointmentDto dto)

        {

            try

            {

                // Ô£à CORRECCI├ôN: Usar la estrategia de ejecuci├│n para manejar transacciones con reintentos (NpgsqlRetryingExecutionStrategy)

                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>

                {

                    // Ô£à PROTECCI├ôN: Abrir transacci├│n ANTES de cualquier operaci├│n para evitar race conditions

                    using var transaction = await _context.Database.BeginTransactionAsync();

                    try

                    {

                        // Ô£à PROTECCI├ôN: Usar row-level locking dentro de la transacci├│n para evitar race conditions

                        // Bloquear el SearchHire con FOR UPDATE para evitar que dos usuarios creen citas simult├íneamente

                        var searchHire = await _context.SearchHires

                            .FromSqlInterpolated($"SELECT *, xmin FROM \"SearchHires\" WHERE \"Id\" = {dto.SearchHireId} FOR UPDATE")

                            .Include(sh => sh.Appointment)

                            .Include(sh => sh.Status)

                            .Include(sh => sh.SearchService)

                                .ThenInclude(ss => ss.ExpertProfile)

                            .FirstOrDefaultAsync();



                        if (searchHire == null)

                            throw new ArgumentException("SearchHire not found");



                        // Ô£à VALIDACI├ôN CR├ìTICA: Verificar que el SearchHire NO est├® finalizado

                        if (searchHire.Status?.IsFinalizationStatus == true)

                        {

                            var searchHireStatus = searchHire.Status?.StatusValue ?? "unknown";

                            throw new InvalidOperationException(

                                $"No se puede crear una cita cuando el servicio est├í en estado de finalizaci├│n '{searchHireStatus}'. " +

                                $"El servicio debe estar activo para poder crear citas."

                            );

                        }



                        // Ô£à VALIDACI├ôN: Verificar que no tenga ya una cita (con el bloqueo activo para evitar race conditions)

                        if (searchHire.Appointment != null)

                            throw new InvalidOperationException("SearchHire already has an appointment");



                        // Ô£à MEJORA: Obtener el estado "awaiting_appointment" usando cache
                        var awaitingStatusId = await GetStatusIdByValueAsync(
                            AppointmentStatus.AwaitingAppointment.ToStringValue(), 
                            "AppointmentStatus"
                        );



                        // Ô£à VALIDACI├ôN: Verificar que la cita tenga al menos 24 horas de anticipaci├│n

                        var proposedDateTime = DateTime.SpecifyKind(dto.ProposedDate, DateTimeKind.Utc).Date + dto.ProposedTime;

                        // 🔧 FIX zona horaria: comparar contra UTC REAL (la cita se guarda en hora local del experto).
                        var proposedUtc = GetAppointmentUtc(dto.ProposedDate, dto.ProposedTime, searchHire.ExpertTimezone);
                        var timeUntilAppointment = proposedUtc - DateTime.UtcNow;

                        

                        if (timeUntilAppointment.TotalHours < 24)

                        {

                            throw new InvalidOperationException(

                                $"Las citas deben crearse con al menos 24 horas de anticipaci├│n. " +

                                $"Tiempo restante: {timeUntilAppointment.TotalHours:F1} horas. " +

                                $"Fecha/hora propuesta: {proposedDateTime:dd/MM/yyyy HH:mm} UTC"

                            );

                        }



                        // Ô£à VALIDACI├ôN: Verificar que la ubicaci├│n propuesta est├® dentro del rango del experto

                        await ValidateAppointmentLocationAsync(searchHire, dto.Latitude, dto.Longitude);



                        // Ô£à VALIDACI├ôN: Verificar que la fecha/hora propuesta est├® dentro del horario de disponibilidad del experto

                        await ValidateAppointmentAvailabilityAsync(searchHire, proposedDateTime);



                        // Crear la cita dentro de la transacci├│n

                        var appointment = new Appointment

                        {

                            SearchHireId = dto.SearchHireId,

                            StatusId = awaitingStatusId,

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

                        // Ô£à Crear timer para propuesta del cliente (24 horas)
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
                        // ✅ Usar método wrapper con nombre descriptivo para Hangfire
                        var jobId = BackgroundJob.Schedule<IAppointmentService>(
                            service => service.ProcessProposalTimerAsync(proposalTimer.Id),
                            proposalTimer.EndTime - DateTime.UtcNow
                        );

                        // Guardar el JobId en el timer
                        proposalTimer.HangfireJobId = jobId;
                        await _context.SaveChangesAsync();

                        // Commit de la transacci├│n

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

                    catch

                    {

                        // Rollback en caso de error

                        await transaction.RollbackAsync();

                        throw;

                    }

                });

            }

            catch (Exception ex)

            {

                throw;

            }

        }



        public async Task<AppointmentDto> ProposeAppointmentAsync(int searchHireId, ProposeAppointmentDto dto, int userId)

        {

            try

            {

                // Ô£à CORRECCI├ôN: Usar la estrategia de ejecuci├│n para manejar transacciones con reintentos (NpgsqlRetryingExecutionStrategy)

                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>

                {

                    // Ô£à PROTECCI├ôN: Abrir transacci├│n ANTES de cualquier operaci├│n para evitar race conditions

                    using var transaction = await _context.Database.BeginTransactionAsync();

                    try

                    {

                        // Ô£à PROTECCI├ôN: Usar row-level locking dentro de la transacci├│n

                        // Intentar obtener la cita con FOR UPDATE (si existe)

                var appointment = await _context.Appointments

                            .FromSqlInterpolated($"SELECT *, xmin FROM \"Appointments\" WHERE \"SearchHireId\" = {searchHireId} FOR UPDATE")

                    .Include(a => a.SearchHire)

                        .ThenInclude(sh => sh.Status)

                    .Include(a => a.Status)

                            .FirstOrDefaultAsync();

                        // Ô£à VALIDACI├ôN CR├ìTICA: Si la cita existe, verificar que el SearchHire NO est├® finalizado
                        if (appointment != null && appointment.SearchHire?.Status?.IsFinalizationStatus == true)
                        {
                            var searchHireStatus = appointment.SearchHire.Status?.StatusValue ?? "unknown";
                            throw new InvalidOperationException(
                                $"No se puede proponer una cita cuando el servicio est├í en estado de finalizaci├│n '{searchHireStatus}'. " +
                                $"El servicio debe estar activo para poder proponer citas."
                            );
                        }

                        // Si no existe la cita, crearla autom├íticamente dentro de la misma transacci├│n

                if (appointment == null)

                {

                    // Ô£à CORRECCI├ôN: Cargar SearchHire con FOR UPDATE para mantener consistencia en la transacci├│n
                    var searchHire = await _context.SearchHires
                        .FromSqlInterpolated($"SELECT *, xmin FROM \"SearchHires\" WHERE \"Id\" = {searchHireId} FOR UPDATE")
                        .Include(sh => sh.SearchService)
                            .ThenInclude(ss => ss.ExpertProfile)
                        .Include(sh => sh.Status)
                        .FirstOrDefaultAsync();



                    if (searchHire == null)

                        throw new ArgumentException("SearchHire not found");



                    // Verificar que el usuario es el cliente

                    if (searchHire.ClientId != userId)

                        throw new UnauthorizedAccessException("Only the client can propose appointments");

                    // Ô£à VALIDACI├ôN CR├ìTICA: Verificar que el SearchHire NO est├® finalizado
                    if (searchHire.Status?.IsFinalizationStatus == true)
                    {
                        var searchHireStatus = searchHire.Status?.StatusValue ?? "unknown";
                        throw new InvalidOperationException(
                            $"No se puede proponer una cita cuando el servicio est├í en estado de finalizaci├│n '{searchHireStatus}'. " +
                            $"El servicio debe estar activo para poder proponer citas."
                        );
                    }

                    // Ô£à VALIDACI├ôN REMOVIDA: Permitir continuar el flujo incluso si la cuenta cambia a Deauthorized

                    // La validaci├│n de Stripe solo se aplica al CREAR contrataciones, no al continuar el flujo



                    // Ô£à MEJORA: Obtener el estado "awaiting_appointment" usando cache
                    var awaitingStatusId = await GetStatusIdByValueAsync(
                        AppointmentStatus.AwaitingAppointment.ToStringValue(), 
                        "AppointmentStatus"
                    );



                            // Crear la cita dentro de la transacci├│n

                    appointment = new Appointment

                    {

                        SearchHireId = searchHireId,

                        StatusId = awaitingStatusId,

                        CreatedAt = DateTime.UtcNow,

                        UpdatedAt = DateTime.UtcNow

                    };



                    _context.Appointments.Add(appointment);
                    // Ô£à CORRECCI├ôN: Hacer SaveChanges para obtener el Id de la cita antes de recargarla
                    await _context.SaveChangesAsync();

                    // Ô£à Crear timer para propuesta del cliente (24 horas)
                    // Cuando se crea la cita autom├íticamente, el estado es "awaiting_appointment", 
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
                    // ✅ Usar método wrapper con nombre descriptivo para Hangfire
                    var jobId = BackgroundJob.Schedule<IAppointmentService>(
                        service => service.ProcessProposalTimerAsync(proposalTimer.Id),
                        proposalTimer.EndTime - DateTime.UtcNow
                    );

                    // Guardar el JobId en el timer
                    proposalTimer.HangfireJobId = jobId;
                    await _context.SaveChangesAsync();

                    // Ô£à Recargar la cita con las relaciones usando FOR UPDATE para mantener el bloqueo
                    // Esto asegura que el estado se carga correctamente y se mantiene el bloqueo de fila
                    appointment = await _context.Appointments
                        .FromSqlInterpolated($"SELECT *, xmin FROM \"Appointments\" WHERE \"Id\" = {appointment.Id} FOR UPDATE")
                        .Include(a => a.SearchHire)
                            .ThenInclude(sh => sh.Status)
                        .Include(a => a.Status)
                        .FirstAsync();

                }



                // Verificar que el usuario es el cliente

                if (appointment.SearchHire.ClientId != userId)

                    throw new UnauthorizedAccessException("Only the client can propose appointments");



                        // Ô£à VALIDACI├ôN CR├ìTICA: Solo se puede proponer si est├í en "awaiting_appointment", "appointment_rejected" o estados de cancelaci├│n (primera cancelaci├│n)

                        // No se puede proponer si ya est├í propuesta, confirmada o cancelada (segunda cancelaci├│n)

                        var currentStatus = appointment.Status?.StatusValue ?? string.Empty;

                        var validStatesForPropose = new[] { 
                            AppointmentStatus.AwaitingAppointment.ToStringValue(), 
                            AppointmentStatus.AppointmentRejected.ToStringValue(),
                            AppointmentStatus.AppointmentCancelledByClient.ToStringValue(),      // Primera cancelaci├│n del cliente
                            AppointmentStatus.AppointmentCancelledByExpert.ToStringValue()        // Primera cancelaci├│n del experto
                        };

                        // Ô£à PROTECCI├ôN: Verificar que no se haya procesado ya (evitar doble click/race condition)
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



                // Ô£à OPTIMIZACI├ôN: Obtener el estado "appointment_proposed" usando cache (m├ís eficiente)
                var proposedStatusId = await GetStatusIdByValueAsync(
                    AppointmentStatus.AppointmentProposed.ToStringValue(), 
                    "AppointmentStatus"
                );

                if (proposedStatusId == 0)
                    throw new InvalidOperationException("Appointment proposed status not found");



                // Ô£à VALIDACI├ôN: Verificar que la cita tenga al menos 24 horas de anticipaci├│n

                var proposedDateTime = DateTime.SpecifyKind(dto.ProposedDate, DateTimeKind.Utc).Date + dto.ProposedTime;

                // 🔧 FIX zona horaria: comparar contra UTC REAL (la cita se guarda en hora local del experto).
                var proposedUtc = GetAppointmentUtc(dto.ProposedDate, dto.ProposedTime, appointment.SearchHire?.ExpertTimezone);
                var timeUntilAppointment = proposedUtc - DateTime.UtcNow;

                

                if (timeUntilAppointment.TotalHours < 24)

                {

                    throw new InvalidOperationException(

                        $"Las citas deben proponerse con al menos 24 horas de anticipaci├│n. " +

                        $"Tiempo restante: {timeUntilAppointment.TotalHours:F1} horas. " +

                        $"Fecha/hora propuesta: {proposedDateTime:dd/MM/yyyy HH:mm} UTC"

                    );

                }



                // Ô£à VALIDACI├ôN: Verificar que la ubicaci├│n propuesta est├® dentro del rango del experto

                await ValidateAppointmentLocationAsync(appointment.SearchHire, dto.Latitude, dto.Longitude);



                        // Ô£à VALIDACI├ôN: Verificar que la fecha/hora propuesta est├® dentro del horario de disponibilidad del experto

                        await ValidateAppointmentAvailabilityAsync(appointment.SearchHire, proposedDateTime);



                // Actualizar la cita - asegurar que los DateTime tengan Kind=UTC

                appointment.ProposedDate = DateTime.SpecifyKind(dto.ProposedDate, DateTimeKind.Utc);

                appointment.ProposedTime = dto.ProposedTime;

                appointment.Location = dto.Location;

                appointment.Latitude = dto.Latitude;

                appointment.Longitude = dto.Longitude;

                appointment.DoorNumber = dto.DoorNumber;

                appointment.OwnerPhone = dto.OwnerPhone;

                appointment.SiteDetails = dto.SiteDetails;

                appointment.StatusId = proposedStatusId;

                appointment.LastProposalAt = DateTime.UtcNow;

                appointment.UpdatedAt = DateTime.UtcNow;



                // Ô£à Cancelar timers de propuesta activos antes de crear el timer de respuesta
                var proposalTimers = await _context.AppointmentTimers
                    .Where(t => t.AppointmentId == appointment.Id && 
                               t.TimerType == "proposal" && 
                               !t.IsExpired)
                    .ToListAsync();

                // Ô£à OPTIMIZACI├ôN: Almacenar JobIds de Hangfire para cancelarlos despu├®s del commit
                var hangfireJobIdsToCancel = new List<string>();
                foreach (var timer in proposalTimers)
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    
                    // Ô£à Almacenar JobId para cancelarlo despu├®s del commit (evitar operaciones Hangfire dentro de transacci├│n)
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

                // Ô£à OPTIMIZACI├ôN: Un solo SaveChangesAsync para todas las operaciones de BD
                await _context.SaveChangesAsync();

                        // Ô£à COMMIT: Confirmar la transacci├│n
                        await transaction.CommitAsync();

                        // Ô£à CANCELAR jobs de Hangfire DESPU├ëS del commit (mejor pr├íctica: operaciones externas fuera de transacci├│n)
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

                        // Ô£à Programar scheduled job para cuando expire el timer de respuesta (24 horas) - DESPU├ëS del commit
                        // ✅ Usar método wrapper con nombre descriptivo para Hangfire
                        var responseJobId = BackgroundJob.Schedule<IAppointmentService>(
                            service => service.ProcessResponseTimerAsync(responseTimer.Id),
                            responseTimer.EndTime - DateTime.UtcNow
                        );

                        // Guardar el JobId en el timer (fuera de la transacci├│n)
                        responseTimer.HangfireJobId = responseJobId;
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



                // Ô£à Enviar mensaje al chat con el cambio de estado (despu├®s del commit)

                await SendAppointmentStatusChangeMessageAsync(

                    appointment.SearchHireId, 

                    AppointmentStatus.AppointmentProposed.ToStringValue(), 

                    userId

                );

                // Ô£à Notificar al experto sobre la nueva propuesta de cita
                if (updatedAppointment.SearchHire?.ExpertId.HasValue == true && updatedAppointment.SearchHire.ExpertId.Value > 0)
                {
                    if (updatedAppointment.ProposedDate.HasValue && updatedAppointment.ProposedTime.HasValue)
                    {
                        var appointmentDateTime = updatedAppointment.ProposedDate.Value.Date + updatedAppointment.ProposedTime.Value;
                        var formattedDate = appointmentDateTime.ToString("dd/MM/yyyy HH:mm");
                    
                    await _loggingService.LogInfoAsync(
                        message: "Nueva propuesta de cita recibida",
                        details: $"El cliente ha propuesto una cita para el {formattedDate} en {updatedAppointment.Location}. Tienes 24 horas para aceptar o rechazar.",
                        userId: updatedAppointment.SearchHire.ExpertId.Value,
                        source: "AppointmentService.ProposeAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: updatedAppointment.Id,
                        notifyUser: true
                    );
                    }
                }

                return MapToDto(updatedAppointment);
                    }
                    catch (Exception innerEx)

                    {

                        // Ô£à ROLLBACK: Revertir la transacci├│n en caso de error

                        await transaction.RollbackAsync();

                        throw;

                    }

                });

            }

            catch (Exception ex)

            {

                // ÔÜá´©Å LOG WARNING: Error general proponiendo cita (no afecta dinero, usuario puede reintentar)

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
                // Ô£à LOG: Inicio del proceso de confirmaci├│n
                await _loggingService.LogInfoAsync(
                    message: "Iniciando confirmaci├│n de cita",
                    details: $"Usuario {userId} intentando confirmar cita {dto.AppointmentId}",
                    userId: userId,
                    source: "AppointmentService.ConfirmAppointmentAsync",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId,
                    notifyUser: false
                );

                // Ô£à CORRECCI├ôN: Usar la estrategia de ejecuci├│n para manejar transacciones con reintentos (NpgsqlRetryingExecutionStrategy)
                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>
                {
                    // Ô£à PROTECCI├ôN: Abrir transacci├│n ANTES del FOR UPDATE para que el bloqueo funcione
                    using (var transaction = await _context.Database.BeginTransactionAsync())
                    {
                        try
                        {
                            // Ô£à PROTECCI├ôN: Usar row-level locking DENTRO de la transacci├│n para evitar doble procesamiento
                            var appointment = await _context.Appointments
                                .FromSqlInterpolated($"SELECT *, xmin FROM \"Appointments\" WHERE \"Id\" = {dto.AppointmentId} FOR UPDATE")
                                .Include(a => a.SearchHire)
                                    .ThenInclude(sh => sh.Status)
                                .Include(a => a.Status)
                                .FirstOrDefaultAsync();

                            if (appointment == null)
                                throw new ArgumentException("Appointment not found");

                            var currentStatus = appointment.Status?.StatusValue ?? string.Empty;

                            // Ô£à LOG: Cita cargada
                            await _loggingService.LogInfoAsync(
                                message: "Cita cargada para confirmaci├│n",
                                details: $"Cita {dto.AppointmentId} cargada. Estado actual: {currentStatus}, SearchHireId: {appointment.SearchHireId}, ExpertId: {appointment.SearchHire.ExpertId}",
                                userId: userId,
                                source: "AppointmentService.ConfirmAppointmentAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: dto.AppointmentId,
                                notifyUser: false
                            );

                            // Ô£à VALIDACI├ôN: Verificar que el usuario es el experto
                            if (appointment.SearchHire.ExpertId != userId)
                                throw new UnauthorizedAccessException("Only the expert can confirm appointments");

                            // Ô£à VALIDACI├ôN CR├ìTICA: Verificar que el SearchHire NO est├® finalizado
                            if (appointment.SearchHire.Status?.IsFinalizationStatus == true)
                            {
                                var searchHireStatus = appointment.SearchHire.Status?.StatusValue ?? "unknown";
                                throw new InvalidOperationException(
                                    $"No se puede confirmar una cita cuando el servicio est├í en estado de finalizaci├│n '{searchHireStatus}'. " +
                                    $"El servicio debe estar activo para poder confirmar citas."
                                );
                            }

                            // Ô£à VALIDACI├ôN CR├ìTICA: Solo se puede confirmar si la cita est├í en estado "appointment_proposed"
                            if (currentStatus != "appointment_proposed")
                            {
                                throw new InvalidOperationException(
                                    $"No se puede confirmar una cita en estado '{currentStatus}'. " +
                                    $"Solo se pueden confirmar citas en estado 'appointment_proposed' (cita propuesta por el cliente)."
                                );
                            }

                            // Ô£à PROTECCI├ôN: Verificar que no se haya procesado ya (evitar doble click)
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
                                
                                // Ô£à CANCELAR job de Hangfire si existe
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

                            // Ô£à Programar job para cambiar a awaiting_report 3 horas despu├®s de la hora de la cita
                            if (appointment.ProposedDate.HasValue && appointment.ProposedTime.HasValue)
                            {
                                var appointmentDateTime = appointment.ProposedDate.Value.Date + appointment.ProposedTime.Value;
                            // 🔧 FIX zona horaria: el +3h se calcula sobre la hora UTC REAL de la cita (no la local-etiquetada).
                            var appointmentUtc = GetAppointmentUtc(appointment.ProposedDate.Value, appointment.ProposedTime.Value, appointment.SearchHire?.ExpertTimezone);
                            var endTimeUtc = appointmentUtc.AddHours(3);
                            var timeUntil3HoursAfter = endTimeUtc - DateTime.UtcNow;
                            
                            if (timeUntil3HoursAfter.TotalSeconds > 0) // Solo programar si a├║n no han pasado las 3 horas
                            {
                                // Crear timer para la transici├│n a awaiting_report (3 horas despu├®s de la cita)
                                var awaitingReportTransitionTimer = new AppointmentTimer
                                {
                                    AppointmentId = appointment.Id,
                                    TimerType = "awaiting_report_transition",
                                    StartTime = DateTime.UtcNow,
                                    EndTime = endTimeUtc,
                                    IsExpired = false,
                                    CreatedAt = DateTime.UtcNow
                                };

                                _context.AppointmentTimers.Add(awaitingReportTransitionTimer);
                                await _context.SaveChangesAsync();

                                // Programar scheduled job para cuando expire el timer (3 horas despu├®s de la cita)
                                var jobId = BackgroundJob.Schedule<IAppointmentService>(
                                    service => service.ProcessAppointmentToAwaitingReportAsync(appointment.Id),
                                    timeUntil3HoursAfter
                                );

                                // Guardar el JobId en el timer
                                awaitingReportTransitionTimer.HangfireJobId = jobId;
                                await _context.SaveChangesAsync();
                                }
                                else
                                {
                                    // 🔧 FIX (#1, defensa en profundidad): la cita se confirmó con su hora+3h ya
                                    // pasada (solo posible bajo un retraso prolongado de los workers de Hangfire,
                                    // ya que ProposeAppointment exige ≥24h de antelación). No hay ventana para
                                    // programar el timer, así que encolamos la transición INMEDIATA para no dejar
                                    // los fondos atascados en appointment_confirmed. ProcessAppointmentToAwaitingReportAsync
                                    // revalida estado (idempotente) y es seguro sin timer previo.
                                    BackgroundJob.Enqueue<IAppointmentService>(
                                        s => s.ProcessAppointmentToAwaitingReportAsync(appointment.Id));
                                }
                            }

                            // Ô£à LOG: Antes del commit
                            await _loggingService.LogInfoAsync(
                                message: "Preparando commit de confirmaci├│n de cita",
                                details: $"Cita {dto.AppointmentId} lista para commit. Nuevo estado: appointment_confirmed",
                                userId: userId,
                                source: "AppointmentService.ConfirmAppointmentAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: dto.AppointmentId,
                                notifyUser: false
                            );

                            // Ô£à COMMIT: Confirmar la transacci├│n
                            await transaction.CommitAsync();

                            // Ô£à LOG: Commit exitoso
                            await _loggingService.LogInfoAsync(
                                message: "Commit de confirmaci├│n de cita exitoso",
                                details: $"Cita {dto.AppointmentId} confirmada exitosamente en la base de datos",
                                userId: userId,
                                source: "AppointmentService.ConfirmAppointmentAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: dto.AppointmentId,
                                notifyUser: false
                            );
                        }
                        catch (Exception innerEx)
                        {
                            // Ô£à LOG: Error en transacci├│n
                            await _loggingService.LogErrorAsync(
                                message: "Error en transacci├│n al confirmar cita",
                                details: $"Error al confirmar cita {dto.AppointmentId} dentro de la transacci├│n. Error: {innerEx.GetType().Name} - {innerEx.Message}",
                                userId: userId,
                                source: "AppointmentService.ConfirmAppointmentAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: dto.AppointmentId,
                                notifyUser: false
                            );

                            // Ô£à ROLLBACK: Revertir la transacci├│n en caso de error
                            await transaction.RollbackAsync();
                            throw;
                        }
                    } // Cierre del using var transaction

                // Ô£à C├ôDIGO POST-COMMIT: Ejecutar fuera de la transacci├│n para evitar errores de NpgsqlTransaction
                // ÔÜá´©Å IMPORTANTE: Si estas operaciones fallan, no deben afectar la respuesta ya que la transacci├│n principal ya se complet├│
                AppointmentDto result;
                try
                {
                    // Ô£à LOG: Iniciando operaciones post-commit
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

                    // Ô£à LOG: Enviando mensaje al chat
                    await _loggingService.LogInfoAsync(
                        message: "Enviando mensaje al chat",
                        details: $"Enviando mensaje de cambio de estado al chat para SearchHire {updatedAppointment.SearchHireId}",
                        userId: userId,
                        source: "AppointmentService.ConfirmAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: dto.AppointmentId,
                        notifyUser: false
                    );

                    // Ô£à Enviar mensaje al chat con el cambio de estado (despu├®s del commit)
                    await SendAppointmentStatusChangeMessageAsync(
                        updatedAppointment.SearchHireId, 
                        AppointmentStatus.AppointmentConfirmed.ToStringValue(), 
                        userId
                    );

                    // Ô£à LOG: Mensaje al chat enviado
                    await _loggingService.LogInfoAsync(
                        message: "Mensaje al chat enviado",
                        details: $"Mensaje de cambio de estado enviado exitosamente al chat",
                        userId: userId,
                        source: "AppointmentService.ConfirmAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: dto.AppointmentId,
                        notifyUser: false
                    );

                    // Ô£à Notificar al cliente que la cita fue confirmada por el experto
                    if (updatedAppointment.SearchHire?.ClientId != null)
                    {
                        // Formatear fecha y hora correctamente (combinar Date + TimeSpan para obtener DateTime)
                        if (updatedAppointment.ProposedDate.HasValue && updatedAppointment.ProposedTime.HasValue)
                        {
                            var appointmentDateTime = updatedAppointment.ProposedDate.Value.Date + updatedAppointment.ProposedTime.Value;
                            var formattedDateTime = appointmentDateTime.ToString("dd/MM/yyyy HH:mm");
                        
                        // Ô£à LOG: Enviando notificaci├│n al cliente
                        await _loggingService.LogInfoAsync(
                            message: "Enviando notificaci├│n al cliente",
                            details: $"Preparando notificaci├│n para cliente {updatedAppointment.SearchHire.ClientId}. Fecha formateada: {formattedDateTime}",
                            userId: userId,
                            source: "AppointmentService.ConfirmAppointmentAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: dto.AppointmentId,
                            notifyUser: false
                        );

                        await _loggingService.LogInfoAsync(
                            message: "Cita confirmada por el experto",
                            details: $"El experto confirm├│ la cita para el {formattedDateTime} en {updatedAppointment.Location}.",
                            userId: updatedAppointment.SearchHire.ClientId,
                            source: "AppointmentService.ConfirmAppointmentAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: updatedAppointment.Id,
                            notifyUser: true
                        );
                        }

                        // Ô£à LOG: Notificaci├│n al cliente enviada
                        await _loggingService.LogInfoAsync(
                            message: "Notificaci├│n al cliente enviada",
                            details: $"Notificaci├│n enviada exitosamente al cliente {updatedAppointment.SearchHire.ClientId}",
                            userId: userId,
                            source: "AppointmentService.ConfirmAppointmentAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: dto.AppointmentId,
                            notifyUser: false
                        );
                    }

                    // Ô£à LOG: Mapeando a DTO
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

                    // Ô£à LOG: Operaciones post-commit completadas
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
                    // Ô£à LOG: Error en operaciones post-commit
                    await _loggingService.LogWarningAsync(
                        message: "Error en operaciones post-commit",
                        details: $"Error en operaciones post-commit para cita {dto.AppointmentId}. Error: {postCommitEx.GetType().Name} - {postCommitEx.Message}. StackTrace: {postCommitEx.StackTrace}",
                        userId: userId,
                        source: "AppointmentService.ConfirmAppointmentAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: dto.AppointmentId,
                        notifyUser: false
                    );

                    // ÔÜá´©Å LOG WARNING: Error en operaciones post-commit (la transacci├│n principal ya se complet├│)
                    // Intentar cargar la cita de forma m├ís simple para devolver el resultado
                    try
                    {
                        // Ô£à LOG: Intentando fallback
                        await _loggingService.LogInfoAsync(
                            message: "Intentando fallback de carga de cita",
                            details: $"Intentando cargar cita {dto.AppointmentId} con relaciones m├¡nimas",
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
                            details: $"La cita {dto.AppointmentId} se confirm├│ exitosamente, pero hubo un error en operaciones post-commit (mensajes/notificaciones). " +
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
                        // Si incluso el fallback falla, intentar una carga m├¡nima
                        try
                        {
                            var minimalAppointment = await _context.Appointments
                                .AsNoTracking()
                                .FirstAsync(a => a.Id == dto.AppointmentId);

                            await _loggingService.LogWarningAsync(
                                message: "Error en operaciones post-commit al confirmar cita - usando carga m├¡nima",
                                details: $"La cita {dto.AppointmentId} se confirm├│ exitosamente, pero hubo errores en operaciones post-commit. " +
                                        $"Error original: {postCommitEx.GetType().Name} - {postCommitEx.Message}. " +
                                        $"Error fallback: {fallbackEx.GetType().Name} - {fallbackEx.Message}. " +
                                        $"Se devuelve resultado con carga m├¡nima. La cita fue confirmada correctamente en la base de datos.",
                                userId: userId,
                                source: "AppointmentService.ConfirmAppointmentAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: dto.AppointmentId,
                                notifyUser: false
                            );

                            // Construir un DTO b├ísico con la informaci├│n m├¡nima disponible
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
                                Status = "appointment_confirmed", // Sabemos que se confirm├│
                                CreatedAt = minimalAppointment.CreatedAt,
                                UpdatedAt = minimalAppointment.UpdatedAt,
                                Timers = new List<AppointmentTimerDto>() // Lista vac├¡a ya que no cargamos relaciones
                            };
                        }
                        catch (Exception minimalEx)
                        {
                            // Solo en este caso extremo lanzar la excepci├│n
                            await _loggingService.LogErrorAsync(
                                message: "Error cr├¡tico al confirmar cita - no se pudo cargar ni m├¡nimamente",
                                details: $"La cita {dto.AppointmentId} se confirm├│ exitosamente en la BD, pero no se pudo cargar para devolver el resultado. " +
                                        $"Error original: {postCommitEx.GetType().Name} - {postCommitEx.Message}. " +
                                        $"Error fallback: {fallbackEx.GetType().Name} - {fallbackEx.Message}. " +
                                        $"Error m├¡nimo: {minimalEx.GetType().Name} - {minimalEx.Message}.",
                                userId: userId,
                                source: "AppointmentService.ConfirmAppointmentAsync",
                                relatedEntityType: "Appointment",
                                relatedEntityId: dto.AppointmentId,
                                notifyUser: false
                            );
                            throw postCommitEx; // Lanzar la excepci├│n original para que el controller la maneje
                        }
                    }
                }

                return result;

                });

            }

            catch (Exception ex)

            {

                // ÔÜá´©Å LOG WARNING: Error general confirmando cita (no afecta dinero, usuario puede reintentar)

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

                // Ô£à CORRECCI├ôN: Usar la estrategia de ejecuci├│n para manejar transacciones con reintentos (NpgsqlRetryingExecutionStrategy)

                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>

                {

                    // Ô£à PROTECCI├ôN: Abrir transacci├│n ANTES del FOR UPDATE para que el bloqueo funcione

                    using var transaction = await _context.Database.BeginTransactionAsync();

                    try

                    {

                        // Ô£à PROTECCI├ôN: Usar row-level locking DENTRO de la transacci├│n para evitar doble procesamiento

                var appointment = await _context.Appointments

                            .FromSqlInterpolated($"SELECT *, xmin FROM \"Appointments\" WHERE \"Id\" = {dto.AppointmentId} FOR UPDATE")

                    .Include(a => a.SearchHire)

                        .ThenInclude(sh => sh.Status)

                    .Include(a => a.Status)

                            .FirstOrDefaultAsync();



                if (appointment == null)

                {

                    throw new ArgumentException("Appointment not found");

                }

                        // Ô£à VALIDACI├ôN: Verificar que el usuario es el experto

                if (appointment.SearchHire.ExpertId != userId)

                {

                    throw new UnauthorizedAccessException("Only the expert can reject appointments");

                }

                        // Ô£à VALIDACI├ôN CR├ìTICA: Verificar que el SearchHire NO est├® finalizado
                        if (appointment.SearchHire.Status?.IsFinalizationStatus == true)
                        {
                            var searchHireStatus = appointment.SearchHire.Status?.StatusValue ?? "unknown";
                            throw new InvalidOperationException(
                                $"No se puede rechazar una cita cuando el servicio est├í en estado de finalizaci├│n '{searchHireStatus}'. " +
                                $"El servicio debe estar activo para poder rechazar citas."
                            );
                        }

                        // Ô£à VALIDACI├ôN CR├ìTICA: Solo se puede rechazar si la cita est├í en estado "appointment_proposed"

                        // No se puede rechazar si est├í en "awaiting_appointment" (no hay propuesta a├║n) o en otros estados finales

                        var currentStatus = appointment.Status?.StatusValue ?? string.Empty;

                        if (currentStatus != "appointment_proposed")

                        {

                            throw new InvalidOperationException(

                                $"No se puede rechazar una cita en estado '{currentStatus}'. " +

                                $"Solo se pueden rechazar citas en estado 'appointment_proposed' (cita propuesta por el cliente)."

                            );

                        }



                        // Ô£à PROTECCI├ôN: Verificar que no se haya procesado ya (evitar doble click)

                        // Si ya est├í en un estado de rechazo o cancelaci├│n, no permitir otra operaci├│n

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

                // ­ƒöì LOGS DETALLADOS: Analizar el estado actual

                // Determinar el estado seg├║n el n├║mero de rechazos

                string statusValue;

                bool isSecondRejection = appointment.RejectionCount >= 1;

                

                

                if (isSecondRejection)

                {

                    // Segundo rechazo o m├ís - cancelar por rechazos m├║ltiples

                    // Ô£à CORRECCI├ôN: Usar el estado correcto para rechazo (no cancelaci├│n)

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

                

                // Ô£à CORRECCI├ôN: Incrementar ExpertCancellationCount para segunda cancelaci├│n

                if (isSecondRejection)

                {

                    appointment.ExpertCancellationCount++;

                }

                

                appointment.LastRejectionAt = DateTime.UtcNow;

                appointment.LastResponseAt = DateTime.UtcNow;

                appointment.UpdatedAt = DateTime.UtcNow;

                // Actualizar el SearchHire seg├║n el mapeo de estados

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
                    // ✅ COMPORTAMIENTO ESPERADO: Si NO hay mapeo, NO cambiar el estado del SearchHire
                    // El Appointment.StatusId YA cambió (línea 1478), esto es correcto
                    // El SearchHire NO cambia porque no hay mapeo (comportamiento esperado para estados no finales)
                    // Ejemplos: appointment_rejected (primer rechazo), appointment_cancelled_by_client (primera cancelación)
                    // Estos estados permiten que el cliente proponga otra cita, por lo que el SearchHire debe seguir en "pending"
                    // NO loguear como Warning porque es el comportamiento esperado y correcto
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
                    
                    // Ô£à CANCELAR job de Hangfire si existe
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



                // Ô£à CORRECCI├ôN: Procesar refund autom├ítico para segunda cancelaci├│n

                if (isSecondRejection)

                {

                    try

                    {

                        // ­ƒöì LOG: Verificar configuraci├│n de dinero antes del refund

                        var moneyConfig = await _systemStatusService.GetMoneyDistributionConfigAsync(

                            AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue(), 

                            appointment.SearchHire.SearchService?.CategoryId, 

                            appointment.SearchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);

                        // Orquestar refund+transfer seg├║n configuraci├│n del subestado de finalizaci├│n
                        // Ô£à OPTIMIZACI├ôN: updateState: false porque ya cambiamos el estado arriba (l├¡neas 1466, 1512-1514)

                        var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(

                            appointment.SearchHireId,

                            AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue(),

                            "Segundo rechazo del experto - penalizaci├│n m├íxima",

                            userId,
                            updateState: false);

                        

                        if (refundSuccess)

                        {

                        }

                        else

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

                        // ­ƒÜ¿ LOG CR├ìTICO: Error procesando refund autom├ítico (una sola vez, con informaci├│n completa)

                        await _loggingService.LogCriticalAsync(

                            message: "CRITICAL: Error processing automatic refund during appointment rejection",

                            details: $"Automatic refund failed during appointment rejection for Appointment {appointment.Id} (SearchHire {appointment.SearchHireId}). " +

                                    $"This occurred on second rejection by expert {userId}. " +

                                    $"Error Type: {refundEx.GetType().Name}, Error Message: {refundEx.Message}. " +

                                    $"SearchHire Amount: {appointment.SearchHire?.Amount}Ôé¼, ClientId: {appointment.SearchHire?.ClientId}, ExpertId: {appointment.SearchHire?.ExpertId}. " +

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

                        

                        // No lanzar la excepci├│n para no afectar el flujo principal

                    }

                }

                else

                {
                    // Ô£à Si es primer rechazo, restaurar timer de 24h para que el cliente proponga otra vez
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
                    // ✅ Usar método wrapper con nombre descriptivo para Hangfire
                    var jobId = BackgroundJob.Schedule<IAppointmentService>(
                        service => service.ProcessProposalTimerAsync(proposalTimer.Id),
                        proposalTimer.EndTime - DateTime.UtcNow
                    );
                    
                    // Guardar el JobId en el timer
                    proposalTimer.HangfireJobId = jobId;
                    await _context.SaveChangesAsync();
                }



                        // Ô£à COMMIT: Confirmar la transacci├│n

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



                // Ô£à Enviar mensaje al chat con el cambio de estado (despu├®s del commit)

                // El statusValue se determina seg├║n si es primera o segunda cancelaci├│n

                var statusValueToSend = isSecondRejection 

                    ? AppointmentStatus.AppointmentCancelledByExpertRejection.ToStringValue()

                    : AppointmentStatus.AppointmentRejected.ToStringValue();

                

                await SendAppointmentStatusChangeMessageAsync(

                    appointment.SearchHireId, 

                    statusValueToSend, 

                    userId

                );



                // Ô£à Notificar al cliente sobre el rechazo

                if (isSecondRejection)

                {

                    // Segunda cancelaci├│n - notificar sobre refund autom├ítico

                    await _loggingService.LogWarningAsync(

                        message: "Cita rechazada por segunda vez",

                        details: $"El experto rechaz├│ la propuesta de cita por segunda vez. Se procesar├í tu reembolso autom├íticamente.",

                        userId: appointment.SearchHire.ClientId,

                        source: "AppointmentService.RejectAppointmentAsync",

                        relatedEntityType: "Appointment",

                        relatedEntityId: appointment.Id,

                        notifyUser: true

                    );

                }

                else

                {

                    // Primera cancelaci├│n - notificar que puede proponer otra

                    await _loggingService.LogInfoAsync(

                        message: "Cita rechazada",

                        details: $"El experto rechaz├│ la propuesta de cita. Puedes proponer otra fecha y hora.",

                        userId: appointment.SearchHire.ClientId,

                        source: "AppointmentService.RejectAppointmentAsync",

                        relatedEntityType: "Appointment",

                        relatedEntityId: appointment.Id,

                        notifyUser: true

                    );

                }



                return MapToDto(updatedAppointment);

                    }

                    catch (Exception innerEx)

                    {

                        // Ô£à ROLLBACK: Revertir la transacci├│n en caso de error

                        await transaction.RollbackAsync();

                        throw;

                    }

                });

            }

            catch (Exception ex)

            {

                // ÔÜá´©Å LOG WARNING: Error general rechazando cita (el refund tiene su propio CRITICAL si falla, usuario puede reintentar)

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

                // Ô£à CORRECCI├ôN: Usar la estrategia de ejecuci├│n para manejar transacciones con reintentos (NpgsqlRetryingExecutionStrategy)

                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>

                {

                    // Ô£à PROTECCI├ôN: Abrir transacci├│n ANTES del FOR UPDATE para que el bloqueo funcione

                    using var transaction = await _context.Database.BeginTransactionAsync();

                    try

                    {

                        // Ô£à PROTECCI├ôN: Usar row-level locking DENTRO de la transacci├│n para evitar doble procesamiento

                var appointment = await _context.Appointments

                            .FromSqlInterpolated($"SELECT *, xmin FROM \"Appointments\" WHERE \"Id\" = {dto.AppointmentId} FOR UPDATE")

                    .Include(a => a.SearchHire)

                        .ThenInclude(sh => sh.Status)

                    .Include(a => a.Status)

                            .FirstOrDefaultAsync();



                if (appointment == null)

                    throw new ArgumentException("Appointment not found");



                        var currentStatus = appointment.Status?.StatusValue ?? string.Empty;

                        // Ô£à VALIDACI├ôN: Verificar que el usuario es el cliente o el experto

                if (appointment.SearchHire.ClientId != userId && appointment.SearchHire.ExpertId != userId)

                    throw new UnauthorizedAccessException("Only the client or expert can cancel appointments");

                        // Ô£à VALIDACI├ôN CR├ìTICA: Verificar que el SearchHire NO est├® finalizado
                        if (appointment.SearchHire.Status?.IsFinalizationStatus == true)
                        {
                            var searchHireStatus = appointment.SearchHire.Status?.StatusValue ?? "unknown";
                            throw new InvalidOperationException(
                                $"No se puede cancelar una cita cuando el servicio est├í en estado de finalizaci├│n '{searchHireStatus}'. " +
                                $"El servicio debe estar activo para poder cancelar citas."
                            );
                        }

                        // Ô£à VALIDACI├ôN CR├ìTICA: No se puede cancelar si est├í en "awaiting_appointment" (no hay propuesta a├║n)

                        // Solo se puede cancelar si hay una propuesta o cita confirmada

                        if (currentStatus == AppointmentStatus.AwaitingAppointment.ToStringValue())

                        {

                            throw new InvalidOperationException(

                                "No se puede cancelar una cita en estado 'awaiting_appointment'. " +

                                "En este estado no hay ninguna propuesta de cita vigente. " +

                                "Solo se pueden cancelar citas que ya han sido propuestas o confirmadas."

                            );

                        }



                        // Ô£à PROTECCI├ôN: Verificar que no se haya procesado ya (evitar doble click)

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

                                $"La cita ya est├í cancelada o finalizada (estado: '{currentStatus}'). " +

                                $"No se puede cancelar nuevamente."

                            );

                        }



                        // Ô£à VALIDACI├ôN: Solo se pueden cancelar citas en estados v├ílidos

                        // Estados v├ílidos: SOLO appointment_confirmed (cuando la cita ya est├í confirmada)
                        // - appointment_proposed: El experto puede rechazar/aprobar, no necesita cancelar
                        // - appointment_rejected: El cliente puede proponer nueva cita, no necesita cancelar
                        // - appointment_confirmed: No hay otra acci├│n disponible, cancelar es la ├║nica opci├│n

                        var validStatesForCancel = new[] { 

                            "appointment_confirmed" // Solo cuando est├í confirmada

                        };

                        

                        if (!validStatesForCancel.Contains(currentStatus))

                        {

                            throw new InvalidOperationException(

                                $"No se puede cancelar una cita en estado '{currentStatus}'. " +

                                $"Solo se pueden cancelar citas en estados: {string.Join(", ", validStatesForCancel)}."

                            );

                        }

                        // Ô£à VALIDACI├ôN: No se puede cancelar si quedan menos de 12 horas antes de la cita
                        // Solo aplicar si la cita est├í confirmada (appointment_confirmed)
                        if (currentStatus == "appointment_confirmed")
                        {
                            // Verificar que la fecha propuesta sea v├ílida (no sea DateTime.MinValue o default)
                            if (appointment.ProposedDate.HasValue && appointment.ProposedTime.HasValue && appointment.ProposedDate.Value != default(DateTime) && appointment.ProposedDate.Value > DateTime.MinValue)
                            {
                                var appointmentDateTime = appointment.ProposedDate.Value.Date + appointment.ProposedTime.Value;
                                // 🔧 FIX zona horaria: comparar contra UTC REAL.
                                var appointmentUtc = GetAppointmentUtc(appointment.ProposedDate.Value, appointment.ProposedTime.Value, appointment.SearchHire?.ExpertTimezone);
                                var timeUntilAppointment = appointmentUtc - DateTime.UtcNow;
                                
                                if (timeUntilAppointment.TotalHours < 12)
                                {
                                    string errorMessage;
                                    if (timeUntilAppointment.TotalHours < 0)
                                    {
                                        // La cita ya pas├│
                                        errorMessage = $"No se puede cancelar una cita que ya ha pasado. " +
                                                      $"La cita era el {appointmentDateTime:dd/MM/yyyy HH:mm} UTC y ya ha transcurrido.";
                                    }
                                    else
                                    {
                                        // La cita est├í muy cerca (menos de 12h)
                                        var hoursRemaining = (int)Math.Ceiling(timeUntilAppointment.TotalHours);
                                        errorMessage = $"No se puede cancelar una cita con menos de 12 horas de antelaci├│n. " +
                                                      $"Quedan aproximadamente {hoursRemaining} horas hasta la cita " +
                                                      $"(programada para el {appointmentDateTime:dd/MM/yyyy HH:mm} UTC).";
                                    }
                                    
                                    throw new InvalidOperationException(errorMessage);
                                }
                            }
                        }



                // Determinar el estado de cancelaci├│n seg├║n qui├®n cancela y el n├║mero de cancelaciones espec├¡ficas

                string statusValue;

                if (appointment.SearchHire.ClientId == userId)

                {

                    // Cliente cancela - verificar si es primera o segunda cancelaci├│n del cliente

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

                    // Experto cancela - verificar si es primera o segunda cancelaci├│n del experto

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

                

                // Incrementar contadores espec├¡ficos seg├║n qui├®n cancela

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



                // Actualizar el SearchHire seg├║n el mapeo de estados

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
                    // ✅ COMPORTAMIENTO ESPERADO: Si NO hay mapeo, NO cambiar el estado del SearchHire
                    // El Appointment.StatusId YA cambió (línea 2208), esto es correcto
                    // El SearchHire NO cambia porque no hay mapeo (comportamiento esperado para estados no finales)
                    // Ejemplos: appointment_cancelled_by_client (primera cancelación), appointment_cancelled_by_expert (primera cancelación)
                    // Estos estados permiten que el cliente proponga otra cita, por lo que el SearchHire debe seguir en "pending"
                    // NO loguear como Warning porque es el comportamiento esperado y correcto
                }

                // Ô£à CR├ìTICO: Guardar estados ANTES de procesar dinero
                // El estado debe cambiar SIEMPRE, incluso si falla el procesamiento de dinero
                await _context.SaveChangesAsync();

                // Si el subestado NO es de finalizaci├│n, no invocar orquestador (primera cancelaci├│n, reprogramable)

                if (cancelledStatus.IsFinalizationStatus)

                {

                    // Orquestar movimientos de dinero seg├║n el estado determinado (subestado ÔåÆ fallback final), respetando granularidad
                    // Ô£à OPTIMIZACI├ôN: updateState: false porque ya cambiamos el estado arriba (l├¡neas 2225, 2285)
                    // Ô£à CR├ìTICO: SaveChanges ya se hizo arriba, estados ya est├ín guardados

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
                            // ✅ ALERTA: el estado YA se guardó arriba; el flujo continúa, solo avisamos.
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Money distribution failed on appointment cancellation",
                                details: $"SearchHire {appointment.SearchHireId} cancelled (status {statusValue}) but money distribution returned false. State was committed; money needs retry/manual handling.",
                                userId: userId,
                                source: "AppointmentService.CancelAppointmentAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: appointment.SearchHireId);
                        }
                    }
                    catch (Exception distEx)
                    {
                        // ✅ ALERTA: no relanzamos (el estado ya está guardado, el flujo continúa); avisamos.
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Exception during money distribution on appointment cancellation",
                            details: $"SearchHire {appointment.SearchHireId} (status {statusValue}): {distEx.Message}. State was committed; money needs retry/manual handling.",
                            userId: userId,
                            source: "AppointmentService.CancelAppointmentAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: appointment.SearchHireId);
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
                    
                    // Ô£à CANCELAR job de Hangfire si existe
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
                
                // Ô£à CANCELAR expl├¡citamente el timer de transici├│n a awaiting_report si existe
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
                
                // Ô£à Si NO es cancelaci├│n final (primera cancelaci├│n), restaurar timer de 24h para que el cliente proponga otra vez
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
                    // ✅ Usar método wrapper con nombre descriptivo para Hangfire
                    var jobId = BackgroundJob.Schedule<IAppointmentService>(
                        service => service.ProcessProposalTimerAsync(proposalTimer.Id),
                        proposalTimer.EndTime - DateTime.UtcNow
                    );
                    
                    // Guardar el JobId en el timer
                    proposalTimer.HangfireJobId = jobId;
                    await _context.SaveChangesAsync();
                }



                        // Ô£à COMMIT: Confirmar la transacci├│n

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



                // Ô£à Enviar mensaje al chat con el cambio de estado (despu├®s del commit)

                await SendAppointmentStatusChangeMessageAsync(

                    appointment.SearchHireId, 

                    statusValue, 

                    userId

                );



                // Ô£à Notificar a la otra parte sobre la cancelaci├│n

                if (appointment.SearchHire.ClientId == userId)

                {

                    // Cliente cancel├│ - notificar al experto

                    if (appointment.SearchHire.ExpertId.HasValue)

                    {

                        var refundMessage = cancelledStatus.IsFinalizationStatus 

                            ? " Se procesar├í el reembolso al cliente." 

                            : "";

                        await _loggingService.LogWarningAsync(

                            message: "Cita cancelada por el cliente",

                            details: $"El cliente cancel├│ la cita #{appointment.Id}.{refundMessage}",

                            userId: appointment.SearchHire.ExpertId.Value,

                            source: "AppointmentService.CancelAppointmentAsync",

                            relatedEntityType: "Appointment",

                            relatedEntityId: appointment.Id,

                            notifyUser: true

                        );

                    }

                }

                else

                {

                    // Experto cancel├│ - notificar al cliente

                    var refundMessage = cancelledStatus.IsFinalizationStatus 

                        ? " Se procesar├í tu reembolso." 

                        : "";

                    await _loggingService.LogWarningAsync(

                        message: "Cita cancelada por el experto",

                        details: $"El experto cancel├│ la cita #{appointment.Id}.{refundMessage}",

                        userId: appointment.SearchHire.ClientId,

                        source: "AppointmentService.CancelAppointmentAsync",

                        relatedEntityType: "Appointment",

                        relatedEntityId: appointment.Id,

                        notifyUser: true

                    );

                }



                return MapToDto(updatedAppointment);

                    }

                    catch (Exception innerEx)

                    {

                        // Ô£à ROLLBACK: Revertir la transacci├│n en caso de error

                        await transaction.RollbackAsync();

                        throw;

                    }

                });

            }

            catch (Exception ex)

            {

                // ÔÜá´©Å LOG WARNING: Error general cancelando cita (el refund tiene su propio CRITICAL si falla, usuario puede reintentar)

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

                    PendingDisputes = 0, // Ô£à REMOVED: DisputeReason field eliminated

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



        /// <summary>
        /// WATCHDOG (frente 8): re-despacha al handler COMPLETO (ProcessAppointmentTimerAsync) cada timer
        /// vencido y no procesado. Pensado para ejecutarse periódicamente (Hangfire RecurringJob) y
        /// recuperar timers perdidos (crash entre Schedule y Save) o atrasados — SIN el bug del antiguo
        /// CheckAppointmentTimersAsync, que consumía los timers de proposal/client_decision sin
        /// procesarlos. Es seguro/idempotente: ProcessAppointmentTimerAsync re-chequea el estado y la
        /// distribución de dinero usa clave de idempotencia por hire, así que no duplica avances ni pagos.
        /// </summary>
        public async Task ProcessOverdueTimersAsync()
        {
            try
            {
                var overdueTimers = await _context.AppointmentTimers
                    .Where(t => !t.IsExpired && t.EndTime <= DateTime.UtcNow)
                    .Select(t => new { t.Id, t.TimerType, t.AppointmentId })
                    .ToListAsync();

                foreach (var timer in overdueTimers)
                {
                    try
                    {
                        // ⚠️ El timer "awaiting_report_transition" NO lo maneja ProcessAppointmentTimerAsync
                        // (su switch cubre proposal/response/expert_report/client_decision y NO tiene default,
                        // así que lo CONSUMIRÍA sin transicionar). Se re-despacha a su handler propio, que va
                        // por appointmentId. El resto de tipos sí los maneja el handler por timerId.
                        if (timer.TimerType == "awaiting_report_transition")
                        {
                            await ProcessAppointmentToAwaitingReportAsync(timer.AppointmentId);
                        }
                        else
                        {
                            await ProcessAppointmentTimerAsync(timer.Id);
                        }
                    }
                    catch (Exception exTimer)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Watchdog failed to process an overdue appointment timer",
                            details: $"ProcessOverdueTimersAsync could not process timer {timer.Id} (type {timer.TimerType}): {exTimer.Message}",
                            userId: null,
                            source: "AppointmentService.ProcessOverdueTimersAsync",
                            relatedEntityType: "AppointmentTimer",
                            relatedEntityId: timer.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Appointment-timers watchdog failed",
                    details: $"ProcessOverdueTimersAsync failed: {ex.Message}",
                    userId: null,
                    source: "AppointmentService.ProcessOverdueTimersAsync",
                    relatedEntityType: "AppointmentTimer",
                    relatedEntityId: null);
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



                    // L├│gica espec├¡fica seg├║n el tipo de timer

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

                                // ­ƒÄ» PROCESAR DINERO AUTOM├üTICAMENTE
                                // Ô£à MEJORA: Usar l├│gica autom├ítica de mapeo - ProcessMoneyDistributionAsync mapea autom├íticamente
                                // appointment_cancelled_by_expert_no_response ÔåÆ cancelled (gen├®rico)
                                // Usa los % del AppointmentStatus (100/0/0) porque tiene configuraci├│n
                                try

                                {
                                        var moneySuccess = await _refundService.ProcessMoneyDistributionAsync(

                                        timer.Appointment.SearchHireId,

                                        AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),

                                        "Expert did not respond within 24h - automatic cancellation",

                                        null,
                                        updateState: true); // Ô£à updateState: true para que haga el mapeo autom├ítico

                                    

                                    if (moneySuccess)

                                    {

                                        // Ô£à LOG INFO: Timer expirado correctamente - experto no respondi├│ (comportamiento esperado)

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



                                        // Ô£à Notificar a cliente y experto sobre cancelaci├│n autom├ítica

                                        if (timer.Appointment.SearchHire?.ClientId > 0)

                                        {

                                            await _loggingService.LogInfoAsync(

                                                message: "Cita cancelada - experto no respondi├│",

                                                details: $"El experto no respondi├│ a tu propuesta de cita en 24 horas. La cita fue cancelada autom├íticamente. Se procesar├í tu reembolso completo.",

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

                                                message: "Cita cancelada autom├íticamente",

                                                details: $"No respondiste a la propuesta de cita en 24 horas. La cita fue cancelada autom├íticamente.",

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

                                        // ­ƒÜ¿ LOG CR├ìTICO: Fallo en distribuci├│n de dinero por timer expirado

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

                                    // ­ƒÜ¿ LOG CR├ìTICO: Excepci├│n en timer expirado

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

                                

                                // Ô£à Enviar mensaje al chat con el cambio de estado autom├ítico

                                // Para cambios autom├íticos, el senderId es el ExpertId del SearchHire

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

                                // Si todos los archivos est├ín listos, enviar el reporte autom├íticamente

                                // La cita se marca como informe enviado

                                var appointmentReportSentStatus = await _context.SystemStatuses

                                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 

                                                            s.StatusValue == "appointment_report_sent");

                                

                                // El SearchHire pasa a esperar decisi├│n del cliente

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

                                    // Ô£à Enviar mensaje al chat con el cambio de estado autom├ítico

                                    // Para cambios autom├íticos, el senderId es el ExpertId del SearchHire

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

                                    

                                    // Tambi├®n actualizar el SearchHire para que use el estado del sistema centralizado

                                    var cancelledStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());

                                    timer.Appointment.SearchHire.StatusId = cancelledStatusId;

                                    timer.Appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

                                    

                                    // ­ƒÄ» PROCESAR DINERO AUTOM├üTICAMENTE
                                    // Ô£à OPTIMIZACI├ôN: updateState: false porque ya cambiamos el estado arriba

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

                                            // Ô£à Notificar a cliente y experto sobre cancelaci├│n por falta de reporte

                                            if (timer.Appointment.SearchHire?.ClientId > 0)

                                            {

                                                await _loggingService.LogWarningAsync(

                                                    message: "Cita cancelada - experto no envi├│ reporte",

                                                    details: $"El experto no envi├│ el reporte a tiempo. La cita fue cancelada autom├íticamente. Se procesar├í tu reembolso.",

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

                                                    details: $"No enviaste el reporte de la cita #{timer.Appointment.Id} en 24 horas. La cita fue cancelada autom├íticamente.",

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

                                            // ­ƒÜ¿ LOG CR├ìTICO: Fallo en distribuci├│n de dinero por falta de reporte (una sola vez, con informaci├│n completa)

                                            await _loggingService.LogCriticalAsync(

                                                message: "CRITICAL: Money distribution failed for expired report timer",

                                                details: $"Appointment {timer.Appointment.Id} timer expired (expert did not submit report within 24h) but money distribution failed. " +

                                                        $"Timer Type: expert_report, AppointmentId: {timer.Appointment.Id}, SearchHireId: {timer.Appointment.SearchHireId}. " +

                                                        $"ClientId: {timer.Appointment.SearchHire?.ClientId}, ExpertId: {timer.Appointment.SearchHire?.ExpertId}, Amount: {timer.Appointment.SearchHire?.Amount}Ôé¼. " +

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

                                        // ­ƒÜ¿ LOG CR├ìTICO: Excepci├│n procesando distribuci├│n por falta de reporte (una sola vez, con informaci├│n completa)

                                        await _loggingService.LogCriticalAsync(

                                            message: "CRITICAL: Exception during money distribution for expired report timer",

                                            details: $"Exception occurred while processing money distribution for Appointment {timer.Appointment.Id} due to expired report timer. " +

                                                    $"Timer Type: expert_report, AppointmentId: {timer.Appointment.Id}, SearchHireId: {timer.Appointment.SearchHireId}. " +

                                                    $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +

                                                    $"ClientId: {timer.Appointment.SearchHire?.ClientId}, ExpertId: {timer.Appointment.SearchHire?.ExpertId}, Amount: {timer.Appointment.SearchHire?.Amount}Ôé¼. " +

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

                                    // Ô£à Enviar mensaje al chat con el cambio de estado autom├ítico

                                    // Para cambios autom├íticos, el senderId es el ExpertId del SearchHire

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



                // 2. Verificar citas confirmadas que deben cambiar a awaiting_report (3 horas despu├®s)

                var cutoffTime = DateTime.UtcNow.AddHours(-3);

                var confirmedAppointments = await _context.Appointments

                    .Include(a => a.Status)

                    .Where(a => a.Status.StatusValue == "appointment_confirmed")

                    .ToListAsync();



                // Filtrar en memoria para evitar problemas de traducci├│n de EF

                confirmedAppointments = confirmedAppointments

                    .Where(a => a.ProposedDate.HasValue && a.ProposedTime.HasValue && a.ProposedDate.Value.Add(a.ProposedTime.Value) <= cutoffTime)

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
                        // ✅ Usar método wrapper con nombre descriptivo para Hangfire
                        var jobId = BackgroundJob.Schedule<IAppointmentService>(
                            service => service.ProcessExpertReportTimerAsync(expertReportTimer.Id),
                            expertReportTimer.EndTime - DateTime.UtcNow
                        );

                        // Guardar el JobId en el timer
                        expertReportTimer.HangfireJobId = jobId;
                        await _context.SaveChangesAsync();

                        // Ô£à Enviar mensaje al chat con el cambio de estado autom├ítico

                        // Para cambios autom├íticos, el senderId es el ExpertId del SearchHire

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
        /// Procesa un timer de cita expirado. Hangfire reintenta autom├íticamente hasta 5 veces con delays progresivos
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

                if (timer == null)
                {
                    return; // Timer no encontrado
                }

                // Verificar si el timer ya est├í expirado (puede haber sido cancelado)
                if (timer.IsExpired)
                {
                    return; // Timer ya procesado o cancelado
                }

                // Ô£à VALIDACI├ôN CR├ìTICA: Verificar que el SearchHire y Appointment existan
                if (timer.Appointment == null || timer.Appointment.SearchHire == null)
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Appointment o SearchHire eliminados
                }

                var searchHire = timer.Appointment.SearchHire;
                var appointment = timer.Appointment;

                // Ô£à VALIDACI├ôN CR├ìTICA: Verificar que el SearchHire NO est├® finalizado
                if (searchHire.Status?.IsFinalizationStatus == true)
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // SearchHire ya finalizado, no procesar
                }

                // Ô£à VALIDACI├ôN CR├ìTICA: Verificar que los usuarios existan y no est├®n bloqueados
                if (searchHire.Client == null || searchHire.Client.IsBlocked)
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Cliente eliminado o bloqueado
                }

                if (searchHire.ExpertId.HasValue && (searchHire.Expert == null || searchHire.Expert.IsBlocked))
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Experto eliminado o bloqueado
                }

                // Ô£à VALIDACI├ôN CR├ìTICA: Verificar estado del SearchHire seg├║n el tipo de timer
                // 🔒 FRENTE 7: re-leer el estado FRESCO de BD justo antes de los guards para reducir la
                // ventana TOCTOU (una acción manual del cliente/experto pudo cambiar el estado entre la
                // carga inicial del timer y este punto). El doble-PAGO ya lo evita la idempotencia por-hire;
                // esto reduce además el doble-AVANCE de estado en la carrera timer-vs-acción-manual.
                var freshStatusId = await _context.SearchHires
                    .Where(sh => sh.Id == searchHire.Id)
                    .Select(sh => sh.StatusId)
                    .FirstOrDefaultAsync();
                var searchHireStatus = await _context.SystemStatuses
                    .Where(s => s.Id == freshStatusId)
                    .Select(s => s.StatusValue)
                    .FirstOrDefaultAsync() ?? (searchHire.Status?.StatusValue ?? string.Empty);

                // Para timers de "proposal" y "response", solo procesar si SearchHire est├í en "pending"
                if (timer.TimerType == "proposal" || timer.TimerType == "response")
                {
                    if (searchHireStatus != "pending")
                    {
                        timer.IsExpired = true;
                        timer.ExpiredAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                        return; // SearchHire no est├í en pending, no procesar
                    }
                }

                // Para timer de "expert_report", solo procesar si est├í en "pending"
                if (timer.TimerType == "expert_report")
                {
                    if (searchHireStatus != "pending")
                    {
                        timer.IsExpired = true;
                        timer.ExpiredAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                        return; // SearchHire no est├í en pending, no procesar
                    }
                }

                // Ô£à VALIDACI├ôN CR├ìTICA: Verificar estado de la cita antes de procesar
                var appointmentStatus = appointment.Status?.StatusValue ?? string.Empty;
                
                if (timer.TimerType == "proposal" && appointmentStatus != AppointmentStatus.AwaitingAppointment.ToStringValue() && appointmentStatus != AppointmentStatus.AppointmentRejected.ToStringValue() && 
                    appointmentStatus != AppointmentStatus.AppointmentCancelledByClient.ToStringValue() && appointmentStatus != AppointmentStatus.AppointmentCancelledByExpert.ToStringValue())
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Estado de cita no v├ílido para timer de proposal
                }

                if (timer.TimerType == "response" && appointmentStatus != "appointment_proposed")
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Estado de cita no v├ílido para timer de response
                }

                if (timer.TimerType == "expert_report" && appointmentStatus != "appointment_awaiting_report")
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Estado de cita no v├ílido para timer de expert_report
                }
                
                // Para timer de "client_decision", solo procesar si SearchHire est├í en "awaiting_client_decision"
                if (timer.TimerType == "client_decision")
                {
                    if (searchHireStatus != "awaiting_client_decision")
                    {
                        timer.IsExpired = true;
                        timer.ExpiredAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                        return; // SearchHire no est├í en awaiting_client_decision, no procesar
                    }
                }
                
                if (timer.TimerType == "client_decision" && appointmentStatus != "appointment_report_sent")
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Estado de cita no v├ílido para timer de client_decision
                }

                // Marcar timer como expirado
                timer.IsExpired = true;
                timer.ExpiredAt = DateTime.UtcNow;

                // Procesar seg├║n el tipo de timer
                switch (timer.TimerType)
                {
                    case "proposal":
                        // Si el cliente no propone en 24h, cancelar
                        var noProposalStatus = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                    s.StatusValue == AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue());

                        if (noProposalStatus != null && timer.Appointment != null)
                        {
                            timer.Appointment.StatusId = noProposalStatus.Id;
                            timer.Appointment.UpdatedAt = DateTime.UtcNow;

                            // Procesar dinero automáticamente
                            // ✅ MEJORA: Usar lógica automática de mapeo - ProcessMoneyDistributionAsync mapea automáticamente
                            // appointment_cancelled_by_client_no_proposal: culpa del CLIENTE (no propuso) → EXPERTO 100% (0/100/0).
                            // El % sale de la StatusConfiguration ligada a este AppointmentStatus (sembrada en BD + migración
                            // 20260116131817 corregida). NO es 100/0/0: eso robaría al experto que reservó su disponibilidad.
                            try
                            {
                                var moneySuccess = await _refundService.ProcessMoneyDistributionAsync(
                                    timer.Appointment.SearchHireId,
                                    AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue(),
                                    "Client did not propose within 24h - automatic cancellation",
                                    null,
                                    updateState: true); // ✅ updateState: true para que haga el mapeo automático
                                
                                if (!moneySuccess)
                                {
                                    // ✅ FALLBACK: Verificar si el estado se cambió (puede haber fallado en Fase 1 o 2)
                                    // Si NO se cambió, cambiarlo manualmente para evitar que el sistema quede bloqueado
                                    var currentSearchHire = await _context.SearchHires
                                        .Include(sh => sh.Status)
                                        .Include(sh => sh.Appointment)
                                            .ThenInclude(a => a.Status)
                                        .FirstOrDefaultAsync(sh => sh.Id == timer.Appointment.SearchHireId);
                                    
                                    bool stateWasChanged = false;
                                    if (currentSearchHire != null && currentSearchHire.Status != null)
                                    {
                                        // Verificar si el estado ya está en "cancelled" (cambió en Fase 2)
                                        var isCancelled = currentSearchHire.Status.StatusValue == "cancelled" ||
                                                        currentSearchHire.Status.IsFinalizationStatus == true;
                                        
                                        if (!isCancelled)
                                        {
                                            // Estado NO cambió (falló en Fase 1 o 2) → Cambiarlo manualmente como fallback
                                            try
                                            {
                                                // Mapear appointment_cancelled_by_client_no_proposal → cancelled
                                                var cancelledStatusId = await GetStatusIdByValueAsync("cancelled", "SearchHireStatus");
                                                currentSearchHire.StatusId = cancelledStatusId;
                                                currentSearchHire.UpdatedAt = DateTime.UtcNow;
                                                
                                                await _context.SaveChangesAsync();
                                                stateWasChanged = true;
                                                
                                                // Log del fallback
                                                await _loggingService.LogWarningAsync(
                                                    message: "State updated manually after ProcessMoneyDistributionAsync failure",
                                                    details: $"SearchHire {timer.Appointment.SearchHireId} state was manually updated to 'cancelled' because ProcessMoneyDistributionAsync returned false. " +
                                                            $"This prevents the system from being blocked. Money distribution still needs manual processing.",
                                                    userId: currentSearchHire.ClientId,
                                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                                    relatedEntityType: "SearchHire",
                                                    relatedEntityId: timer.Appointment.SearchHireId,
                                                    additionalData: new { 
                                                        TimerType = "proposal",
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
                                                        TimerType = "proposal",
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
                                    
                                    await _loggingService.LogWarningAsync(
                                        message: "ProcessMoneyDistributionAsync returned false for timer proposal",
                                        details: $"Timer {timerId} (proposal type) - ProcessMoneyDistributionAsync returned false. SearchHireId: {timer.Appointment.SearchHireId}, Status: {AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue()}. " +
                                                $"State was {(stateWasChanged ? "updated" : "NOT updated - system may be blocked")}.",
                                        userId: null,
                                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                                        relatedEntityType: "AppointmentTimer",
                                        relatedEntityId: timerId
                                    );

                                    // 🔁 FRENTE 11: encolar el reintento ASÍNCRONO del dinero (idempotente) en
                                    // vez de dejar el reembolso al cliente pendiente de gestión manual.
                                    await EnqueueTimerMoneyRetryAsync(
                                        timer.Appointment.SearchHireId,
                                        AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue(),
                                        "Client did not propose within 24h - automatic cancellation",
                                        "proposal",
                                        timerId);
                                }
                            }
                            catch (Exception ex)
                            {
                                // ✅ MEJORA: Log error con detalles para debugging
                                await _loggingService.LogErrorAsync(
                                    message: "Error processing money distribution for timer proposal",
                                    details: $"Timer {timerId} (proposal type) - Error in ProcessMoneyDistributionAsync: {ex.Message}. StackTrace: {ex.StackTrace}. SearchHireId: {timer.Appointment?.SearchHireId}, Status: {AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue()}",
                                    userId: null,
                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                    relatedEntityType: "AppointmentTimer",
                                    relatedEntityId: timerId,
                                    additionalData: new {
                                        TimerId = timerId,
                                        TimerType = "proposal",
                                        SearchHireId = timer.Appointment?.SearchHireId,
                                        Status = AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue(),
                                        Error = ex.Message,
                                        StackTrace = ex.StackTrace
                                    }
                                );

                                // 🔁 FRENTE 11: aun con excepción, encolar el reintento async del dinero.
                                if (timer.Appointment != null)
                                {
                                    await EnqueueTimerMoneyRetryAsync(
                                        timer.Appointment.SearchHireId,
                                        AppointmentStatus.AppointmentCancelledByClientNoProposal.ToStringValue(),
                                        "Client did not propose within 24h - automatic cancellation",
                                        "proposal",
                                        timerId);
                                }
                            }
                        }
                        break;

                    case "response":
                        // Si el experto no responde en 24h, cancelar
                        var noResponseStatus = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                    s.StatusValue == AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue());

                        if (noResponseStatus != null && timer.Appointment != null)
                        {
                            timer.Appointment.StatusId = noResponseStatus.Id;
                            timer.Appointment.UpdatedAt = DateTime.UtcNow;

                            // Procesar dinero autom├íticamente
                            // Ô£à MEJORA: Usar l├│gica autom├ítica de mapeo - ProcessMoneyDistributionAsync mapea autom├íticamente
                            // appointment_cancelled_by_expert_no_response ÔåÆ cancelled (gen├®rico)
                            // Usa los % del AppointmentStatus (100/0/0) porque tiene configuraci├│n
                            try
                            {
                                var moneySuccess = await _refundService.ProcessMoneyDistributionAsync(
                                    timer.Appointment.SearchHireId,
                                    AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),
                                    "Expert did not respond within 24h - automatic cancellation",
                                    null,
                                    updateState: true); // Ô£à updateState: true para que haga el mapeo autom├ítico
                                
                                if (!moneySuccess)
                                {
                                    // ✅ FALLBACK: Verificar si el estado se cambió (puede haber fallado en Fase 1 o 2)
                                    // Si NO se cambió, cambiarlo manualmente para evitar que el sistema quede bloqueado
                                    var currentSearchHire = await _context.SearchHires
                                        .Include(sh => sh.Status)
                                        .Include(sh => sh.Appointment)
                                            .ThenInclude(a => a.Status)
                                        .FirstOrDefaultAsync(sh => sh.Id == timer.Appointment.SearchHireId);
                                    
                                    bool stateWasChanged = false;
                                    if (currentSearchHire != null && currentSearchHire.Status != null)
                                    {
                                        // Verificar si el estado ya está en "cancelled" (cambió en Fase 2)
                                        var isCancelled = currentSearchHire.Status.StatusValue == "cancelled" ||
                                                        currentSearchHire.Status.IsFinalizationStatus == true;
                                        
                                        if (!isCancelled)
                                        {
                                            // Estado NO cambió (falló en Fase 1 o 2) → Cambiarlo manualmente como fallback
                                            try
                                            {
                                                // Mapear appointment_cancelled_by_expert_no_response → cancelled
                                                var cancelledStatusId = await GetStatusIdByValueAsync("cancelled", "SearchHireStatus");
                                                currentSearchHire.StatusId = cancelledStatusId;
                                                currentSearchHire.UpdatedAt = DateTime.UtcNow;
                                                
                                                await _context.SaveChangesAsync();
                                                stateWasChanged = true;
                                                
                                                // Log del fallback
                                                await _loggingService.LogWarningAsync(
                                                    message: "State updated manually after ProcessMoneyDistributionAsync failure",
                                                    details: $"SearchHire {timer.Appointment.SearchHireId} state was manually updated to 'cancelled' because ProcessMoneyDistributionAsync returned false. " +
                                                            $"This prevents the system from being blocked. Money distribution still needs manual processing.",
                                                    userId: currentSearchHire.ClientId,
                                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                                    relatedEntityType: "SearchHire",
                                                    relatedEntityId: timer.Appointment.SearchHireId,
                                                    additionalData: new { 
                                                        TimerType = "response",
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
                                                        TimerType = "response",
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
                                    
                                    await _loggingService.LogWarningAsync(
                                        message: "ProcessMoneyDistributionAsync returned false for timer response",
                                        details: $"Timer {timerId} (response type) - ProcessMoneyDistributionAsync returned false. SearchHireId: {timer.Appointment.SearchHireId}, Status: {AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue()}. " +
                                                $"State was {(stateWasChanged ? "updated" : "NOT updated - system may be blocked")}.",
                                        userId: null,
                                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                                        relatedEntityType: "AppointmentTimer",
                                        relatedEntityId: timerId
                                    );

                                    // 🔁 FRENTE 11: encolar el reintento ASÍNCRONO del dinero (idempotente).
                                    await EnqueueTimerMoneyRetryAsync(
                                        timer.Appointment.SearchHireId,
                                        AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),
                                        "Expert did not respond within 24h - automatic cancellation",
                                        "response",
                                        timerId);
                                }
                            }
                            catch (Exception ex)
                            {
                                // ✅ MEJORA: Log error con detalles para debugging
                                await _loggingService.LogErrorAsync(
                                    message: "Error processing money distribution for timer response",
                                    details: $"Timer {timerId} (response type) - Error in ProcessMoneyDistributionAsync: {ex.Message}. StackTrace: {ex.StackTrace}. SearchHireId: {timer.Appointment?.SearchHireId}, Status: {AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue()}",
                                    userId: null,
                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                    relatedEntityType: "AppointmentTimer",
                                    relatedEntityId: timerId,
                                    additionalData: new {
                                        TimerId = timerId,
                                        TimerType = "response",
                                        SearchHireId = timer.Appointment?.SearchHireId,
                                        Status = AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),
                                        Error = ex.Message,
                                        StackTrace = ex.StackTrace
                                    }
                                );

                                // 🔁 FRENTE 11: aun con excepción, encolar el reintento async del dinero.
                                if (timer.Appointment != null)
                                {
                                    await EnqueueTimerMoneyRetryAsync(
                                        timer.Appointment.SearchHireId,
                                        AppointmentStatus.AppointmentCancelledByExpertNoResponse.ToStringValue(),
                                        "Expert did not respond within 24h - automatic cancellation",
                                        "response",
                                        timerId);
                                }
                            }
                        }
                        break;

                    case "expert_report":
                        // Si el experto no env├¡a reporte en 24h, cancelar
                        var noReportStatus = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                    s.StatusValue == AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue());

                        if (noReportStatus != null && timer.Appointment != null)
                        {
                            timer.Appointment.StatusId = noReportStatus.Id;
                            timer.Appointment.UpdatedAt = DateTime.UtcNow;

                            // Procesar dinero autom├íticamente
                            // Ô£à MEJORA: Usar l├│gica autom├ítica de mapeo - ProcessMoneyDistributionAsync mapea autom├íticamente
                            // appointment_cancelled_by_no_report ÔåÆ cancelled (gen├®rico)
                            // Usa los % del AppointmentStatus (95/0/5) porque tiene configuraci├│n
                            try
                            {
                                // 🔁 FRENTE 11: ANTES se ignoraba el valor de retorno → si devolvía false (p.ej.
                                // balance Stripe insuficiente) el cliente se quedaba sin su reembolso 95% y nadie
                                // se enteraba. Ahora capturamos el resultado y, si falló, encolamos el reintento.
                                var moneySuccess = await _refundService.ProcessMoneyDistributionAsync(
                                    timer.Appointment.SearchHireId,
                                    AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue(),
                                    "Expert did not submit report within 24h - automatic cancellation",
                                    null,
                                    updateState: true); // Ô£à updateState: true para que haga el mapeo autom├ítico

                                if (!moneySuccess)
                                {
                                    // 🔁 A9: si el dinero falló en fase 1/2 (antes del cambio de estado), avanzar
                                    // SearchHire→cancelled a mano (las otras 3 ramas ya lo hacían; esta no). El ESTADO
                                    // del hire va a 'cancelled', pero el reparto del dinero es 95/0/5 (cliente 95% /
                                    // plataforma 5%), NO 100% — sale de la StatusConfiguration del AppointmentStatus. Evita hire atascado.
                                    try
                                    {
                                        var currentSearchHire = await _context.SearchHires
                                            .Include(sh => sh.Status)
                                            .FirstOrDefaultAsync(sh => sh.Id == timer.Appointment.SearchHireId);
                                        if (currentSearchHire != null && currentSearchHire.Status?.IsFinalizationStatus != true)
                                        {
                                            var cancelledStatusId = await GetStatusIdByValueAsync("cancelled", "SearchHireStatus");
                                            currentSearchHire.StatusId = cancelledStatusId;
                                            currentSearchHire.UpdatedAt = DateTime.UtcNow;
                                            await _context.SaveChangesAsync();
                                        }
                                    }
                                    catch (Exception fbEx)
                                    {
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: expert_report timer - fallback state update failed",
                                            details: $"SearchHire {timer.Appointment.SearchHireId}: could not force 'cancelled' after money failure: {fbEx.Message}",
                                            userId: null,
                                            source: "AppointmentService.ProcessAppointmentTimerAsync",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: timer.Appointment.SearchHireId);
                                    }

                                    await _loggingService.LogWarningAsync(
                                        message: "ProcessMoneyDistributionAsync returned false for timer expert_report",
                                        details: $"Timer {timerId} (expert_report type) - money distribution returned false for SearchHire {timer.Appointment.SearchHireId}, Status: {AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue()}. Enqueuing async retry.",
                                        userId: null,
                                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                                        relatedEntityType: "AppointmentTimer",
                                        relatedEntityId: timerId);
                                    await EnqueueTimerMoneyRetryAsync(
                                        timer.Appointment.SearchHireId,
                                        AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue(),
                                        "Expert did not submit report within 24h - automatic cancellation",
                                        "expert_report",
                                        timerId);
                                }
                            }
                            catch (Exception reportEx)
                            {
                                // ✅ ALERTA (antes era un catch VACÍO silencioso): no relanzamos para no
                                // romper el barrido de timers, pero AVISAMOS. El SearchHire depende de la
                                // distribución de dinero, así que si esto falla puede quedar sin finalizar.
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Money distribution failed on expert_report timer (no-report auto-cancel)",
                                    details: $"SearchHire {timer.Appointment?.SearchHireId}: expert_report timer fired but money distribution threw: {reportEx.Message}. The SearchHire may not have advanced — needs retry/manual handling.",
                                    userId: null,
                                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: timer.Appointment?.SearchHireId);

                                // 🔁 FRENTE 11: aun con excepción, encolar el reintento async del dinero.
                                if (timer.Appointment != null)
                                {
                                    await EnqueueTimerMoneyRetryAsync(
                                        timer.Appointment.SearchHireId,
                                        AppointmentStatus.AppointmentCancelledByNoReport.ToStringValue(),
                                        "Expert did not submit report within 24h - automatic cancellation",
                                        "expert_report",
                                        timerId);
                                }
                            }
                        }
                        break;

                    case "client_decision":
                        // Si el cliente no aprueba/disputa en 24h, completar autom├íticamente a favor del experto
                        try
                        {
                            // Cambiar AppointmentStatus a estado espec├¡fico
                            var completedWithoutApprovalAppointmentStatus = await _context.SystemStatuses
                                .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                         s.StatusValue == AppointmentStatus.AppointmentCompletedWithoutClientApproval.ToStringValue());
                            
                            if (completedWithoutApprovalAppointmentStatus != null && timer.Appointment != null)
                            {
                                timer.Appointment.StatusId = completedWithoutApprovalAppointmentStatus.Id;
                                timer.Appointment.UpdatedAt = DateTime.UtcNow;
                            }

                            // Procesar dinero autom├íticamente
                            // Ô£à MEJORA: Usar l├│gica autom├ítica de mapeo - ProcessMoneyDistributionAsync mapea autom├íticamente
                            // appointment_completed_without_client_approval ÔåÆ completed (gen├®rico)
                            // Usa los % del AppointmentStatus (0/100/0) porque tiene configuraci├│n
                            var moneySuccess = await _refundService.ProcessMoneyDistributionAsync(
                                timer.Appointment.SearchHireId,
                                AppointmentStatus.AppointmentCompletedWithoutClientApproval.ToStringValue(),
                                "Client did not respond within 24h - automatic completion in favor of expert",
                                null,
                                updateState: true); // Ô£à updateState: true para que haga el mapeo autom├ítico

                            if (!moneySuccess)
                            {
                                // Ô£à FALLBACK: Verificar si el estado se cambi├│ (puede haber fallado en Fase 1 o 2)
                                // Si NO se cambi├│, cambiarlo manualmente para evitar que el sistema quede bloqueado
                                var currentSearchHire = await _context.SearchHires
                                    .Include(sh => sh.Status)
                                    .Include(sh => sh.Appointment)
                                        .ThenInclude(a => a.Status)
                                    .FirstOrDefaultAsync(sh => sh.Id == timer.Appointment.SearchHireId);
                                
                                bool stateWasChanged = false;
                                if (currentSearchHire != null)
                                {
                                    // Verificar si el estado ya est├í en "completed" (cambi├│ en Fase 2)
                                    var isCompleted = currentSearchHire.Status?.StatusValue == "completed" ||
                                                    currentSearchHire.Status?.IsFinalizationStatus == true;
                                    
                                    if (!isCompleted)
                                    {
                                        // Estado NO cambi├│ (fall├│ en Fase 1 o 2) ÔåÆ Cambiarlo manualmente como fallback
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
                                                    // Fallback: buscar cualquier estado de finalizaci├│n de Appointment
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
                                            // Si el fallback tambi├®n falla, log cr├¡tico
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
                                        // Estado YA cambi├│ (fall├│ en Fase 3 - Stripe) ÔåÆ Correcto, solo falta dinero
                                        stateWasChanged = true;
                                    }
                                }
                                
                                // ­ƒÜ¿ LOG CR├ìTICO: Fallo en distribuci├│n de dinero por timer expirado (client_decision)
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Money distribution failed for expired client_decision timer",
                                    details: $"Appointment {timer.Appointment.Id} timer expired (client did not respond within 24h) but money distribution failed. " +
                                            $"Timer Type: client_decision, AppointmentId: {timer.Appointment.Id}, SearchHireId: {timer.Appointment.SearchHireId}. " +
                                            $"ClientId: {timer.Appointment.SearchHire?.ClientId}, ExpertId: {timer.Appointment.SearchHire?.ExpertId}, Amount: {timer.Appointment.SearchHire?.Amount}Ôé¼. " +
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

                                // 🔁 FRENTE 11: encolar el reintento ASÍNCRONO del pago al experto (idempotente).
                                await EnqueueTimerMoneyRetryAsync(
                                    timer.Appointment.SearchHireId,
                                    AppointmentStatus.AppointmentCompletedWithoutClientApproval.ToStringValue(),
                                    "Client did not respond within 24h - automatic completion in favor of expert",
                                    "client_decision",
                                    timerId);
                            }
                            else
                            {
                                // Ô£à LOG INFO: Timer expirado - cliente no respondi├│, completado autom├íticamente
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
                                    }
                                );
                                
                                // Ô£à Notificar al experto que el servicio se complet├│ autom├íticamente a su favor
                                if (timer.Appointment.SearchHire?.ExpertId.HasValue == true)
                                {
                                    await _loggingService.LogInfoAsync(
                                        message: "Servicio completado autom├íticamente a tu favor",
                                        details: $"El cliente no respondi├│ en 24 horas. El servicio #{timer.Appointment.SearchHireId} se complet├│ autom├íticamente a tu favor y se proces├│ tu pago.",
                                        userId: timer.Appointment.SearchHire.ExpertId.Value,
                                        source: "AppointmentService.ProcessAppointmentTimerAsync",
                                        relatedEntityType: "Appointment",
                                        relatedEntityId: timer.Appointment.Id,
                                        notifyUser: true
                                    );
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Ô£à FALLBACK: Si hay excepci├│n, intentar cambiar estado manualmente
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
                                    // Ô£à MEJORA: Usar cache para obtener el estado "completed"
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
                                                // Fallback: buscar cualquier estado de finalizaci├│n de AppointmentStatus
                                                var appointmentCompletedStatus = await GetStatusByValueAndTypeAsync(
                                                    "appointment_completed_auto", 
                                                    "AppointmentStatus"
                                                );
                                                if (appointmentCompletedStatus == null)
                                                {
                                                    // Buscar cualquier estado de finalizaci├│n
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
                                // Si el fallback tambi├®n falla, continuar con el log cr├¡tico
                            }
                            
                            // ­ƒÜ¿ LOG CR├ìTICO: Excepci├│n procesando distribuci├│n por falta de decisi├│n del cliente
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Exception during money distribution for expired client_decision timer",
                                details: $"Exception occurred while processing money distribution for Appointment {timer.Appointment.Id} due to expired client_decision timer. " +
                                        $"Timer Type: client_decision, AppointmentId: {timer.Appointment.Id}, SearchHireId: {timer.Appointment.SearchHireId}. " +
                                        $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                                        $"ClientId: {timer.Appointment.SearchHire?.ClientId}, ExpertId: {timer.Appointment.SearchHire?.ExpertId}, Amount: {timer.Appointment.SearchHire?.Amount}Ôé¼. " +
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

                            // 🔁 FRENTE 11: aun con excepción, encolar el reintento async del dinero.
                            if (timer.Appointment != null)
                            {
                                await EnqueueTimerMoneyRetryAsync(
                                    timer.Appointment.SearchHireId,
                                    AppointmentStatus.AppointmentCompletedWithoutClientApproval.ToStringValue(),
                                    "Client did not respond within 24h - automatic completion in favor of expert",
                                    "client_decision",
                                    timerId);
                            }
                        }
                        break;
                }

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // ­ƒÜ¿ LOG CR├ìTICO: Excepci├│n general procesando timer
                // Intentar obtener informaci├│n del timer si es posible
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
                    // Si no podemos obtener el timer, continuar sin esa informaci├│n
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

                // No lanzar excepci├│n para evitar que Hangfire reintente indefinidamente
                // El timer se procesar├í en el pr├│ximo CheckAppointmentTimersAsync si es necesario
            }
        }

        /// <summary>
        /// FRENTE 11 (timers no-bloqueantes): cuando un timer finaliza el servicio pero la distribución
        /// de dinero NO se completó inline (ProcessMoneyDistributionAsync devolvió false o lanzó), encola
        /// el reintento ASÍNCRONO del dinero (StripeRefundService.RetryMoneyDistributionJobAsync, que es
        /// idempotente por-hire y lo reintenta Hangfire) para que el dinero se mueva solo, sin bloquear ni
        /// dejar el servicio esperando intervención manual. El estado ya fue avanzado (por
        /// ProcessMoneyDistributionAsync o por el fallback del propio timer); por eso el reintento usa
        /// updateState:false. Se programa con 2 min de margen (igual que los finalizadores de los
        /// controladores) para dar tiempo a que Stripe asiente el balance tras la captura.
        /// 'statusValue' DEBE ser el MISMO que usó la llamada original (define el reparto/config); la clave
        /// de idempotencia es por-hire, así que un doble-encolado NO duplica pagos.
        /// Best-effort: si el propio encolado falla, se avisa con Critical (ahí sí requiere intervención).
        /// </summary>
        private async Task EnqueueTimerMoneyRetryAsync(int searchHireId, string statusValue, string reason, string timerType, int timerId)
        {
            try
            {
                var jobId = BackgroundJob.Schedule<StripeRefundService>(
                    s => s.RetryMoneyDistributionJobAsync(searchHireId, statusValue, reason, null),
                    TimeSpan.FromMinutes(2));
                await _loggingService.LogInfoAsync(
                    message: "Money distribution retry enqueued after timer",
                    details: $"Timer {timerId} ({timerType}) finalized SearchHire {searchHireId} but money did not move inline; " +
                             $"scheduled RetryMoneyDistributionJobAsync (job {jobId}, +2min) for status '{statusValue}'. " +
                             $"Idempotent + Hangfire-retried, so the user is NOT blocked.",
                    userId: null,
                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId);
            }
            catch (Exception enqueueEx)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Failed to enqueue money distribution retry after timer",
                    details: $"Timer {timerId} ({timerType}) finalized SearchHire {searchHireId} but money did NOT move inline AND the async retry could NOT be scheduled for status '{statusValue}'. " +
                             $"Money is NOT scheduled — MANUAL INTERVENTION REQUIRED. Error: {enqueueEx.Message}",
                    userId: null,
                    source: "AppointmentService.ProcessAppointmentTimerAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId);
            }
        }

        /// <summary>
        /// Cambia el estado de una cita confirmada a "awaiting_report" 3 horas despu├®s de la hora de la cita.
        /// Hangfire reintenta autom├íticamente hasta 5 veces con delays progresivos
        /// (1m, 5m, 10m, 15m, 20m) para cubrir fallos transitorios de BD/red.
        /// </summary>
        [AutomaticRetry(
            Attempts = 5, 
            DelaysInSeconds = new[] { 60, 300, 600, 900, 1200 },  // 1m, 5m, 10m, 15m, 20m
            OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        [JobDisplayName("⏰ Timer Transición a Awaiting Report (3h después de cita) - Appointment #{0}")]
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
                    return; // Cita no encontrada o no est├í confirmada
                }

                // Ô£à VALIDACI├ôN CR├ìTICA: Verificar que el SearchHire exista
                if (appointment.SearchHire == null)
                {
                    return; // SearchHire eliminado
                }

                var searchHire = appointment.SearchHire;

                // Ô£à VALIDACI├ôN CR├ìTICA: Verificar que el SearchHire NO est├® finalizado
                if (searchHire.Status?.IsFinalizationStatus == true)
                {
                    return; // SearchHire ya finalizado, no procesar
                }

                // Ô£à VALIDACI├ôN CR├ìTICA: Verificar estado del SearchHire (debe estar en "pending" o "awaiting_client_decision")
                var searchHireStatus = searchHire.Status?.StatusValue ?? string.Empty;
                if (searchHireStatus != "pending" && searchHireStatus != "awaiting_client_decision")
                {
                    return; // SearchHire no est├í en estado v├ílido
                }

                // Ô£à VALIDACI├ôN CR├ìTICA: Verificar que los usuarios existan y no est├®n bloqueados
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
                    // ✅ Usar método wrapper con nombre descriptivo para Hangfire
                    var jobId = BackgroundJob.Schedule<IAppointmentService>(
                        service => service.ProcessExpertReportTimerAsync(expertReportTimer.Id),
                        expertReportTimer.EndTime - DateTime.UtcNow
                    );

                    // Guardar el JobId en el timer
                    expertReportTimer.HangfireJobId = jobId;
                    await _context.SaveChangesAsync();
                    
                    // Ô£à Enviar mensaje al chat con el cambio de estado (despu├®s del commit)
                    // Para cambios autom├íticos, el senderId es el ExpertId del SearchHire
                    var expertIdForMessage = searchHire.ExpertId ?? 0;
                    if (expertIdForMessage > 0)
                    {
                        await SendAppointmentStatusChangeMessageAsync(
                            searchHire.Id,
                            "appointment_awaiting_report",
                            expertIdForMessage
                        );
                    }
                    
                    // Ô£à Notificar al experto que debe enviar el reporte en 24 horas
                    if (searchHire.ExpertId.HasValue)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Debes enviar el reporte de la cita",
                            details: $"Han pasado 3 horas desde la cita. Tienes 24 horas para enviar el reporte del servicio #{searchHire.Id}. Si no lo env├¡as a tiempo, la cita ser├í cancelada autom├íticamente.",
                            userId: searchHire.ExpertId.Value,
                            source: "AppointmentService.ProcessAppointmentToAwaitingReportAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: appointment.Id,
                            notifyUser: true
                        );
                    }
                    
                    // Ô£à Marcar el timer de transici├│n como expirado ya que el job se ejecut├│ exitosamente
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
                throw;
            }
        }



        public async Task<AppointmentDto> SubmitExpertReportAsync(int appointmentId, int expertId, string? notes = null)

        {

            try

            {

                // Ô£à CORRECCI├ôN: Usar la estrategia de ejecuci├│n para manejar transacciones con reintentos (NpgsqlRetryingExecutionStrategy)

                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>

                {

                    // Ô£à PROTECCI├ôN: Abrir transacci├│n ANTES del FOR UPDATE para que el bloqueo funcione

                    using var transaction = await _context.Database.BeginTransactionAsync();

                    try

                    {

                        // Ô£à PROTECCI├ôN: Usar row-level locking DENTRO de la transacci├│n para evitar doble procesamiento

                var appointment = await _context.Appointments

                            .FromSqlInterpolated($"SELECT *, xmin FROM \"Appointments\" WHERE \"Id\" = {appointmentId} FOR UPDATE")

                    .Include(a => a.SearchHire)

                        .ThenInclude(sh => sh.Status)

                    .Include(a => a.Status)

                            .FirstOrDefaultAsync();



                if (appointment == null)

                    throw new ArgumentException("Appointment not found");



                        var currentStatus = appointment.Status?.StatusValue ?? string.Empty;

                        // Ô£à VALIDACI├ôN: Verificar que el usuario es el experto

                if (appointment.SearchHire.ExpertId != expertId)

                    throw new UnauthorizedAccessException("Only the expert can submit reports");

                        // Ô£à VALIDACI├ôN CR├ìTICA: Verificar que el SearchHire NO est├® finalizado
                        if (appointment.SearchHire.Status?.IsFinalizationStatus == true)
                        {
                            var searchHireStatus = appointment.SearchHire.Status?.StatusValue ?? "unknown";
                            throw new InvalidOperationException(
                                $"No se puede enviar el reporte cuando el servicio est├í en estado de finalizaci├│n '{searchHireStatus}'. " +
                                $"El servicio debe estar activo para poder enviar reportes."
                            );
                        }

                        // Ô£à VALIDACI├ôN CR├ìTICA: Solo se puede enviar reporte si est├í en estado "appointment_awaiting_report"

                        if (currentStatus != "appointment_awaiting_report")

                        {

                            throw new InvalidOperationException(

                                $"No se puede enviar el reporte en estado '{currentStatus}'. " +

                                $"Solo se pueden enviar reportes cuando la cita est├í en estado 'appointment_awaiting_report'."

                            );

                        }



                        // Ô£à PROTECCI├ôN: Verificar que no se haya procesado ya (evitar doble click)

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



                // Actualizar el SearchHire seg├║n el mapeo de estados

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

                // Ô£à CANCELAR TODOS los timers activos (expert_report, response, proposal, etc.) antes de crear el timer de client_decision
                // Esto asegura que no queden timers antiguos activos cuando se env├¡a el reporte
                var activeTimers = await _context.AppointmentTimers

                    .Where(t => t.AppointmentId == appointment.Id && 

                               !t.IsExpired)

                    .ToListAsync();



                foreach (var timer in activeTimers)

                {

                    timer.IsExpired = true;

                    timer.ExpiredAt = DateTime.UtcNow;
                    
                    // Ô£à CANCELAR job de Hangfire si existe
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
                
                // Ô£à Crear timer para decisi├│n del cliente (24 horas)
                // Si el cliente no aprueba/disputa en 24h, se completa autom├íticamente a favor del experto
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
                // ✅ Usar método wrapper con nombre descriptivo para Hangfire
                var jobId = BackgroundJob.Schedule<IAppointmentService>(
                    service => service.ProcessClientDecisionTimerAsync(clientDecisionTimer.Id),
                    clientDecisionTimer.EndTime - DateTime.UtcNow
                );

                // Guardar el JobId en el timer
                clientDecisionTimer.HangfireJobId = jobId;
                await _context.SaveChangesAsync();

                        // Ô£à COMMIT: Confirmar la transacci├│n

                        await transaction.CommitAsync();
                        
                // Ô£à Notificar al cliente que el experto envi├│ el reporte
                if (appointment.SearchHire?.ClientId != null)
                {
                    await _loggingService.LogInfoAsync(
                        message: "Reporte del experto recibido",
                        details: $"El experto envi├│ el reporte del servicio #{appointment.SearchHireId}. Tienes 24 horas para aprobar o disputar el servicio.",
                        userId: appointment.SearchHire.ClientId,
                        source: "AppointmentService.SubmitExpertReportAsync",
                        relatedEntityType: "Appointment",
                        relatedEntityId: appointment.Id,
                        notifyUser: true
                    );
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

                // Ô£à Enviar mensaje al chat con el cambio de estado (despu├®s del commit)

                await SendAppointmentStatusChangeMessageAsync(

                    appointment.SearchHireId, 

                    AppointmentStatus.AppointmentReportSent.ToStringValue(), 

                    expertId

                );



                return MapToDto(updatedAppointment);

                    }

                    catch (Exception innerEx)

                    {

                        // Ô£à ROLLBACK: Revertir la transacci├│n en caso de error

                        await transaction.RollbackAsync();

                        throw;

                    }

                });

            }

            catch (Exception ex)

            {

                // ÔÜá´©Å LOG WARNING: Error general enviando reporte de experto (no afecta dinero, usuario puede reintentar)

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



                // Verificar video si est├í configurado

                var videoType = requiredDeliverableTypes.FirstOrDefault(dt => dt.Name == "Video");

                if (videoType != null)

                {

                    var hasVideo = uploadedDeliverables.Any(d => d.Type == "video");

                    if (!hasVideo)

                    {

                        missingFiles.Add("MP4");

                    }

                }



                // Si faltan archivos, devolver mensaje espec├¡fico

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

        /// Valida que la ubicaci├│n propuesta para la cita est├® dentro del rango del experto

        /// definido cuando se contrat├│ el servicio. Esto asegura que el experto no pueda

        /// cambiar su ubicaci├│n despu├®s de ser contratado para afectar las citas.

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



                // Obtener las coordenadas del experto al momento de la contrataci├│n

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



                // Obtener el rango de ubicaci├│n del Search original desde SearchParameters

                var searchLocationRange = hire.Search.SearchParameters?.FirstOrDefault()?.LocationRange;

                if (searchLocationRange == null || searchLocationRange <= 0)

                {

                    searchLocationRange = 50; // Rango por defecto

                }



                // Calcular la distancia entre la ubicaci├│n del experto y la ubicaci├│n propuesta para la cita

                var distance = CalculateDistance(expertLatitude, expertLongitude, appointmentLatitude.Value, appointmentLongitude.Value);





                // Verificar que la distancia est├® dentro del rango permitido

                if (distance > searchLocationRange)

                {

                    throw new InvalidOperationException(

                        $"La ubicaci├│n propuesta para la cita est├í fuera del rango del experto. " +

                        $"Distancia: {distance:F1} km, Rango m├íximo: {searchLocationRange} km. " +

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

        /// Valida que la fecha/hora propuesta para la cita est├® dentro del horario de disponibilidad del experto

        /// </summary>

        private async Task ValidateAppointmentAvailabilityAsync(SearchHire searchHire, DateTime proposedDateTime)

        {

            try

            {

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



                // Deserializar los d├¡as de la semana

                var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(availability.DaysOfWeek) ?? new List<string>();



                if (daysOfWeek.Count == 0)

                {

                    throw new InvalidOperationException(

                        "El experto no tiene d├¡as de disponibilidad configurados."

                    );

                }



                // Obtener el d├¡a de la semana de la fecha propuesta (en ingl├®s)

                var dayOfWeek = proposedDateTime.DayOfWeek.ToString(); // "Monday", "Tuesday", etc.



                // Verificar que el d├¡a est├® en los d├¡as disponibles

                if (!daysOfWeek.Contains(dayOfWeek))

                {

                    var availableDaysSpanish = string.Join(", ", daysOfWeek.Select(d =>

                    {

                        return d switch

                        {

                            "Monday" => "Lunes",

                            "Tuesday" => "Martes",

                            "Wednesday" => "Mi├®rcoles",

                            "Thursday" => "Jueves",

                            "Friday" => "Viernes",

                            "Saturday" => "S├íbado",

                            "Sunday" => "Domingo",

                            _ => d

                        };

                    }));



                    var daySpanish = dayOfWeek switch

                    {

                        "Monday" => "Lunes",

                        "Tuesday" => "Martes",

                        "Wednesday" => "Mi├®rcoles",

                        "Thursday" => "Jueves",

                        "Friday" => "Viernes",

                        "Saturday" => "S├íbado",

                        "Sunday" => "Domingo",

                        _ => dayOfWeek

                    };



                    throw new InvalidOperationException(

                        $"El d├¡a propuesto ({daySpanish}) no est├í dentro de los horarios de disponibilidad del experto. " +

                        $"D├¡as disponibles: {availableDaysSpanish}. " +

                        $"Fecha propuesta: {proposedDateTime:dd/MM/yyyy}"

                    );

                }



                // Obtener la hora propuesta (solo horas y minutos, sin segundos)

                var proposedTime = proposedDateTime.TimeOfDay;

                var proposedTimeOnly = new TimeSpan(proposedTime.Hours, proposedTime.Minutes, 0);



                // Verificar que la hora est├® dentro del rango de disponibilidad

                if (proposedTimeOnly < availability.StartTime || proposedTimeOnly > availability.EndTime)

                {

                    var startTimeFormatted = $"{availability.StartTime.Hours:D2}:{availability.StartTime.Minutes:D2}";

                    var endTimeFormatted = $"{availability.EndTime.Hours:D2}:{availability.EndTime.Minutes:D2}";

                    var proposedTimeFormatted = $"{proposedTimeOnly.Hours:D2}:{proposedTimeOnly.Minutes:D2}";



                    throw new InvalidOperationException(

                        $"La hora propuesta ({proposedTimeFormatted}) est├í fuera del horario de disponibilidad del experto. " +

                        $"Horario disponible: {startTimeFormatted} - {endTimeFormatted}. " +

                        $"Fecha/hora propuesta: {proposedDateTime:dd/MM/yyyy HH:mm}"

                    );

                }

            }

            catch (Exception ex)

            {

                throw;

            }

        }



        /// <summary>

        /// Calcula la distancia entre dos puntos geogr├íficos usando la f├│rmula de Haversine

        /// </summary>

        /// <summary>

        /// Env├¡a un mensaje autom├ítico al chat cuando cambia el estado de una cita

        /// Formato: "APPointmentStatusChange:{status_value}"

        /// </summary>

        private async Task SendAppointmentStatusChangeMessageAsync(int searchHireId, string statusValue, int senderId)

        {

            try

            {

                // Buscar la conversaci├│n activa del SearchHire

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

                // No lanzar excepci├│n - el env├¡o del mensaje no debe afectar el flujo principal

            }

        }



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

                // Ô£à NUEVOS CAMPOS: Informaci├│n de ubicaci├│n del experto

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

        /// <summary>
        /// Wrapper para procesar timer de propuesta del cliente.
        /// Si el cliente no propone en 24h, se cancela y se penaliza al cliente.
        /// </summary>
        [AutomaticRetry(
            Attempts = 5, 
            DelaysInSeconds = new[] { 60, 300, 600, 900, 1200 },
            OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        [JobDisplayName("⏰ Timer Propuesta Cliente (Penaliza Cliente) - Timer #{0}")]
        public async Task ProcessProposalTimerAsync(int timerId)
        {
            await ProcessAppointmentTimerAsync(timerId);
        }

        /// <summary>
        /// Wrapper para procesar timer de respuesta del experto.
        /// Si el experto no responde en 24h, se cancela y se penaliza al experto.
        /// </summary>
        [AutomaticRetry(
            Attempts = 5, 
            DelaysInSeconds = new[] { 60, 300, 600, 900, 1200 },
            OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        [JobDisplayName("⏰ Timer Respuesta Experto (Penaliza Experto) - Timer #{0}")]
        public async Task ProcessResponseTimerAsync(int timerId)
        {
            await ProcessAppointmentTimerAsync(timerId);
        }

        /// <summary>
        /// Wrapper para procesar timer de reporte del experto.
        /// Si el experto no envía el reporte en 24h, se penaliza al experto.
        /// </summary>
        [AutomaticRetry(
            Attempts = 5, 
            DelaysInSeconds = new[] { 60, 300, 600, 900, 1200 },
            OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        [JobDisplayName("⏰ Timer Reporte Experto (24h para enviar reporte) - Timer #{0}")]
        public async Task ProcessExpertReportTimerAsync(int timerId)
        {
            await ProcessAppointmentTimerAsync(timerId);
        }

        /// <summary>
        /// Wrapper para procesar timer de decisión del cliente.
        /// Si el cliente no decide en 24h, se completa automáticamente a favor del experto.
        /// </summary>
        [AutomaticRetry(
            Attempts = 5, 
            DelaysInSeconds = new[] { 60, 300, 600, 900, 1200 },
            OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        [JobDisplayName("⏰ Timer Decisión Cliente (24h para aprobar/disputar) - Timer #{0}")]
        public async Task ProcessClientDecisionTimerAsync(int timerId)
        {
            await ProcessAppointmentTimerAsync(timerId);
        }

        /// <summary>
        /// Wrapper para procesar timer de transición a awaiting_report.
        /// Se ejecuta 3 horas después de la cita para cambiar el estado a "awaiting_report".
        /// </summary>
        [AutomaticRetry(
            Attempts = 5, 
            DelaysInSeconds = new[] { 60, 300, 600, 900, 1200 },
            OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        [JobDisplayName("⏰ Timer Transición a Awaiting Report (3h después de cita) - Timer #{0}")]
        public async Task ProcessAwaitingReportTransitionTimerAsync(int timerId)
        {
            await ProcessAppointmentTimerAsync(timerId);
        }

    }

}

