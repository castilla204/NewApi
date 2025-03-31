using Microsoft.AspNetCore.Mvc;
using DataLayer.Models;
using DataLayer.Models.DTOs;
using DataLayer.Models.DTOs;
using newApi.Services;

namespace newApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WebMixerController : ControllerBase
    {
        private readonly IWebMixerService _webMixerService;
        private readonly ILogger<WebMixerController> _logger;

        public WebMixerController(IWebMixerService webMixerService, ILogger<WebMixerController> logger)
        {
            _webMixerService = webMixerService ?? throw new ArgumentNullException(nameof(webMixerService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpPost("Search")]
        public async Task<IActionResult> Search([FromBody] SearchRequestDto request)
        {
            try
            {
                _logger.LogInformation("Received search request with keywords: {Keywords}", request.Keywords);

                // Set IsProgrammed to false for direct API calls
                request.IsProgrammed = false;

                var results = await _webMixerService.SearchAsync(request);

                if (results == null || !results.Any())
                {
                    return NotFound("No se encontraron resultados.");
                }

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing search request");
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}