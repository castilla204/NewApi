using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para parámetros de búsqueda y paginación en la lista de búsquedas
    /// </summary>
    public class SearchListRequestDto
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
    /// DTO de respuesta paginada para la lista de búsquedas
    /// </summary>
    public class SearchListResponseDto
    {
        /// <summary>
        /// Lista de búsquedas
        /// </summary>
        public List<SearchListDto> Searches { get; set; } = new List<SearchListDto>();

        /// <summary>
        /// Metadatos de paginación
        /// </summary>
        public PaginationMetadata Pagination { get; set; } = new PaginationMetadata();
    }

    /// <summary>
    /// Metadatos de paginación
    /// </summary>
    public class PaginationMetadata
    {
        /// <summary>
        /// Página actual
        /// </summary>
        public int CurrentPage { get; set; }

        /// <summary>
        /// Tamaño de página
        /// </summary>
        public int PageSize { get; set; }

        /// <summary>
        /// Total de elementos
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// Total de páginas
        /// </summary>
        public int TotalPages { get; set; }

        /// <summary>
        /// Indica si hay página anterior
        /// </summary>
        public bool HasPrevious { get; set; }

        /// <summary>
        /// Indica si hay página siguiente
        /// </summary>
        public bool HasNext { get; set; }
    }
}
