namespace newApi.Services
{
    /// <summary>
    /// Política PURA de cancelación escalonada (Fase D), testeable en aislamiento.
    /// Decide el AppointmentStatus de cancelación según la antelación y el actor.
    /// Solo aplica a citas con hueco (StartsAtUtc); las citas legacy usan la lógica clásica.
    /// </summary>
    public static class CancellationPolicy
    {
        public const string ClientGt24h = "appointment_cancelled_by_client_gt24h";   // 100/0/0
        public const string Client6to24h = "appointment_cancelled_by_client_6to24h"; // 50/50/0
        public const string ClientLt6h = "appointment_cancelled_by_client_lt6h";     // 0/100/0
        public const string ExpertStrike = "appointment_cancelled_by_expert_strike"; // 100/0/0 + strike

        /// <summary>
        /// Estado para una cancelación del CLIENTE.
        /// - ≥ tierHighHours y aún le quedan cancelaciones gratis (penaltyFreeUsed &lt; N) → 100% (ClientGt24h).
        /// - ≥ tierHighHours pero agotada N → baja al tramo medio (Client6to24h, 50/50).
        /// - tierLowHours … tierHighHours → tramo medio (Client6to24h, 50/50).
        /// - &lt; tierLowHours / no-show → tramo duro (ClientLt6h, 0/100).
        /// Con N=0 (default) ninguna cancelación es penalty-free: hasta &gt;24h cae a 50/50.
        /// </summary>
        public static string ResolveClientStatus(
            double hoursUntilAppointment,
            int penaltyFreeUsed,
            int tierHighHours,
            int tierLowHours,
            int freeCancellationsPerParty)
        {
            if (hoursUntilAppointment >= tierHighHours)
                return penaltyFreeUsed < freeCancellationsPerParty ? ClientGt24h : Client6to24h;
            if (hoursUntilAppointment >= tierLowHours)
                return Client6to24h;
            return ClientLt6h;
        }
    }
}
