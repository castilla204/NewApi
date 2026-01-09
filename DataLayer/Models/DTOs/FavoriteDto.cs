using newApi.DataLayer.Models.PostGresModels;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para responder con un favorito simple
    /// </summary>
    public class FavoriteDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int SearchServiceId { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// DTO para responder con el servicio favorito y sus detalles
    /// </summary>
    public class FavoriteWithServiceDto
    {
        public int Id { get; set; }
        public DateTime CreatedAt { get; set; }
        
        // Información del servicio
        public SearchServiceHomepageDto Service { get; set; }
    }

    /// <summary>
    /// DTO para agregar un servicio a favoritos
    /// </summary>
    public class AddFavoriteDto
    {
        public int SearchServiceId { get; set; }
    }

    /// <summary>
    /// DTO para verificar si un servicio es favorito
    /// </summary>
    public class IsFavoriteDto
    {
        public bool IsFavorite { get; set; }
        public int? FavoriteId { get; set; } // ID del favorito si existe
    }
}
