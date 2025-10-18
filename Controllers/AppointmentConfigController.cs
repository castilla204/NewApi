using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Controllers
{
    /// <summary>
    /// Controlador de compatibilidad para mantener las rutas del frontend
    /// Redirige a SystemStatusController
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [AllowAnonymous] // Permitir acceso sin autenticación para compatibilidad
    public class AppointmentConfigController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AppointmentConfigController> _logger;

        public AppointmentConfigController(AppDbContext context, ILogger<AppointmentConfigController> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// Endpoint de compatibilidad: /api/AppointmentConfig/appointment-status
        /// Redirige a SystemStatusController
        /// </summary>
        [HttpGet("appointment-status")]
        public async Task<IActionResult> GetAppointmentStatusConfigs()
        {
            try
            {
                var appointmentStatuses = await _context.SystemStatuses
                    .Where(s => s.IsFinalizationStatus && 
                                s.IsActive)
                    .OrderBy(s => s.StatusType)
                    .ThenBy(s => s.SortOrder)
                    .Select(s => new
                    {
                        s.Id,
                        s.StatusName,
                        s.StatusValue,
                        s.DisplayName,
                        s.SortOrder,
                        s.CreatedAt,
                        s.UpdatedAt
                    })
                    .ToListAsync();

                return Ok(appointmentStatuses);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting appointment status configs for compatibility");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Endpoint de compatibilidad: /api/AppointmentConfig/service-type-category
        /// Redirige a SystemStatusController
        /// </summary>
        [HttpGet("service-type-category")]
        public async Task<IActionResult> GetServiceTypeCategoryConfigs()
        {
            try
            {
                var serviceTypeCategories = await _context.ServiceTypeCategories
                    .Where(stc => stc.IsActive)
                    .OrderBy(stc => stc.Name)
                    .Select(stc => new
                    {
                        stc.Id,
                        stc.Name,
                        stc.IsActive,
                        stc.CreatedAt,
                        stc.UpdatedAt
                    })
                    .ToListAsync();

                return Ok(serviceTypeCategories);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting service type category configs for compatibility");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Endpoint de compatibilidad: Devuelve configuraciones de distribución de dinero por estado de cita
        /// Para el panel de administración
        /// </summary>
        [HttpGet("appointment-status-configs")]
        public async Task<IActionResult> GetAppointmentStatusConfigurations()
        {
            try
            {
                var configs = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .Where(sc => sc.Status.IsFinalizationStatus && 
                                sc.IsActive &&
                                sc.CategoryId == null && 
                                sc.ServiceTypeCategoryId == null)
                    .OrderBy(sc => sc.Status.StatusType)
                    .ThenBy(sc => sc.Status.SortOrder)
                    .Select(sc => new
                    {
                        Id = sc.Id,
                        Estado = sc.Status.DisplayName, // ✅ Usando la relación con SystemStatus
                        StatusId = sc.Status.Id,        // ✅ ID del estado para referencia
                        StatusValue = sc.Status.StatusValue,
                        StatusName = sc.Status.StatusName,
                        Cliente = sc.ClientPercentage,
                        Experto = sc.ExpertPercentage,
                        Plataforma = sc.PlatformPercentage,
                        Prioridad = "Nivel 4 - Por Defecto", // Valor fijo para compatibilidad
                        Activo = sc.IsActive ? "Activo" : "Inactivo",
                        // Información de la categoría (si aplica)
                        CategoryId = sc.CategoryId,
                        CategoryName = sc.Category != null ? sc.Category.Name : "Todas las categorías",
                        // Información del tipo de servicio (si aplica)
                        ServiceTypeCategoryId = sc.ServiceTypeCategoryId,
                        ServiceTypeCategoryName = sc.ServiceTypeCategory != null ? sc.ServiceTypeCategory.Name : "Todos los tipos",
                        CreatedAt = sc.CreatedAt,
                        UpdatedAt = sc.UpdatedAt
                    })
                    .ToListAsync();

                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting appointment status configurations for compatibility");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Endpoint de debug: Verificar qué datos hay en la base de datos
        /// </summary>
        [HttpGet("debug-status-data")]
        public async Task<IActionResult> DebugStatusData()
        {
            try
            {
                // Verificar SystemStatuses de AppointmentStatus
                var appointmentStatuses = await _context.SystemStatuses
                    .Where(s => s.StatusType == "AppointmentStatus")
                    .Select(s => new
                    {
                        s.Id,
                        s.StatusType,
                        s.StatusName,
                        s.StatusValue,
                        s.DisplayName,
                        s.IsActive
                    })
                    .ToListAsync();

                // Verificar StatusConfigurations para AppointmentStatus
                var statusConfigs = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Where(sc => sc.Status.StatusType == "AppointmentStatus")
                    .Select(sc => new
                    {
                        sc.Id,
                        StatusId = sc.StatusId,
                        StatusName = sc.Status.StatusName,
                        StatusValue = sc.Status.StatusValue,
                        StatusDisplayName = sc.Status.DisplayName,
                        sc.ClientPercentage,
                        sc.ExpertPercentage,
                        sc.PlatformPercentage,
                        sc.IsActive
                    })
                    .ToListAsync();

                return Ok(new
                {
                    message = "Debug data for AppointmentStatus",
                    appointmentStatusesCount = appointmentStatuses.Count,
                    appointmentStatuses,
                    statusConfigsCount = statusConfigs.Count,
                    statusConfigs
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting debug status data");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        /// <summary>
        /// Crear configuraciones por categoría para todos los estados
        /// </summary>
        [HttpPost("create-category-configurations")]
        public async Task<IActionResult> CreateCategoryConfigurations()
        {
            try
            {
                // Obtener todos los estados de AppointmentStatus
                var appointmentStatuses = await _context.SystemStatuses
                    .Where(s => s.StatusType == "AppointmentStatus")
                    .ToListAsync();

                // Obtener todas las categorías
                var categories = await _context.Categories
                    .Where(c => c.IsActive)
                    .ToListAsync();

                var createdConfigs = new List<object>();

                foreach (var category in categories)
                {
                    foreach (var status in appointmentStatuses)
                    {
                        // Verificar si ya existe una configuración para esta categoría y estado
                        var existingConfig = await _context.StatusConfigurations
                            .FirstOrDefaultAsync(sc => sc.StatusId == status.Id && 
                                                      sc.CategoryId == category.Id && 
                                                      sc.ServiceTypeCategoryId == null);

                        if (existingConfig == null)
                        {
                            // Crear nueva configuración por categoría
                            var newConfig = new StatusConfiguration
                            {
                                StatusId = status.Id,
                                CategoryId = category.Id,
                                ServiceTypeCategoryId = null, // Nivel 3 - Granularidad Básica
                                ClientPercentage = GetDefaultClientPercentage(status.StatusValue),
                                ExpertPercentage = GetDefaultExpertPercentage(status.StatusValue),
                                PlatformPercentage = GetDefaultPlatformPercentage(status.StatusValue),
                                IsActive = true,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            _context.StatusConfigurations.Add(newConfig);
                            await _context.SaveChangesAsync();

                            createdConfigs.Add(new
                            {
                                Id = newConfig.Id,
                                StatusName = status.DisplayName,
                                CategoryName = category.Name,
                                ClientPercentage = newConfig.ClientPercentage,
                                ExpertPercentage = newConfig.ExpertPercentage,
                                PlatformPercentage = newConfig.PlatformPercentage
                            });
                        }
                    }
                }

                return Ok(new
                {
                    message = "Configuraciones por categoría creadas exitosamente",
                    createdCount = createdConfigs.Count,
                    configurations = createdConfigs
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating category configurations");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        /// <summary>
        /// Consulta directa a la base de datos para verificar StatusConfigurations
        /// </summary>
        [HttpGet("debug-database-query")]
        public async Task<IActionResult> GetDebugDatabaseQuery()
        {
            try
            {
                var query = from sc in _context.StatusConfigurations
                           join s in _context.SystemStatuses on sc.StatusId equals s.Id
                           join c in _context.Categories on sc.CategoryId equals c.Id into categoryGroup
                           from c in categoryGroup.DefaultIfEmpty()
                           join stc in _context.ServiceTypeCategories on sc.ServiceTypeCategoryId equals stc.Id into serviceTypeGroup
                           from stc in serviceTypeGroup.DefaultIfEmpty()
                           where s.StatusType == "AppointmentStatus"
                           orderby sc.CategoryId, sc.ServiceTypeCategoryId, sc.StatusId
                           select new
                           {
                               Id = sc.Id,
                               StatusId = sc.StatusId,
                               StatusName = s.DisplayName,
                               StatusValue = s.StatusValue,
                               CategoryId = sc.CategoryId,
                               CategoryName = c != null ? c.Name : "NULL",
                               ServiceTypeCategoryId = sc.ServiceTypeCategoryId,
                               ServiceTypeName = stc != null ? stc.Name : "NULL",
                               ClientPercentage = sc.ClientPercentage,
                               ExpertPercentage = sc.ExpertPercentage,
                               PlatformPercentage = sc.PlatformPercentage,
                               IsActive = sc.IsActive,
                               CreatedAt = sc.CreatedAt
                           };

                var results = await query.ToListAsync();

                return Ok(new
                {
                    totalConfigurations = results.Count,
                    configurations = results,
                    summary = new
                    {
                        withCategory = results.Count(r => r.CategoryId != null),
                        withServiceType = results.Count(r => r.ServiceTypeCategoryId != null),
                        withBoth = results.Count(r => r.CategoryId != null && r.ServiceTypeCategoryId != null),
                        withNeither = results.Count(r => r.CategoryId == null && r.ServiceTypeCategoryId == null)
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in debug database query");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        /// <summary>
        /// Endpoint de debug: Ver qué datos está enviando el frontend
        /// </summary>
        [HttpPost("debug-post-data")]
        public async Task<IActionResult> DebugPostData([FromBody] object request)
        {
            try
            {
                _logger.LogInformation("=== DEBUG POST DATA ===");
                _logger.LogInformation("Request Body: {RequestBody}", System.Text.Json.JsonSerializer.Serialize(request));
                _logger.LogInformation("Content-Type: {ContentType}", Request.ContentType);
                _logger.LogInformation("Headers: {Headers}", string.Join(", ", Request.Headers.Select(h => $"{h.Key}: {h.Value}")));
                
                return Ok(new
                {
                    message = "Datos recibidos correctamente",
                    receivedData = request,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en debug POST data");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        /// <summary>
        /// Crear o actualizar configuración de distribución de dinero para estado de cita
        /// </summary>
        [HttpPost("appointment-status-configs")]
        public async Task<IActionResult> CreateOrUpdateAppointmentStatusConfiguration([FromBody] CreateAppointmentStatusConfigRequest request)
        {
            try
            {
                _logger.LogInformation("=== APPOINTMENT STATUS CONFIG ACTION ===");
                _logger.LogInformation("Action: {Action}, Request: {Request}", request.Action, System.Text.Json.JsonSerializer.Serialize(request));

                // Manejar acción DELETE
                if (request.Action == "delete" && request.ConfigId.HasValue)
                {
                    return await HandleDeleteAction(request.ConfigId.Value);
                }

                // Manejar acción UPDATE
                if (request.Action == "update" && request.ConfigId.HasValue)
                {
                    return await HandleUpdateAction(request);
                }

                // Por defecto, manejar como CREATE
                return await HandleCreateAction(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in appointment status configuration action");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        private async Task<IActionResult> HandleDeleteAction(int configId)
        {
            try
            {
                _logger.LogInformation("=== HANDLING DELETE ACTION ===");
                _logger.LogInformation("Config ID: {ConfigId}", configId);

                var config = await _context.StatusConfigurations.FindAsync(configId);
                if (config == null)
                {
                    _logger.LogError("Configuration not found: ConfigId={ConfigId}", configId);
                    return NotFound(new { message = $"Configuración con ID {configId} no encontrada" });
                }

                _context.StatusConfigurations.Remove(config);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Configuration deleted successfully: ConfigId={ConfigId}", configId);

                return Ok(new { message = "Configuración eliminada correctamente", deletedId = configId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in delete action");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        private async Task<IActionResult> HandleUpdateAction(CreateAppointmentStatusConfigRequest request)
        {
            try
            {
                _logger.LogInformation("=== HANDLING UPDATE ACTION ===");
                _logger.LogInformation("Config ID: {ConfigId}", request.ConfigId);

                // Buscar la configuración existente
                var existingConfig = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .FirstOrDefaultAsync(sc => sc.Id == request.ConfigId);

                if (existingConfig == null)
                {
                    _logger.LogError("Configuration not found: ConfigId={ConfigId}", request.ConfigId);
                    return NotFound(new { message = $"Configuración con ID {request.ConfigId} no encontrada" });
                }

                // Validar que el StatusId esté presente
                if (request.StatusId <= 0)
                {
                    _logger.LogError("StatusId is invalid: {StatusId}", request.StatusId);
                    return BadRequest(new { message = "StatusId es requerido y debe ser válido" });
                }

                // Validar que el estado existe y es de finalización
                var status = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.Id == request.StatusId && s.IsFinalizationStatus);

                if (status == null)
                {
                    _logger.LogError("Status not found: StatusId={StatusId}", request.StatusId);
                    return BadRequest(new { message = $"Estado con ID {request.StatusId} no encontrado o no es un estado de finalización" });
                }

                // Validar que los porcentajes sumen 100%
                var totalPercentage = request.ClientPercentage + request.ExpertPercentage + request.PlatformPercentage;
                if (totalPercentage != 100)
                {
                    _logger.LogError("Percentages don't sum 100%: {Total}%", totalPercentage);
                    return BadRequest(new { 
                        message = $"Los porcentajes deben sumar 100%. Actual: {totalPercentage}%",
                        clientPercentage = request.ClientPercentage,
                        expertPercentage = request.ExpertPercentage,
                        platformPercentage = request.PlatformPercentage,
                        total = totalPercentage
                    });
                }

                // Actualizar la configuración existente
                existingConfig.StatusId = request.StatusId;
                existingConfig.CategoryId = request.CategoryId;
                existingConfig.ServiceTypeCategoryId = request.ServiceTypeCategoryId;
                existingConfig.ClientPercentage = request.ClientPercentage;
                existingConfig.ExpertPercentage = request.ExpertPercentage;
                existingConfig.PlatformPercentage = request.PlatformPercentage;
                existingConfig.IsActive = request.IsActive;
                existingConfig.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                _logger.LogInformation("Configuration updated successfully: ConfigId={ConfigId}", request.ConfigId);

                // Devolver la configuración actualizada
                var updatedConfig = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .Where(sc => sc.Id == request.ConfigId)
                    .Select(sc => new
                    {
                        Id = sc.Id,
                        Estado = sc.Status.DisplayName,
                        StatusId = sc.StatusId,
                        StatusValue = sc.Status.StatusValue,
                        Cliente = sc.ClientPercentage,
                        Experto = sc.ExpertPercentage,
                        Plataforma = sc.PlatformPercentage,
                        Prioridad = "Nivel 4 - Por Defecto",
                        Activo = sc.IsActive ? "Activo" : "Inactivo",
                        CategoryName = sc.Category != null ? sc.Category.Name : "Todas las categorías",
                        ServiceTypeCategoryName = sc.ServiceTypeCategory != null ? sc.ServiceTypeCategory.Name : "Todos los tipos",
                        CreatedAt = sc.CreatedAt,
                        UpdatedAt = sc.UpdatedAt
                    })
                    .FirstOrDefaultAsync();

                return Ok(updatedConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in update action");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        private async Task<IActionResult> HandleCreateAction(CreateAppointmentStatusConfigRequest request)
        {
            try
            {
                _logger.LogInformation("=== HANDLING CREATE ACTION ===");

                // Validar que el request no sea null
                if (request == null)
                {
                    _logger.LogError("Request is null");
                    return BadRequest(new { message = "Request body is required" });
                }

                // Validar que el StatusId esté presente
                if (request.StatusId <= 0)
                {
                    _logger.LogError("StatusId is invalid: {StatusId}", request.StatusId);
                    return BadRequest(new { message = "StatusId es requerido y debe ser válido" });
                }

                // Validar que el estado existe y es de finalización
                var status = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.Id == request.StatusId && s.IsFinalizationStatus);

                if (status == null)
                {
                    _logger.LogError("Status not found: StatusId={StatusId}", request.StatusId);
                    return BadRequest(new { message = $"Estado con ID {request.StatusId} no encontrado o no es un estado de finalización" });
                }

                _logger.LogInformation("Status found: {StatusName} ({StatusValue})", status.StatusName, status.StatusValue);

                // Validar que los porcentajes sumen 100%
                var totalPercentage = request.ClientPercentage + request.ExpertPercentage + request.PlatformPercentage;
                if (totalPercentage != 100)
                {
                    _logger.LogError("Percentages don't sum 100%: {Total}%", totalPercentage);
                    return BadRequest(new { 
                        message = $"Los porcentajes deben sumar 100%. Actual: {totalPercentage}%",
                        clientPercentage = request.ClientPercentage,
                        expertPercentage = request.ExpertPercentage,
                        platformPercentage = request.PlatformPercentage,
                        total = totalPercentage
                    });
                }

                // Verificar si ya existe una configuración para este estado
                var existingConfig = await _context.StatusConfigurations
                    .FirstOrDefaultAsync(sc => sc.StatusId == request.StatusId && 
                                               sc.CategoryId == request.CategoryId && 
                                               sc.ServiceTypeCategoryId == request.ServiceTypeCategoryId);

                if (existingConfig != null)
                {
                    _logger.LogInformation("Configuration exists, updating instead of creating. ConfigId={ConfigId}", existingConfig.Id);
                    
                    // ACTUALIZAR configuración existente en lugar de crear nueva
                    existingConfig.ClientPercentage = request.ClientPercentage;
                    existingConfig.ExpertPercentage = request.ExpertPercentage;
                    existingConfig.PlatformPercentage = request.PlatformPercentage;
                    existingConfig.IsActive = request.IsActive;
                    existingConfig.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Configuration updated successfully: ConfigId={ConfigId}", existingConfig.Id);

                    // Devolver la configuración actualizada
                    var updatedConfig = await _context.StatusConfigurations
                        .Include(sc => sc.Status)
                        .Include(sc => sc.Category)
                        .Include(sc => sc.ServiceTypeCategory)
                        .Where(sc => sc.Id == existingConfig.Id)
                        .Select(sc => new
                        {
                            Id = sc.Id,
                            Estado = sc.Status.DisplayName,
                            StatusId = sc.StatusId,
                            StatusValue = sc.Status.StatusValue,
                            Cliente = sc.ClientPercentage,
                            Experto = sc.ExpertPercentage,
                            Plataforma = sc.PlatformPercentage,
                            Prioridad = "Nivel 4 - Por Defecto",
                            Activo = sc.IsActive ? "Activo" : "Inactivo",
                            CategoryName = sc.Category != null ? sc.Category.Name : "Todas las categorías",
                            ServiceTypeCategoryName = sc.ServiceTypeCategory != null ? sc.ServiceTypeCategory.Name : "Todos los tipos",
                            CreatedAt = sc.CreatedAt,
                            UpdatedAt = sc.UpdatedAt
                        })
                        .FirstOrDefaultAsync();

                    return Ok(updatedConfig);
                }

                // Crear nueva configuración
                var newConfig = new StatusConfiguration
                {
                    StatusId = request.StatusId,
                    CategoryId = request.CategoryId,
                    ServiceTypeCategoryId = request.ServiceTypeCategoryId,
                    ClientPercentage = request.ClientPercentage,
                    ExpertPercentage = request.ExpertPercentage,
                    PlatformPercentage = request.PlatformPercentage,
                    IsActive = request.IsActive,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.StatusConfigurations.Add(newConfig);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Configuration created successfully with ID: {ConfigId}", newConfig.Id);

                // Devolver la configuración creada con información del estado
                var createdConfig = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .Where(sc => sc.Id == newConfig.Id)
                    .Select(sc => new
                    {
                        Id = sc.Id,
                        Estado = sc.Status.DisplayName,
                        StatusId = sc.StatusId,
                        StatusValue = sc.Status.StatusValue,
                        Cliente = sc.ClientPercentage,
                        Experto = sc.ExpertPercentage,
                        Plataforma = sc.PlatformPercentage,
                        Prioridad = "Nivel 4 - Por Defecto",
                        Activo = sc.IsActive ? "Activo" : "Inactivo",
                        CategoryName = sc.Category != null ? sc.Category.Name : "Todas las categorías",
                        ServiceTypeCategoryName = sc.ServiceTypeCategory != null ? sc.ServiceTypeCategory.Name : "Todos los tipos",
                        CreatedAt = sc.CreatedAt,
                        UpdatedAt = sc.UpdatedAt
                    })
                    .FirstOrDefaultAsync();

                return CreatedAtAction(nameof(GetAppointmentStatusConfigurations), new { id = newConfig.Id }, createdConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating appointment status configuration");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        /// <summary>
        /// Actualizar configuración existente de distribución de dinero para estado de cita
        /// </summary>
        [HttpPut("appointment-status/{id}")]
        public async Task<IActionResult> UpdateAppointmentStatusConfiguration(int id, [FromBody] CreateAppointmentStatusConfigRequest request)
        {
            try
            {
                _logger.LogInformation("=== UPDATING APPOINTMENT STATUS CONFIG ===");
                _logger.LogInformation("Config ID: {ConfigId}", id);
                _logger.LogInformation("Request: {Request}", System.Text.Json.JsonSerializer.Serialize(request));

                // Validar que el request no sea null
                if (request == null)
                {
                    _logger.LogError("Request is null");
                    return BadRequest(new { message = "Request body is required" });
                }

                // Buscar la configuración existente
                var existingConfig = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .FirstOrDefaultAsync(sc => sc.Id == id);

                if (existingConfig == null)
                {
                    _logger.LogError("Configuration not found: ConfigId={ConfigId}", id);
                    return NotFound(new { message = $"Configuración con ID {id} no encontrada" });
                }

                // Validar que el StatusId esté presente
                if (request.StatusId <= 0)
                {
                    _logger.LogError("StatusId is invalid: {StatusId}", request.StatusId);
                    return BadRequest(new { message = "StatusId es requerido y debe ser válido" });
                }

                // Validar que el estado existe y es de finalización
                var status = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.Id == request.StatusId && s.IsFinalizationStatus);

                if (status == null)
                {
                    _logger.LogError("Status not found: StatusId={StatusId}", request.StatusId);
                    return BadRequest(new { message = $"Estado con ID {request.StatusId} no encontrado o no es un estado de finalización" });
                }

                _logger.LogInformation("Status found: {StatusName} ({StatusValue})", status.StatusName, status.StatusValue);

                // Validar que los porcentajes sumen 100%
                var totalPercentage = request.ClientPercentage + request.ExpertPercentage + request.PlatformPercentage;
                if (totalPercentage != 100)
                {
                    _logger.LogError("Percentages don't sum 100%: {Total}%", totalPercentage);
                    return BadRequest(new { 
                        message = $"Los porcentajes deben sumar 100%. Actual: {totalPercentage}%",
                        clientPercentage = request.ClientPercentage,
                        expertPercentage = request.ExpertPercentage,
                        platformPercentage = request.PlatformPercentage,
                        total = totalPercentage
                    });
                }

                // Verificar si ya existe otra configuración para este estado (excluyendo la actual)
                var duplicateConfig = await _context.StatusConfigurations
                    .FirstOrDefaultAsync(sc => sc.Id != id &&
                                               sc.StatusId == request.StatusId && 
                                               sc.CategoryId == request.CategoryId && 
                                               sc.ServiceTypeCategoryId == request.ServiceTypeCategoryId);

                if (duplicateConfig != null)
                {
                    _logger.LogError("Duplicate configuration exists for StatusId={StatusId}, CategoryId={CategoryId}, ServiceTypeCategoryId={ServiceTypeCategoryId}", 
                        request.StatusId, request.CategoryId, request.ServiceTypeCategoryId);
                    return BadRequest(new { message = "Ya existe otra configuración para este estado, categoría y tipo de servicio" });
                }

                // Actualizar la configuración existente
                existingConfig.StatusId = request.StatusId;
                existingConfig.CategoryId = request.CategoryId;
                existingConfig.ServiceTypeCategoryId = request.ServiceTypeCategoryId;
                existingConfig.ClientPercentage = request.ClientPercentage;
                existingConfig.ExpertPercentage = request.ExpertPercentage;
                existingConfig.PlatformPercentage = request.PlatformPercentage;
                existingConfig.IsActive = request.IsActive;
                existingConfig.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                _logger.LogInformation("Configuration updated successfully: ConfigId={ConfigId}", id);

                // Devolver la configuración actualizada con información del estado
                var updatedConfig = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .Where(sc => sc.Id == id)
                    .Select(sc => new
                    {
                        Id = sc.Id,
                        Estado = sc.Status.DisplayName,
                        StatusId = sc.StatusId,
                        StatusValue = sc.Status.StatusValue,
                        Cliente = sc.ClientPercentage,
                        Experto = sc.ExpertPercentage,
                        Plataforma = sc.PlatformPercentage,
                        Prioridad = "Nivel 4 - Por Defecto",
                        Activo = sc.IsActive ? "Activo" : "Inactivo",
                        CategoryName = sc.Category != null ? sc.Category.Name : "Todas las categorías",
                        ServiceTypeCategoryName = sc.ServiceTypeCategory != null ? sc.ServiceTypeCategory.Name : "Todos los tipos",
                        CreatedAt = sc.CreatedAt,
                        UpdatedAt = sc.UpdatedAt
                    })
                    .FirstOrDefaultAsync();

                return Ok(updatedConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating appointment status configuration");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        /// <summary>
        /// Eliminar configuración de distribución de dinero para estado de cita
        /// </summary>
        [HttpPost("appointment-status-delete/{id}")]
        public async Task<IActionResult> DeleteAppointmentStatusConfiguration(int id)
        {
            try
            {
                _logger.LogInformation("=== DELETING APPOINTMENT STATUS CONFIG ===");
                _logger.LogInformation("Config ID: {ConfigId}", id);

                var config = await _context.StatusConfigurations.FindAsync(id);
                if (config == null)
                {
                    _logger.LogError("Configuration not found: ConfigId={ConfigId}", id);
                    return NotFound(new { message = $"Configuración con ID {id} no encontrada" });
                }

                _context.StatusConfigurations.Remove(config);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Configuration deleted successfully: ConfigId={ConfigId}", id);

                return Ok(new { message = "Configuración eliminada correctamente", deletedId = id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting appointment status configuration");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }


        /// <summary>
        /// Obtener todas las configuraciones granulares (Nivel 1 - Máxima Granularidad)
        /// </summary>
        [HttpGet("granular-configurations")]
        public async Task<IActionResult> GetAllGranularConfigurations()
        {
            try
            {
                var configs = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .Where(sc => sc.Status.IsFinalizationStatus &&
                                sc.IsActive && 
                                sc.CategoryId != null && 
                                sc.ServiceTypeCategoryId != null)
                    .OrderBy(sc => sc.CategoryId)
                    .ThenBy(sc => sc.ServiceTypeCategoryId)
                    .ThenBy(sc => sc.Status.SortOrder)
                    .Select(sc => new
                    {
                        Id = sc.Id,
                        Estado = sc.Status.DisplayName,
                        StatusId = sc.StatusId,
                        StatusValue = sc.Status.StatusValue,
                        Cliente = sc.ClientPercentage,
                        Experto = sc.ExpertPercentage,
                        Plataforma = sc.PlatformPercentage,
                        Prioridad = "Nivel 1 - Máxima Granularidad",
                        Activo = sc.IsActive ? "Activo" : "Inactivo",
                        CategoryId = sc.CategoryId,
                        CategoryName = sc.Category.Name,
                        ServiceTypeCategoryId = sc.ServiceTypeCategoryId,
                        ServiceTypeCategoryName = sc.ServiceTypeCategory.Name,
                        CreatedAt = sc.CreatedAt,
                        UpdatedAt = sc.UpdatedAt
                    })
                    .ToListAsync();

                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all granular configurations");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        /// <summary>
        /// Obtener configuraciones granulares por categoría y tipo de servicio específicos
        /// </summary>
        [HttpGet("granular-configurations/{categoryId}/{serviceTypeId}")]
        public async Task<IActionResult> GetGranularConfigurationsByCategoryAndService(int categoryId, int serviceTypeId)
        {
            try
            {
                var configs = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .Where(sc => sc.Status.IsFinalizationStatus &&
                                sc.IsActive && 
                                sc.CategoryId != null && 
                                sc.ServiceTypeCategoryId != null)
                    .OrderBy(sc => sc.CategoryId)
                    .ThenBy(sc => sc.ServiceTypeCategoryId)
                    .ThenBy(sc => sc.Status.SortOrder)
                    .Select(sc => new
                    {
                        Id = sc.Id,
                        Estado = sc.Status.DisplayName,
                        StatusId = sc.StatusId,
                        StatusValue = sc.Status.StatusValue,
                        Cliente = sc.ClientPercentage,
                        Experto = sc.ExpertPercentage,
                        Plataforma = sc.PlatformPercentage,
                        Prioridad = "Nivel 1 - Máxima Granularidad",
                        Activo = sc.IsActive ? "Activo" : "Inactivo",
                        CategoryId = sc.CategoryId,
                        CategoryName = sc.Category.Name,
                        ServiceTypeCategoryId = sc.ServiceTypeCategoryId,
                        ServiceTypeCategoryName = sc.ServiceTypeCategory.Name,
                        CreatedAt = sc.CreatedAt,
                        UpdatedAt = sc.UpdatedAt
                    })
                    .ToListAsync();

                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting granular configurations");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        /// <summary>
        /// Obtener todas las categorías disponibles
        /// </summary>
        [HttpGet("categories")]
        public async Task<IActionResult> GetCategories()
        {
            try
            {
                var categories = await _context.Categories
                    .Where(c => c.IsActive)
                    .OrderBy(c => c.Name)
                    .Select(c => new
                    {
                        Id = c.Id,
                        Name = c.Name,
                        IsActive = c.IsActive
                    })
                    .ToListAsync();

                return Ok(categories);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting categories");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        /// <summary>
        /// Obtener todos los tipos de servicio disponibles
        /// </summary>
        [HttpGet("service-types")]
        public async Task<IActionResult> GetServiceTypes()
        {
            try
            {
                var serviceTypes = await _context.ServiceTypeCategories
                    .Where(st => st.IsActive)
                    .OrderBy(st => st.Name)
                    .Select(st => new
                    {
                        Id = st.Id,
                        Name = st.Name,
                        IsActive = st.IsActive
                    })
                    .ToListAsync();

                return Ok(serviceTypes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting service types");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        /// <summary>
        /// Obtener configuraciones por categoría (Nivel 3 - Granularidad Básica)
        /// </summary>
        [HttpGet("configurations-by-category")]
        public async Task<IActionResult> GetConfigurationsByCategoryForFrontend()
        {
            try
            {
                var configs = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .Where(sc => sc.Status.IsFinalizationStatus && 
                                sc.IsActive && 
                                sc.CategoryId != null && 
                                sc.ServiceTypeCategoryId == null)
                    .OrderBy(sc => sc.CategoryId)
                    .ThenBy(sc => sc.Status.SortOrder)
                    .Select(sc => new
                    {
                        Id = sc.Id,
                        Estado = sc.Status.DisplayName,
                        StatusId = sc.StatusId,
                        StatusValue = sc.Status.StatusValue,
                        Cliente = sc.ClientPercentage,
                        Experto = sc.ExpertPercentage,
                        Plataforma = sc.PlatformPercentage,
                        Prioridad = "Nivel 3 - Granularidad Básica",
                        Activo = sc.IsActive ? "Activo" : "Inactivo",
                        CategoryId = sc.CategoryId,
                        CategoryName = sc.Category.Name,
                        ServiceTypeCategoryId = sc.ServiceTypeCategoryId,
                        ServiceTypeCategoryName = "Todos los tipos",
                        CreatedAt = sc.CreatedAt,
                        UpdatedAt = sc.UpdatedAt
                    })
                    .ToListAsync();

                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting configurations by category");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }


        /// <summary>
        /// Obtener configuraciones por categoría específica
        /// </summary>
        [HttpGet("configurations-by-category/{categoryId}")]
        public async Task<IActionResult> GetConfigurationsByCategory(int categoryId)
        {
            try
            {
                var configs = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .Where(sc => sc.Status.IsFinalizationStatus &&
                                sc.IsActive && 
                                sc.CategoryId == categoryId)
                    .OrderBy(sc => sc.Status.SortOrder)
                    .Select(sc => new
                    {
                        Id = sc.Id,
                        Estado = sc.Status.DisplayName,
                        StatusId = sc.StatusId,
                        StatusValue = sc.Status.StatusValue,
                        Cliente = sc.ClientPercentage,
                        Experto = sc.ExpertPercentage,
                        Plataforma = sc.PlatformPercentage,
                        Prioridad = sc.ServiceTypeCategoryId != null ? "Nivel 1 - Máxima Granularidad" : "Nivel 3 - Granularidad Básica",
                        Activo = sc.IsActive ? "Activo" : "Inactivo",
                        CategoryId = sc.CategoryId,
                        CategoryName = sc.Category.Name,
                        ServiceTypeCategoryId = sc.ServiceTypeCategoryId,
                        ServiceTypeCategoryName = sc.ServiceTypeCategory != null ? sc.ServiceTypeCategory.Name : "Todos los tipos",
                        CreatedAt = sc.CreatedAt,
                        UpdatedAt = sc.UpdatedAt
                    })
                    .ToListAsync();

                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting configurations by category");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }



        /// <summary>
        /// Obtener configuración de distribución de dinero con sistema de prioridades
        /// </summary>
        [HttpGet("money-distribution")]
        public async Task<IActionResult> GetMoneyDistribution(
            [FromQuery] string statusValue,
            [FromQuery] int? categoryId = null,
            [FromQuery] int? serviceTypeCategoryId = null)
        {
            try
            {
                _logger.LogInformation("=== GETTING MONEY DISTRIBUTION ===");
                _logger.LogInformation("StatusValue: {StatusValue}, CategoryId: {CategoryId}, ServiceTypeCategoryId: {ServiceTypeCategoryId}", 
                    statusValue, categoryId, serviceTypeCategoryId);

                // Buscar el SystemStatus por StatusValue
                var systemStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.IsActive);

                if (systemStatus == null)
                {
                    _logger.LogWarning("SystemStatus not found for StatusValue: {StatusValue}", statusValue);
                    return NotFound(new { message = $"Estado '{statusValue}' no encontrado" });
                }

                // NIVEL 1: Máxima Granularidad - Categoría + Tipo de Servicio + Estado
                if (categoryId.HasValue && serviceTypeCategoryId.HasValue)
                {
                    var level1Config = await _context.StatusConfigurations
                        .Where(sc => sc.StatusId == systemStatus.Id &&
                                    sc.CategoryId == categoryId &&
                                    sc.ServiceTypeCategoryId == serviceTypeCategoryId &&
                                    sc.IsActive)
                        .FirstOrDefaultAsync();

                    if (level1Config != null)
                    {
                        _logger.LogInformation("Found Level 1 configuration: CategoryId={CategoryId}, ServiceTypeCategoryId={ServiceTypeCategoryId}", 
                            categoryId, serviceTypeCategoryId);
                        return Ok(new
                        {
                            level = 1,
                            levelName = "Máxima Granularidad",
                            description = "Categoría + Tipo de Servicio + Estado",
                            clientPercentage = level1Config.ClientPercentage,
                            expertPercentage = level1Config.ExpertPercentage,
                            platformPercentage = level1Config.PlatformPercentage,
                            statusValue = systemStatus.StatusValue,
                            statusDisplayName = systemStatus.DisplayName,
                            categoryId = level1Config.CategoryId,
                            serviceTypeCategoryId = level1Config.ServiceTypeCategoryId
                        });
                    }
                }

                // NIVEL 2: Granularidad Media - Tipo de Servicio + Estado
                if (serviceTypeCategoryId.HasValue)
                {
                    var level2Config = await _context.StatusConfigurations
                        .Where(sc => sc.StatusId == systemStatus.Id &&
                                    sc.CategoryId == null &&
                                    sc.ServiceTypeCategoryId == serviceTypeCategoryId &&
                                    sc.IsActive)
                        .FirstOrDefaultAsync();

                    if (level2Config != null)
                    {
                        _logger.LogInformation("Found Level 2 configuration: ServiceTypeCategoryId={ServiceTypeCategoryId}", 
                            serviceTypeCategoryId);
                        return Ok(new
                        {
                            level = 2,
                            levelName = "Granularidad Media",
                            description = "Tipo de Servicio + Estado",
                            clientPercentage = level2Config.ClientPercentage,
                            expertPercentage = level2Config.ExpertPercentage,
                            platformPercentage = level2Config.PlatformPercentage,
                            statusValue = systemStatus.StatusValue,
                            statusDisplayName = systemStatus.DisplayName,
                            categoryId = level2Config.CategoryId,
                            serviceTypeCategoryId = level2Config.ServiceTypeCategoryId
                        });
                    }
                }

                // NIVEL 3: Granularidad Básica - Categoría + Estado
                if (categoryId.HasValue)
                {
                    var level3Config = await _context.StatusConfigurations
                        .Where(sc => sc.StatusId == systemStatus.Id &&
                                    sc.CategoryId == categoryId &&
                                    sc.ServiceTypeCategoryId == null &&
                                    sc.IsActive)
                        .FirstOrDefaultAsync();

                    if (level3Config != null)
                    {
                        _logger.LogInformation("Found Level 3 configuration: CategoryId={CategoryId}", categoryId);
                        return Ok(new
                        {
                            level = 3,
                            levelName = "Granularidad Básica",
                            description = "Categoría + Estado",
                            clientPercentage = level3Config.ClientPercentage,
                            expertPercentage = level3Config.ExpertPercentage,
                            platformPercentage = level3Config.PlatformPercentage,
                            statusValue = systemStatus.StatusValue,
                            statusDisplayName = systemStatus.DisplayName,
                            categoryId = level3Config.CategoryId,
                            serviceTypeCategoryId = level3Config.ServiceTypeCategoryId
                        });
                    }
                }

                // NIVEL 4: Por Defecto - Solo Estado
                var level4Config = await _context.StatusConfigurations
                    .Where(sc => sc.StatusId == systemStatus.Id &&
                                sc.CategoryId == null &&
                                sc.ServiceTypeCategoryId == null &&
                                sc.IsActive)
                    .FirstOrDefaultAsync();

                if (level4Config != null)
                {
                    _logger.LogInformation("Found Level 4 configuration: Status only");
                    return Ok(new
                    {
                        level = 4,
                        levelName = "Por Defecto",
                        description = "Solo Estado",
                        clientPercentage = level4Config.ClientPercentage,
                        expertPercentage = level4Config.ExpertPercentage,
                        platformPercentage = level4Config.PlatformPercentage,
                        statusValue = systemStatus.StatusValue,
                        statusDisplayName = systemStatus.DisplayName,
                        categoryId = level4Config.CategoryId,
                        serviceTypeCategoryId = level4Config.ServiceTypeCategoryId
                    });
                }

                _logger.LogWarning("No configuration found for any level");
                return NotFound(new { message = "No se encontró configuración para el estado especificado" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting money distribution");
                return StatusCode(500, new { message = "Error interno del servidor", error = ex.Message });
            }
        }

        /// <summary>
        /// Métodos auxiliares para obtener porcentajes por defecto
        /// </summary>
        private int GetDefaultClientPercentage(string statusValue)
        {
            return statusValue switch
            {
                "appointment_awaiting_report" => 0,
                "appointment_cancelled_by_client" => 20,
                "appointment_cancelled_by_client_second" => 100,
                "appointment_cancelled_by_expert" => 100,
                "appointment_cancelled_by_no_response" => 100,
                "appointment_cancelled_by_expert_rejection" => 100,
                "appointment_rejected" => 20,
                "appointment_proposed" => 20,
                "appointment_confirmed" => 30,
                "awaiting_appointment" => 20,
                _ => 20
            };
        }

        private int GetDefaultExpertPercentage(string statusValue)
        {
            return statusValue switch
            {
                "appointment_awaiting_report" => 80,
                "appointment_cancelled_by_client" => 20,
                "appointment_cancelled_by_client_second" => 0,
                "appointment_cancelled_by_expert" => 0,
                "appointment_cancelled_by_no_response" => 0,
                "appointment_cancelled_by_expert_rejection" => 0,
                "appointment_rejected" => 20,
                "appointment_proposed" => 20,
                "appointment_confirmed" => 30,
                "awaiting_appointment" => 20,
                _ => 20
            };
        }

        private int GetDefaultPlatformPercentage(string statusValue)
        {
            return statusValue switch
            {
                "appointment_awaiting_report" => 20,
                "appointment_cancelled_by_client" => 60,
                "appointment_cancelled_by_client_second" => 0,
                "appointment_cancelled_by_expert" => 0,
                "appointment_cancelled_by_no_response" => 0,
                "appointment_cancelled_by_expert_rejection" => 0,
                "appointment_rejected" => 60,
                "appointment_proposed" => 60,
                "appointment_confirmed" => 40,
                "awaiting_appointment" => 60,
                _ => 60
            };
        }

        /// <summary>
        /// Obtiene TODOS los estados del sistema para gestión administrativa
        /// </summary>
        [HttpGet("all-statuses")]
        public async Task<IActionResult> GetAllStatuses()
        {
            try
            {
                var allStatuses = await _context.SystemStatuses
                    .Where(s => s.IsActive)
                    .OrderBy(s => s.StatusType)
                    .ThenBy(s => s.SortOrder)
                    .Select(s => new
                    {
                        Id = s.Id,
                        StatusType = s.StatusType,
                        StatusName = s.StatusName,
                        StatusValue = s.StatusValue,
                        DisplayName = s.DisplayName,
                        Description = s.Description,
                        SortOrder = s.SortOrder,
                        IsActive = s.IsActive,
                        IsFinalizationStatus = s.IsFinalizationStatus,
                        CreatedAt = s.CreatedAt,
                        UpdatedAt = s.UpdatedAt
                    })
                    .ToListAsync();

                _logger.LogInformation("Retrieved {Count} statuses for admin management", allStatuses.Count);

                return Ok(allStatuses);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all statuses for admin management");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Actualiza el estado de finalización de un estado específico
        /// </summary>
        [HttpPut("update-finalization-status/{statusId}")]
        public async Task<IActionResult> UpdateFinalizationStatus(int statusId, [FromBody] UpdateFinalizationStatusRequest request)
        {
            try
            {
                _logger.LogInformation("=== UPDATING FINALIZATION STATUS ===");
                _logger.LogInformation("Status ID: {StatusId}", statusId);
                _logger.LogInformation("IsFinalizationStatus: {IsFinalizationStatus}", request.IsFinalizationStatus);

                // Buscar el estado
                var status = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.Id == statusId);

                if (status == null)
                {
                    _logger.LogError("Status not found: StatusId={StatusId}", statusId);
                    return NotFound(new { message = $"Estado con ID {statusId} no encontrado" });
                }

                // Actualizar el estado de finalización
                status.IsFinalizationStatus = request.IsFinalizationStatus;
                status.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                _logger.LogInformation("Finalization status updated successfully: StatusId={StatusId}, StatusValue={StatusValue}, IsFinalizationStatus={IsFinalizationStatus}", 
                    statusId, status.StatusValue, request.IsFinalizationStatus);

                return Ok(new { 
                    message = "Estado de finalización actualizado correctamente",
                    statusId = statusId,
                    statusValue = status.StatusValue,
                    statusName = status.StatusName,
                    statusType = status.StatusType,
                    isFinalizationStatus = status.IsFinalizationStatus,
                    updatedAt = status.UpdatedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating finalization status for StatusId={StatusId}", statusId);
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }
    }

    // DTO para crear configuraciones de estado de cita
    public class CreateAppointmentStatusConfigRequest
    {
        public int StatusId { get; set; }
        public int? CategoryId { get; set; }
        public int? ServiceTypeCategoryId { get; set; }
        public decimal ClientPercentage { get; set; }
        public decimal ExpertPercentage { get; set; }
        public decimal PlatformPercentage { get; set; }
        public bool IsActive { get; set; } = true;
        public string? Action { get; set; } // "create", "update", "delete"
        public int? ConfigId { get; set; } // ID de la configuración a actualizar/eliminar
    }

    // DTO para actualizar el estado de finalización
    public class UpdateFinalizationStatusRequest
    {
        public bool IsFinalizationStatus { get; set; }
    }
}
