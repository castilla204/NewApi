using Microsoft.AspNetCore.Mvc;
using newApi.Services;
using newApi.DataLayer.Models.DTOs;

namespace newApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WebMixerController : ControllerBase
    {
        private readonly IWebMixerService _webMixerService;
        public WebMixerController(IWebMixerService webMixerService)
        {
            _webMixerService = webMixerService ?? throw new ArgumentNullException(nameof(webMixerService));
        }

        [HttpPost("Search")]
        public async Task<IActionResult> Search([FromBody] SearchRequestDto request)
        {
            try
            {
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
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}