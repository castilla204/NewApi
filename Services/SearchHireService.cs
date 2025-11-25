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
        private readonly string _domain = "https://inspecciono.com";

        public SearchHireService(AppDbContext context)
        {
            _context = context;
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
                .Where(h => h.ClientId.HasValue && h.ClientId.Value == userId)
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
                    return (false, validationResult.ErrorMessage);
                }
                
                hire.UpdatedAt = DateTime.UtcNow;
            }

            var statusId = await FindStatusIdByValueAsync(status);
            if (!statusId.HasValue)
            {
                return (false, $"Status '{status}' does not exist for SearchHire entities");
            }

            hire.StatusId = statusId.Value;
            if (status != "completed")
            {
                hire.UpdatedAt = DateTime.UtcNow;
            }
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
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, "Error interno al validar los archivos obligatorios");
            }
        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue
        /// </summary>
        private async Task<int?> FindStatusIdByValueAsync(string statusValue)
        {
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == "SearchHireStatus");

            return systemStatus?.Id;
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
                Client = hire.ClientId.HasValue && hire.Client != null ? new UserDto
                {
                    Name = hire.Client.Name,
                    Email = hire.Client.Email
                } : null, // ✅ Manejar caso donde ClientId es null (usuario eliminado)
                Expert = hire.ExpertId.HasValue && hire.Expert != null ? new UserDto
                {
                    Name = hire.Expert.Name,
                    Email = hire.Expert.Email
                } : null, // ✅ Manejar caso donde ExpertId es null (usuario eliminado)
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
                
                // ✅ NUEVO: Información completa del estado con colores
                StatusInfo = hire.Status != null ? new SystemStatusDto
                {
                    Id = hire.Status.Id,
                    StatusType = hire.Status.StatusType,
                    StatusName = hire.Status.StatusName,
                    StatusValue = hire.Status.StatusValue,
                    DisplayName = hire.Status.DisplayName,
                    Description = hire.Status.Description,
                    Color = hire.Status.Color,
                    IsActive = hire.Status.IsActive,
                    IsFinalizationStatus = hire.Status.IsFinalizationStatus,
                    SortOrder = hire.Status.SortOrder,
                    CreatedAt = hire.Status.CreatedAt,
                    UpdatedAt = hire.Status.UpdatedAt
                } : null,
                
                // Nuevos campos agregados
                SearchTitle = hire.Search?.Title,
                SearchDescription = hire.Search?.Description,
                UnreadMessagesCount = unreadMessagesCount
            };
        }

    }
}