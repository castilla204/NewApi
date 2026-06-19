using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;

namespace newApi.Controllers
{
    /// <summary>
    /// Magic link del vendedor (modo "seller"). SIN login: el token secreto de la URL ES la
    /// credencial. Solo expone lo imprescindible para reservar; NUNCA datos personales del cliente.
    /// El vendedor elige día/hora + lugar dentro del horario y el rango del experto.
    ///
    /// ⚠️ El POST /confirm crea una cita confirmada con la MISMA reserva atómica que el webhook
    /// (exclusion constraint GiST anti-doble-booking). Verificar con pruebas de concurrencia.
    /// </summary>
    [ApiController]
    [Route("api/seller-booking")]
    [AllowAnonymous]
    public class SellerBookingController : ControllerBase
    {
        private readonly IAvailabilityService _availability;
        private readonly AppDbContext _context;

        public SellerBookingController(IAvailabilityService availability, AppDbContext context)
        {
            _availability = availability;
            _context = context;
        }

        private static bool TokenLooksValid(string? token) =>
            !string.IsNullOrWhiteSpace(token) && token.Length >= 32 && token.Length <= 128;

        private Task<SearchHire?> FindByTokenAsync(string token, bool tracking) =>
            (tracking ? _context.SearchHires : _context.SearchHires.AsNoTracking())
                .Include(h => h.Appointment)
                .FirstOrDefaultAsync(h => h.SellerBookingToken == token);

