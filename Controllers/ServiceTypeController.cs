using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models;
using newApi.Services;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ServiceTypeController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IAuthorizationServices _authService;

        public ServiceTypeController(AppDbContext context, IAuthorizationServices authService)
        {
            _context = context;
            _authService = authService;
        }

        /// <summary>
        /// Obtiene todos los tipos de servicio activos para el frontend
        /// </summary>
        /// <returns>Lista de tipos de servicio activos</returns>
        [HttpGet]
        [AllowAnonymous] // ✅ PÚBLICO: Permitir acceso sin autenticación para explorar servicios
        public async Task<IActionResult> GetServiceTypes()
        {
            try
            {
                var serviceTypes = await _context.ServiceTypes
                    .Include(st => st.ServiceTypeCategory)
                    .Where(st => st.IsActive)
                    .OrderBy(st => st.Position) // Ordenar por posición personalizada
                    .ThenBy(st => st.Name) // Luego alfabéticamente como fallback
                    .Select(st => new ServiceTypeDto
                    {
                        Id = st.Id,
                        Name = st.Name,
                        Description = st.Description,
                        ServiceTypeCategoryId = st.ServiceTypeCategoryId,
                        ServiceTypeCategoryName = st.ServiceTypeCategory != null ? st.ServiceTypeCategory.Name : null,
                        Position = st.Position,
                        IsActive = st.IsActive,
                        RequiresAppointment = st.RequiresAppointment,
                        CreatedAt = st.CreatedAt,
                        UpdatedAt = st.UpdatedAt
                    })
                    .ToListAsync();
                return Ok(new 
                { 
                    success = true,
                    data = serviceTypes,
                    count = serviceTypes.Count,
                    message = "Service types retrieved successfully"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new 
                { 
                    success = false,
                    message = "Error retrieving service types",
                    error = ex.Message 
                });
            }
        }

        /// <summary>
        /// Obtiene todos los tipos de servicio activos (endpoint público sin autenticación)
        /// </summary>
        /// <returns>Lista de tipos de servicio activos</returns>
        [HttpGet("public")]
        [AllowAnonymous]
        public async Task<IActionResult> GetServiceTypesPublic()
        {
            try
            {
                var serviceTypes = await _context.ServiceTypes
                    .Include(st => st.ServiceTypeCategory)
                    .Where(st => st.IsActive)
                    .OrderBy(st => st.Position) // Ordenar por posición personalizada
                    .ThenBy(st => st.Name) // Luego alfabéticamente como fallback
                    .Select(st => new 
                    {
                        Id = st.Id,
                        Name = st.Name,
                        Description = st.Description,
                        ServiceTypeCategoryId = st.ServiceTypeCategoryId,
                        ServiceTypeCategoryName = st.ServiceTypeCategory != null ? st.ServiceTypeCategory.Name : null,
                        Position = st.Position,
                        RequiresAppointment = st.RequiresAppointment
                    })
                    .ToListAsync();

                
                return Ok(new 
                { 
                    success = true,
                    data = serviceTypes,
                    count = serviceTypes.Count,
                    message = "Service types retrieved successfully"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new 
                { 
                    success = false,
                    message = "Error retrieving service types",
                    error = ex.Message 
                });
            }
        }

        /// <summary>
        /// Obtiene un tipo de servicio específico por ID
        /// </summary>
        /// <param name="id">ID del tipo de servicio</param>
        /// <returns>Tipo de servicio específico</returns>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetServiceType(int id)
        {
            try
            {
                var serviceType = await _context.ServiceTypes
                    .Include(st => st.ServiceTypeCategory)
                    .Where(st => st.Id == id && st.IsActive)
                    .Select(st => new ServiceTypeDto
                    {
                        Id = st.Id,
                        Name = st.Name,
                        Description = st.Description,
                        ServiceTypeCategoryId = st.ServiceTypeCategoryId,
                        ServiceTypeCategoryName = st.ServiceTypeCategory != null ? st.ServiceTypeCategory.Name : null,
                        Position = st.Position,
                        IsActive = st.IsActive,
                        RequiresAppointment = st.RequiresAppointment,
                        CreatedAt = st.CreatedAt,
                        UpdatedAt = st.UpdatedAt
                    })
                    .FirstOrDefaultAsync();

                if (serviceType == null)
                {
                    return NotFound(new 
                    { 
                        success = false,
                        message = "Service type not found" 
                    });
                }
                return Ok(new 
                { 
                    success = true,
                    data = serviceType,
                    message = "Service type retrieved successfully"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new 
                { 
                    success = false,
                    message = "Error retrieving service type",
                    error = ex.Message 
                });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CreateServiceType([FromBody] ServiceTypeDto createDto)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                if (string.IsNullOrWhiteSpace(createDto.Name))
                {
                    return BadRequest(new { message = "Name is required" });
                }

                var serviceType = new ServiceType
                {
                    Name = createDto.Name,
                    Description = createDto.Description ?? "",
                    Position = createDto.Position,
                    IsActive = createDto.IsActive,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await _context.ServiceTypes.AddAsync(serviceType);
                await _context.SaveChangesAsync();

                var resultDto = new ServiceTypeDto
                {
                    Id = serviceType.Id,
                    Name = serviceType.Name,
                    Description = serviceType.Description,
                    Position = serviceType.Position,
                    IsActive = serviceType.IsActive,
                    CreatedAt = serviceType.CreatedAt,
                    UpdatedAt = serviceType.UpdatedAt
                };

                return CreatedAtAction(nameof(GetServiceType), new { id = serviceType.Id }, resultDto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateServiceType(int id, [FromBody] ServiceTypeDto updateDto)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                if (id != updateDto.Id)
                {
                    return BadRequest(new { message = "ID mismatch" });
                }

                var serviceType = await _context.ServiceTypes.FindAsync(id);
                if (serviceType == null)
                {
                    return NotFound(new { message = "Service type not found" });
                }

                if (string.IsNullOrWhiteSpace(updateDto.Name))
                {
                    return BadRequest(new { message = "Name is required" });
                }

                serviceType.Name = updateDto.Name;
                serviceType.Description = updateDto.Description ?? "";
                serviceType.Position = updateDto.Position;
                serviceType.IsActive = updateDto.IsActive;
                serviceType.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                var resultDto = new ServiceTypeDto
                {
                    Id = serviceType.Id,
                    Name = serviceType.Name,
                    Description = serviceType.Description,
                    Position = serviceType.Position,
                    IsActive = serviceType.IsActive,
                    CreatedAt = serviceType.CreatedAt,
                    UpdatedAt = serviceType.UpdatedAt
                };

                return Ok(resultDto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteServiceType(int id)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                var serviceType = await _context.ServiceTypes.FindAsync(id);
                if (serviceType == null)
                {
                    return NotFound(new { message = "Service type not found" });
                }

                var hasDependencies = await _context.SearchParameters.AnyAsync(sp => sp.ServiceTypeId == id) ||
                                     await _context.SearchServices.AnyAsync(ss => ss.ServiceTypeId == id);
                if (hasDependencies)
                {
                    return BadRequest(new { message = "Cannot delete service type with associated search parameters or services" });
                }

                _context.ServiceTypes.Remove(serviceType);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Service type deleted successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
    public class ServiceTypeDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int? ServiceTypeCategoryId { get; set; }
        public string? ServiceTypeCategoryName { get; set; }
        public int Position { get; set; }
        public bool IsActive { get; set; }
        public bool RequiresAppointment { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}