using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models;
using newApi.Services;

namespace newApi.Controllers
{
    /// <summary>
    /// Controlador para gestionar las categorías de tipos de servicio
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ServiceTypeCategoryController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IAuthorizationServices _authService;

        public ServiceTypeCategoryController(AppDbContext context, IAuthorizationServices authService)
        {
            _context = context;
            _authService = authService;
        }

        /// <summary>
        /// Obtiene todas las categorías de tipos de servicio activas
        /// </summary>
        /// <returns>Lista de categorías activas</returns>
        [HttpGet]
        [AllowAnonymous] // ✅ PÚBLICO: Permitir acceso sin autenticación para explorar servicios
        public async Task<IActionResult> GetServiceTypeCategories()
        {
            try
            {
                var categories = await _context.ServiceTypeCategories
                    .Where(stc => stc.IsActive)
                    .OrderBy(stc => stc.Position)
                    .ThenBy(stc => stc.Name)
                    .Select(stc => new ServiceTypeCategoryDto
                    {
                        Id = stc.Id,
                        Name = stc.Name,
                        Description = stc.Description,
                        Position = stc.Position,
                        IsActive = stc.IsActive,
                        CreatedAt = stc.CreatedAt,
                        UpdatedAt = stc.UpdatedAt
                    })
                    .ToListAsync();
                return Ok(new 
                { 
                    success = true,
                    data = categories,
                    count = categories.Count,
                    message = "Service type categories retrieved successfully"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new 
                { 
                    success = false,
                    message = "Error retrieving service type categories",
                    error = ex.Message 
                });
            }
        }

        /// <summary>
        /// Obtiene todas las categorías de tipos de servicio activas (endpoint público sin autenticación)
        /// </summary>
        /// <returns>Lista de categorías activas</returns>
        [HttpGet("public")]
        [AllowAnonymous]
        public async Task<IActionResult> GetServiceTypeCategoriesPublic()
        {
            try
            {
                var categories = await _context.ServiceTypeCategories
                    .Where(stc => stc.IsActive)
                    .OrderBy(stc => stc.Position)
                    .ThenBy(stc => stc.Name)
                    .Select(stc => new 
                    {
                        Id = stc.Id,
                        Name = stc.Name,
                        Description = stc.Description,
                        Position = stc.Position
                    })
                    .ToListAsync();

                
                return Ok(new 
                { 
                    success = true,
                    data = categories,
                    count = categories.Count,
                    message = "Service type categories retrieved successfully"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new 
                { 
                    success = false,
                    message = "Error retrieving service type categories",
                    error = ex.Message 
                });
            }
        }

        /// <summary>
        /// Obtiene una categoría específica por ID
        /// </summary>
        /// <param name="id">ID de la categoría</param>
        /// <returns>Categoría específica</returns>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetServiceTypeCategory(int id)
        {
            try
            {
                var category = await _context.ServiceTypeCategories
                    .Where(stc => stc.Id == id && stc.IsActive)
                    .Select(stc => new ServiceTypeCategoryDto
                    {
                        Id = stc.Id,
                        Name = stc.Name,
                        Description = stc.Description,
                        Position = stc.Position,
                        IsActive = stc.IsActive,
                        CreatedAt = stc.CreatedAt,
                        UpdatedAt = stc.UpdatedAt
                    })
                    .FirstOrDefaultAsync();

                if (category == null)
                {
                    return NotFound(new 
                    { 
                        success = false,
                        message = "Service type category not found" 
                    });
                }
                return Ok(new 
                { 
                    success = true,
                    data = category,
                    message = "Service type category retrieved successfully"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new 
                { 
                    success = false,
                    message = "Error retrieving service type category",
                    error = ex.Message 
                });
            }
        }

        /// <summary>
        /// Crea una nueva categoría de tipo de servicio
        /// </summary>
        /// <param name="createDto">Datos de la nueva categoría</param>
        /// <returns>Categoría creada</returns>
        [HttpPost]
        public async Task<IActionResult> CreateServiceTypeCategory([FromBody] CreateServiceTypeCategoryDto createDto)
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

                var category = new ServiceTypeCategory
                {
                    Name = createDto.Name,
                    Description = createDto.Description ?? "",
                    Position = createDto.Position,
                    IsActive = createDto.IsActive,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await _context.ServiceTypeCategories.AddAsync(category);
                await _context.SaveChangesAsync();

                var resultDto = new ServiceTypeCategoryDto
                {
                    Id = category.Id,
                    Name = category.Name,
                    Description = category.Description,
                    Position = category.Position,
                    IsActive = category.IsActive,
                    CreatedAt = category.CreatedAt,
                    UpdatedAt = category.UpdatedAt
                };

                return CreatedAtAction(nameof(GetServiceTypeCategory), new { id = category.Id }, resultDto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /// <summary>
        /// Actualiza una categoría de tipo de servicio existente
        /// </summary>
        /// <param name="id">ID de la categoría</param>
        /// <param name="updateDto">Datos actualizados</param>
        /// <returns>Categoría actualizada</returns>
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateServiceTypeCategory(int id, [FromBody] UpdateServiceTypeCategoryDto updateDto)
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

                var category = await _context.ServiceTypeCategories.FindAsync(id);
                if (category == null)
                {
                    return NotFound(new { message = "Service type category not found" });
                }

                if (string.IsNullOrWhiteSpace(updateDto.Name))
                {
                    return BadRequest(new { message = "Name is required" });
                }

                category.Name = updateDto.Name;
                category.Description = updateDto.Description ?? "";
                category.Position = updateDto.Position;
                category.IsActive = updateDto.IsActive;
                category.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                var resultDto = new ServiceTypeCategoryDto
                {
                    Id = category.Id,
                    Name = category.Name,
                    Description = category.Description,
                    Position = category.Position,
                    IsActive = category.IsActive,
                    CreatedAt = category.CreatedAt,
                    UpdatedAt = category.UpdatedAt
                };

                return Ok(resultDto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /// <summary>
        /// Elimina una categoría de tipo de servicio (soft delete)
        /// </summary>
        /// <param name="id">ID de la categoría</param>
        /// <returns>Resultado de la operación</returns>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteServiceTypeCategory(int id)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                var category = await _context.ServiceTypeCategories.FindAsync(id);
                if (category == null)
                {
                    return NotFound(new { message = "Service type category not found" });
                }

                // Verificar si hay tipos de servicio asociados
                var hasAssociatedTypes = await _context.ServiceTypes
                    .AnyAsync(st => st.ServiceTypeCategoryId == id && st.IsActive);

                if (hasAssociatedTypes)
                {
                    return BadRequest(new { message = "Cannot delete category with associated service types" });
                }

                category.IsActive = false;
                category.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                return Ok(new { message = "Service type category deleted successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}

