using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using System.Security.Claims;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SystemStatusController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly SystemStatusService _systemStatusService;
        private readonly ILogger<SystemStatusController> _logger;
        private readonly IAuthorizationServices _authService;

        public SystemStatusController(
            AppDbContext context, 
            SystemStatusService systemStatusService, 
            ILogger<SystemStatusController> logger,
            IAuthorizationServices authService)
        {
            _context = context;
            _systemStatusService = systemStatusService;
            _logger = logger;
            _authService = authService;
        }

        /// <summary>
        /// Obtiene todos los estados del sistema por tipo
        /// </summary>
        [HttpGet("statuses")]
        public async Task<IActionResult> GetStatuses([FromQuery] string? statusType = null)
        {
            try
            {
                var query = _context.SystemStatuses.AsQueryable();
                
                if (!string.IsNullOrEmpty(statusType))
                {
                    query = query.Where(s => s.StatusType == statusType);
                }

                var statuses = await query
                    .Where(s => s.IsActive)
                    .OrderBy(s => s.StatusType)
                    .ThenBy(s => s.SortOrder)
                    .Select(s => new
                    {
                        s.Id,
                        s.StatusType,
                        s.StatusName,
                        s.StatusValue,
                        s.DisplayName,
                        s.Description,
                        s.SortOrder,
                        s.CreatedAt,
                        s.UpdatedAt
                    })
                    .ToListAsync();

                return Ok(statuses);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting system statuses");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Obtiene todos los mapeos de estados
        /// </summary>
        [HttpGet("mappings")]
        public async Task<IActionResult> GetStatusMappings()
        {
            try
            {
                var mappings = await _systemStatusService.GetStatusMappingsAsync();
                
                var result = mappings.Select(m => new
                {
                    m.Id,
                    SourceStatus = new
                    {
                        m.SourceStatus.Id,
                        m.SourceStatus.StatusType,
                        m.SourceStatus.StatusName,
                        m.SourceStatus.StatusValue,
                        m.SourceStatus.DisplayName
                    },
                    TargetStatus = new
                    {
                        m.TargetStatus.Id,
                        m.TargetStatus.StatusType,
                        m.TargetStatus.StatusName,
                        m.TargetStatus.StatusValue,
                        m.TargetStatus.DisplayName
                    },
                    m.IsActive,
                    m.CreatedAt
                }).ToList();

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting status mappings");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Obtiene todas las configuraciones de distribución de dinero
        /// </summary>
        [HttpGet("configurations")]
        public async Task<IActionResult> GetStatusConfigurations([FromQuery] string? statusValue = null)
        {
            try
            {
                var query = _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .AsQueryable();

                if (!string.IsNullOrEmpty(statusValue))
                {
                    query = query.Where(sc => sc.Status.StatusValue == statusValue);
                }

                var configurations = await query
                    .Where(sc => sc.IsActive)
                    .OrderBy(sc => sc.Status.StatusType)
                    .ThenBy(sc => sc.Status.SortOrder)
                    .ThenBy(sc => sc.CategoryId)
                    .ThenBy(sc => sc.ServiceTypeCategoryId)
                    .Select(sc => new
                    {
                        sc.Id,
                        Status = new
                        {
                            sc.Status.Id,
                            sc.Status.StatusType,
                            sc.Status.StatusName,
                            sc.Status.StatusValue,
                            sc.Status.DisplayName
                        },
                        Category = sc.Category != null ? new
                        {
                            sc.Category.Id,
                            sc.Category.Name
                        } : null,
                        ServiceTypeCategory = sc.ServiceTypeCategory != null ? new
                        {
                            sc.ServiceTypeCategory.Id,
                            sc.ServiceTypeCategory.Name
                        } : null,
                        sc.ClientPercentage,
                        sc.ExpertPercentage,
                        sc.PlatformPercentage,
                        sc.IsActive,
                        sc.CreatedAt,
                        sc.UpdatedAt
                    })
                    .ToListAsync();

                return Ok(configurations);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting status configurations");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Crea un nuevo estado del sistema (Solo Admin)
        /// </summary>
        [HttpPost("statuses")]
        public async Task<IActionResult> CreateStatus([FromBody] CreateSystemStatusRequest request)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                // Validar que no exista un estado con el mismo StatusValue en el mismo StatusType
                var existingStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == request.StatusType && 
                                            s.StatusValue == request.StatusValue);

                if (existingStatus != null)
                {
                    return BadRequest(new { message = $"Ya existe un estado con el valor '{request.StatusValue}' en el tipo '{request.StatusType}'" });
                }

                var newStatus = new SystemStatus
                {
                    StatusType = request.StatusType,
                    StatusName = request.StatusName,
                    StatusValue = request.StatusValue,
                    DisplayName = request.DisplayName,
                    Description = request.Description,
                    SortOrder = request.SortOrder,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.SystemStatuses.Add(newStatus);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Created new system status: {StatusType} - {StatusValue}", 
                    request.StatusType, request.StatusValue);

                return CreatedAtAction(nameof(GetStatuses), new { id = newStatus.Id }, new
                {
                    newStatus.Id,
                    newStatus.StatusType,
                    newStatus.StatusName,
                    newStatus.StatusValue,
                    newStatus.DisplayName,
                    newStatus.Description,
                    newStatus.SortOrder,
                    newStatus.CreatedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating system status");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Crea un nuevo mapeo de estados (Solo Admin)
        /// </summary>
        [HttpPost("mappings")]
        public async Task<IActionResult> CreateStatusMapping([FromBody] CreateStatusMappingRequest request)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }

                // Validar que los estados existan
                var sourceStatus = await _context.SystemStatuses.FindAsync(request.SourceStatusId);
                var targetStatus = await _context.SystemStatuses.FindAsync(request.TargetStatusId);

                if (sourceStatus == null || targetStatus == null)
                {
                    return BadRequest(new { message = "Uno o ambos estados no existen" });
                }

                // Validar que no exista ya el mapeo
                var existingMapping = await _context.StatusMappings
                    .FirstOrDefaultAsync(sm => sm.SourceStatusId == request.SourceStatusId && 
                                             sm.TargetStatusId == request.TargetStatusId);

                if (existingMapping != null)
                {
                    return BadRequest(new { message = "Ya existe este mapeo de estados" });
                }

                var newMapping = new StatusMapping
                {
                    SourceStatusId = request.SourceStatusId,
                    TargetStatusId = request.TargetStatusId,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };

                _context.StatusMappings.Add(newMapping);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Created new status mapping: {SourceStatus} → {TargetStatus}", 
                    sourceStatus.StatusValue, targetStatus.StatusValue);

                return CreatedAtAction(nameof(GetStatusMappings), new { id = newMapping.Id }, new
                {
                    newMapping.Id,
                    SourceStatus = new { sourceStatus.Id, sourceStatus.StatusValue, sourceStatus.DisplayName },
                    TargetStatus = new { targetStatus.Id, targetStatus.StatusValue, targetStatus.DisplayName },
                    newMapping.IsActive,
                    newMapping.CreatedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating status mapping");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Crea una nueva configuración de distribución de dinero (Solo Admin)
        /// </summary>
        [HttpPost("configurations")]
        public async Task<IActionResult> CreateStatusConfiguration([FromBody] CreateStatusConfigurationRequest request)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                // Validar que el estado exista
                var status = await _context.SystemStatuses.FindAsync(request.StatusId);
                if (status == null)
                {
                    return BadRequest(new { message = "El estado especificado no existe" });
                }

                // Validar que los porcentajes sumen 100%
                if (request.ClientPercentage + request.ExpertPercentage + request.PlatformPercentage != 100)
                {
                    return BadRequest(new { message = "Los porcentajes deben sumar exactamente 100%" });
                }

                // Validar que no exista ya la configuración
                var existingConfig = await _context.StatusConfigurations
                    .FirstOrDefaultAsync(sc => sc.StatusId == request.StatusId && 
                                             sc.CategoryId == request.CategoryId && 
                                             sc.ServiceTypeCategoryId == request.ServiceTypeCategoryId);

                if (existingConfig != null)
                {
                    return BadRequest(new { message = "Ya existe una configuración para esta combinación de estado, categoría y tipo de servicio" });
                }

                var newConfiguration = new StatusConfiguration
                {
                    StatusId = request.StatusId,
                    CategoryId = request.CategoryId,
                    ServiceTypeCategoryId = request.ServiceTypeCategoryId,
                    ClientPercentage = request.ClientPercentage,
                    ExpertPercentage = request.ExpertPercentage,
                    PlatformPercentage = request.PlatformPercentage,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.StatusConfigurations.Add(newConfiguration);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Created new status configuration: {Status} - Client: {Client}%, Expert: {Expert}%, Platform: {Platform}%", 
                    status.StatusValue, request.ClientPercentage, request.ExpertPercentage, request.PlatformPercentage);

                return CreatedAtAction(nameof(GetStatusConfigurations), new { id = newConfiguration.Id }, new
                {
                    newConfiguration.Id,
                    Status = new { status.Id, status.StatusValue, status.DisplayName },
                    newConfiguration.CategoryId,
                    newConfiguration.ServiceTypeCategoryId,
                    newConfiguration.ClientPercentage,
                    newConfiguration.ExpertPercentage,
                    newConfiguration.PlatformPercentage,
                    newConfiguration.CreatedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating status configuration");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Actualiza un estado del sistema (Solo Admin)
        /// </summary>
        [HttpPut("statuses/{statusId}")]
        public async Task<IActionResult> UpdateStatus(int statusId, [FromBody] UpdateSystemStatusRequest request)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                var status = await _context.SystemStatuses.FindAsync(statusId);
                if (status == null)
                {
                    return NotFound(new { message = "Estado no encontrado" });
                }

                // Validar que no exista otro estado con el mismo StatusValue en el mismo StatusType
                var existingStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == request.StatusType && 
                                            s.StatusValue == request.StatusValue &&
                                            s.Id != statusId);

                if (existingStatus != null)
                {
                    return BadRequest(new { message = $"Ya existe un estado con el valor '{request.StatusValue}' en el tipo '{request.StatusType}'" });
                }

                // Actualizar campos
                status.StatusType = request.StatusType;
                status.StatusName = request.StatusName;
                status.StatusValue = request.StatusValue;
                status.DisplayName = request.DisplayName;
                status.Description = request.Description;
                status.SortOrder = request.SortOrder;
                status.IsActive = request.IsActive;
                status.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                _logger.LogInformation("Updated system status: {StatusType} - {StatusValue}", 
                    request.StatusType, request.StatusValue);

                return Ok(new
                {
                    status.Id,
                    status.StatusType,
                    status.StatusName,
                    status.StatusValue,
                    status.DisplayName,
                    status.Description,
                    status.SortOrder,
                    status.IsActive,
                    status.UpdatedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating system status");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Elimina un estado del sistema (Solo Admin)
        /// </summary>
        [HttpDelete("statuses/{statusId}")]
        public async Task<IActionResult> DeleteStatus(int statusId)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                var status = await _context.SystemStatuses.FindAsync(statusId);
                if (status == null)
                {
                    return NotFound(new { message = "Estado no encontrado" });
                }

                // Verificar si hay mapeos que usan este estado
                var hasMappings = await _context.StatusMappings
                    .AnyAsync(sm => sm.SourceStatusId == statusId || sm.TargetStatusId == statusId);

                if (hasMappings)
                {
                    return BadRequest(new { message = "No se puede eliminar el estado porque tiene mapeos asociados" });
                }

                // Verificar si hay configuraciones que usan este estado
                var hasConfigurations = await _context.StatusConfigurations
                    .AnyAsync(sc => sc.StatusId == statusId);

                if (hasConfigurations)
                {
                    return BadRequest(new { message = "No se puede eliminar el estado porque tiene configuraciones asociadas" });
                }

                _context.SystemStatuses.Remove(status);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Deleted system status: {StatusType} - {StatusValue}", 
                    status.StatusType, status.StatusValue);

                return Ok(new { message = "Estado eliminado exitosamente" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting system status");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Actualiza un mapeo de estados (Solo Admin)
        /// </summary>
        [HttpPut("mappings/{mappingId}")]
        public async Task<IActionResult> UpdateStatusMapping(int mappingId, [FromBody] UpdateStatusMappingRequest request)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                var mapping = await _context.StatusMappings
                    .Include(sm => sm.SourceStatus)
                    .Include(sm => sm.TargetStatus)
                    .FirstOrDefaultAsync(sm => sm.Id == mappingId);

                if (mapping == null)
                {
                    return NotFound(new { message = "Mapeo no encontrado" });
                }

                // Validar que los estados existan
                var sourceStatus = await _context.SystemStatuses.FindAsync(request.SourceStatusId);
                var targetStatus = await _context.SystemStatuses.FindAsync(request.TargetStatusId);

                if (sourceStatus == null || targetStatus == null)
                {
                    var missingStatuses = new List<string>();
                    if (sourceStatus == null) missingStatuses.Add($"SourceStatusId: {request.SourceStatusId}");
                    if (targetStatus == null) missingStatuses.Add($"TargetStatusId: {request.TargetStatusId}");
                    
                    return BadRequest(new { 
                        message = "Uno o ambos estados no existen",
                        missingStatuses = missingStatuses,
                        availableStatuses = await _context.SystemStatuses
                            .Where(s => s.IsActive)
                            .Select(s => new { s.Id, s.StatusType, s.StatusValue, s.DisplayName })
                            .ToListAsync()
                    });
                }

                // Validar que no exista ya el mapeo (excluyendo el actual)
                var existingMapping = await _context.StatusMappings
                    .FirstOrDefaultAsync(sm => sm.SourceStatusId == request.SourceStatusId && 
                                             sm.TargetStatusId == request.TargetStatusId &&
                                             sm.Id != mappingId);

                if (existingMapping != null)
                {
                    return BadRequest(new { message = "Ya existe este mapeo de estados" });
                }

                // Actualizar campos
                mapping.SourceStatusId = request.SourceStatusId;
                mapping.TargetStatusId = request.TargetStatusId;
                mapping.IsActive = request.IsActive;

                await _context.SaveChangesAsync();

                _logger.LogInformation("Updated status mapping: {SourceStatus} → {TargetStatus}", 
                    sourceStatus.StatusValue, targetStatus.StatusValue);

                return Ok(new
                {
                    mapping.Id,
                    SourceStatus = new { sourceStatus.Id, sourceStatus.StatusValue, sourceStatus.DisplayName },
                    TargetStatus = new { targetStatus.Id, targetStatus.StatusValue, targetStatus.DisplayName },
                    mapping.IsActive
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating status mapping");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Elimina un mapeo de estados (Solo Admin)
        /// </summary>
        [HttpDelete("mappings/{mappingId}")]
        public async Task<IActionResult> DeleteStatusMapping(int mappingId)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                var mapping = await _context.StatusMappings
                    .Include(sm => sm.SourceStatus)
                    .Include(sm => sm.TargetStatus)
                    .FirstOrDefaultAsync(sm => sm.Id == mappingId);

                if (mapping == null)
                {
                    return NotFound(new { message = "Mapeo no encontrado" });
                }

                _context.StatusMappings.Remove(mapping);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Deleted status mapping: {SourceStatus} → {TargetStatus}", 
                    mapping.SourceStatus.StatusValue, mapping.TargetStatus.StatusValue);

                return Ok(new { message = "Mapeo eliminado exitosamente" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting status mapping");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Endpoint temporal para debug - verificar estados y mapeos existentes
        /// </summary>
        [HttpGet("debug-info")]
        public async Task<IActionResult> GetDebugInfo()
        {
            try
            {
                var statuses = await _context.SystemStatuses
                    .Where(s => s.IsActive)
                    .OrderBy(s => s.StatusType)
                    .ThenBy(s => s.SortOrder)
                    .Select(s => new
                    {
                        s.Id,
                        s.StatusType,
                        s.StatusValue,
                        s.DisplayName,
                        s.Description,
                        s.SortOrder
                    })
                    .ToListAsync();

                var mappings = await _context.StatusMappings
                    .Include(sm => sm.SourceStatus)
                    .Include(sm => sm.TargetStatus)
                    .Where(sm => sm.IsActive)
                    .Select(sm => new
                    {
                        sm.Id,
                        SourceStatus = new { sm.SourceStatus.Id, sm.SourceStatus.StatusValue, sm.SourceStatus.DisplayName },
                        TargetStatus = new { sm.TargetStatus.Id, sm.TargetStatus.StatusValue, sm.TargetStatus.DisplayName },
                        sm.IsActive
                    })
                    .ToListAsync();

                return Ok(new
                {
                    Statuses = statuses,
                    Mappings = mappings,
                    TotalStatuses = statuses.Count,
                    TotalMappings = mappings.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting debug info");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Prueba la obtención de configuración de distribución de dinero
        /// </summary>
        [HttpGet("test-distribution")]
        public async Task<IActionResult> TestMoneyDistribution(
            [FromQuery] string statusValue, 
            [FromQuery] int? categoryId = null, 
            [FromQuery] int? serviceTypeCategoryId = null)
        {
            try
            {
                var config = await _systemStatusService.GetMoneyDistributionAsync(statusValue, categoryId, serviceTypeCategoryId);
                
                if (config == null)
                {
                    return NotFound(new { message = "No se encontró configuración para los parámetros especificados" });
                }

                return Ok(new
                {
                    Status = new { config.Status.Id, config.Status.StatusValue, config.Status.DisplayName },
                    CategoryId = config.CategoryId,
                    ServiceTypeCategoryId = config.ServiceTypeCategoryId,
                    ClientPercentage = config.ClientPercentage,
                    ExpertPercentage = config.ExpertPercentage,
                    PlatformPercentage = config.PlatformPercentage,
                    IsValid = config.IsValid
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing money distribution");
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }
    }

    // DTOs para las requests
    public class CreateSystemStatusRequest
    {
        public string StatusType { get; set; } = string.Empty;
        public string StatusName { get; set; } = string.Empty;
        public string StatusValue { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int SortOrder { get; set; } = 0;
    }

    public class UpdateSystemStatusRequest
    {
        public string StatusType { get; set; } = string.Empty;
        public string StatusName { get; set; } = string.Empty;
        public string StatusValue { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int SortOrder { get; set; } = 0;
        public bool IsActive { get; set; } = true;
    }

    public class CreateStatusMappingRequest
    {
        public int SourceStatusId { get; set; }
        public int TargetStatusId { get; set; }
    }

    public class UpdateStatusMappingRequest
    {
        public int SourceStatusId { get; set; }
        public int TargetStatusId { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class CreateStatusConfigurationRequest
    {
        public int StatusId { get; set; }
        public int? CategoryId { get; set; }
        public int? ServiceTypeCategoryId { get; set; }
        public decimal ClientPercentage { get; set; }
        public decimal ExpertPercentage { get; set; }
        public decimal PlatformPercentage { get; set; }
    }
}
