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

            var expertProfileId = svc.ExpertProfileId;
            var expertUserId = svc.ExpertProfile.UserId;       // bookings (Appointment.ExpertId) = User id
            var durationHours = AppointmentSlotHours; // longitud del hueco; el plazo (DurationInHours) no manda aquí
            var fallbackTz = string.IsNullOrWhiteSpace(svc.ExpertProfile.Timezone)
                ? "UTC" : svc.ExpertProfile.Timezone;

            // Solo se honra la parte de fecha (evita que el Kind/hora del binder desplace el día).
            // Kind=Utc obligatorio: estas fechas se comparan contra columnas timestamptz (Npgsql lo exige).
            var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
            var dayPlus1 = day.AddDays(1);
            var dow = (int)day.DayOfWeek;

            var rules = await _context.ExpertAvailabilityRules
                .Where(r => r.ExpertId == expertProfileId && r.IsActive && r.DayOfWeek == dow
                            && r.EffectiveFrom < dayPlus1
                            && (r.EffectiveTo == null || r.EffectiveTo >= day))
                .AsNoTracking()
                .ToListAsync(ct);

            if (rules.Count == 0)
                return new List<AvailableSlot>();

            var windows = rules
                .Select(r => new AvailabilityWindow(
                    r.StartLocal, r.EndLocal,
                    string.IsNullOrWhiteSpace(r.Timezone) ? fallbackTz : r.Timezone!))
                .ToList();

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

            var expertProfileId = svc.ExpertProfileId;
            var expertUserId = svc.ExpertProfile.UserId;
            var durationHours = AppointmentSlotHours; // longitud del hueco; el plazo (DurationInHours) no manda aquí
            var fallbackTz = string.IsNullOrWhiteSpace(svc.ExpertProfile.Timezone) ? "UTC" : svc.ExpertProfile.Timezone;

            var start = DateTime.SpecifyKind(fromDate.Date, DateTimeKind.Utc);
            var endExclusive = start.AddDays(days);

            // Una sola query de reglas + una de reservas para toda la ventana.
            var allRules = await _context.ExpertAvailabilityRules
                .Where(r => r.ExpertId == expertProfileId && r.IsActive
                            && r.EffectiveFrom < endExclusive
                            && (r.EffectiveTo == null || r.EffectiveTo >= start))
                .AsNoTracking()
                .ToListAsync(ct);

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

            var now = DateTime.UtcNow;
            for (var i = 0; i < days; i++)
            {
                var day = start.AddDays(i);
                var dayPlus1 = day.AddDays(1);
                var dow = (int)day.DayOfWeek;
                var windows = allRules
                    .Where(r => r.DayOfWeek == dow && r.EffectiveFrom < dayPlus1
                                && (r.EffectiveTo == null || r.EffectiveTo >= day))
                    .Select(r => new AvailabilityWindow(
                        r.StartLocal, r.EndLocal,
                        string.IsNullOrWhiteSpace(r.Timezone) ? fallbackTz : r.Timezone!))
                    .ToList();

                var free = windows.Count == 0
                    ? 0
                    : SlotCalculator.ComputeFreeSlots(windows, bookings, day, durationHours, now).Count;

                result.Add(new DayAvailability(day.ToString("yyyy-MM-dd"), free));
            }

            return result;
        }
    }
}
