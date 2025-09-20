using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using newApi.DataLayer.Models.DTOs;
using newApi.Services;
using System.Security.Claims;

namespace newApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AppointmentConfigController : ControllerBase
    {
        private readonly IAppointmentConfigService _configService;
        private readonly ILogger<AppointmentConfigController> _logger;

        public AppointmentConfigController(IAppointmentConfigService configService, ILogger<AppointmentConfigController> logger)
        {
            _configService = configService;
            _logger = logger;
        }

        #region Appointment Status Configs

        /// <summary>
        /// Obtener todas las configuraciones de estados de citas
        /// </summary>
        [HttpGet("appointment-status")]
        public async Task<IActionResult> GetAppointmentStatusConfigs()
        {
            try
            {
                var configs = await _configService.GetAppointmentStatusConfigsAsync();
                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting appointment status configs");
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener una configuración de estado de cita por ID
        /// </summary>
        [HttpGet("appointment-status/{id}")]
        public async Task<IActionResult> GetAppointmentStatusConfig(int id)
        {
            try
            {
                var config = await _configService.GetAppointmentStatusConfigAsync(id);
                if (config == null)
                    return NotFound(new { message = "Configuration not found" });

                return Ok(config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting appointment status config: {Id}", id);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Crear una nueva configuración de estado de cita
        /// </summary>
        [HttpPost("appointment-status")]
        public async Task<IActionResult> CreateAppointmentStatusConfig([FromBody] CreateAppointmentStatusConfigDto dto)
        {
            try
            {
                var config = await _configService.CreateAppointmentStatusConfigAsync(dto);
                return CreatedAtAction(nameof(GetAppointmentStatusConfig), new { id = config.Id }, config);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating appointment status config");
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Actualizar una configuración de estado de cita
        /// </summary>
        [HttpPut("appointment-status/{id}")]
        public async Task<IActionResult> UpdateAppointmentStatusConfig(int id, [FromBody] CreateAppointmentStatusConfigDto dto)
        {
            try
            {
                var config = await _configService.UpdateAppointmentStatusConfigAsync(id, dto);
                return Ok(config);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating appointment status config: {Id}", id);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Eliminar una configuración de estado de cita
        /// </summary>
        [HttpDelete("appointment-status/{id}")]
        public async Task<IActionResult> DeleteAppointmentStatusConfig(int id)
        {
            try
            {
                var success = await _configService.DeleteAppointmentStatusConfigAsync(id);
                if (!success)
                    return NotFound(new { message = "Configuration not found" });

                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting appointment status config: {Id}", id);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        #endregion

        #region Service Type Category Configs

        /// <summary>
        /// Obtener todas las configuraciones de categorías de servicios
        /// </summary>
        [HttpGet("service-type-category")]
        public async Task<IActionResult> GetServiceTypeCategoryConfigs()
        {
            try
            {
                var configs = await _configService.GetServiceTypeCategoryConfigsAsync();
                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting service type category configs");
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener configuraciones de categorías de servicios por categoría
        /// </summary>
        [HttpGet("service-type-category/category/{serviceTypeCategoryId}")]
        public async Task<IActionResult> GetServiceTypeCategoryConfigsByCategory(int serviceTypeCategoryId)
        {
            try
            {
                var configs = await _configService.GetServiceTypeCategoryConfigsByCategoryAsync(serviceTypeCategoryId);
                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting service type category configs for category: {CategoryId}", serviceTypeCategoryId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener una configuración de categoría de servicio por ID
        /// </summary>
        [HttpGet("service-type-category/{id}")]
        public async Task<IActionResult> GetServiceTypeCategoryConfig(int id)
        {
            try
            {
                var config = await _configService.GetServiceTypeCategoryConfigAsync(id);
                if (config == null)
                    return NotFound(new { message = "Configuration not found" });

                return Ok(config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting service type category config: {Id}", id);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Crear una nueva configuración de categoría de servicio
        /// </summary>
        [HttpPost("service-type-category")]
        public async Task<IActionResult> CreateServiceTypeCategoryConfig([FromBody] CreateServiceTypeCategoryConfigDto dto)
        {
            try
            {
                var config = await _configService.CreateServiceTypeCategoryConfigAsync(dto);
                return CreatedAtAction(nameof(GetServiceTypeCategoryConfig), new { id = config.Id }, config);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating service type category config");
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Actualizar una configuración de categoría de servicio
        /// </summary>
        [HttpPut("service-type-category/{id}")]
        public async Task<IActionResult> UpdateServiceTypeCategoryConfig(int id, [FromBody] CreateServiceTypeCategoryConfigDto dto)
        {
            try
            {
                var config = await _configService.UpdateServiceTypeCategoryConfigAsync(id, dto);
                return Ok(config);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating service type category config: {Id}", id);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Eliminar una configuración de categoría de servicio
        /// </summary>
        [HttpDelete("service-type-category/{id}")]
        public async Task<IActionResult> DeleteServiceTypeCategoryConfig(int id)
        {
            try
            {
                var success = await _configService.DeleteServiceTypeCategoryConfigAsync(id);
                if (!success)
                    return NotFound(new { message = "Configuration not found" });

                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting service type category config: {Id}", id);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        #endregion

        #region Money Distribution Preview

        /// <summary>
        /// Obtener configuración de distribución de dinero para un estado, categoría y tipo de servicio específicos
        /// </summary>
        [HttpGet("money-distribution")]
        public async Task<IActionResult> GetMoneyDistributionConfig([FromQuery] string status, [FromQuery] int? categoryId = null, [FromQuery] int? serviceTypeCategoryId = null)
        {
            try
            {
                var config = await _configService.GetMoneyDistributionConfigAsync(status, categoryId, serviceTypeCategoryId);
                if (config == null)
                    return NotFound(new { message = "No configuration found for the specified parameters" });

                return Ok(config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting money distribution config for status: {Status}, categoryId: {CategoryId}, serviceTypeCategoryId: {ServiceTypeCategoryId}", status, categoryId, serviceTypeCategoryId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        #endregion

        private int GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(userIdClaim, out var userId) ? userId : 0;
        }
    }
}
