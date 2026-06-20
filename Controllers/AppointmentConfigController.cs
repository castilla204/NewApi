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
    // 🛡️ A1 SECURITY FIX (Round 7): quitar [AllowAnonymous] a nivel controller. Los GETs
    // de lectura pública (status, category) mantienen [AllowAnonymous] individualmente,
    // pero los POST/PUT/DELETE que MUTAN StatusConfigurations (% reparto del dinero del
    // marketplace) requieren [Authorize(Roles="Admin")] obligatorio. Antes cualquiera
    // sin auth podía crear/editar/borrar status configs y modificar el reparto $$$.
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Roles = "Admin")]
    public class AppointmentConfigController : ControllerBase
    {
        private readonly AppDbContext _context;
        public AppointmentConfigController(AppDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Endpoint de compatibilidad: /api/AppointmentConfig/appointment-status
        /// Redirige a SystemStatusController
        /// </summary>
        [HttpGet("appointment-status")]
        [AllowAnonymous] // 🛡️ A1: solo lectura pública, sin auth required
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
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Endpoint de compatibilidad: /api/AppointmentConfig/service-type-category
        /// Redirige a SystemStatusController
        /// </summary>
        [HttpGet("service-type-category")]
        [AllowAnonymous] // 🛡️ A1: solo lectura pública
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
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Endpoint de compatibilidad: Devuelve configuraciones de distribución de dinero por estado de cita
        /// Para el panel de administración
        /// </summary>
        [HttpGet("appointment-status-configs")]
        [AllowAnonymous] // 🛡️ A1: solo lectura pública
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "DEBUG_STATUS_DATA_ERROR" });
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
                            // 🔧 FIX (hallazgo G): NO usar porcentajes hardcodeados. Divergían del SEED y el
                            // catch-all (20/20/60) podía ROBAR el reparto, porque las filas por categoría tienen
                            // PRIORIDAD sobre la global en GetMoneyDistributionAsync. Sembramos la fila por
                            // categoría ESPEJANDO la config GLOBAL ya existente del estado; si no hay global de
                            // referencia, NO creamos fila (no inventamos porcentajes que distorsionen el reparto).
                            var globalConfig = await _context.StatusConfigurations
                                .FirstOrDefaultAsync(sc => sc.StatusId == status.Id &&
                                                           sc.CategoryId == null &&
                                                           sc.ServiceTypeCategoryId == null &&
                                                           sc.IsActive);
                            if (globalConfig != null)
                            {
                                var newConfig = new StatusConfiguration
                                {
                                    StatusId = status.Id,
                                    CategoryId = category.Id,
                                    ServiceTypeCategoryId = null, // Nivel 3 - Granularidad Básica
                                    ClientPercentage = globalConfig.ClientPercentage,
                                    ExpertPercentage = globalConfig.ExpertPercentage,
                                    PlatformPercentage = globalConfig.PlatformPercentage,
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "CREATE_CATEGORY_CONFIGS_ERROR" });
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "DEBUG_DB_QUERY_ERROR" });
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
                
                return Ok(new
                {
                    message = "Datos recibidos correctamente",
                    receivedData = request,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "DEBUG_POST_DATA_ERROR" });
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "CREATE_OR_UPDATE_CONFIG_ERROR" });
            }
        }

        private async Task<IActionResult> HandleDeleteAction(int configId)
        {
            try
            {
                var config = await _context.StatusConfigurations.FindAsync(configId);
                if (config == null)
                {
                    return NotFound(new { message = $"Configuración con ID {configId} no encontrada" });
                }

                _context.StatusConfigurations.Remove(config);
                await _context.SaveChangesAsync();
                return Ok(new { message = "Configuración eliminada correctamente", deletedId = configId });
            }
            catch (Exception ex)
            {
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "DELETE_CONFIG_ACTION_ERROR" });
            }
        }

        private async Task<IActionResult> HandleUpdateAction(CreateAppointmentStatusConfigRequest request)
        {
            try
            {
                // Buscar la configuración existente
                var existingConfig = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .FirstOrDefaultAsync(sc => sc.Id == request.ConfigId);

                if (existingConfig == null)
                {
                    return NotFound(new { message = $"Configuración con ID {request.ConfigId} no encontrada" });
                }

                // Validar que el StatusId esté presente
                if (request.StatusId <= 0)
                {
                    return BadRequest(new { message = "StatusId es requerido y debe ser válido" });
                }

                // Validar que el estado existe y es de finalización
                var status = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.Id == request.StatusId && s.IsFinalizationStatus);

                if (status == null)
                {
                    return BadRequest(new { message = $"Estado con ID {request.StatusId} no encontrado o no es un estado de finalización" });
                }

                // Validar que los porcentajes sumen 100%
                var totalPercentage = request.ClientPercentage + request.ExpertPercentage + request.PlatformPercentage;
                if (totalPercentage != 100)
                {
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "UPDATE_CONFIG_ACTION_ERROR" });
            }
        }

        private async Task<IActionResult> HandleCreateAction(CreateAppointmentStatusConfigRequest request)
        {
            try
            {
                // Validar que el request no sea null
                if (request == null)
                {
                    return BadRequest(new { message = "Request body is required" });
                }

                // Validar que el StatusId esté presente
                if (request.StatusId <= 0)
                {
                    return BadRequest(new { message = "StatusId es requerido y debe ser válido" });
                }

                // Validar que el estado existe y es de finalización
                var status = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.Id == request.StatusId && s.IsFinalizationStatus);

                if (status == null)
                {
                    return BadRequest(new { message = $"Estado con ID {request.StatusId} no encontrado o no es un estado de finalización" });
                }


                // Validar que los porcentajes sumen 100%
                var totalPercentage = request.ClientPercentage + request.ExpertPercentage + request.PlatformPercentage;
                if (totalPercentage != 100)
                {
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
                    // ACTUALIZAR configuración existente en lugar de crear nueva
                    existingConfig.ClientPercentage = request.ClientPercentage;
                    existingConfig.ExpertPercentage = request.ExpertPercentage;
                    existingConfig.PlatformPercentage = request.PlatformPercentage;
                    existingConfig.IsActive = request.IsActive;
                    existingConfig.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "CREATE_CONFIG_ACTION_ERROR" });
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

                // Validar que el request no sea null
                if (request == null)
                {
                    return BadRequest(new { message = "Request body is required" });
                }

                // Buscar la configuración existente
                var existingConfig = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .FirstOrDefaultAsync(sc => sc.Id == id);

                if (existingConfig == null)
                {
                    return NotFound(new { message = $"Configuración con ID {id} no encontrada" });
                }

                // Validar que el StatusId esté presente
                if (request.StatusId <= 0)
                {
                    return BadRequest(new { message = "StatusId es requerido y debe ser válido" });
                }

                // Validar que el estado existe y es de finalización
                var status = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.Id == request.StatusId && s.IsFinalizationStatus);

                if (status == null)
                {
                    return BadRequest(new { message = $"Estado con ID {request.StatusId} no encontrado o no es un estado de finalización" });
                }


                // Validar que los porcentajes sumen 100%
                var totalPercentage = request.ClientPercentage + request.ExpertPercentage + request.PlatformPercentage;
                if (totalPercentage != 100)
                {
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "UPDATE_CONFIG_ERROR" });
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
                var config = await _context.StatusConfigurations.FindAsync(id);
                if (config == null)
                {
                    return NotFound(new { message = $"Configuración con ID {id} no encontrada" });
                }

                _context.StatusConfigurations.Remove(config);
                await _context.SaveChangesAsync();
                return Ok(new { message = "Configuración eliminada correctamente", deletedId = id });
            }
            catch (Exception ex)
            {
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "DELETE_CONFIG_ERROR" });
            }
        }


        /// <summary>
        /// Obtener todas las configuraciones granulares (Nivel 1 - Máxima Granularidad)
        /// </summary>
        [HttpGet("granular-configurations")]
        public async Task<IActionResult> GetAllGranularConfigurations([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                var query = _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .Where(sc => sc.Status.IsFinalizationStatus &&
                                sc.IsActive && 
                                sc.CategoryId != null && 
                                sc.ServiceTypeCategoryId != null)
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
                    });

                var totalCount = await query.CountAsync();

                var configs = await query
                    .OrderBy(sc => sc.CategoryId)
                    .ThenBy(sc => sc.ServiceTypeCategoryId)
                    .ThenBy(sc => sc.StatusValue)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                return Ok(new
                {
                    configs,
                    pagination = new
                    {
                        page,
                        pageSize,
                        totalCount,
                        totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                        hasNextPage = page * pageSize < totalCount,
                        hasPreviousPage = page > 1
                    }
                });
            }
            catch (Exception ex)
            {
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "GET_GRANULAR_CONFIGS_ERROR" });
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "GET_GRANULAR_CONFIGS_BY_CAT_SERVICE_ERROR" });
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "GET_CATEGORIES_ERROR" });
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "GET_SERVICE_TYPES_ERROR" });
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "GET_CONFIGS_BY_CATEGORY_FRONTEND_ERROR" });
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
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "GET_CONFIGS_BY_CATEGORY_ERROR" });
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
                // Buscar el SystemStatus por StatusValue
                var systemStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.IsActive);

                if (systemStatus == null)
                {
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
                return NotFound(new { message = "No se encontró configuración para el estado especificado" });
            }
            catch (Exception ex)
            {
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error interno del servidor", errorCode = "GET_MONEY_DISTRIBUTION_ERROR" });
            }
        }

        // 🔧 FIX (hallazgo G): ELIMINADOS GetDefaultClientPercentage/ExpertPercentage/PlatformPercentage.
        // Eran porcentajes hardcodeados que divergían del SEED oficial e incluían un catch-all 20/20/60 que
        // podía robar el reparto (las filas por categoría tienen prioridad sobre la global). El seeder por
        // categoría (CreateCategoryConfigurations) ahora ESPEJA la config global existente de cada estado.

        /// <summary>
        /// Obtiene TODOS los estados del sistema para gestión administrativa
        /// </summary>
        [HttpGet("all-statuses")]
        public async Task<IActionResult> GetAllStatuses([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                var query = _context.SystemStatuses
                    .Where(s => s.IsActive)
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
                    });

                var totalCount = await query.CountAsync();

                var allStatuses = await query
                    .OrderBy(s => s.StatusType)
                    .ThenBy(s => s.SortOrder)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                return Ok(new
                {
                    statuses = allStatuses,
                    pagination = new
                    {
                        page,
                        pageSize,
                        totalCount,
                        totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                        hasNextPage = page * pageSize < totalCount,
                        hasPreviousPage = page > 1
                    }
                });
            }
            catch (Exception ex)
            {
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
                // Buscar el estado
                var status = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.Id == statusId);

                if (status == null)
                {
                    return NotFound(new { message = $"Estado con ID {statusId} no encontrado" });
                }

                // Actualizar el estado de finalización
                status.IsFinalizationStatus = request.IsFinalizationStatus;
                status.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();
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
