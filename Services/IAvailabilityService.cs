namespace newApi.Services
{
    /// <summary>
    /// Genera los huecos libres ofrecibles a un cliente para un servicio en una fecha.
    /// Servicio pequeño y enfocado (solo necesita AppDbContext) para que el cálculo de
    /// disponibilidad sea testeable sin el grafo de DI de AppointmentService.
    /// </summary>
    public interface IAvailabilityService
    {
        Task<List<AvailableSlot>> GetAvailableSlotsAsync(int serviceId, DateTime date, CancellationToken ct = default);

        /// <summary>Nº de huecos libres por día en una ventana, para colorear el calendario por ocupación.</summary>
        Task<List<DayAvailability>> GetAvailabilitySummaryAsync(int serviceId, DateTime fromDate, int days, CancellationToken ct = default);

        /// <summary>
        /// Valida que (startUtc, endUtc) sea un hueco REALMENTE libre y ofrecible para el servicio.
        /// Reutiliza GetAvailableSlotsAsync sobre el día local candidato (UTC date ±1 para cubrir el
        /// offset de timezone) y devuelve true sólo si algún hueco coincide exactamente. Defensa
        /// server-side: el cliente no puede inventarse un hueco que el calendario no ofrece.
        /// </summary>
        Task<bool> IsSlotBookableAsync(int serviceId, DateTime startUtc, DateTime endUtc, CancellationToken ct = default);
    }
}
