namespace newApi.Services
{
    /// <summary>
    /// Política de la ventana de fechas del modo "seller" (Coordínalo Inspecciono).
    /// La cita debe caer entre +1 día (suelo) y, preferentemente, +5 (objetivo). Si el experto
    /// no tiene huecos en ≤5 días, se amplía hasta +14 (tope duro). Constantes GLOBALES fijas:
    /// el cliente NO las configura. La ventana se ancla al día de pago (hire.CreatedAt) y se
    /// DERIVA en runtime — no se persiste en columnas (evita migración EF en esquema con drift).
    ///
    /// El suelo NO baja de 1 día porque la protección real contra "cita fantasma" (el experto no
    /// llega a confirmar) la da el suelo GLOBAL de generación de huecos: AvailabilityService nunca
    /// ofrece un hueco a menos de SelfBookingPolicy.LeadTimeHours (12h) de "ahora". Con suelo +1d
    /// el experto siempre conserva ≥12h para confirmar (deadline = min(ahora+48h, inicioCita)).
    /// Si la coordinación no cuaja, el pago (authorize-only) se reembolsa al 100%.
    /// </summary>
    public static class SellerBookingWindow
    {
        public const int MinLeadDays = 1;
        public const int TargetWindowDays = 5;
        public const int HardMaxDays = 14;

        /// <summary>Nº de días seleccionables hasta el objetivo (+1..+5 inclusive).</summary>
        public const int TargetDays = TargetWindowDays - MinLeadDays + 1;

        /// <summary>Nº de días seleccionables hasta el tope (+1..+14 inclusive).</summary>
        public const int HardDays = HardMaxDays - MinLeadDays + 1;

        private static DateTime AnchorDateUtc(DateTime anchor) =>
            DateTime.SpecifyKind(anchor, DateTimeKind.Utc).Date;

        /// <summary>Primer instante citable: inicio del día (anchor + 1 día).</summary>
        public static DateTime StartUtc(DateTime anchor) => AnchorDateUtc(anchor).AddDays(MinLeadDays);

        /// <summary>Fin objetivo (inicio del día anchor + 6): exclusivo.</summary>
        public static DateTime TargetEndExclusiveUtc(DateTime anchor) =>
            AnchorDateUtc(anchor).AddDays(TargetWindowDays + 1);

        /// <summary>Fin duro (inicio del día anchor + 15): exclusivo. Permite citar el día +14.</summary>
        public static DateTime HardEndExclusiveUtc(DateTime anchor) =>
            AnchorDateUtc(anchor).AddDays(HardMaxDays + 1);

        /// <summary>true si el inicio de cita cae dentro de [suelo, tope] anclado al pago.</summary>
        public static bool IsWithinWindow(DateTime anchor, DateTime startUtc)
        {
            var s = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
            return s >= StartUtc(anchor) && s < HardEndExclusiveUtc(anchor);
        }
    }
}
