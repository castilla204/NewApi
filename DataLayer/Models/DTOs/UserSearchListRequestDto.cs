using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para parámetros de búsqueda y paginación en la lista de búsquedas del usuario
    /// </summary>
    public class UserSearchListRequestDto
    {
        /// <summary>
        /// Número de página (empezando en 1)
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "Page must be greater than 0")]
        public int Page { get; set; } = 1;

        /// <summary>
        /// Tamaño de página (máximo 50)
        /// </summary>
        [Range(1, 50, ErrorMessage = "PageSize must be between 1 and 50")]
        public int PageSize { get; set; } = 20;

        /// <summary>
        /// Término de búsqueda para título y descripción
        /// </summary>
        public string? SearchTerm { get; set; }

        /// <summary>
        /// Filtrar por categoría
        /// </summary>
        public int? Category { get; set; }

        /// <summary>
        /// Filtrar por estado activo (IsActive)
        /// </summary>
        public bool? IsActive { get; set; }

        /// <summary>
        /// Filtrar por estado revisado (IsRevised)
        /// </summary>
        public bool? IsRevised { get; set; }

        /// <summary>
        /// Filtrar por estado del SearchHire
        /// </summary>
        public string? SearchHireStatus { get; set; }

        /// <summary>
        /// Campo por el cual ordenar
        /// </summary>
        public string? SortBy { get; set; } = "CreatedAt";

        /// <summary>
        /// Dirección del ordenamiento (asc/desc)
        /// </summary>
        public string? SortDirection { get; set; } = "desc";
    }

    /// <summary>
    /// DTO de respuesta paginada para la lista de búsquedas del usuario
    /// </summary>
    public class UserSearchListResponseDto
    {
        /// <summary>
        /// Lista de búsquedas del usuario
        /// </summary>
        public List<SearchListDto> Searches { get; set; } = new List<SearchListDto>();

        /// <summary>
        /// Metadatos de paginación
        /// </summary>
        public PaginationMetadata Pagination { get; set; } = new PaginationMetadata();

        /// <summary>
        /// Estadísticas del usuario
        /// </summary>
        public UserSearchStats Stats { get; set; } = new UserSearchStats();
    }

    /// <summary>
    /// Estadísticas de búsquedas del usuario
    /// </summary>
    public class UserSearchStats
    {
        /// <summary>
        /// Total de búsquedas activas
        /// </summary>
        public int ActiveSearches { get; set; }

        /// <summary>
        /// Total de búsquedas inactivas
        /// </summary>
        public int InactiveSearches { get; set; }

        /// <summary>
        /// Total de búsquedas con contratación
        /// </summary>
        public int SearchesWithHire { get; set; }

        /// <summary>
        /// Total de búsquedas sin contratación
        /// </summary>
        public int SearchesWithoutHire { get; set; }

        /// <summary>
        /// Total de mensajes sin leer
        /// </summary>
        public int UnreadMessages { get; set; }

        /// <summary>
        /// Total de citas pendientes
        /// </summary>
        public int PendingAppointments { get; set; }
    }
}
