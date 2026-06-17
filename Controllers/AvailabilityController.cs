using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using newApi.Services;

namespace newApi.Controllers
{
    /// <summary>
    /// Huecos de cita ofrecibles al cliente (modelo Calendly: elegir hueco antes de pagar).
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AvailabilityController : ControllerBase
    {
        private readonly IAvailabilityService _availability;

        public AvailabilityController(IAvailabilityService availability)
        {
            _availability = availability;
        }

        /// <summary>
        /// Huecos libres de un servicio para una fecha local (YYYY-MM-DD).
        /// Devuelve intervalos en UTC + etiqueta local para mostrar.
        /// </summary>
        [HttpGet("service/{serviceId:int}/slots")]
        public async Task<IActionResult> GetSlots(int serviceId, [FromQuery] DateTime date, CancellationToken ct)
        {
            var slots = await _availability.GetAvailableSlotsAsync(serviceId, date, ct);
            return Ok(slots);
        }

        /// <summary>
        /// Resumen de disponibilidad por día (nº de huecos libres) en una ventana,
        /// para colorear el calendario según ocupación. Por defecto 14 días desde hoy.
        /// </summary>
        [HttpGet("service/{serviceId:int}/summary")]
        public async Task<IActionResult> GetSummary(int serviceId, [FromQuery] DateTime? from, [FromQuery] int days, CancellationToken ct)
        {
            var fromDate = from ?? DateTime.UtcNow.Date;
            var n = days <= 0 ? 14 : days;
            var summary = await _availability.GetAvailabilitySummaryAsync(serviceId, fromDate, n, ct);
            return Ok(summary);
        }
    }
}
