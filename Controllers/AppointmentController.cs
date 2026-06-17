using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.enums;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using System.Security.Claims;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Configuration;

namespace newApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AppointmentController : ControllerBase
    {
        private readonly IAppointmentService _appointmentService;
        private readonly IAuthorizationServices _authService;
        private readonly AppDbContext _context;
        private readonly SystemStatusService _systemStatusService;
        private readonly StorageClient _storageClient;
        private readonly ISupabaseStorageService _supabaseStorage;
        private readonly IConfiguration _configuration;
        private readonly ISignedUrlService _signedUrlService;
        private readonly ILoggingService _loggingService;

        public AppointmentController(
            IAppointmentService appointmentService,
            IAuthorizationServices authService,
            AppDbContext context,
            SystemStatusService systemStatusService,
            StorageClient storageClient,
            ISupabaseStorageService supabaseStorage,
            IConfiguration configuration,
            ISignedUrlService signedUrlService,
            ILoggingService loggingService)
        {
            _appointmentService = appointmentService;
            _authService = authService;
            _context = context;
            _systemStatusService = systemStatusService;
            _storageClient = storageClient;
            _supabaseStorage = supabaseStorage;
            _configuration = configuration;
            _signedUrlService = signedUrlService;
            _loggingService = loggingService;
        }

        /// <summary>
        /// Obtener todos los estados de citas disponibles
        /// </summary>
        [HttpGet("statuses")]
        public async Task<IActionResult> GetAppointmentStatuses()
        {
            try
            {
                var statuses = await _context.SystemStatuses
                    .Where(s => s.StatusType == "AppointmentStatus" && s.IsActive)
                    .OrderBy(s => s.SortOrder)
                    .Select(s => new {
                        id = s.Id,
                        statusValue = s.StatusValue,
                        displayName = s.DisplayName,
                        description = s.Description,
                        isFinalizationStatus = s.IsFinalizationStatus,
                        sortOrder = s.SortOrder,
                        createdAt = s.CreatedAt,
                        updatedAt = s.UpdatedAt
                    })
                    .AsNoTracking() // Optimización: no tracking para solo lectura
                    .ToListAsync();
                
                return Ok(statuses);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener configuración de distribución de dinero para un estado específico
        /// </summary>
        [HttpGet("money-distribution-config")]
        [ResponseCache(Duration = 300)] // Cache de 5 minutos
        public async Task<IActionResult> GetMoneyDistributionConfig(
            [FromQuery] string status,
            [FromQuery] int? categoryId = null,
            [FromQuery] int? serviceTypeCategoryId = null)
        {
            try
            {
                var config = await _systemStatusService.GetMoneyDistributionConfigAsync(status, categoryId, serviceTypeCategoryId);
                
                if (config == null)
                {
                    // Para estados que no requieren distribución de dinero (estados intermedios)
                    // Devolver una respuesta indicando que no aplica distribución
                    var response = new {
                        clientPercentage = "0",
                        expertPercentage = "0", 
                        platformPercentage = "0",
                        source = "no_distribution_required",
                        status = status,
                        categoryId = categoryId,
                        serviceTypeCategoryId = serviceTypeCategoryId,
                        message = "Este estado no requiere distribución de dinero"
                    };

                    return Ok(response);
                }

                var configResponse = new {
                    clientPercentage = config.ClientPercentage,
                    expertPercentage = config.ExpertPercentage,
                    platformPercentage = config.PlatformPercentage,
                    source = "dynamic", // Indica que viene de la configuración dinámica
                    status = status,
                    categoryId = categoryId,
                    serviceTypeCategoryId = serviceTypeCategoryId
                };

                return Ok(configResponse);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener una cita por ID
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetAppointment(int id)
        {
            try
            {
                // 🛡️ R9 FIX: ownership check — sin esto cualquier user autenticado leía
                // cualquier appointment (datos del cliente/experto + fecha/hora/ubicación).
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }
                var isAdmin = _authService.IsAdmin(User);

                var appointment = await _appointmentService.GetAppointmentAsync(id);
                if (appointment == null)
                    return NotFound(new { message = "Appointment not found" });

                // Ownership via SearchHire del appointment.
                var hireOwnership = await _context.SearchHires
                    .AsNoTracking()
                    .Where(sh => sh.Id == appointment.SearchHireId)
                    .Select(sh => new { sh.ClientId, sh.ExpertId })
                    .FirstOrDefaultAsync();
                if (!isAdmin && hireOwnership != null
                    && hireOwnership.ClientId != userId && hireOwnership.ExpertId != userId)
                {
                    return Forbid();
                }

                return Ok(appointment);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener una cita por SearchHire ID
        /// </summary>
        [HttpGet("search-hire/{searchHireId}")]
        public async Task<IActionResult> GetAppointmentBySearchHireId(int searchHireId)
        {
            try
            {
                // 🛡️ R10 FIX: ownership check via SearchHire — sin esto cualquier user enumeraba
                // citas por hireId ajeno.
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }
                var isAdmin = _authService.IsAdmin(User);

                var hireOwnership = await _context.SearchHires
                    .AsNoTracking()
                    .Where(sh => sh.Id == searchHireId)
                    .Select(sh => new { sh.ClientId, sh.ExpertId })
                    .FirstOrDefaultAsync();
                if (hireOwnership == null)
                {
                    return NotFound(new { message = "SearchHire not found" });
                }
                if (!isAdmin && hireOwnership.ClientId != userId && hireOwnership.ExpertId != userId)
                {
                    return Forbid();
                }

                var appointment = await _appointmentService.GetAppointmentBySearchHireIdAsync(searchHireId);
                if (appointment == null)
                    return NotFound(new { message = "Appointment not found" });

                return Ok(appointment);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener todas las citas del usuario
        /// </summary>
        [HttpGet("my-appointments")]
        public async Task<IActionResult> GetMyAppointments()
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointments = await _appointmentService.GetUserAppointmentsAsync(userId);
                return Ok(appointments);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

#if false // ═══ SISTEMA ANTIGUO: crear cita "pagar primero y luego proponer" (DESACTIVADO — sustituido por el flujo atómico cita+pago; NO BORRAR) ═══
        // ⚠️ SEGURIDAD: este endpoint creaba la cita SIN pago simultáneo, SIN comprobar
        // solape con otras citas (ValidateAppointmentAvailabilityAsync solo valida día/franja)
        // y SIN poblar StartsAtUtc/EndsAtUtc/BlocksCalendar=true, por lo que la exclusion
        // constraint GiST ux_expert_no_overlap NUNCA se aplicaba → permitía DOBLE RESERVA del
        // mismo experto. Además no verificaba que el llamante fuese dueño del SearchHire (IDOR).
        // El único flujo soportado es el atómico: SearchController.CreateSearchWithHire →
        // Checkout Stripe (manual capture) → webhook HandlePendingHireCompleted, que inserta la
        // cita confirmada dentro de la transacción y captura el pago solo si el hueco queda
        // asegurado por la constraint GiST.
        /// <summary>
        /// Crear una nueva cita
        /// </summary>
        [HttpPost("create")]
        public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.CreateAppointmentAsync(dto);
                
                await _loggingService.LogInfoAsync(
                    message: "Cita creada exitosamente",
                    details: $"Appointment {appointment.Id} creada para SearchHire {dto.SearchHireId}",
                    userId: userId,
                    source: "AppointmentController.CreateAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: appointment.Id
                );

                return CreatedAtAction(nameof(GetAppointment), new { id = appointment.Id }, appointment);
            }
            catch (Exception ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogErrorAsync(
                    message: "Error al crear cita",
                    details: $"Error creando cita para SearchHire {dto?.SearchHireId}. Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "AppointmentController.CreateAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: null,
                    additionalData: new { 
                        SearchHireId = dto?.SearchHireId,
                        ProposedDate = dto?.ProposedDate,
                        ProposedTime = dto?.ProposedTime,
                        Location = dto?.Location,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Internal server error" });
            }
        }
#endif // ═══ FIN SISTEMA ANTIGUO (crear cita "pagar primero y luego proponer") ═══

#if false // ═══ SISTEMA ANTIGUO: endpoints proponer/aceptar/rechazar cita (sin uso en prod, conservado, NO BORRAR) ═══
        /// <summary>
        /// Proponer una cita (Cliente)
        /// </summary>
        [HttpPost("propose/{searchHireId}")]
        public async Task<IActionResult> ProposeAppointment(int searchHireId, [FromBody] ProposeAppointmentDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.ProposeAppointmentAsync(searchHireId, dto, userId);
                
                await _loggingService.LogInfoAsync(
                    message: "Propuesta de cita enviada exitosamente",
                    details: $"Propuesta de cita para SearchHire {searchHireId} enviada por usuario {userId}",
                    userId: userId,
                    source: "AppointmentController.ProposeAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: appointment.Id
                );

                return Ok(appointment);
            }
            catch (UnauthorizedAccessException ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogWarningAsync(
                    message: "Intento no autorizado de proponer cita",
                    details: $"Usuario {userId} intentó proponer cita para SearchHire {searchHireId} sin autorización. Error: {ex.Message}",
                    userId: userId,
                    source: "AppointmentController.ProposeAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: null
                );

                return Unauthorized(new { message = "Only the client can propose appointments" });
            }
            catch (InvalidOperationException ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogWarningAsync(
                    message: "Operación inválida al proponer cita",
                    details: $"Usuario {userId} intentó proponer cita para SearchHire {searchHireId} con operación inválida. Error: {ex.Message}",
                    userId: userId,
                    source: "AppointmentController.ProposeAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: null
                );

                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogErrorAsync(
                    message: "Error al proponer cita",
                    details: $"Error proponiendo cita para SearchHire {searchHireId}, Usuario {userId}. Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "AppointmentController.ProposeAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: null,
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        ProposedDate = dto?.ProposedDate,
                        ProposedTime = dto?.ProposedTime,
                        Location = dto?.Location,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Confirmar una cita (Experto)
        /// </summary>
        [HttpPost("confirm")]
        public async Task<IActionResult> ConfirmAppointment([FromBody] ConfirmAppointmentDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.ConfirmAppointmentAsync(dto, userId);
                
                await _loggingService.LogInfoAsync(
                    message: "Cita confirmada exitosamente",
                    details: $"Appointment {dto.AppointmentId} confirmada por usuario {userId}",
                    userId: userId,
                    source: "AppointmentController.ConfirmAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId
                );

                return Ok(appointment);
            }
            catch (UnauthorizedAccessException ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogWarningAsync(
                    message: "Intento no autorizado de confirmar cita",
                    details: $"Usuario {userId} intentó confirmar cita {dto.AppointmentId} sin autorización. Error: {ex.Message}",
                    userId: userId,
                    source: "AppointmentController.ConfirmAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId
                );

                return Unauthorized(new { message = "Only the expert can confirm appointments" });
            }
            catch (InvalidOperationException ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogWarningAsync(
                    message: "Operación inválida al confirmar cita",
                    details: $"Usuario {userId} intentó confirmar cita {dto.AppointmentId} con operación inválida. Error: {ex.Message}",
                    userId: userId,
                    source: "AppointmentController.ConfirmAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId
                );

                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogErrorAsync(
                    message: "Error al confirmar cita",
                    details: $"Error confirmando cita {dto.AppointmentId}, Usuario {userId}. Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "AppointmentController.ConfirmAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId,
                    additionalData: new { 
                        AppointmentId = dto.AppointmentId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Rechazar una cita (Experto)
        /// </summary>
        [HttpPost("reject")]
        public async Task<IActionResult> RejectAppointment([FromBody] RejectAppointmentDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.RejectAppointmentAsync(dto, userId);
                
                await _loggingService.LogInfoAsync(
                    message: "Cita rechazada exitosamente",
                    details: $"Appointment {dto.AppointmentId} rechazada por usuario {userId}",
                    userId: userId,
                    source: "AppointmentController.RejectAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId
                );

                return Ok(appointment);
            }
            catch (UnauthorizedAccessException ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogWarningAsync(
                    message: "Intento no autorizado de rechazar cita",
                    details: $"Usuario {userId} intentó rechazar cita {dto.AppointmentId} sin autorización. Error: {ex.Message}",
                    userId: userId,
                    source: "AppointmentController.RejectAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId
                );

                return Unauthorized(new { message = "Only the expert can reject appointments" });
            }
            catch (InvalidOperationException ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogWarningAsync(
                    message: "Operación inválida al rechazar cita",
                    details: $"Usuario {userId} intentó rechazar cita {dto.AppointmentId} con operación inválida. Error: {ex.Message}",
                    userId: userId,
                    source: "AppointmentController.RejectAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId
                );

                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogErrorAsync(
                    message: "Error al rechazar cita",
                    details: $"Error rechazando cita {dto.AppointmentId}, Usuario {userId}. Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "AppointmentController.RejectAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId,
                    additionalData: new { 
                        AppointmentId = dto.AppointmentId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Internal server error" });
            }
        }
#endif // ═══ FIN SISTEMA ANTIGUO (endpoints proponer/aceptar/rechazar) ═══

        /// <summary>
        /// Cancelar una cita (Cliente o Experto)
        /// </summary>
        [HttpPost("cancel")]
        public async Task<IActionResult> CancelAppointment([FromBody] CancelAppointmentDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.CancelAppointmentAsync(dto, userId);
                
                await _loggingService.LogInfoAsync(
                    message: "Cita cancelada exitosamente",
                    details: $"Appointment {dto.AppointmentId} cancelada por usuario {userId}",
                    userId: userId,
                    source: "AppointmentController.CancelAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId
                );

                return Ok(appointment);
            }
            catch (UnauthorizedAccessException ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogWarningAsync(
                    message: "Intento no autorizado de cancelar cita",
                    details: $"Usuario {userId} intentó cancelar cita {dto.AppointmentId} sin autorización. Error: {ex.Message}",
                    userId: userId,
                    source: "AppointmentController.CancelAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId
                );

                return Unauthorized(new { message = "Only the client or expert can cancel appointments" });
            }
            // 🛡️ FIX UX idempotente (Round 28): si la cita YA está en estado final, no es un error
            // funcional — la intención del usuario ya se cumplió. Devolvemos 200 con payload mínimo
            // para que el frontend NO muestre toast de error confuso tras un doble-click o tras un
            // 500 absorbido. Este catch filtrado va ANTES del catch genérico InvalidOperationException.
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("ya está cancelada")
                || ex.Message.Contains("ya ha sido procesada")
                || ex.Message.Contains("Appointment is already cancelled"))
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogInfoAsync(
                    message: "Cancel idempotente: cita ya en estado final",
                    details: $"Usuario {userId} intentó cancelar cita {dto.AppointmentId} que ya está cancelada/finalizada. Tratado como éxito idempotente. Mensaje original: {ex.Message}",
                    userId: userId,
                    source: "AppointmentController.CancelAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId
                );

                return Ok(new { appointmentId = dto.AppointmentId, alreadyCancelled = true, message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogWarningAsync(
                    message: "Operación inválida al cancelar cita",
                    details: $"Usuario {userId} intentó cancelar cita {dto.AppointmentId} con operación inválida. Error: {ex.Message}",
                    userId: userId,
                    source: "AppointmentController.CancelAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId
                );

                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                var userId = GetCurrentUserId();
                await _loggingService.LogErrorAsync(
                    message: "Error al cancelar cita",
                    details: $"Error cancelando cita {dto.AppointmentId}, Usuario {userId}. Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "AppointmentController.CancelAppointment",
                    relatedEntityType: "Appointment",
                    relatedEntityId: dto.AppointmentId,
                    additionalData: new { 
                        AppointmentId = dto.AppointmentId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Subir reporte del experto (Experto)
        /// </summary>
        [HttpPost("submit-report/{appointmentId}")]
        public async Task<IActionResult> SubmitExpertReport(int appointmentId, [FromBody] SubmitExpertReportDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.SubmitExpertReportAsync(appointmentId, userId, dto.Notes);
                return Ok(appointment);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the expert can submit reports" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener archivos subidos para una cita (Experto)
        /// </summary>
        [HttpGet("files/{appointmentId}")]
        public async Task<IActionResult> GetAppointmentFiles(int appointmentId)
        {
            try
            {
                var userId = GetCurrentUserId();
                
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Deliverables)
                    .FirstOrDefaultAsync(a => a.Id == appointmentId);

                if (appointment == null)
                {
                    return NotFound(new { message = "Cita no encontrada" });
                }

                if (appointment.SearchHire.ExpertId != userId)
                {
                    return Forbid();
                }

                var files = appointment.SearchHire.Deliverables
                    .Select(d => new
                    {
                        Id = d.Id,
                        FileName = d.Url.Split('/').Last(),
                        FileType = d.Type,
                        Url = ResolveDeliverableUrl(d),
                        CreatedAt = d.CreatedAt
                    })
                    .ToList();

                return Ok(new { files });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Eliminar un archivo de una cita (Experto)
        /// </summary>
        [HttpDelete("files/{appointmentId}/{deliverableId}")]
        public async Task<IActionResult> DeleteAppointmentFile(int appointmentId, int deliverableId)
        {
            try
            {
                var userId = GetCurrentUserId();
                
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                    .FirstOrDefaultAsync(a => a.Id == appointmentId);

                if (appointment == null)
                {
                    return NotFound(new { message = "Cita no encontrada" });
                }

                if (appointment.SearchHire.ExpertId != userId)
                {
                    return Forbid();
                }

                var deliverable = await _context.SearchHireDeliverables
                    .FirstOrDefaultAsync(d => d.Id == deliverableId && d.SearchHireId == appointment.SearchHireId);

                if (deliverable == null)
                {
                    return NotFound(new { message = "Archivo no encontrado" });
                }

                // ✅ MIGRACIÓN: eliminar del Supabase Storage (bucket privado de ficheros) en vez de GCS.
                try
                {
                    if (!string.IsNullOrEmpty(deliverable.ObjectName))
                    {
                        await _supabaseStorage.DeleteAsync(_supabaseStorage.FilesBucket, deliverable.ObjectName);
                    }
                }
                catch (Exception ex)
                {
                }

                // Eliminar de la base de datos
                _context.SearchHireDeliverables.Remove(deliverable);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Archivo eliminado exitosamente" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Validar archivos requeridos antes de enviar reporte (Experto)
        /// </summary>
        [HttpGet("validate-files/{appointmentId}")]
        public async Task<IActionResult> ValidateRequiredFiles(int appointmentId)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.SearchService)
                            .ThenInclude(ss => ss.SelectedDeliverableTypes)
                                .ThenInclude(ssdt => ssdt.DeliverableType)
                    .Include(a => a.SearchHire)
                        .ThenInclude(sh => sh.Deliverables)
                    .FirstOrDefaultAsync(a => a.Id == appointmentId);

                if (appointment == null)
                {
                    return NotFound(new { message = "Cita no encontrada" });
                }

                if (appointment.SearchHire.ExpertId != userId)
                {
                    return Forbid();
                }

                // Obtener tipos de entregables requeridos
                var requiredDeliverableTypes = appointment.SearchHire.SearchService.SelectedDeliverableTypes
                    .Where(ssdt => ssdt.IsSelected)
                    .Select(ssdt => ssdt.DeliverableType)
                    .ToList();

                var uploadedDeliverables = appointment.SearchHire.Deliverables.ToList();
                var missingFiles = new List<string>();
                var isValid = true;


                // Verificar PDF obligatorio
                var pdfType = requiredDeliverableTypes.FirstOrDefault(dt => dt.Name == "PDF");
                if (pdfType != null)
                {
                    var hasPdf = uploadedDeliverables.Any(d => d.Type == "pdf");
                    if (!hasPdf)
                    {
                        missingFiles.Add("PDF");
                        isValid = false;
                    }
                    else
                    {
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
                        isValid = false;
                    }
                    else
                    {
                    }
                }

                var validationResult = new
                {
                    IsValid = isValid,
                    MissingFiles = missingFiles,
                    UploadedFiles = uploadedDeliverables.Select(d => new
                    {
                        Id = d.Id,
                        Type = d.Type,
                        FileName = d.Url.Split('/').Last(),
                        CreatedAt = d.CreatedAt
                    }).ToList(),
                    RequiredTypes = requiredDeliverableTypes.Select(dt => dt.Name).ToList()
                };


                return Ok(validationResult);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Subir archivos y enviar reporte del experto en una sola operación (Experto)
        /// </summary>
        [HttpPost("submit-report-with-files/{appointmentId}")]
        public async Task<IActionResult> SubmitExpertReportWithFiles(int appointmentId, [FromForm] SubmitExpertReportWithFilesDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                
                // Primero subir los archivos si los hay
                if (dto.Files != null && dto.Files.Any())
                {
                    var uploadResult = await UploadDeliverablesForAppointment(appointmentId, userId, dto.Files);
                    if (!uploadResult.Success)
                    {
                        return BadRequest(new { message = uploadResult.ErrorMessage });
                    }
                }

                // Luego enviar el reporte
                var appointment = await _appointmentService.SubmitExpertReportAsync(appointmentId, userId, dto.Notes);
                return Ok(new { 
                    message = "Reporte enviado exitosamente con archivos adjuntos",
                    appointment = appointment 
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the expert can submit reports" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }


        #region Admin Endpoints

        /// <summary>
        /// Obtener métricas de citas (Admin)
        /// </summary>
        [HttpGet("admin/metrics")]
        public async Task<IActionResult> GetAppointmentMetrics()
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                var metrics = await _appointmentService.GetAppointmentMetricsAsync();
                return Ok(metrics);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }


        /// <summary>
        /// Verificar timers de citas (Admin)
        /// </summary>
        [HttpPost("admin/check-timers")]
        public async Task<IActionResult> CheckAppointmentTimers()
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                // 🔁 FRENTE 11: se repunta al watchdog ProcessOverdueTimersAsync (re-despacha cada timer
                // vencido a ProcessAppointmentTimerAsync, que YA encola el reintento de dinero idempotente),
                // en vez del antiguo CheckAppointmentTimersAsync (deprecado: consumía mal proposal/
                // client_decision y NO reintentaba el dinero al fallar). Así el disparo manual del admin
                // usa exactamente el mismo camino fiable que los timers automáticos.
                await _appointmentService.ProcessOverdueTimersAsync();
                return Ok(new { message = "Appointment timers checked successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// 🧪 PRUEBAS (Admin): salta la espera de 3h y transiciona una cita 'appointment_confirmed'
        /// directamente a 'appointment_awaiting_report' (creando el timer expert_report de 24h).
        /// Llama al MISMO handler que el watchdog (ProcessAppointmentToAwaitingReportAsync), que
        /// re-valida el estado antes de actuar, así que es idempotente y seguro. Pensado para QA del
        /// flujo de citas sin tener que esperar/antedatar en BD.
        /// </summary>
        [HttpPost("admin/{appointmentId:int}/skip-to-awaiting-report")]
        public async Task<IActionResult> AdminSkipToAwaitingReport(int appointmentId)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }

                var appointment = await _context.Appointments
                    .Include(a => a.Status)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == appointmentId);

                if (appointment == null)
                {
                    return NotFound(new { message = $"Cita {appointmentId} no encontrada" });
                }

                // El handler solo actúa sobre 'appointment_confirmed'; avisamos explícitamente si no lo está
                // para que el botón de pruebas dé feedback útil en vez de un no-op silencioso.
                if (appointment.Status?.StatusValue != "appointment_confirmed")
                {
                    return BadRequest(new
                    {
                        message = $"La cita {appointmentId} está en '{appointment.Status?.StatusValue}', no en 'appointment_confirmed'. El salto solo aplica desde una cita confirmada.",
                        currentStatus = appointment.Status?.StatusValue
                    });
                }

                await _loggingService.LogWarningAsync(
                    message: "🧪 ADMIN TEST: salto manual confirmed→awaiting_report",
                    details: $"Un admin forzó la transición de la cita {appointmentId} a awaiting_report saltándose la espera de 3h post-cita.",
                    userId: GetCurrentUserId(),
                    source: "AppointmentController.AdminSkipToAwaitingReport",
                    relatedEntityType: "Appointment",
                    relatedEntityId: appointmentId,
                    notifyUser: false);

                await _appointmentService.ProcessAppointmentToAwaitingReportAsync(appointmentId);

                // Re-leer el estado resultante para confirmarlo al cliente
                var updated = await _context.Appointments
                    .Include(a => a.Status)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == appointmentId);

                return Ok(new
                {
                    message = "Cita transicionada a 'awaiting_report' (salto de prueba). El experto ya tiene 24h para subir el reporte.",
                    appointmentId,
                    newStatus = updated?.Status?.StatusValue
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        #endregion

        #region Private Methods

        private int GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                throw new UnauthorizedAccessException("Invalid user ID");
            }
            return userId;
        }

        /// <summary>
        /// Método auxiliar para subir deliverables para una cita
        /// </summary>
        private async Task<(bool Success, string ErrorMessage)> UploadDeliverablesForAppointment(int appointmentId, int userId, List<IFormFile> files)
        {
            try
            {
                // Obtener la cita y verificar que el usuario es el experto
                var appointment = await _context.Appointments
                    .Include(a => a.SearchHire)
                    .FirstOrDefaultAsync(a => a.Id == appointmentId);

                if (appointment == null)
                {
                    return (false, "Cita no encontrada");
                }

                if (appointment.SearchHire.ExpertId != userId)
                {
                    return (false, "Solo el experto puede subir archivos para esta cita");
                }

                var searchHireId = appointment.SearchHireId;
                var bucketName = _configuration["GoogleCloud:BucketName"];
                
                if (string.IsNullOrEmpty(bucketName))
                {
                    return (false, "Configuración de Google Cloud Storage faltante");
                }
                foreach (var file in files)
                {
                    if (file.Length > 0)
                    {

                        // Validar tipo de archivo
                        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                        if (!new[] { ".pdf", ".mp4" }.Contains(extension))
                        {
                            return (false, "Solo se permiten archivos PDF y MP4");
                        }

                        // Validar tamaño (10MB máximo)
                        if (file.Length > 10 * 1024 * 1024)
                        {
                            return (false, $"El archivo {file.FileName} excede el tamaño máximo de 10MB");
                        }

                        var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                        var objectName = $"deliverables/{uniqueFileName}";
                        var contentType = extension == ".pdf" ? "application/pdf" : "video/mp4";

                        try
                        {
                            using (var inputStream = file.OpenReadStream())
                            {
                                // ✅ MIGRACIÓN: subir a Supabase Storage (bucket privado de ficheros) en vez de GCS.
                                await _supabaseStorage.UploadAsync(
                                    _supabaseStorage.FilesBucket,
                                    objectName,
                                    inputStream,
                                    contentType
                                );
                            }

                            // ✅ MIGRACIÓN: URL firmada inicial; las lecturas la regeneran vía ResolveDeliverableUrl.
                            var deliverable = new SearchHireDeliverable
                            {
                                SearchHireId = searchHireId,
                                Url = _signedUrlService.GetSignedUrl(objectName) ?? string.Empty,
                                ObjectName = objectName,
                                Type = extension == ".pdf" ? "pdf" : "video",
                                CreatedAt = DateTime.UtcNow
                            };
                            
                            _context.SearchHireDeliverables.Add(deliverable);
                        }
                        catch (Exception ex)
                        {
                            return (false, $"Error al subir el archivo {file.FileName}");
                        }
                    }
                }
                await _context.SaveChangesAsync();
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, "Error interno al subir archivos");
            }
        }

        private string ResolveDeliverableUrl(SearchHireDeliverable? deliverable)
        {
            if (deliverable == null)
            {
                return string.Empty;
            }

            var fallback = string.IsNullOrWhiteSpace(deliverable.Url) ? string.Empty : deliverable.Url;
            return _signedUrlService.GetSignedUrl(deliverable.ObjectName ?? string.Empty) ?? fallback;
        }

        #endregion
    }
}
