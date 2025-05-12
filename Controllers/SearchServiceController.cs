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
        public async Task<IActionResult> GetAllServices()
        {
            try
            {
                var services = await _searchServiceService.GetAllServices();
                return Ok(services);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving search services");
                return StatusCode(500, new { message = "Failed to retrieve services" });
            }
        }

        [HttpGet("expert/{expertId}")]
        public async Task<IActionResult> GetExpertServices(int expertId)
        {
            try
            {
                var services = await _searchServiceService.GetExpertServices(expertId);
                return Ok(services);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving expert services");
                return StatusCode(500, new { message = "Failed to retrieve expert services" });
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
                _logger.LogError(ex, "Error retrieving service");
                return StatusCode(500, new { message = "Failed to retrieve service" });
            }
        }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> CreateSearchService([FromForm] CreateSearchServiceRequestDto request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
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
                    return BadRequest(new { message = "Failed to create service" });
                }

                return Ok(new
                {
                    message = "Search service created successfully",
                    searchService = new
                    {
                        service.Id,
                        service.ExpertProfileId,
                        service.CategoryId,
                        service.Price,
                        service.Conditions,
                        service.DurationInHours,
                        service.CreatedAt,
                        ImageUrls = imageUrls
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating search service");
                return StatusCode(500, new { message = "Failed to create search service" });
            }
        }
    }
}