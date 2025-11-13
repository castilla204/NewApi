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

    public class AppointmentService : IAppointmentService

    {

        private readonly AppDbContext _context;

        private readonly SystemStatusService _systemStatusService;

        private readonly StripeRefundService _refundService;

        private readonly ILoggingService _loggingService;

        private readonly IStripeValidationService _stripeValidationService;



        public AppointmentService(AppDbContext context, SystemStatusService systemStatusService, StripeRefundService refundService, ILoggingService loggingService, IStripeValidationService stripeValidationService)

        {

            _context = context;

            _systemStatusService = systemStatusService;

            _refundService = refundService;

            _loggingService = loggingService;

            _stripeValidationService = stripeValidationService;

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

                // ✅ CORRECCIÓN: Usar la estrategia de ejecución para manejar transacciones con reintentos (NpgsqlRetryingExecutionStrategy)

                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>

                {

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



                        // ✅ VALIDACIÓN: Verificar que la fecha/hora propuesta esté dentro del horario de disponibilidad del experto

                        await ValidateAppointmentAvailabilityAsync(searchHire, proposedDateTime);



                        // Crear la cita dentro de la transacción

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

                // ✅ CORRECCIÓN: Usar la estrategia de ejecución para manejar transacciones con reintentos (NpgsqlRetryingExecutionStrategy)

                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>

                {

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

                    // Verificar que el SearchHire existe

                    var searchHire = await _context.SearchHires

                        .Include(sh => sh.SearchService)

                            .ThenInclude(ss => ss.ExpertProfile)

                        .FirstOrDefaultAsync(sh => sh.Id == searchHireId);



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



                    // Obtener el estado "awaiting_appointment"

                    var awaitingStatus = await _context.SystemStatuses

                        .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 

                                                s.StatusValue == "awaiting_appointment");



                    if (awaitingStatus == null)

                        throw new InvalidOperationException("Awaiting appointment status not found");



                            // Crear la cita dentro de la transacción

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

                                    .ThenInclude(sh => sh.Status)

                        .Include(a => a.Status)

                        .FirstAsync(a => a.Id == appointment.Id);

                }



                // Verificar que el usuario es el cliente

                if (appointment.SearchHire.ClientId != userId)

                    throw new UnauthorizedAccessException("Only the client can propose appointments");



                        // ✅ VALIDACIÓN CRÍTICA: Solo se puede proponer si está en "awaiting_appointment", "appointment_rejected" o estados de cancelación (primera cancelación)

                        // No se puede proponer si ya está propuesta, confirmada o cancelada (segunda cancelación)

                        var currentStatus = appointment.Status?.StatusValue ?? string.Empty;

                        var validStatesForPropose = new[] { 
                            "awaiting_appointment", 
                            "appointment_rejected",
                            "appointment_cancelled_by_client",      // Primera cancelación del cliente
                            "appointment_cancelled_by_expert"        // Primera cancelación del experto
                        };

                        

                        if (!validStatesForPropose.Contains(currentStatus))

                        {

                            throw new InvalidOperationException(

                                $"No se puede proponer una cita en estado '{currentStatus}'. " +

                                $"Solo se pueden proponer citas en estados: {string.Join(", ", validStatesForPropose)}."

                            );

                        }



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



                        // ✅ VALIDACIÓN: Verificar que la fecha/hora propuesta esté dentro del horario de disponibilidad del experto

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

                appointment.StatusId = proposedStatus.Id;

                appointment.LastProposalAt = DateTime.UtcNow;

                appointment.UpdatedAt = DateTime.UtcNow;



                // ✅ Cancelar timers de propuesta activos antes de crear el timer de respuesta
                var proposalTimers = await _context.AppointmentTimers
                    .Where(t => t.AppointmentId == appointment.Id && 
                               t.TimerType == "proposal" && 
                               !t.IsExpired)
                    .ToListAsync();

                foreach (var timer in proposalTimers)
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

                await _context.SaveChangesAsync();

                // Programar scheduled job para cuando expire el timer de respuesta (24 horas)
                var jobId = BackgroundJob.Schedule<IAppointmentService>(
                    service => service.ProcessAppointmentTimerAsync(responseTimer.Id),
                    responseTimer.EndTime - DateTime.UtcNow
                );

                // Guardar el JobId en el timer
                responseTimer.HangfireJobId = jobId;
                await _context.SaveChangesAsync();



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

                    AppointmentStatus.AppointmentProposed.ToStringValue(), 

                    userId

                );

                // ✅ Notificar al experto sobre la nueva propuesta de cita
                if (updatedAppointment.SearchHire?.ExpertId.HasValue == true && updatedAppointment.SearchHire.ExpertId.Value > 0)
                {
                    var appointmentDateTime = updatedAppointment.ProposedDate.Date + updatedAppointment.ProposedTime;
                    var formattedDate = appointmentDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
                    
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

                return MapToDto(updatedAppointment);

                    }

                    catch (Exception innerEx)

                    {

                        // ✅ ROLLBACK: Revertir la transacción en caso de error

                        await transaction.RollbackAsync();

                        throw;

                    }

                });

            }

            catch (Exception ex)

            {

                // 🚨 LOG CRÍTICO: Error general proponiendo cita (una sola vez, con información completa)

                await _loggingService.LogCriticalAsync(

                    message: "CRITICAL: Error proposing appointment",

                    details: $"An unexpected exception occurred while proposing appointment for SearchHire {searchHireId}. " +

                            $"User {userId} attempted to propose appointment. " +

                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +

                            $"Stack Trace: {ex.StackTrace}. " +

                            $"ACTION REQUIRED: Review error - appointment proposal failed. User may need to retry.",

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

                // ✅ CORRECCIÓN: Usar la estrategia de ejecución para manejar transacciones con reintentos (NpgsqlRetryingExecutionStrategy)

                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>

                {

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

                            "appointment_rejected", 

                            "appointment_cancelled_by_expert_rejection",

                            "appointment_cancelled_by_client",

                            "appointment_cancelled_by_client_second",

                            "appointment_cancelled_by_expert",

                            "appointment_cancelled_by_expert_second"

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
                var appointmentDateTime = appointment.ProposedDate.Date + appointment.ProposedTime;
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

                    AppointmentStatus.AppointmentConfirmed.ToStringValue(), 

                    userId

                );



                return MapToDto(updatedAppointment);

                    }

                    catch (Exception innerEx)

                    {

                        // ✅ ROLLBACK: Revertir la transacción en caso de error

                        await transaction.RollbackAsync();

                        throw;

                    }

                });

            }

            catch (Exception ex)

            {

                // 🚨 LOG CRÍTICO: Error general confirmando cita (una sola vez, con información completa)

                await _loggingService.LogCriticalAsync(

                    message: "CRITICAL: Error confirming appointment",

                    details: $"An unexpected exception occurred while confirming appointment {dto.AppointmentId}. " +

                            $"User {userId} attempted to confirm appointment. " +

                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +

                            $"Stack Trace: {ex.StackTrace}. " +

                            $"ACTION REQUIRED: Review error - appointment confirmation failed. User may need to retry.",

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

                // ✅ CORRECCIÓN: Usar la estrategia de ejecución para manejar transacciones con reintentos (NpgsqlRetryingExecutionStrategy)

                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>

                {

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

                            "appointment_rejected", 

                            "appointment_cancelled_by_expert_rejection",

                            "appointment_cancelled_by_client",

                            "appointment_cancelled_by_client_second",

                            "appointment_cancelled_by_expert",

                            "appointment_cancelled_by_expert_second",

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

                

                // ✅ CORRECCIÓN: Incrementar ExpertCancellationCount para segunda cancelación

                if (isSecondRejection)

                {

                    appointment.ExpertCancellationCount++;

                }

                

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

                    var statusId = await GetStatusIdByValueAsync(targetSearchHireStatus.Value.ToStringValue());

                    appointment.SearchHire.StatusId = statusId;

                    appointment.SearchHire.UpdatedAt = DateTime.UtcNow;

                }

                else

                {

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



                await _context.SaveChangesAsync();



                // ✅ CORRECCIÓN: Procesar refund automático para segunda cancelación

                if (isSecondRejection)

                {

                    try

                    {

                        // 🔍 LOG: Verificar configuración de dinero antes del refund

                        var moneyConfig = await _systemStatusService.GetMoneyDistributionConfigAsync(

                            "appointment_cancelled_by_expert_rejection", 

                            appointment.SearchHire.SearchService?.CategoryId, 

                            appointment.SearchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);

                        // Orquestar refund+transfer según configuración del subestado de finalización
                        // ✅ OPTIMIZACIÓN: updateState: false porque ya cambiamos el estado arriba (líneas 1466, 1512-1514)

                        var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(

                            appointment.SearchHireId,

                            "appointment_cancelled_by_expert_rejection",

                            "Segundo rechazo del experto - penalización máxima",

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

                else

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

                        notifyUser: true

                    );

                }

                else

                {

                    // Primera cancelación - notificar que puede proponer otra

                    await _loggingService.LogInfoAsync(

                        message: "Cita rechazada",

                        details: $"El experto rechazó la propuesta de cita. Puedes proponer otra fecha y hora.",

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

                        // ✅ ROLLBACK: Revertir la transacción en caso de error

                        await transaction.RollbackAsync();

                        throw;

                    }

                });

            }

            catch (Exception ex)

            {

                // 🚨 LOG CRÍTICO: Error general rechazando cita (una sola vez, con información completa)

                await _loggingService.LogCriticalAsync(

                    message: "CRITICAL: Error rejecting appointment",

                    details: $"An unexpected exception occurred while rejecting appointment {dto.AppointmentId}. " +

                            $"Expert {userId} attempted to reject appointment. " +

                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +

                            $"Stack Trace: {ex.StackTrace}. " +

                            $"ACTION REQUIRED: Review error - appointment rejection failed. Expert may need to retry.",

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

                // ✅ CORRECCIÓN: Usar la estrategia de ejecución para manejar transacciones con reintentos (NpgsqlRetryingExecutionStrategy)

                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>

                {

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

                        if (currentStatus == "awaiting_appointment")

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

                            "appointment_cancelled_by_client",

                            "appointment_cancelled_by_client_second",

                            "appointment_cancelled_by_expert",

                            "appointment_cancelled_by_expert_second",

                            "appointment_cancelled_by_expert_rejection",

                            "appointment_cancelled_by_no_response",

                            "appointment_report_sent"

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
                            // Verificar que la fecha propuesta sea válida (no sea DateTime.MinValue o default)
                            if (appointment.ProposedDate != default(DateTime) && appointment.ProposedDate > DateTime.MinValue)
                            {
                                var appointmentDateTime = appointment.ProposedDate.Date + appointment.ProposedTime;
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
                    // ✅ OPTIMIZACIÓN: updateState: false porque ya cambiamos el estado arriba (líneas 2250-2252)

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

                }



                return MapToDto(updatedAppointment);

                    }

                    catch (Exception innerEx)

                    {

                        // ✅ ROLLBACK: Revertir la transacción en caso de error

                        await transaction.RollbackAsync();

                        throw;

                    }

                });

            }

            catch (Exception ex)

            {

                // 🚨 LOG CRÍTICO: Error general cancelando cita (una sola vez, con información completa)

                await _loggingService.LogCriticalAsync(

                    message: "CRITICAL: Error cancelling appointment",

                    details: $"An unexpected exception occurred while cancelling appointment {dto.AppointmentId}. " +

                            $"User {userId} attempted to cancel appointment. " +

                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +

                            $"Stack Trace: {ex.StackTrace}. " +

                            $"ACTION REQUIRED: Review error - appointment cancellation failed. User may need to retry.",

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
                                // ✅ OPTIMIZACIÓN: updateState: false porque ya cambiamos el estado arriba

                                try

                                {

                                    var moneySuccess = await _refundService.ProcessMoneyDistributionAsync(

                                        timer.Appointment.SearchHireId,

                                        "appointment_cancelled_by_no_response",

                                        "Client did not respond within 24h - automatic cancellation",

                                        null,
                                        updateState: false);

                                    

                                    if (moneySuccess)

                                    {

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



                                        // ✅ Notificar a cliente y experto sobre cancelación automática

                                        if (timer.Appointment.SearchHire?.ClientId > 0)

                                        {

                                            await _loggingService.LogWarningAsync(

                                                message: "Cita cancelada automáticamente",

                                                details: $"No respondiste a la propuesta de cita en 24 horas. La cita fue cancelada automáticamente. Se procesará tu reembolso.",

                                                userId: timer.Appointment.SearchHire.ClientId,

                                                source: "AppointmentService.CheckAppointmentTimersAsync",

                                                relatedEntityType: "Appointment",

                                                relatedEntityId: timer.Appointment.Id,

                                                notifyUser: true

                                            );

                                        }



                                        if (timer.Appointment.SearchHire?.ExpertId.HasValue == true)

                                        {

                                            await _loggingService.LogInfoAsync(

                                                message: "Cita cancelada - cliente no respondió",

                                                details: $"El cliente no respondió a tu propuesta de cita en 24 horas. La cita fue cancelada automáticamente.",

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

                                                Status = "appointment_cancelled_by_no_response",

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

                                            Status = "appointment_cancelled_by_no_response",

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

                                        AppointmentStatus.AppointmentCancelledByNoResponse.ToStringValue(),

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
                                    // ✅ OPTIMIZACIÓN: updateState: false porque ya cambiamos el estado arriba

                                    try

                                    {

                                        var moneySuccess = await _refundService.ProcessMoneyDistributionAsync(

                                            timer.Appointment.SearchHireId,

                                            "appointment_cancelled_by_no_report",

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

                                                    Status = "appointment_cancelled_by_no_report",

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

                                                Status = "appointment_cancelled_by_no_report",

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

                                        "appointment_cancelled_by_no_report",

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

                // Verificar si el timer ya está expirado (puede haber sido cancelado)
                if (timer.IsExpired)
                {
                    return; // Timer ya procesado o cancelado
                }

                // ✅ VALIDACIÓN CRÍTICA: Verificar que el SearchHire y Appointment existan
                if (timer.Appointment == null || timer.Appointment.SearchHire == null)
                {
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
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // SearchHire ya finalizado, no procesar
                }

                // ✅ VALIDACIÓN CRÍTICA: Verificar que los usuarios existan y no estén bloqueados
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

                // ✅ VALIDACIÓN CRÍTICA: Verificar estado del SearchHire según el tipo de timer
                var searchHireStatus = searchHire.Status?.StatusValue ?? string.Empty;
                
                // Para timers de "proposal" y "response", solo procesar si SearchHire está en "pending"
                if (timer.TimerType == "proposal" || timer.TimerType == "response")
                {
                    if (searchHireStatus != "pending")
                    {
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
                
                if (timer.TimerType == "proposal" && appointmentStatus != "awaiting_appointment" && appointmentStatus != "appointment_rejected" && 
                    appointmentStatus != "appointment_cancelled_by_client" && appointmentStatus != "appointment_cancelled_by_expert")
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Estado de cita no válido para timer de proposal
                }

                if (timer.TimerType == "response" && appointmentStatus != "appointment_proposed")
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Estado de cita no válido para timer de response
                }

                if (timer.TimerType == "expert_report" && appointmentStatus != "appointment_awaiting_report")
                {
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
                        timer.IsExpired = true;
                        timer.ExpiredAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                        return; // SearchHire no está en awaiting_client_decision, no procesar
                    }
                }
                
                if (timer.TimerType == "client_decision" && appointmentStatus != "appointment_report_sent")
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return; // Estado de cita no válido para timer de client_decision
                }

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
                                                    s.StatusValue == "appointment_cancelled_by_no_response");

                        if (noProposalStatus != null && timer.Appointment != null)
                        {
                            timer.Appointment.StatusId = noProposalStatus.Id;
                            timer.Appointment.UpdatedAt = DateTime.UtcNow;

                            // Procesar dinero automáticamente
                            // ✅ OPTIMIZACIÓN: updateState: false porque ya cambiamos el estado arriba
                            try
                            {
                                await _refundService.ProcessMoneyDistributionAsync(
                                    timer.Appointment.SearchHireId,
                                    "appointment_cancelled_by_no_response",
                                    "Client did not propose within 24h - automatic cancellation",
                                    null,
                                    updateState: false);
                            }
                            catch
                            {
                                // Log error pero continuar
                            }
                        }
                        break;

                    case "response":
                        // Si el experto no responde en 24h, cancelar
                        var noResponseStatus = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                    s.StatusValue == "appointment_cancelled_by_no_response");

                        if (noResponseStatus != null && timer.Appointment != null)
                        {
                            timer.Appointment.StatusId = noResponseStatus.Id;
                            timer.Appointment.UpdatedAt = DateTime.UtcNow;

                            // Procesar dinero automáticamente
                            // ✅ OPTIMIZACIÓN: updateState: false porque ya cambiamos el estado arriba
                            try
                            {
                                await _refundService.ProcessMoneyDistributionAsync(
                                    timer.Appointment.SearchHireId,
                                    "appointment_cancelled_by_no_response",
                                    "Expert did not respond within 24h - automatic cancellation",
                                    null,
                                    updateState: false);
                            }
                            catch
                            {
                                // Log error pero continuar
                            }
                        }
                        break;

                    case "expert_report":
                        // Si el experto no envía reporte en 24h, cancelar
                        var noReportStatus = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                    s.StatusValue == "appointment_cancelled_by_no_report");

                        if (noReportStatus != null && timer.Appointment != null)
                        {
                            timer.Appointment.StatusId = noReportStatus.Id;
                            timer.Appointment.UpdatedAt = DateTime.UtcNow;

                            // Procesar dinero automáticamente
                            // ✅ OPTIMIZACIÓN: updateState: false porque ya cambiamos el estado arriba
                            try
                            {
                                await _refundService.ProcessMoneyDistributionAsync(
                                    timer.Appointment.SearchHireId,
                                    "appointment_cancelled_by_no_report",
                                    "Expert did not submit report within 24h - automatic cancellation",
                                    null,
                                    updateState: false);
                            }
                            catch
                            {
                                // Log error pero continuar
                            }
                        }
                        break;

                    case "client_decision":
                        // Si el cliente no aprueba/disputa en 24h, completar automáticamente a favor del experto
                        try
                        {
                            var moneySuccess = await _refundService.ProcessMoneyDistributionAsync(
                                timer.Appointment.SearchHireId,
                                "completed_without_client_approval",
                                "Client did not respond within 24h - automatic completion in favor of expert",
                                null);

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
                                        Status = "completed_without_client_approval",
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
                                        Status = "completed_without_client_approval",
                                        MoneyDistributionSuccess = true
                                    }
                                );
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
                                    var completedStatus = await _context.SystemStatuses
                                        .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && 
                                                                 s.StatusValue == "completed");
                                    if (completedStatus != null)
                                    {
                                        currentSearchHire.StatusId = completedStatus.Id;
                                        currentSearchHire.UpdatedAt = DateTime.UtcNow;
                                        
                                        if (currentSearchHire.Appointment != null)
                                        {
                                            var appointmentCompletedStatus = await _context.SystemStatuses
                                                .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                                         s.StatusValue == "appointment_completed_auto");
                                            if (appointmentCompletedStatus == null)
                                            {
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
                                    }
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

                await _context.SaveChangesAsync();
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
                throw;
            }
        }



        public async Task<AppointmentDto> SubmitExpertReportAsync(int appointmentId, int expertId, string? notes = null)

        {

            try

            {

                // ✅ CORRECCIÓN: Usar la estrategia de ejecución para manejar transacciones con reintentos (NpgsqlRetryingExecutionStrategy)

                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>

                {

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

                            "appointment_report_sent",

                            "appointment_cancelled_by_client",

                            "appointment_cancelled_by_client_second",

                            "appointment_cancelled_by_expert",

                            "appointment_cancelled_by_expert_second",

                            "appointment_cancelled_by_expert_rejection",

                            "appointment_cancelled_by_no_response"

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

                    catch (Exception innerEx)

                    {

                        // ✅ ROLLBACK: Revertir la transacción en caso de error

                        await transaction.RollbackAsync();

                        throw;

                    }

                });

            }

            catch (Exception ex)

            {

                // 🚨 LOG CRÍTICO: Error general enviando reporte de experto (una sola vez, con información completa)

                await _loggingService.LogCriticalAsync(

                    message: "CRITICAL: Error submitting expert report",

                    details: $"An unexpected exception occurred while submitting expert report for appointment {appointmentId}. " +

                            $"Expert {expertId} attempted to submit report. " +

                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +

                            $"Stack Trace: {ex.StackTrace}. " +

                            $"ACTION REQUIRED: Review error - report submission failed. Expert may need to retry.",

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



                // Deserializar los días de la semana

                var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(availability.DaysOfWeek) ?? new List<string>();



                if (daysOfWeek.Count == 0)

                {

                    throw new InvalidOperationException(

                        "El experto no tiene días de disponibilidad configurados."

                    );

                }



                // Obtener el día de la semana de la fecha propuesta (en inglés)

                var dayOfWeek = proposedDateTime.DayOfWeek.ToString(); // "Monday", "Tuesday", etc.



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

                        $"Fecha propuesta: {proposedDateTime:dd/MM/yyyy}"

                    );

                }



                // Obtener la hora propuesta (solo horas y minutos, sin segundos)

                var proposedTime = proposedDateTime.TimeOfDay;

                var proposedTimeOnly = new TimeSpan(proposedTime.Hours, proposedTime.Minutes, 0);



                // Verificar que la hora esté dentro del rango de disponibilidad

                if (proposedTimeOnly < availability.StartTime || proposedTimeOnly > availability.EndTime)

                {

                    var startTimeFormatted = $"{availability.StartTime.Hours:D2}:{availability.StartTime.Minutes:D2}";

                    var endTimeFormatted = $"{availability.EndTime.Hours:D2}:{availability.EndTime.Minutes:D2}";

                    var proposedTimeFormatted = $"{proposedTimeOnly.Hours:D2}:{proposedTimeOnly.Minutes:D2}";



                    throw new InvalidOperationException(

                        $"La hora propuesta ({proposedTimeFormatted}) está fuera del horario de disponibilidad del experto. " +

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

        /// Calcula la distancia entre dos puntos geográficos usando la fórmula de Haversine

        /// </summary>

        /// <summary>

        /// Envía un mensaje automático al chat cuando cambia el estado de una cita

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

