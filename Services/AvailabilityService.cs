using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;

namespace newApi.Services
{
    /// <inheritdoc cref="IAvailabilityService"/>
    public class AvailabilityService : IAvailabilityService
    {
        private readonly AppDbContext _context;

        // 🗓️ Longitud de la CITA reservable, en horas. DESACOPLADA de SearchService.DurationInHours,
        // que es el PLAZO/SLA del servicio (p. ej. "informe en 24h") y NO la duración de la visita.
        // Antes se usaba DurationInHours como longitud de hueco → un servicio de "24 horas" no cabía
        // en ninguna franja diaria y devolvía 0 huecos siempre. El timer Hangfire post-cita (3h) y el
        // plazo siguen usando DurationInHours como antes; esto solo fija el tamaño del hueco del calendario.
        private const int AppointmentSlotHours = 1;

        public AvailabilityService(AppDbContext context)
        {
            _context = context;
        }

        public async Task<List<AvailableSlot>> GetAvailableSlotsAsync(int serviceId, DateTime date, CancellationToken ct = default)
        {
            var svc = await _context.SearchServices
                .Include(s => s.ExpertProfile)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == serviceId, ct);

            if (svc?.ExpertProfile == null)
                return new List<AvailableSlot>();

            var expertProfileId = svc.ExpertProfileId!.Value; // svc.ExpertProfile no es null (guard arriba)
            var expertUserId = svc.ExpertProfile.UserId;       // bookings (Appointment.ExpertId) = User id
            var durationHours = AppointmentSlotHours; // longitud del hueco; el plazo (DurationInHours) no manda aquí
            var fallbackTz = string.IsNullOrWhiteSpace(svc.ExpertProfile.Timezone)
                ? "UTC" : svc.ExpertProfile.Timezone;

            // Solo se honra la parte de fecha (evita que el Kind/hora del binder desplace el día).
            // Kind=Utc obligatorio: estas fechas se comparan contra columnas timestamptz (Npgsql lo exige).
            var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
            var dayPlus1 = day.AddDays(1);
            var dow = (int)day.DayOfWeek;

            // Reglas por-día (modelo Calendly) o, si el experto nunca tocó ese editor,
            // fallback retrocompatible al horario legacy del formulario de perfil.
            var effective = await GetEffectiveRulesAsync(expertProfileId, fallbackTz, day, dayPlus1, ct);

            var windows = effective
                .Where(r => r.DayOfWeek == dow)
                .Select(r => new AvailabilityWindow(r.Start, r.End, r.Timezone))
                .ToList();

            // Excepción por fecha concreta: sobreescribe por completo el horario semanal de ese día.
            var dateKey = DateOnly.FromDateTime(day);
            var exceptions = await GetExceptionsAsync(expertProfileId, fallbackTz, dateKey, dateKey, ct);
            if (exceptions.TryGetValue(dateKey, out var ex))
                windows = ex.IsWorking ? ex.Windows : new List<AvailabilityWindow>();

            if (windows.Count == 0)
                return new List<AvailableSlot>();

            // ±2 días en UTC: cubre offsets de hasta ±14h + la duración del servicio sin perder reservas.
            var spanStartUtc = DateTime.SpecifyKind(day.AddDays(-2), DateTimeKind.Utc);
            var spanEndUtc = DateTime.SpecifyKind(day.AddDays(2), DateTimeKind.Utc);

            var bookings = (await _context.Appointments
                    .Where(a => a.ExpertId == expertUserId && a.BlocksCalendar
                                && a.StartsAtUtc != null && a.EndsAtUtc != null
                                && a.StartsAtUtc < spanEndUtc && a.EndsAtUtc > spanStartUtc)
                    .Select(a => new { a.StartsAtUtc, a.EndsAtUtc })
                    .AsNoTracking()
                    .ToListAsync(ct))
                .Select(a => new BookingInterval(a.StartsAtUtc!.Value, a.EndsAtUtc!.Value))
                .ToList();

            return SlotCalculator.ComputeFreeSlots(windows, bookings, day, durationHours, DateTime.UtcNow);
        }

