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
    }
}
