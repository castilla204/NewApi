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

    /// <summary>
    /// DTO ultra ligero para marcadores del mapa (solo coordenadas + precio)
    /// Optimizado para carga inicial rápida - similar a Airbnb/Google Maps
    /// </summary>
    public class MapMarkerDto
    {
        public int Id { get; set; }
        public int ServiceId { get; set; }
        public string Latitude { get; set; } = string.Empty;
        public string Longitude { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }

    public class MapMarkersResponseDto
    {
        public List<MapMarkerDto> Markers { get; set; } = new List<MapMarkerDto>();
        public int TotalCount { get; set; }
    }

    /// <summary>
    /// DTO medio para información del sidebar (mínimo 3 imágenes + info completa)
    /// Optimizado para mostrar cards en el sidebar con información suficiente
    /// </summary>
    public class MapSidebarServiceDto
    {
        public int Id { get; set; }
        public decimal Price { get; set; }
        public string ServiceTypeName { get; set; } = string.Empty;
        public string ServiceDescription { get; set; } = string.Empty;  // ✅ Descripción del servicio
        public string ExpertName { get; set; } = string.Empty;
        public string ExpertProfilePictureUrl { get; set; } = string.Empty;
        public double AverageRating { get; set; }
        public int TotalReviews { get; set; }
        public List<string> ImageUrls { get; set; } = new List<string>();  // ✅ Mínimo 3 imágenes
        public string Latitude { get; set; } = string.Empty;
        public string Longitude { get; set; } = string.Empty;
        public decimal? Distance { get; set; }  // Distancia si hay ubicación del usuario
        public CurrentExpertAvailabilityDto? CurrentAvailability { get; set; }  // ✅ Horario de disponibilidad
    }

    public class MapSidebarResponseDto
    {
        public List<MapSidebarServiceDto> Services { get; set; } = new List<MapSidebarServiceDto>();
        public int TotalCount { get; set; }
    }
}
