namespace newApi.Services
{
    /// <summary>Franja de disponibilidad en hora local del experto (para un día concreto).</summary>
    public readonly record struct AvailabilityWindow(TimeSpan StartLocal, TimeSpan EndLocal, string Timezone);

    /// <summary>Intervalo ya reservado, en UTC.</summary>
    public readonly record struct BookingInterval(DateTime StartUtc, DateTime EndUtc);

    /// <summary>Hueco libre ofrecible al cliente.</summary>
    public sealed record AvailableSlot(DateTime StartUtc, DateTime EndUtc, string StartLocal, string Timezone);

    /// <summary>Resumen de disponibilidad de un día (para colorear el calendario por ocupación).</summary>
    public sealed record DayAvailability(string Date, int FreeSlots, bool IsWorking);

    /// <summary>
    /// Cálculo PURO de huecos libres (sin BD ni DI, testeable en aislamiento):
    /// trocea las franjas de disponibilidad por la duración del servicio, las convierte a UTC
    /// (DST-safe, misma lógica que AppointmentService.GetAppointmentUtc) y descarta las que
    /// solapan con reservas o ya están en el pasado.
    /// </summary>
    public static class SlotCalculator
    {
        public static List<AvailableSlot> ComputeFreeSlots(
            IReadOnlyList<AvailabilityWindow> windows,
            IReadOnlyList<BookingInterval> bookings,
            DateTime date,
            int durationHours,
            DateTime minStartUtc)
        {
            var result = new List<AvailableSlot>();
            if (durationHours <= 0 || windows.Count == 0)
                return result;

            var duration = TimeSpan.FromHours(durationHours);
            var seenStarts = new HashSet<DateTime>();

            foreach (var w in windows)
            {
                for (var s = w.StartLocal; s + duration <= w.EndLocal; s += duration)
                {
                    var startUtc = LocalToUtc(date, s, w.Timezone);
                    // Tiempo transcurrido REAL: no recalcular el wall-clock del fin (cruzar un cambio
                    // de hora DST colapsaría/estiraría el intervalo UTC y dejaría de casar con la cita reservada).
                    var endUtc = startUtc + duration;

                    // LEAD-FIX: el caller pasa minStartUtc = ahora + antelación mínima (SelfBookingPolicy.LeadTimeHours).
                    // Descarta huecos en el pasado Y los demasiado próximos (evita la "cita fantasma" del modo self
                    // cuyo ExpertConfirmationDeadline colapsaría). El modo seller no se ve afectado (arranca en +3 días).
                    if (startUtc < minStartUtc)
                        continue;

                    if (!seenStarts.Add(startUtc))
                        continue; // dedup entre franjas solapadas del mismo día

                    var overlaps = false;
                    foreach (var b in bookings)
                    {
                        if (startUtc < b.EndUtc && endUtc > b.StartUtc) { overlaps = true; break; }
                    }
                    if (overlaps)
                        continue;

                    var localDisplay = (date.Date + s).ToString("yyyy-MM-dd HH:mm");
                    result.Add(new AvailableSlot(startUtc, endUtc, localDisplay, w.Timezone));
                }
            }

            result.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            return result;
        }

        /// <summary>
        /// Hora local de pared → UTC, DST-safe. Réplica de AppointmentService.GetAppointmentUtc:
        /// en fall-back ambiguo toma la primera ocurrencia (offset mayor); en spring-forward
        /// inválido adelanta una hora; si el timezone es desconocido/ vacío trata la hora como UTC.
        /// </summary>
        private static DateTime LocalToUtc(DateTime date, TimeSpan time, string timezone)
        {
            var localWall = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified);
            try
            {
                if (!string.IsNullOrWhiteSpace(timezone))
                {
                    var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
                    if (tz.IsInvalidTime(localWall))
                        localWall = localWall.AddHours(1); // gap de spring-forward

                    if (tz.IsAmbiguousTime(localWall))
                    {
                        var offsets = tz.GetAmbiguousTimeOffsets(localWall);
                        var off = offsets.Length > 0 ? offsets.Max() : tz.GetUtcOffset(localWall);
                        return DateTime.SpecifyKind(localWall - off, DateTimeKind.Utc);
                    }
                    return TimeZoneInfo.ConvertTimeToUtc(localWall, tz);
                }
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }

            return DateTime.SpecifyKind(localWall, DateTimeKind.Utc);
        }
    }
}
