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
            // Defensa: no servir días fuera de la ventana [suelo, tope] anclada al pago.
            var reqDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
            if (reqDate < SellerBookingWindow.StartUtc(hire.CreatedAt).Date
                || reqDate >= SellerBookingWindow.HardEndExclusiveUtc(hire.CreatedAt).Date)
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
            // La ventana manda: ignoramos el from/days del cliente y anclamos al pago.
            var fromDate = SellerBookingWindow.StartUtc(hire.CreatedAt);
            var n = days <= 0 ? SellerBookingWindow.HardDays : Math.Min(days, SellerBookingWindow.HardDays);
            var summary = await _availability.GetAvailabilitySummaryAsync(hire.SearchServiceId, fromDate, n, ct);
            return Ok(summary);
        }

        // ── Ventana de fechas efectiva (expansión progresiva objetivo→tope) ───────
        [HttpGet("{token}/window")]
        public async Task<IActionResult> GetWindow(string token, CancellationToken ct)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });
            var hire = await FindByTokenAsync(token, tracking: false);
            if (hire == null) return NotFound(new { message = "Enlace no válido o caducado." });

            var fromUtc = SellerBookingWindow.StartUtc(hire.CreatedAt);
            var fromYmd = fromUtc.ToString("yyyy-MM-dd");

            // Enlace ya usado o plazo de reserva vencido: no hay ventana ofrecible.
            if (hire.Appointment != null
                || (hire.SellerBookingDeadline.HasValue && DateTime.UtcNow > hire.SellerBookingDeadline.Value))
                return Ok(new { fromYmd, days = SellerBookingWindow.TargetDays, windowExtended = false, hasAvailability = false });

            // 1) ¿Hay huecos dentro del objetivo (+3..+7)?
            var target = await _availability.GetAvailabilitySummaryAsync(
                hire.SearchServiceId, fromUtc, SellerBookingWindow.TargetDays, ct);
            if (target.Any(d => d.FreeSlots > 0))
                return Ok(new { fromYmd, days = SellerBookingWindow.TargetDays, windowExtended = false, hasAvailability = true });

            // 2) No los hay → ampliar al tope (+3..+14).
            var hard = await _availability.GetAvailabilitySummaryAsync(
                hire.SearchServiceId, fromUtc, SellerBookingWindow.HardDays, ct);
            var hasHard = hard.Any(d => d.FreeSlots > 0);
            return Ok(new
            {
                fromYmd,
                days = hasHard ? SellerBookingWindow.HardDays : SellerBookingWindow.TargetDays,
                windowExtended = hasHard,
                hasAvailability = hasHard,
            });
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

            // Ventana fija anclada al pago: suelo +3 días, tope +14 (la expansión a 7→14 la
            // resuelve el endpoint /window; aquí solo defendemos el rango duro server-side).
            if (startUtc < SellerBookingWindow.StartUtc(hire.CreatedAt))
                return BadRequest(new { message = $"La cita debe ser al menos {SellerBookingWindow.MinLeadDays} días después de la compra." });
            if (startUtc >= SellerBookingWindow.HardEndExclusiveUtc(hire.CreatedAt))
                return BadRequest(new { message = $"La cita no puede ser más de {SellerBookingWindow.HardMaxDays} días después de la compra." });

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
