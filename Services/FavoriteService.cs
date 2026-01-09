using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public class FavoriteService : IFavoriteService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<FavoriteService> _logger;
        private readonly ISearchServiceService _searchServiceService;

        public FavoriteService(
            AppDbContext context,
            ILogger<FavoriteService> logger,
            ISearchServiceService searchServiceService)
        {
            _context = context;
            _logger = logger;
            _searchServiceService = searchServiceService;
        }

        public async Task<(bool Success, string Message, FavoriteDto? Favorite)> AddFavoriteAsync(int userId, int searchServiceId)
        {
            try
            {
                // Verificar que el usuario existe
                var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
                if (!userExists)
                {
                    return (false, "Usuario no encontrado", null);
                }

                // Verificar que el servicio existe y está activo
                var serviceExists = await _context.SearchServices
                    .AnyAsync(ss => ss.Id == searchServiceId && ss.IsActive);
                if (!serviceExists)
                {
                    return (false, "Servicio no encontrado o no está activo", null);
                }

                // Verificar si ya existe el favorito
                var existingFavorite = await _context.SearchServiceFavorites
                    .FirstOrDefaultAsync(f => f.UserId == userId && f.SearchServiceId == searchServiceId);

                if (existingFavorite != null)
                {
                    return (false, "El servicio ya está en favoritos", new FavoriteDto
                    {
                        Id = existingFavorite.Id,
                        UserId = existingFavorite.UserId,
                        SearchServiceId = existingFavorite.SearchServiceId,
                        CreatedAt = existingFavorite.CreatedAt
                    });
                }

                // Crear el favorito
                var favorite = new SearchServiceFavorite
                {
                    UserId = userId,
                    SearchServiceId = searchServiceId,
                    CreatedAt = DateTime.UtcNow
                };

                _context.SearchServiceFavorites.Add(favorite);
                await _context.SaveChangesAsync();

                _logger.LogInformation($"[FAVORITO] Usuario {userId} agregó servicio {searchServiceId} a favoritos");

                return (true, "Servicio agregado a favoritos", new FavoriteDto
                {
                    Id = favorite.Id,
                    UserId = favorite.UserId,
                    SearchServiceId = favorite.SearchServiceId,
                    CreatedAt = favorite.CreatedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[FAVORITO] Error al agregar servicio {searchServiceId} a favoritos del usuario {userId}");
                return (false, "Error al agregar a favoritos", null);
            }
        }

        public async Task<(bool Success, string Message)> RemoveFavoriteAsync(int userId, int searchServiceId)
        {
            try
            {
                var favorite = await _context.SearchServiceFavorites
                    .FirstOrDefaultAsync(f => f.UserId == userId && f.SearchServiceId == searchServiceId);

                if (favorite == null)
                {
                    return (false, "El servicio no está en favoritos");
                }

                _context.SearchServiceFavorites.Remove(favorite);
                await _context.SaveChangesAsync();

                _logger.LogInformation($"[FAVORITO] Usuario {userId} eliminó servicio {searchServiceId} de favoritos");

                return (true, "Servicio eliminado de favoritos");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[FAVORITO] Error al eliminar servicio {searchServiceId} de favoritos del usuario {userId}");
                return (false, "Error al eliminar de favoritos");
            }
        }

        public async Task<(IEnumerable<FavoriteWithServiceDto> Favorites, int TotalCount)> GetUserFavoritesAsync(
            int userId, 
            int page = 1, 
            int pageSize = 20)
        {
            try
            {
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                // Obtener IDs de servicios favoritos con paginación
                var query = _context.SearchServiceFavorites
                    .AsNoTracking()
                    .Where(f => f.UserId == userId)
                    .OrderByDescending(f => f.CreatedAt);

                var totalCount = await query.CountAsync();

                var favorites = await query
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(f => new
                    {
                        f.Id,
                        f.CreatedAt,
                        f.SearchServiceId
                    })
                    .ToListAsync();

                if (!favorites.Any())
                {
                    return (new List<FavoriteWithServiceDto>(), 0);
                }

                // Obtener detalles de los servicios
                var serviceIds = favorites.Select(f => f.SearchServiceId).ToArray();
                
                // Usar el servicio existente para obtener detalles de los servicios
                var servicesQuery = _context.SearchServices
                    .AsNoTracking()
                    .Where(ss => serviceIds.Contains(ss.Id) && ss.IsActive)
                    .Select(ss => new
                    {
                        ServiceId = ss.Id,
                        CategoryId = ss.CategoryId,
                        CategoryName = ss.Category.Name,
                        ServiceTypeId = ss.ServiceTypeId,
                        ServiceTypeName = ss.ServiceType.Name,
                        Price = ss.Price,
                        Images = ss.Images
                            .OrderBy(img => img.Id)
                            .Take(2)
                            .Select(img => new
                            {
                                ImageUrl = img.ImageUrl,
                                ImageObjectName = img.ImageObjectName
                            })
                            .ToList(),
                        ExpertId = ss.ExpertProfile.Id,
                        ExpertName = ss.ExpertProfile.User.Name,
                        ExpertProfilePictureUrl = ss.ExpertProfile.ProfilePictureUrl,
                        ExpertProfilePictureObjectName = ss.ExpertProfile.ProfilePictureObjectName,
                        ExpertCountry = ss.ExpertProfile.Country,
                        AverageRating = ss.ExpertProfile.User.ReviewsReceived.Any()
                            ? ss.ExpertProfile.User.ReviewsReceived.Average(r => (double)r.Score)
                            : 0.0,
                        CompletedSearches = ss.ExpertProfile.User.SearchHiresAsExpert
                            .Count(sh => sh.Status != null && sh.Status.StatusValue == "completed")
                    });

                var services = await servicesQuery.ToListAsync();

                // Combinar favoritos con detalles del servicio
                var result = favorites.Select(f =>
                {
                    var service = services.FirstOrDefault(s => s.ServiceId == f.SearchServiceId);
                    if (service == null) return null;

                    return new FavoriteWithServiceDto
                    {
                        Id = f.Id,
                        CreatedAt = f.CreatedAt,
                        Service = new SearchServiceHomepageDto
                        {
                            Id = service.ServiceId,
                            CategoryId = service.CategoryId,
                            CategoryName = service.CategoryName,
                            ServiceTypeId = service.ServiceTypeId,
                            ServiceTypeName = service.ServiceTypeName,
                            Price = service.Price,
                            Images = service.Images.Select(img => img.ImageUrl).ToList(),
                            ExpertId = service.ExpertId,
                            ExpertName = service.ExpertName,
                            ExpertProfilePictureUrl = service.ExpertProfilePictureUrl,
                            ExpertCountry = service.ExpertCountry,
                            AverageRating = service.AverageRating,
                            CompletedSearches = service.CompletedSearches,
                            IsAvailableNow = false // Por defecto, se puede mejorar después
                        }
                    };
                }).Where(f => f != null).ToList();

                return (result, totalCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[FAVORITO] Error al obtener favoritos del usuario {userId}");
                return (new List<FavoriteWithServiceDto>(), 0);
            }
        }

        public async Task<IsFavoriteDto> IsFavoriteAsync(int userId, int searchServiceId)
        {
            try
            {
                var favorite = await _context.SearchServiceFavorites
                    .AsNoTracking()
                    .FirstOrDefaultAsync(f => f.UserId == userId && f.SearchServiceId == searchServiceId);

                return new IsFavoriteDto
                {
                    IsFavorite = favorite != null,
                    FavoriteId = favorite?.Id
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[FAVORITO] Error al verificar favorito: usuario {userId}, servicio {searchServiceId}");
                return new IsFavoriteDto { IsFavorite = false };
            }
        }

        public async Task<int> GetServiceFavoritesCountAsync(int searchServiceId)
        {
            try
            {
                return await _context.SearchServiceFavorites
                    .AsNoTracking()
                    .CountAsync(f => f.SearchServiceId == searchServiceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[FAVORITO] Error al obtener cantidad de favoritos del servicio {searchServiceId}");
                return 0;
            }
        }

        public async Task<(bool Success, string Message, bool IsNowFavorite)> ToggleFavoriteAsync(int userId, int searchServiceId)
        {
            try
            {
                var existingFavorite = await _context.SearchServiceFavorites
                    .FirstOrDefaultAsync(f => f.UserId == userId && f.SearchServiceId == searchServiceId);

                if (existingFavorite != null)
                {
                    // Eliminar favorito
                    _context.SearchServiceFavorites.Remove(existingFavorite);
                    await _context.SaveChangesAsync();

                    _logger.LogInformation($"[FAVORITO] Usuario {userId} eliminó servicio {searchServiceId} de favoritos (toggle)");
                    return (true, "Servicio eliminado de favoritos", false);
                }
                else
                {
                    // Verificar que el servicio existe y está activo
                    var serviceExists = await _context.SearchServices
                        .AnyAsync(ss => ss.Id == searchServiceId && ss.IsActive);
                    
                    if (!serviceExists)
                    {
                        return (false, "Servicio no encontrado o no está activo", false);
                    }

                    // Agregar favorito
                    var favorite = new SearchServiceFavorite
                    {
                        UserId = userId,
                        SearchServiceId = searchServiceId,
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.SearchServiceFavorites.Add(favorite);
                    await _context.SaveChangesAsync();

                    _logger.LogInformation($"[FAVORITO] Usuario {userId} agregó servicio {searchServiceId} a favoritos (toggle)");
                    return (true, "Servicio agregado a favoritos", true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[FAVORITO] Error al hacer toggle de favorito: usuario {userId}, servicio {searchServiceId}");
                return (false, "Error al actualizar favorito", false);
            }
        }
    }
}
