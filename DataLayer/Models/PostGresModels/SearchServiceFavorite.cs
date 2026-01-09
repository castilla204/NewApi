using System;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Representa un servicio marcado como favorito por un usuario
    /// </summary>
    public class SearchServiceFavorite
    {
        public int Id { get; set; }
        
        /// <summary>
        /// ID del usuario que marcó el servicio como favorito
        /// </summary>
        public int UserId { get; set; }
        
        /// <summary>
        /// ID del servicio marcado como favorito
        /// </summary>
        public int SearchServiceId { get; set; }
        
        /// <summary>
        /// Fecha en que se marcó como favorito
        /// </summary>
        public DateTime CreatedAt { get; set; }

        // Relaciones
        public virtual User User { get; set; }
        public virtual SearchService SearchService { get; set; }
    }
}