        public async Task<bool> IsSlotBookableAsync(int serviceId, DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
        {
            // Sanidad: hueco de longitud exacta AppointmentSlotHours y fin > inicio.
            if (endUtc <= startUtc) return false;
            if ((endUtc - startUtc) != TimeSpan.FromHours(AppointmentSlotHours)) return false;

            var startUtcKind = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
            var endUtcKind = DateTime.SpecifyKind(endUtc, DateTimeKind.Utc);

            // El start cae en un día LOCAL que, según el offset del experto, mapea a la fecha UTC del
            // start o a ±1 día. Probamos las tres y comprobamos coincidencia exacta con un hueco real.
            var probeDates = new[]
            {
                startUtcKind.Date.AddDays(-1),
                startUtcKind.Date,
                startUtcKind.Date.AddDays(1),
            };

            foreach (var probe in probeDates.Distinct())
            {
                var slots = await GetAvailableSlotsAsync(serviceId, probe, ct);
                if (slots.Any(s => s.StartUtc == startUtcKind && s.EndUtc == endUtcKind))
                    return true;
            }
            return false;
        }

        public async Task<List<DayAvailability>> GetAvailabilitySummaryAsync(int serviceId, DateTime fromDate, int days, CancellationToken ct = default)
        {
            var result = new List<DayAvailability>();
            if (days <= 0) return result;
            days = Math.Min(days, 60); // techo defensivo

            var svc = await _context.SearchServices
                .Include(s => s.ExpertProfile)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == serviceId, ct);
            if (svc?.ExpertProfile == null) return result;

            var expertProfileId = svc.ExpertProfileId!.Value; // svc.ExpertProfile no es null (guard arriba)
            var expertUserId = svc.ExpertProfile.UserId;
            var durationHours = AppointmentSlotHours; // longitud del hueco; el plazo (DurationInHours) no manda aquí
            var fallbackTz = string.IsNullOrWhiteSpace(svc.ExpertProfile.Timezone) ? "UTC" : svc.ExpertProfile.Timezone;

            var start = DateTime.SpecifyKind(fromDate.Date, DateTimeKind.Utc);
            var endExclusive = start.AddDays(days);

            // Una sola query de reglas (o fallback legacy) + una de reservas para toda la ventana.
            var allRules = await GetEffectiveRulesAsync(expertProfileId, fallbackTz, start, endExclusive, ct);

            var spanStartUtc = DateTime.SpecifyKind(start.AddDays(-2), DateTimeKind.Utc);
            var spanEndUtc = DateTime.SpecifyKind(endExclusive.AddDays(2), DateTimeKind.Utc);
            var bookings = (await _context.Appointments
                    .Where(a => a.ExpertId == expertUserId && a.BlocksCalendar
                                && a.StartsAtUtc != null && a.EndsAtUtc != null
                                && a.StartsAtUtc < spanEndUtc && a.EndsAtUtc > spanStartUtc)
                    .Select(a => new { a.StartsAtUtc, a.EndsAtUtc })
                    .AsNoTracking()
                    .ToListAsync(ct))
                .Select(a => new BookingInterval(a.StartsAtUtc!.Value, a.EndsAtUtc!.Value))
                .ToList();

            var exceptions = await GetExceptionsAsync(
                expertProfileId, fallbackTz,
                DateOnly.FromDateTime(start), DateOnly.FromDateTime(endExclusive.AddDays(-1)), ct);

            var now = DateTime.UtcNow;
            for (var i = 0; i < days; i++)
            {
                var day = start.AddDays(i);
                var dayPlus1 = day.AddDays(1);
                var dow = (int)day.DayOfWeek;
                var windows = allRules
                    .Where(r => r.DayOfWeek == dow && r.EffectiveFrom < dayPlus1
                                && (r.EffectiveTo == null || r.EffectiveTo >= day))
                    .Select(r => new AvailabilityWindow(r.Start, r.End, r.Timezone))
                    .ToList();

                var dateKey = DateOnly.FromDateTime(day);
                if (exceptions.TryGetValue(dateKey, out var ex))
                    windows = ex.IsWorking ? ex.Windows : new List<AvailabilityWindow>();

                var free = windows.Count == 0
                    ? 0
                    : SlotCalculator.ComputeFreeSlots(windows, bookings, day, durationHours, now).Count;

                result.Add(new DayAvailability(day.ToString("yyyy-MM-dd"), free));
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 🛟 Fuente de verdad del horario reservable, con auto-reparación.
        // Prioridad: ExpertAvailabilityRule (editor por-día, modelo Calendly). Si el experto
        // NUNCA usó ese editor (0 reglas), caemos al horario legacy del formulario de perfil
        // (ExpertAvailability) y lo sintetizamos como franjas por día. Así todo experto que
        // configuró su horario por la vía "clásica" tiene huecos reservables sin reconfigurar.
        // Las reglas, si existen, SIEMPRE mandan (el legacy se ignora por completo).
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>Día de la semana (nombre inglés del modelo legacy) → entero 0=domingo … 6=sábado.</summary>
        private static readonly IReadOnlyDictionary<string, int> DayNameToInt =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sunday"] = 0, ["Monday"] = 1, ["Tuesday"] = 2, ["Wednesday"] = 3,
                ["Thursday"] = 4, ["Friday"] = 5, ["Saturday"] = 6,
            };

        /// <summary>Franja efectiva normalizada (venga de reglas por-día o de legacy).</summary>
        private sealed record EffectiveRule(
            int DayOfWeek, TimeSpan Start, TimeSpan End, string Timezone,
            DateTime EffectiveFrom, DateTime? EffectiveTo);

        /// <summary>Excepción resuelta para una fecha: cerrado, o trabajando con N franjas.</summary>
        private sealed record DayException(bool IsWorking, List<AvailabilityWindow> Windows);

        /// <summary>
        /// Excepciones por fecha del experto en [from, to] (inclusive). Agrupa filas-por-franja
        /// en una entrada por fecha. Una fila con IsWorking=false marca el día cerrado.
        /// </summary>
        private async Task<Dictionary<DateOnly, DayException>> GetExceptionsAsync(
            int expertProfileId, string fallbackTz, DateOnly from, DateOnly to, CancellationToken ct)
        {
            var rows = await _context.ExpertAvailabilityExceptions
                .Where(e => e.ExpertId == expertProfileId && e.Date >= from && e.Date <= to)
                .AsNoTracking()
                .Select(e => new { e.Date, e.IsWorking, e.StartLocal, e.EndLocal, e.Timezone })
                .ToListAsync(ct);

            var map = new Dictionary<DateOnly, DayException>();
            foreach (var g in rows.GroupBy(r => r.Date))
            {
                var working = g.Any(r => r.IsWorking);
                var windows = working
                    ? g.Where(r => r.IsWorking && r.StartLocal != null && r.EndLocal != null)
                        .Select(r => new AvailabilityWindow(
                            r.StartLocal!.Value, r.EndLocal!.Value,
                            string.IsNullOrWhiteSpace(r.Timezone) ? fallbackTz : r.Timezone!))
                        .ToList()
                    : new List<AvailabilityWindow>();
                map[g.Key] = new DayException(working, windows);
            }
            return map;
        }

        private async Task<List<EffectiveRule>> GetEffectiveRulesAsync(
            int expertProfileId, string fallbackTz, DateTime rangeStart, DateTime rangeEndExclusive, CancellationToken ct)
        {
            var rules = await _context.ExpertAvailabilityRules
                .Where(r => r.ExpertId == expertProfileId && r.IsActive
                            && r.EffectiveFrom < rangeEndExclusive
                            && (r.EffectiveTo == null || r.EffectiveTo >= rangeStart))
                .AsNoTracking()
                .Select(r => new { r.DayOfWeek, r.StartLocal, r.EndLocal, r.Timezone, r.EffectiveFrom, r.EffectiveTo })
                .ToListAsync(ct);

            if (rules.Count > 0)
            {
                return rules
                    .Select(r => new EffectiveRule(
                        r.DayOfWeek, r.StartLocal, r.EndLocal,
                        string.IsNullOrWhiteSpace(r.Timezone) ? fallbackTz : r.Timezone!,
                        r.EffectiveFrom, r.EffectiveTo))
                    .ToList();
            }

            // Fallback legacy: disponibilidad activa del formulario de perfil.
            var legacy = await _context.ExpertAvailabilities
                .Where(ea => ea.ExpertId == expertProfileId && ea.IsActive && ea.EffectiveTo == null
                             && ea.EffectiveFrom < rangeEndExclusive)
                .OrderByDescending(ea => ea.EffectiveFrom)
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            if (legacy == null)
                return new List<EffectiveRule>();

            List<string> dayNames;
            try { dayNames = JsonSerializer.Deserialize<List<string>>(legacy.DaysOfWeek) ?? new List<string>(); }
            catch { dayNames = new List<string>(); }

            var tz = string.IsNullOrWhiteSpace(legacy.Timezone) ? fallbackTz : legacy.Timezone!;
            var result = new List<EffectiveRule>();
            foreach (var name in dayNames)
            {
                if (name != null && DayNameToInt.TryGetValue(name.Trim(), out var dow))
                    result.Add(new EffectiveRule(dow, legacy.StartTime, legacy.EndTime, tz, legacy.EffectiveFrom, legacy.EffectiveTo));
            }
            return result;
        }
    }
}
