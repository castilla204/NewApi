using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para parámetros de búsqueda y paginación en la lista de disputas
    /// </summary>
    public class DisputeListRequestDto
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
        /// Término de búsqueda para razón y comentarios
        /// </summary>
        public string? SearchTerm { get; set; }

        /// <summary>
        /// Filtrar por estado de la disputa
        /// </summary>
        public string? Status { get; set; }

        /// <summary>
        /// Filtrar por ID del usuario que reportó
        /// </summary>
        public int? ReporterId { get; set; }

        /// <summary>
        /// Filtrar por ID del cliente
        /// </summary>
        public int? ClientId { get; set; }

        /// <summary>
        /// Filtrar por ID del experto
        /// </summary>
        public int? ExpertId { get; set; }

        /// <summary>
        /// Fecha de inicio para filtrar (CreatedAt >= StartDate)
        /// </summary>
        public DateTime? StartDate { get; set; }

        /// <summary>
        /// Fecha de fin para filtrar (CreatedAt menor o igual a EndDate)
        /// </summary>
        public DateTime? EndDate { get; set; }

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
    /// DTO de respuesta paginada para la lista de disputas
    /// </summary>
    public class DisputeListResponseDto
    {
        /// <summary>
        /// Lista de disputas
        /// </summary>
        public List<DisputeDto> Disputes { get; set; } = new List<DisputeDto>();

        /// <summary>
        /// Metadatos de paginación
        /// </summary>
        public PaginationMetadata Pagination { get; set; } = new PaginationMetadata();

        /// <summary>
        /// Estadísticas de disputas
        /// </summary>
        public DisputeStats Stats { get; set; } = new DisputeStats();
    }

    /// <summary>
    /// DTO para mostrar información de una disputa
    /// </summary>
    public class DisputeDto
    {
        /// <summary>
        /// Identificador único de la disputa
        /// </summary>
        public int Id { get; set; }
        
        /// <summary>
        /// Identificador de la contratación asociada
        /// </summary>
        public int SearchHireId { get; set; }
        
        /// <summary>
        /// Identificador del usuario que reportó la disputa
        /// </summary>
        public int ReporterId { get; set; }
        
        /// <summary>
        /// Razón de la disputa (código raw de Stripe — útil para auditoría / lógica frontend)
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// 🛡️ LOTE C-13: razón traducida al español para mostrar al usuario.
        /// Se rellena en los controllers vía TranslateDisputeReason(Reason).
        /// </summary>
        public string ReasonTranslated { get; set; } = string.Empty;

        /// <summary>
        /// Estado actual de la disputa
        /// </summary>
        public string Status { get; set; } = string.Empty;
        
        /// <summary>
        /// Estado traducido al español
        /// </summary>
        public string StatusTranslated { get; set; } = string.Empty;
        
        /// <summary>
        /// Comentarios de resolución del administrador
        /// </summary>
        public string? ResolutionComments { get; set; }
        
        /// <summary>
        /// Fecha de creación de la disputa
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Respuesta del experto a la disputa del cliente
        /// </summary>
        public string? ExpertResponse { get; set; }

        /// <summary>
        /// Fecha límite para que el experto responda (48h después de la disputa)
        /// </summary>
        public DateTime? ExpertResponseDeadline { get; set; }

        /// <summary>
        /// Fecha cuando el experto respondió
        /// </summary>
        public DateTime? ExpertResponseAt { get; set; }

        /// <summary>
        /// Indica si el experto puede aún responder (dentro de las 48h)
        /// </summary>
        public bool CanExpertRespond { get; set; }

        /// <summary>
        /// Información del SearchHire asociado
        /// </summary>
        public SearchHireInfoDto SearchHire { get; set; } = new SearchHireInfoDto();
        
        /// <summary>
        /// Información del usuario que reportó la disputa
        /// </summary>
        public UserDto Reporter { get; set; } = new UserDto();
        
        /// <summary>
        /// Información del cliente
        /// </summary>
        public UserDto Client { get; set; } = new UserDto();
        
        /// <summary>
        /// Información del experto (puede ser null)
        /// </summary>
        public UserDto? Expert { get; set; }
        
        /// <summary>
        /// Información de la búsqueda asociada
        /// </summary>
        public SearchInfoDto Search { get; set; } = new SearchInfoDto();
        
        /// <summary>
        /// Archivos adjuntos de la disputa
        /// </summary>
        public List<DisputeFileDto> Files { get; set; } = new List<DisputeFileDto>();
    }

    /// <summary>
    /// DTO con información básica del SearchHire
    /// </summary>
    public class SearchHireInfoDto
    {
        /// <summary>
        /// Identificador del SearchHire
        /// </summary>
        public int Id { get; set; }
        
        /// <summary>
        /// Estado del SearchHire
        /// </summary>
        public string Status { get; set; } = string.Empty;
        
        /// <summary>
        /// Estado traducido al español
        /// </summary>
        public string StatusTranslated { get; set; } = string.Empty;
        
        /// <summary>
        /// Monto de la contratación
        /// </summary>
        public decimal Amount { get; set; }
        
        /// <summary>
        /// Fecha de creación
        /// </summary>
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// DTO con información básica de la búsqueda
    /// </summary>
    public class SearchInfoDto
    {
        /// <summary>
        /// Identificador de la búsqueda
        /// </summary>
        public int Id { get; set; }
        
        /// <summary>
        /// Título de la búsqueda
        /// </summary>
        public string Title { get; set; } = string.Empty;
        
        /// <summary>
        /// Descripción de la búsqueda
        /// </summary>
        public string Description { get; set; } = string.Empty;
        
        /// <summary>
        /// Fecha de creación
        /// </summary>
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Estadísticas de disputas para el panel de administración
    /// </summary>
    public class DisputeStats
    {
        /// <summary>
        /// Total de disputas pendientes
        /// </summary>
        public int PendingDisputes { get; set; }

        /// <summary>
        /// Total de disputas resueltas
        /// </summary>
        public int ResolvedDisputes { get; set; }

        /// <summary>
        /// Total de disputas reportadas por clientes
        /// </summary>
        public int ClientDisputes { get; set; }

        /// <summary>
        /// Total de disputas reportadas por expertos
        /// </summary>
        public int ExpertDisputes { get; set; }

        /// <summary>
        /// Total de disputas de esta semana
        /// </summary>
        public int ThisWeekDisputes { get; set; }

        /// <summary>
        /// Total de disputas de este mes
        /// </summary>
        public int ThisMonthDisputes { get; set; }
    }

    /// <summary>
    /// DTO para crear una nueva disputa
    /// </summary>
    public class CreateDisputeDto
    {
        /// <summary>
        /// Identificador de la contratación a disputar
        /// </summary>
        [Required(ErrorMessage = "El ID de la contratación es obligatorio")]
        public int SearchHireId { get; set; }

        /// <summary>
        /// Razón de la disputa
        /// </summary>
        [Required(ErrorMessage = "La razón de la disputa es obligatoria")]
        [StringLength(1000, ErrorMessage = "La razón no puede exceder 1000 caracteres")]
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Archivos adjuntos de la disputa (opcional)
        /// </summary>
        public List<IFormFile>? Files { get; set; }
    }

    /// <summary>
    /// DTO para archivos adjuntos en disputas
    /// </summary>
    public class DisputeFileDto
    {
        /// <summary>
        /// Identificador único del archivo
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Nombre original del archivo
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// URL del archivo en Google Cloud Storage
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// Tipo de archivo (extensión)
        /// </summary>
        public string FileType { get; set; } = string.Empty;

        /// <summary>
        /// Tamaño del archivo en bytes
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// Fecha de creación del archivo
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// URL completa para acceder al archivo (igual que FilePath)
        /// </summary>
        public string FileUrl { get; set; } = string.Empty;

        /// <summary>
        /// ID del usuario que subió el archivo
        /// </summary>
        public int UploadedByUserId { get; set; }

        /// <summary>
        /// Nombre del usuario que subió el archivo
        /// </summary>
        public string UploadedByUserName { get; set; } = string.Empty;

        /// <summary>
        /// Email del usuario que subió el archivo
        /// </summary>
        public string UploadedByUserEmail { get; set; } = string.Empty;

        /// <summary>
        /// Categoría del archivo: "client" o "expert"
        /// </summary>
        public string FileCategory { get; set; } = string.Empty;

        /// <summary>
        /// Etiqueta para mostrar en el frontend (ej: "Archivo del Cliente", "Archivo del Experto")
        /// </summary>
        public string FileCategoryLabel { get; set; } = string.Empty;
    }

    /// <summary>
    /// DTO para resolver una disputa por parte del administrador
    /// </summary>
    public class ResolveDisputeDto
    {
        /// <summary>
        /// Comentarios de resolución del administrador
        /// </summary>
        [Required(ErrorMessage = "Los comentarios de resolución son obligatorios")]
        [StringLength(1000, ErrorMessage = "Los comentarios no pueden exceder 1000 caracteres")]
        public string ResolutionComments { get; set; } = string.Empty;

        /// <summary>
        /// Acción a tomar: refund_client (reembolsar cliente), pay_expert (pagar experto)
        /// </summary>
        [Required(ErrorMessage = "La acción es obligatoria")]
        public string Action { get; set; } = string.Empty;
    }

    /// <summary>
    /// DTO para que el experto responda a una disputa del cliente
    /// </summary>
    public class ExpertResponseDto
    {
        /// <summary>
        /// Respuesta del experto a la disputa
        /// </summary>
        [Required(ErrorMessage = "La respuesta del experto es obligatoria")]
        [StringLength(2000, ErrorMessage = "La respuesta no puede exceder 2000 caracteres")]
        public string Response { get; set; } = string.Empty;

        /// <summary>
        /// Archivos adjuntos de la respuesta del experto (opcional)
        /// </summary>
        public List<IFormFile>? Files { get; set; }
    }
}
