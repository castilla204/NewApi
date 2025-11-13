namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para la respuesta completa de detalles de búsqueda
    /// </summary>
    public class SearchDetailsCompleteResponseDto
    {
        public SearchListDto Search { get; set; }
        public MoneyDistributionConfigDto? MoneyDistribution { get; set; }
        public CategoryDto? Category { get; set; }
        public ReviewDto? Review { get; set; }
        public AppointmentDto? Appointment { get; set; }
        public List<DeliverableDto> Deliverables { get; set; } = new List<DeliverableDto>();
        public List<DisputeDto> Disputes { get; set; } = new List<DisputeDto>();
        /// <summary>
        /// ✅ NUEVO: Tipos de reportes requeridos para este servicio
        /// </summary>
        public List<DeliverableTypeDto> RequiredDeliverableTypes { get; set; } = new List<DeliverableTypeDto>();
        /// <summary>
        /// ✅ NUEVO: Perfil completo del experto incluyendo horarios de disponibilidad
        /// </summary>
        public ExpertProfileDto? ExpertProfile { get; set; }
    }
}















