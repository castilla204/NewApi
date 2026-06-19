namespace newApi.Services
{
    /// <summary>
    /// Política de la ventana de fechas del modo "seller" (Coordínalo Inspecciono).
    /// La cita debe caer entre +3 días (suelo) y, preferentemente, +7 (objetivo). Si el experto
    /// no tiene huecos en ≤7 días, se amplía hasta +14 (tope duro). Constantes GLOBALES fijas:
    /// el cliente NO las configura. La ventana se ancla al día de pago (hire.CreatedAt) y se
    /// DERIVA en runtime — no se persiste en columnas (evita migración EF en esquema con drift).
    /// </summary>
    public static class SellerBookingWindow
    {
        public const int MinLeadDays = 3;
        public const int TargetWindowDays = 7;
        public const int HardMaxDays = 14;

        /// <summary>Nº de días seleccionables hasta el objetivo (+3..+7 inclusive).</summary>
        public const int TargetDays = TargetWindowDays - MinLeadDays + 1;

        /// <summary>Nº de días seleccionables hasta el tope (+3..+14 inclusive).</summary>
        public const int HardDays = HardMaxDays - MinLeadDays + 1;

        private static DateTime AnchorDateUtc(DateTime anchor) =>
            DateTime.SpecifyKind(anchor, DateTimeKind.Utc).Date;

        /// <summary>Primer instante citable: inicio del día (anchor + 3 días).</summary>
        public static DateTime StartUtc(DateTime anchor) => AnchorDateUtc(anchor).AddDays(MinLeadDays);

        /// <summary>Fin objetivo (inicio del día anchor + 8): exclusivo.</summary>
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
