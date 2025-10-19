using Microsoft.EntityFrameworkCore;
using Stripe.Checkout;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Controllers;
using newApi.Common;

namespace newApi.Services
{
    public class SearchHireService : ISearchHireService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<SearchHireService> _logger;
        private readonly string _domain = "https://atrapo.io";

        public SearchHireService(
    AppDbContext context,
    ILogger<SearchHireService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<IEnumerable<SearchHireResponseDto>> GetClientHires(int userId)
        {
            var hires = await _context.SearchHires
                .Include(h => h.Client)
                .Include(h => h.Expert)
                .Include(h => h.Status)
                .Include(h => h.SearchService)
                    .ThenInclude(s => s.Images)
                .Include(h => h.SearchService)
                    .ThenInclude(s => s.ServiceType)
                .Include(h => h.SearchService)
                    .ThenInclude(s => s.SelectedDeliverableTypes)
                        .ThenInclude(sdt => sdt.DeliverableType)
                .Where(h => h.ClientId == userId)
                .OrderByDescending(h => h.CreatedAt)
                .ToListAsync();

            return hires.Select(MapToResponseDto).ToList();
        }

        public async Task<IEnumerable<SearchHireResponseDto>> GetExpertHires(int userId)
        {
            var hires = await _context.SearchHires
                .Include(h => h.Client)
                .Include(h => h.Expert)
                .Include(h => h.Status)
                .Include(h => h.SearchService)
                    .ThenInclude(s => s.Images)
                .Include(h => h.SearchService)
                    .ThenInclude(s => s.ServiceType)
                .Include(h => h.SearchService)
                    .ThenInclude(s => s.SelectedDeliverableTypes)
                        .ThenInclude(sdt => sdt.DeliverableType)
                .Include(h => h.Search) // Incluir datos de Search para título y descripción
                .Include(h => h.Conversations)
                    .ThenInclude(c => c.Messages) // Incluir mensajes para contar pendientes
                .Where(h => h.ExpertId == userId)
                .OrderByDescending(h => h.CreatedAt)
                .ToListAsync();

            return hires.Select(MapToResponseDto).ToList();
        }

        public async Task<(bool Success, string ErrorMessage)> UpdateHireStatus(int userId, int hireId, string status)
        {
            var hire = await _context.SearchHires
                .Include(sh => sh.SearchService)
                    .ThenInclude(ss => ss.SelectedDeliverableTypes)
                        .ThenInclude(ssdt => ssdt.DeliverableType)
                .Include(sh => sh.Deliverables)
                .Include(sh => sh.Status)
                .FirstOrDefaultAsync(sh => sh.Id == hireId && sh.ExpertId == userId);
            
            if (hire == null)
                return (false, "Servicio no encontrado o no tienes permisos para modificarlo");

            // Validar archivos obligatorios cuando se cambia a "Completed"
            if (status == "completed")
            {
                var validationResult = await ValidateRequiredDeliverables(hire);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Cannot complete SearchHire {HireId}: {ValidationError}", hireId, validationResult.ErrorMessage);
                    return (false, validationResult.ErrorMessage);
                }
                
                hire.UpdatedAt = DateTime.UtcNow;
            }

            hire.StatusId = await GetStatusIdByValueAsync(status);
            await _context.SaveChangesAsync();
            return (true, string.Empty);
        }

        private async Task<(bool IsValid, string ErrorMessage)> ValidateRequiredDeliverables(SearchHire hire)
        {
            try
            {
                // Obtener los tipos de entregables requeridos para este servicio
                var requiredDeliverableTypes = hire.SearchService.SelectedDeliverableTypes
                    .Where(ssdt => ssdt.IsSelected)
                    .Select(ssdt => ssdt.DeliverableType)
                    .ToList();

                if (!requiredDeliverableTypes.Any())
                {
                    return (false, "No se encontraron tipos de entregables configurados para este servicio");
                }

                // Obtener los archivos ya subidos
                var uploadedDeliverables = hire.Deliverables.ToList();

                // Verificar PDF obligatorio
                var pdfType = requiredDeliverableTypes.FirstOrDefault(dt => dt.Name == "PDF");
                if (pdfType != null)
                {
                    var hasPdf = uploadedDeliverables.Any(d => d.Type == "pdf");
                    if (!hasPdf)
                    {
                        return (false, "Es obligatorio subir un archivo PDF antes de completar el servicio");
                    }
                }

                // Verificar video si está configurado
                var videoType = requiredDeliverableTypes.FirstOrDefault(dt => dt.Name == "Video");
                if (videoType != null)
                {
                    var hasVideo = uploadedDeliverables.Any(d => d.Type == "video");
                    if (!hasVideo)
                    {
                        return (false, "Es obligatorio subir un archivo de video antes de completar el servicio");
                    }
                }

                _logger.LogInformation("All required deliverables validated for SearchHire {HireId}", hire.Id);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating required deliverables for SearchHire {HireId}", hire.Id);
                return (false, "Error interno al validar los archivos obligatorios");
            }
        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue
        /// </summary>
        private async Task<int> GetStatusIdByValueAsync(string statusValue)
        {
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == "SearchHireStatus");
            
            if (systemStatus == null)
            {
                _logger.LogWarning("SystemStatus not found for StatusValue: {StatusValue}", statusValue);
                // Default to "pending" (ID = 1)
                return 1;
            }
            
            return systemStatus.Id;
        }