        // ── Contexto (solo lectura) ────────────────────────────────────────────
        [HttpGet("{token}")]
        public async Task<IActionResult> GetContext(string token)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });
            var hire = await FindByTokenAsync(token, tracking: false);
            if (hire == null) return NotFound(new { message = "Enlace no válido o caducado." });
            return Ok(new
            {
                serviceId = hire.SearchServiceId,
                alreadyBooked = hire.Appointment != null,
                // Plazo agotado sin reservar: el enlace ya no sirve (habrá reembolso al comprador).
                expired = hire.SellerBookingDeadline.HasValue && DateTime.UtcNow > hire.SellerBookingDeadline.Value && hire.Appointment == null,
                listingUrl = hire.SellerListingUrl,
                // Tope de días a futuro que fijó el cliente (para limitar el calendario).
                maxDays = hire.SellerBookingMaxDays ?? 14,
                // Ubicación/rango del experto (snapshot del hire) para el mapa de selección de lugar.
                expertLatitude = hire.ExpertLatitudeSnapshot,
                expertLongitude = hire.ExpertLongitudeSnapshot,
                expertCountry = hire.ExpertCountry,
                workRadiusKm = hire.ExpertWorkRadiusKmSnapshot,
            });
        }

        // ── Huecos (token-gated; reusa el mismo servicio que el flujo del cliente) ──
        [HttpGet("{token}/slots")]
        public async Task<IActionResult> GetSlots(string token, [FromQuery] DateTime date, CancellationToken ct)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });
            var hire = await FindByTokenAsync(token, tracking: false);
            if (hire == null) return NotFound(new { message = "Enlace no válido o caducado." });
            if (hire.Appointment != null) return Ok(Array.Empty<object>());
            // Plazo agotado: no servir la agenda del experto.
            if (hire.SellerBookingDeadline.HasValue && DateTime.UtcNow > hire.SellerBookingDeadline.Value)
                return Ok(Array.Empty<object>());
            var slots = await _availability.GetAvailableSlotsAsync(hire.SearchServiceId, date, ct);
            return Ok(slots);
        }

        [HttpGet("{token}/summary")]
        public async Task<IActionResult> GetSummary(string token, [FromQuery] DateTime? from, [FromQuery] int days, CancellationToken ct)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });
            var hire = await FindByTokenAsync(token, tracking: false);
            if (hire == null) return NotFound(new { message = "Enlace no válido o caducado." });
            if (hire.Appointment != null
                || (hire.SellerBookingDeadline.HasValue && DateTime.UtcNow > hire.SellerBookingDeadline.Value))
                return Ok(Array.Empty<object>());
            var fromDate = from ?? DateTime.UtcNow.Date;
            var n = days <= 0 ? 14 : Math.Min(days, 60); // tope para evitar escaneos caros desde un endpoint público
            var summary = await _availability.GetAvailabilitySummaryAsync(hire.SearchServiceId, fromDate, n, ct);
            return Ok(summary);
        }

        // ── Confirmar la cita (crea la cita CONFIRMADA con reserva atómica) ────────
        [HttpPost("{token}/confirm")]
        public async Task<IActionResult> Confirm(string token, [FromBody] SellerConfirmDto dto)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });

            // Validaciones del hueco (mismas que el flujo del cliente).
            if (dto == null || dto.StartsAtUtc == default || dto.EndsAtUtc == default)
                return BadRequest(new { message = "Falta el día y la hora de la cita." });
            var startUtc = DateTime.SpecifyKind(dto.StartsAtUtc, DateTimeKind.Utc);
            var endUtc = DateTime.SpecifyKind(dto.EndsAtUtc, DateTimeKind.Utc);
            if (endUtc <= startUtc) return BadRequest(new { message = "El fin de la cita debe ser posterior al inicio." });
            if (startUtc <= DateTime.UtcNow) return BadRequest(new { message = "Ese hueco ya ha pasado, elige otro." });

            var hire = await FindByTokenAsync(token, tracking: true);
            if (hire == null) return NotFound(new { message = "Enlace no válido o caducado." });
            if (hire.Appointment != null) return Conflict(new { message = "La cita ya estaba reservada." });

            // Plazo para reservar agotado: el enlace ya no sirve (se reembolsará al comprador).
            if (hire.SellerBookingDeadline.HasValue && DateTime.UtcNow > hire.SellerBookingDeadline.Value)
                return BadRequest(new { message = "El plazo para reservar la cita ha caducado." });

            // El cliente fijó hasta cuántos días a futuro puede elegir el vendedor (sin margen extra).
            var maxDays = hire.SellerBookingMaxDays ?? 14;
            if (startUtc > DateTime.UtcNow.AddDays(maxDays))
                return BadRequest(new { message = $"La cita debe ser dentro de los próximos {maxDays} días." });

            // El hueco debe seguir ofreciéndose por el calendario del experto.
            if (!await _availability.IsSlotBookableAsync(hire.SearchServiceId, startUtc, endUtc))
                return BadRequest(new { message = "Ese hueco ya no está disponible, elige otro." });

            var confirmedStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_confirmed");
            if (confirmedStatus == null)
                return StatusCode(500, new { message = "Configuración de estados incompleta." });

            var expertTimezone = hire.ExpertTimezone ?? "Europe/Madrid";
            DateTime slotLocalStart;
            try
            {
                var tzi = TimeZoneInfo.FindSystemTimeZoneById(expertTimezone);
                slotLocalStart = TimeZoneInfo.ConvertTimeFromUtc(startUtc, tzi);
            }
            catch { slotLocalStart = startUtc; }

            decimal? lat = decimal.TryParse(dto.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var latVal) ? latVal : (decimal?)null;
            decimal? lng = decimal.TryParse(dto.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var lngVal) ? lngVal : (decimal?)null;

            _context.Appointments.Add(new Appointment
            {
                SearchHireId = hire.Id,
                StatusId = confirmedStatus.Id,
                ExpertId = hire.ExpertId,
                StartsAtUtc = startUtc,
                EndsAtUtc = endUtc,
                BlocksCalendar = true,
                Location = string.IsNullOrWhiteSpace(dto.Location) ? null : dto.Location.Trim(),
                Latitude = lat,
                Longitude = lng,
                DoorNumber = string.IsNullOrWhiteSpace(dto.DoorNumber) ? null : dto.DoorNumber.Trim(),
                // El teléfono del propietario del coche = el del vendedor capturado en el checkout.
                OwnerPhone = hire.SellerPhone,
                SiteDetails = string.IsNullOrWhiteSpace(dto.SiteDetails) ? null : dto.SiteDetails.Trim(),
                ProposedDate = DateTime.SpecifyKind(slotLocalStart.Date, DateTimeKind.Utc),
                ProposedTime = slotLocalStart.TimeOfDay,
                ProposerTimezone = expertTimezone,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            // Token de un solo uso: invalidar para que el enlace no sirva dos veces.
            hire.SellerBookingToken = null;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23P01")
            {
                // Otro reservó el mismo hueco en paralelo (exclusion constraint GiST).
                return Conflict(new { message = "Ese hueco se acaba de ocupar, elige otro." });
            }
            catch (DbUpdateConcurrencyException)
            {
                // El job de expiración (u otro proceso) tocó el mismo hire a la vez (token xmin):
                // el plazo acaba de vencer mientras confirmabas. Devolver 409 en vez de 500.
                return Conflict(new { message = "El plazo para reservar acaba de vencer. Pide al comprador que te reenvíe el enlace." });
            }

            return Ok(new { ok = true });
        }
    }

    public class SellerConfirmDto
    {
        public DateTime StartsAtUtc { get; set; }
        public DateTime EndsAtUtc { get; set; }
        public string? Location { get; set; }
        public string? Latitude { get; set; }
        public string? Longitude { get; set; }
        public string? DoorNumber { get; set; }
        public string? SiteDetails { get; set; }
    }
}
