using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using newApi.DataLayer.Models.DTOs;
using newApi.Services;
using System.Security.Claims;

namespace newApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Admin")]
    public class CategoryServiceTypeConfigController : ControllerBase
    {
        private readonly ICategoryServiceTypeConfigService _configService;
        private readonly ILogger<CategoryServiceTypeConfigController> _logger;

        public CategoryServiceTypeConfigController(ICategoryServiceTypeConfigService configService, ILogger<CategoryServiceTypeConfigController> logger)
        {
            _configService = configService;
            _logger = logger;
        }

        /// <summary>
        /// Obtener todas las configuraciones por Category + ServiceTypeCategory
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCategoryServiceTypeConfigs()
        {
            try
            {
                var configs = await _configService.GetCategoryServiceTypeConfigsAsync();
                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting category service type configs");
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener configuraciones por categoría específica
        /// </summary>
        [HttpGet("category/{categoryId}")]
        public async Task<IActionResult> GetConfigsByCategory(int categoryId)
        {
            try
            {
                var configs = await _configService.GetConfigsByCategoryAsync(categoryId);
                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting configs by category: {CategoryId}", categoryId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener configuraciones por tipo de servicio específico
        /// </summary>
        [HttpGet("service-type/{serviceTypeCategoryId}")]
        public async Task<IActionResult> GetConfigsByServiceType(int serviceTypeCategoryId)
        {
            try
            {
                var configs = await _configService.GetConfigsByServiceTypeAsync(serviceTypeCategoryId);
                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting configs by service type: {ServiceTypeCategoryId}", serviceTypeCategoryId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener una configuración específica por ID
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetCategoryServiceTypeConfig(int id)
        {
            try
            {
                var config = await _configService.GetCategoryServiceTypeConfigAsync(id);
                if (config == null)
                    return NotFound(new { message = "Configuration not found" });

                return Ok(config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting category service type config: {Id}", id);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Crear una nueva configuración por Category + ServiceTypeCategory
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreateCategoryServiceTypeConfig([FromBody] CreateCategoryServiceTypeConfigDto dto)
        {
            try
            {
                var config = await _configService.CreateCategoryServiceTypeConfigAsync(dto);
                return CreatedAtAction(nameof(GetCategoryServiceTypeConfig), new { id = config.Id }, config);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating category service type config");
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Actualizar una configuración existente
        /// </summary>
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateCategoryServiceTypeConfig(int id, [FromBody] CreateCategoryServiceTypeConfigDto dto)
        {
            try
            {
                var config = await _configService.UpdateCategoryServiceTypeConfigAsync(id, dto);
                return Ok(config);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating category service type config: {Id}", id);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Eliminar una configuración
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteCategoryServiceTypeConfig(int id)
        {
            try
            {
                var success = await _configService.DeleteCategoryServiceTypeConfigAsync(id);
                if (!success)
                    return NotFound(new { message = "Configuration not found" });

                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting category service type config: {Id}", id);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        private int GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(userIdClaim, out var userId) ? userId : 0;
        }
    }
}



