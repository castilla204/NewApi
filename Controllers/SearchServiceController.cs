using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using newApi.Services;
using newApi.DataLayer.Models.DTOs;
using System.Security.Claims;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SearchServiceController : ControllerBase
    {
        private readonly SearchServiceService _searchServiceService;
        private readonly ILogger<SearchServiceController> _logger;

        public SearchServiceController(
            SearchServiceService searchServiceService,
            ILogger<SearchServiceController> logger)
        {
            _searchServiceService = searchServiceService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllServices(
            [FromQuery] int categoryId,
            [FromQuery] int serviceTypeId,
            [FromQuery] string latitude,
            [FromQuery] string longitude,
            [FromQuery] int locationRange)
        {
            try
            {
                if (categoryId <= 0)
                {
                    return BadRequest(new { message = "El ID de categoría es requerido y debe ser mayor que 0" });
                }

                if (serviceTypeId <= 0)
                {
                    return BadRequest(new { message = "El tipo de servicio es requerido y debe ser mayor que 0" });
                }

                if (string.IsNullOrEmpty(latitude) || string.IsNullOrEmpty(longitude) || locationRange <= 0)
                {
                    return BadRequest(new { message = "Latitude, Longitude, y LocationRange son requeridos y deben ser válidos" });
                }

                var services = await _searchServiceService.GetAllServices(categoryId, serviceTypeId, latitude, longitude, locationRange);
                return Ok(services);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving services with CategoryId: {CategoryId}, ServiceTypeId: {ServiceTypeId}, Latitude: {Latitude}, Longitude: {Longitude}, LocationRange: {LocationRange}",
                    categoryId, serviceTypeId, latitude, longitude, locationRange);
                return StatusCode(500, new { message = "Failed to retrieve services", detail = ex.Message });
            }
        }

        [HttpGet("expert/{expertId}")]
        public async Task<IActionResult> GetExpertServices(int expertId, [FromQuery] int? serviceTypeId)
        {
            try
            {
                var services = await _searchServiceService.GetExpertServices(expertId, serviceTypeId);
                return Ok(services);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving expert services for ExpertId: {ExpertId}, ServiceTypeId: {ServiceTypeId}",
                    expertId, serviceTypeId);
                return StatusCode(500, new { message = "Failed to retrieve expert services", detail = ex.Message });
            }
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetServiceById(int id)
        {
            try
            {
                var service = await _searchServiceService.GetServiceById(id);
                if (service == null)
                {
                    return NotFound(new { message = "Service not found" });
                }
                return Ok(service);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving service with Id: {Id}", id);
                return StatusCode(500, new { message = "Failed to retrieve service", detail = ex.Message });
            }
        }


        [HttpGet("GetServiceByHireId/{id}")]
        public async Task<IActionResult> GetServiceByHireId(int id)
        {
            try
            {
                var service = await _searchServiceService.GetServiceByHireId(id);
                if (service == null)
                {
                    return NotFound(new { message = "Service not found" });
                }
                return Ok(service);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving service with Id: {Id}", id);
                return StatusCode(500, new { message = "Failed to retrieve service", detail = ex.Message });
            }
        }




        [Authorize]
        [HttpPost]
        public async Task<IActionResult> CreateSearchService([FromForm] CreateSearchServiceRequestDto request)
        {
            try
            {
                foreach (var key in Request.Form.Keys)
                {
                    var values = Request.Form[key];
                    if (key == "Images")
                    {
                        _logger.LogInformation("FormData key: {Key}, Files: {FileCount}", key, Request.Form.Files.Count);
                        foreach (var file in Request.Form.Files)
                        {
                            _logger.LogInformation("Received file: {FileName}, {ContentType}, {FileSize} bytes",
                                file.FileName, file.ContentType, file.Length);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("FormData key: {Key}, Value: {Value}", key, values);
                    }
                }

                _logger.LogInformation("Received request to create service with data: {RequestData}",
                    new
                    {
                        request.ExpertProfileId,
                        request.CategoryId,
                        request.ServiceTypeId,
                        request.Price,
                        request.Conditions,
                        request.DurationInHours,
                        ImageCount = request.Images?.Count ?? 0
                    });

                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                if (request.ServiceTypeId <= 0)
                {
                    return BadRequest(new { message = "El tipo de servicio es requerido" });
                }

                if (string.IsNullOrWhiteSpace(request.Conditions))
                {
                    return BadRequest(new { message = "El campo Condiciones es requerido" });
                }

                if (request.Price <= 0)
                {
                    return BadRequest(new { message = "El precio debe ser mayor que 0" });
                }

                if (request.DurationInHours <= 0)
                {
                    return BadRequest(new { message = "La duración debe ser mayor que 0" });
                }

                var (success, service, imageUrls) = await _searchServiceService.CreateSearchService(userId, request);
                if (!success)
                {
                    return BadRequest(new { message = "Failed to create service, possibly due to invalid ServiceTypeId, ExpertProfileId, or CategoryId" });
                }

                return Ok(new
                {
                    message = "Search service created successfully",
                    searchService = new
                    {
                        service.Id,
                        service.ExpertProfileId,
                        service.CategoryId,
                        service.ServiceTypeId,
                        ServiceTypeName = service.ServiceType?.Name,
                        service.Price,
                        service.Conditions,
                        service.DurationInHours,
                        service.CreatedAt,
                        service.IsActive,
                        ImageUrls = imageUrls
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating search service with ServiceTypeId: {ServiceTypeId}", request.ServiceTypeId);
                return StatusCode(500, new { message = "Failed to create search service", detail = ex.Message });
            }
        }

        [Authorize]
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteSearchService(int id)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                _logger.LogInformation("User {UserId} attempting to delete SearchService with Id: {ServiceId}", userId, id);

                var success = await _searchServiceService.DeleteSearchService(id, userId);
                
                if (!success)
                {
                    return NotFound(new { message = "Service not found or you don't have permission to delete it" });
                }

                return Ok(new { message = "Search service deleted successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting search service with Id: {ServiceId}", id);
                return StatusCode(500, new { message = "Failed to delete search service", detail = ex.Message });
            }
        }
    }
}