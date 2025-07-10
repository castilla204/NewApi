using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ServiceTypeController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<ServiceTypeController> _logger;

        public ServiceTypeController(AppDbContext context, ILogger<ServiceTypeController> logger)
        {
            _context = context;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetServiceTypes()
        {
            try
            {
                var serviceTypes = await _context.ServiceTypes
                    .Where(st => st.IsActive)
                    .Select(st => new ServiceTypeDto
                    {
                        Id = st.Id,
                        Name = st.Name,
                        Description = st.Description,
                        IsActive = st.IsActive,
                        CreatedAt = st.CreatedAt,
                        UpdatedAt = st.UpdatedAt
                    })
                    .ToListAsync();

                return Ok(serviceTypes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving service types");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetServiceType(int id)
        {
            try
            {
                var serviceType = await _context.ServiceTypes
                    .Where(st => st.Id == id && st.IsActive)
                    .Select(st => new ServiceTypeDto
                    {
                        Id = st.Id,
                        Name = st.Name,
                        Description = st.Description,
                        IsActive = st.IsActive,
                        CreatedAt = st.CreatedAt,
                        UpdatedAt = st.UpdatedAt
                    })
                    .FirstOrDefaultAsync();

                if (serviceType == null)
                {
                    return NotFound(new { message = "Service type not found" });
                }

                return Ok(serviceType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving service type");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateServiceType([FromBody] ServiceTypeDto createDto)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(createDto.Name))
                {
                    return BadRequest(new { message = "Name is required" });
                }

                var serviceType = new ServiceType
                {
                    Name = createDto.Name,
                    Description = createDto.Description ?? "",
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
                    IsActive = serviceType.IsActive,
                    CreatedAt = serviceType.CreatedAt,
                    UpdatedAt = serviceType.UpdatedAt
                };

                return CreatedAtAction(nameof(GetServiceType), new { id = serviceType.Id }, resultDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating service type");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateServiceType(int id, [FromBody] ServiceTypeDto updateDto)
        {
            try
            {
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
                serviceType.IsActive = updateDto.IsActive;
                serviceType.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                var resultDto = new ServiceTypeDto
                {
                    Id = serviceType.Id,
                    Name = serviceType.Name,
                    Description = serviceType.Description,
                    IsActive = serviceType.IsActive,
                    CreatedAt = serviceType.CreatedAt,
                    UpdatedAt = serviceType.UpdatedAt
                };

                return Ok(resultDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating service type");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteServiceType(int id)
        {
            try
            {
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
                _logger.LogError(ex, "Error deleting service type");
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
    public class ServiceTypeDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}