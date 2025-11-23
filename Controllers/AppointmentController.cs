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
        private readonly IConfiguration _configuration;
        private readonly ISignedUrlService _signedUrlService;

        public AppointmentController(
            IAppointmentService appointmentService, 
            IAuthorizationServices authService,
            AppDbContext context,
            SystemStatusService systemStatusService,
            StorageClient storageClient,
            IConfiguration configuration,
            ISignedUrlService signedUrlService)
        {
            _appointmentService = appointmentService;
            _authService = authService;
            _context = context;
            _systemStatusService = systemStatusService;
            _storageClient = storageClient;
            _configuration = configuration;
            _signedUrlService = signedUrlService;
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
                var appointment = await _appointmentService.GetAppointmentAsync(id);
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
        /// Obtener una cita por SearchHire ID
        /// </summary>
        [HttpGet("search-hire/{searchHireId}")]
        public async Task<IActionResult> GetAppointmentBySearchHireId(int searchHireId)
        {
            try
            {
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

        /// <summary>
        /// Crear una nueva cita
        /// </summary>
        [HttpPost("create")]
        public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentDto dto)
        {
            try
            {
                var appointment = await _appointmentService.CreateAppointmentAsync(dto);
                return CreatedAtAction(nameof(GetAppointment), new { id = appointment.Id }, appointment);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

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
                return Ok(appointment);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the client can propose appointments" });
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
        /// Confirmar una cita (Experto)
        /// </summary>
        [HttpPost("confirm")]
        public async Task<IActionResult> ConfirmAppointment([FromBody] ConfirmAppointmentDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.ConfirmAppointmentAsync(dto, userId);
                return Ok(appointment);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the expert can confirm appointments" });
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
        /// Rechazar una cita (Experto)
        /// </summary>
        [HttpPost("reject")]
        public async Task<IActionResult> RejectAppointment([FromBody] RejectAppointmentDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.RejectAppointmentAsync(dto, userId);
                return Ok(appointment);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the expert can reject appointments" });
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
        /// Cancelar una cita (Cliente o Experto)
        /// </summary>
        [HttpPost("cancel")]
        public async Task<IActionResult> CancelAppointment([FromBody] CancelAppointmentDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.CancelAppointmentAsync(dto, userId);
                return Ok(appointment);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the client or expert can cancel appointments" });
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

                // Eliminar del Google Cloud Storage
                try
                {
                    var bucketName = _configuration["GoogleCloud:BucketName"];
                    if (!string.IsNullOrEmpty(bucketName) && !string.IsNullOrEmpty(deliverable.ObjectName))
                    {
                        await _storageClient.DeleteObjectAsync(bucketName, deliverable.ObjectName);
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
                await _appointmentService.CheckAppointmentTimersAsync();
                return Ok(new { message = "Appointment timers checked successfully" });
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
                                await _storageClient.UploadObjectAsync(
                                    bucket: bucketName,
                                    objectName: objectName,
                                    contentType: contentType,
                                    source: inputStream
                                    // ✅ FIX: Quitar PredefinedAcl cuando el bucket tiene uniform bucket-level access habilitado
                                    // El acceso se controla mediante IAM policies del bucket, no ACLs por objeto
                                    // options: new UploadObjectOptions { PredefinedAcl = PredefinedObjectAcl.Private }
                                );
                            }

                            var deliverableUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
                            var deliverable = new SearchHireDeliverable
                            {
                                SearchHireId = searchHireId,
                                Url = deliverableUrl,
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
