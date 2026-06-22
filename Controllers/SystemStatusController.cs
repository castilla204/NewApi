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
        private readonly IAuthorizationServices _authService;
        private readonly IAlertChannelService? _alertChannelService;

        public SystemStatusController(
            AppDbContext context, 
            SystemStatusService systemStatusService, 

            IAuthorizationServices authService,
            IAlertChannelService? alertChannelService = null)
        {
            _context = context;
            _systemStatusService = systemStatusService;
            _authService = authService;
            _alertChannelService = alertChannelService;
        }

        /// <summary>
        /// P3-3: Health check del canal alternativo de alertas. Devuelve estado de configuración
        /// (sin exponer tokens) para Slack y Telegram.
        /// </summary>
        [HttpGet("alert-channels")]
        [Authorize(Roles = "Admin")]
        public IActionResult GetAlertChannelsStatus()
        {
            if (_alertChannelService == null)
            {
                return Ok(new { configured = false, slack = false, telegram = false, message = "AlertChannelService no registrado." });
            }
            var status = _alertChannelService.GetStatus();
            return Ok(new
            {
                configured = status.SlackConfigured || status.TelegramConfigured,
                slack = status.SlackConfigured,
                telegram = status.TelegramConfigured
            });
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
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Obtiene todos los mapeos de estados
        /// </summary>
        [HttpGet("mappings")]
        public async Task<IActionResult> GetStatusMappings([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            if (!_authService.IsAdmin(User))
            {
                return Forbid();
            }

            try
            {
                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                var allMappings = await _systemStatusService.GetStatusMappingsAsync();
                
                var totalCount = allMappings.Count();
                
                var result = allMappings
                    .Select(m => new
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
                    })
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                return Ok(new
                {
                    mappings = result,
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
        /// Obtiene todas las configuraciones de distribución de dinero
        /// </summary>
        [HttpGet("configurations")]
        public async Task<IActionResult> GetStatusConfigurations([FromQuery] string? statusValue = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            if (!_authService.IsAdmin(User))
            {
                return Forbid();
            }

            try
            {
                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                var query = _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .AsQueryable();

                if (!string.IsNullOrEmpty(statusValue))
                {
                    query = query.Where(sc => sc.Status.StatusValue == statusValue);
                }

                var baseQuery = query
                    .Where(sc => sc.IsActive);

                var totalCount = await baseQuery.CountAsync();

                var configurations = await baseQuery
                    .OrderBy(sc => sc.Status.StatusType)
                    .ThenBy(sc => sc.Status.SortOrder)
                    .ThenBy(sc => sc.CategoryId)
                    .ThenBy(sc => sc.ServiceTypeCategoryId)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
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

                return Ok(new
                {
                    configurations,
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

                // 🛡️ FIX [E4]: StatusValue es la clave por la que RefundService/SystemStatusService
                // localizan estados y su config de reparto (búsquedas por string). Renombrar el
                // StatusValue de un estado de finalización o referenciado por StatusConfigurations/
                // StatusMappings rompería el reparto de dinero (finalización abortada / fallback), y
                // desactivarlo (IsActive=false) rompe los mapeos. Solo se permite editar campos
                // cosméticos (DisplayName/Description/SortOrder/StatusName) en esos estados.
                if (request.StatusValue != status.StatusValue || request.IsActive != status.IsActive)
                {
                    bool isCodeReferenced =
                        status.IsFinalizationStatus ||
                        await _context.StatusConfigurations.AnyAsync(sc => sc.StatusId == statusId) ||
                        await _context.StatusMappings.AnyAsync(m => m.SourceStatusId == statusId || m.TargetStatusId == statusId);

                    if (isCodeReferenced)
                    {
                        return BadRequest(new { message = "No se puede cambiar StatusValue ni IsActive de un estado de finalización o referenciado por configuraciones de reparto/mapeos: rompería la distribución de dinero. Edita solo DisplayName/Description/SortOrder." });
                    }
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
                return Ok(new { message = "Estado eliminado exitosamente" });
            }
            catch (Exception ex)
            {
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

                // 🛡️ FIX [E4-análogo]: los mapeos cuyo origen es un estado de finalización son los que
                // resuelven el reparto de dinero (GetTargetSearchHireStatusAsync mapea AppointmentStatus
                // de finalización → SearchHireStatus). Repuntar su origen/destino o desactivarlos
                // cambiaría/rompería a qué estado se reparte el dinero. Solo se permite tocarlos si NO se
                // alteran esos campos (origen/destino/activo).
                bool mappingTouchesFinalization =
                    (mapping.SourceStatus?.IsFinalizationStatus ?? false) ||
                    (mapping.TargetStatus?.IsFinalizationStatus ?? false);
                bool structuralChange =
                    request.SourceStatusId != mapping.SourceStatusId ||
                    request.TargetStatusId != mapping.TargetStatusId ||
                    request.IsActive != mapping.IsActive;
                if (mappingTouchesFinalization && structuralChange)
                {
                    return BadRequest(new { message = "No se puede cambiar el origen/destino ni desactivar un mapeo que involucra un estado de finalización: alteraría la resolución del reparto de dinero." });
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
                return Ok(new { message = "Mapeo eliminado exitosamente" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Endpoint temporal para debug - verificar estados y mapeos existentes
        /// </summary>
        [HttpGet("debug-info")]
        public async Task<IActionResult> GetDebugInfo()
        {
            if (!_authService.IsAdmin(User))
            {
                return Forbid();
            }

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
