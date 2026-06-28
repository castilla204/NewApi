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
        /// - ≥ tierHighHours → 100% (ClientGt24h) salvo ABUSO por repetición; honra la promesa del checkout
        ///   ("cancelación sin coste antes de la revisión"). El cupo N (freeCancellationsPerParty) ya NO
        ///   gobierna la 1ª cancelación legítima: cuenta cancelaciones &gt;24h PREVIAS del cliente en la ventana
        ///   móvil; solo cuando se SUPERA ese cupo de repeticiones se degrada a 50/50.
        ///   Con N=0 (default): la 1ª cancelación &gt;24h es 100% (penaltyFreeUsed=0 ≤ 0); la 2ª en la ventana
        ///   (penaltyFreeUsed=1 &gt; 0) cae a 50/50 (anti-abuso). Antes (con &lt;) incluso la 1ª caía a 50/50,
        ///   contradiciendo la promesa.
        /// - tierLowHours … tierHighHours → tramo medio (Client6to24h, 50/50).
        /// - &lt; tierLowHours / no-show → tramo duro (ClientLt6h, 0/100).
        /// </summary>
        public static string ResolveClientStatus(
            double hoursUntilAppointment,
            int penaltyFreeUsed,
            int tierHighHours,
            int tierLowHours,
            int freeCancellationsPerParty)
        {
            if (hoursUntilAppointment >= tierHighHours)
                // BUG #2 FIX: `<=` (era `<`). Desacopla el reembolso legítimo por antelación del cupo
                // anti-abuso: la 1ª cancelación con antelación suficiente SIEMPRE es 100% (promesa del
                // checkout); el cupo penaliza solo la REPETICIÓN (2ª+ en la ventana con N=0).
                return penaltyFreeUsed <= freeCancellationsPerParty ? ClientGt24h : Client6to24h;
            if (hoursUntilAppointment >= tierLowHours)
                return Client6to24h;
            return ClientLt6h;
        }
    }
}
