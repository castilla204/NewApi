using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.Services;
using Stripe;

namespace newApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [EnableRateLimiting("admin")] // ✅ SEGURIDAD: 200 requests/minuto para admin
    public class AdminController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly StripeRefundService _refundService;
        private readonly IStripeConfigService _stripeConfigService;
        private readonly ILogger<AdminController> _logger;
        
        public AdminController(
            AppDbContext context, 
            StripeRefundService refundService,
            IStripeConfigService stripeConfigService,
            ILogger<AdminController> logger)
        {
            _context = context;
            _refundService = refundService;
            _stripeConfigService = stripeConfigService;
            _logger = logger;
        }


        /// <summary>
        /// Obtener el modo actual de Stripe (development/production)
        /// </summary>
        [HttpGet("stripe/mode")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetStripeMode()
        {
            try
            {
                var mode = await _stripeConfigService.GetStripeModeAsync();
                var setting = await _context.SystemSettings.FirstOrDefaultAsync();
                
                return Ok(new
                {
                    mode = mode,
                    changedAt = setting?.StripeModeChangedAt,
                    changedByUserId = setting?.StripeModeChangedByUserId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo modo Stripe");
                return StatusCode(500, new { message = "Error obteniendo modo Stripe", error = ex.Message });
            }
        }

        /// <summary>
        /// Cambiar el modo de Stripe entre development y production
        /// </summary>
        [HttpPost("stripe/mode")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SetStripeMode([FromBody] SetStripeModeDto request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Mode))
                {
                    return BadRequest(new { message = "El modo es requerido" });
                }

                if (request.Mode != "development" && request.Mode != "production")
                {
                    return BadRequest(new { message = "El modo debe ser 'development' o 'production'" });
                }

                var userId = int.Parse(User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value ?? "0");
                
                var success = await _stripeConfigService.SetStripeModeAsync(request.Mode, userId);
                
                if (success)
                {
                    // Recargar las claves de Stripe con el nuevo modo
                    // Esto requiere reiniciar la aplicación o recargar la configuración
                    _logger.LogWarning($"⚠️ Modo Stripe cambiado a {request.Mode}. Se requiere reiniciar la aplicación para aplicar los cambios.");
                    
                    return Ok(new
                    {
                        message = $"Modo Stripe cambiado a {request.Mode}",
                        mode = request.Mode,
                        warning = "Se requiere reiniciar la aplicación para aplicar los cambios completamente"
                    });
                }

                return StatusCode(500, new { message = "Error cambiando modo Stripe" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cambiando modo Stripe");
                return StatusCode(500, new { message = "Error cambiando modo Stripe", error = ex.Message });
            }
        }
    }

    public class SetStripeModeDto
    {
        public string Mode { get; set; } = string.Empty;
    }
}
