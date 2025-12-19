namespace newApi.DataLayer.Models.DTOs
{
    public class ExpertMapDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string ProfilePictureUrl { get; set; }
        public double AverageRating { get; set; }
        public int TotalReviews { get; set; }
        public int CompletedSearches { get; set; }
        public DateTime RegisteredSince { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
        
        // ✅ NUEVO: Precio del servicio (del primer servicio del experto para este tipo de servicio)
        /// <summary>
        /// Precio del servicio en euros (del primer servicio del experto para este categoryId y serviceTypeId)
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// Descripción del servicio (SearchService.Conditions)
        /// </summary>
        public string ServiceDescription { get; set; }

        /// <summary>
        /// Nombre del tipo de servicio (ServiceType.Name)
        /// </summary>
        public string ServiceTypeName { get; set; }

        /// <summary>
        /// Descripción del tipo de servicio (ServiceType.Description)
        /// </summary>
        public string ServiceTypeDescription { get; set; }

        /// <summary>
        /// Disponibilidad actual del experto
        /// </summary>
        public CurrentExpertAvailabilityDto? CurrentAvailability { get; set; }
    }

    public class ExpertMapResponseDto
    {
        public List<ExpertMapDto> Experts { get; set; } = new List<ExpertMapDto>();
        public int TotalCount { get; set; }
    }
}
