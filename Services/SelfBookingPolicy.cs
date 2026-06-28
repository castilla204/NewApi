namespace newApi.Services
{
    /// <summary>
    /// Política de reserva del modo SELF (el cliente elige un hueco del calendario del experto).
    /// Paralela a <see cref="SellerBookingWindow"/> (modo seller), pero el suelo aquí es una
    /// ANTELACIÓN MÍNIMA en horas, no una ventana de días.
    ///
    /// Por qué existe: sin antelación mínima, un cliente podía reservar un hueco que empieza dentro
    /// de minutos. Entonces ExpertConfirmationDeadline = min(now+48h, slotStart) colapsa a esos
    /// minutos → el experto no llega a confirmar → la cita se auto-cancela ("cita fantasma").
    /// Sin pérdida de dinero (authorize-only → reembolso 100%), pero mala UX. El modo seller NO
    /// sufre esto porque arranca en +3 días (72h ≫ LeadTimeHours), así que aplicar este suelo de
    /// forma global a la generación de huecos no recorta nada del seller.
    /// </summary>
    public static class SelfBookingPolicy
    {
        /// <summary>Antelación mínima (horas) para reservar una cita en modo self.</summary>
        public const int LeadTimeHours = 12;
    }
}