        private static SearchHireResponseDto MapToResponseDto(SearchHire hire)
        {
            // Contar mensajes no leídos del experto en las conversaciones
            var unreadMessagesCount = hire.Conversations
                .SelectMany(c => c.Messages)
                .Count(m => !m.IsRead && m.SenderId != hire.ExpertId); // Mensajes no leídos que NO fueron enviados por el experto

            return new SearchHireResponseDto
            {
                Id = hire.Id,
                ClientId = hire.ClientId,
                ExpertId = hire.ExpertId,
                SearchServiceId = hire.SearchServiceId,
                SearchId = hire.SearchId,
                Status = hire.Status?.StatusValue ?? "unknown",
                StatusTranslated = hire.Status?.StatusValue?.ToSpanishTranslation() ?? "Desconocido",
                ExpertTransferId = hire.ExpertTransferId,
                Amount = hire.Amount,
                CreatedAt = hire.CreatedAt,
                UpdatedAt = hire.UpdatedAt,
                Client = new UserDto
                {
                    Name = hire.Client.Name,
                    Email = hire.Client.Email
                },
                Expert = new UserDto
                {
                    Name = hire.Expert.Name,
                    Email = hire.Expert.Email
                },
                Service = new SearchServiceResponseDto
                {
                    Id = hire.SearchService.Id,
                    CategoryId = hire.SearchService.CategoryId,
                    ServiceTypeId = hire.SearchService.ServiceTypeId,
                    ServiceTypeName = hire.SearchService.ServiceType?.Name,
                    ServiceTypeCategoryId = hire.SearchService.ServiceType?.ServiceTypeCategoryId,
                    RequiresAppointment = hire.SearchService.ServiceType?.RequiresAppointment ?? false,
                    Price = hire.SearchService.Price,
                    Conditions = hire.SearchService.Conditions,
                    DurationInHours = hire.SearchService.DurationInHours ?? 0,
                    CreatedAt = hire.SearchService.CreatedAt,
                    IsActive = hire.SearchService.IsActive,
                    ImageUrls = hire.SearchService.Images.Select(i => i.ImageUrl).ToList(),
                    SelectedDeliverableTypes = hire.SearchService.SelectedDeliverableTypes?.Select(sdt => new DeliverableTypeDto
                    {
                        Id = sdt.DeliverableType.Id,
                        Name = sdt.DeliverableType.Name,
                        DisplayName = sdt.DeliverableType.DisplayName,
                        Description = sdt.DeliverableType.Description,
                        IsRequired = sdt.DeliverableType.IsRequired,
                        IsActive = sdt.DeliverableType.IsActive,
                        SortOrder = sdt.DeliverableType.SortOrder
                    }).ToList() ?? new List<DeliverableTypeDto>()
                },
                ServiceType = hire.SearchService.ServiceType != null ? new ServiceTypeDto
                {
                    Id = hire.SearchService.ServiceType.Id,
                    Name = hire.SearchService.ServiceType.Name,
                    Description = hire.SearchService.ServiceType.Description,
                    IsActive = hire.SearchService.ServiceType.IsActive,
                    CreatedAt = hire.SearchService.ServiceType.CreatedAt,
                    UpdatedAt = hire.SearchService.ServiceType.UpdatedAt
                } : null,
                
                // Nuevos campos agregados
                SearchTitle = hire.Search?.Title,
                SearchDescription = hire.Search?.Description,
                UnreadMessagesCount = unreadMessagesCount
            };
        }

    }
}