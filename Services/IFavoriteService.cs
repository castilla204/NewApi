using newApi.DataLayer.Models.DTOs;

namespace newApi.Services
{
    public interface IFavoriteService
    {
        /// <summary>
        /// Agrega un servicio a favoritos del usuario
        /// </summary>
        Task<(bool Success, string Message, FavoriteDto? Favorite)> AddFavoriteAsync(int userId, int searchServiceId);

        /// <summary>
        /// Elimina un servicio de favoritos del usuario
        /// </summary>
        Task<(bool Success, string Message)> RemoveFavoriteAsync(int userId, int searchServiceId);

        /// <summary>
        /// Obtiene todos los favoritos del usuario con detalles del servicio
        /// </summary>
        Task<(IEnumerable<FavoriteWithServiceDto> Favorites, int TotalCount)> GetUserFavoritesAsync(int userId, int page = 1, int pageSize = 20);

        /// <summary>
        /// Verifica si un servicio es favorito del usuario
        /// </summary>
        Task<IsFavoriteDto> IsFavoriteAsync(int userId, int searchServiceId);

        /// <summary>
        /// Obtiene la cantidad de favoritos de un servicio
        /// </summary>
        Task<int> GetServiceFavoritesCountAsync(int searchServiceId);

        /// <summary>
        /// Toggle favorito: agregar si no existe, eliminar si existe
        /// </summary>
        Task<(bool Success, string Message, bool IsNowFavorite)> ToggleFavoriteAsync(int userId, int searchServiceId);
    }
}
