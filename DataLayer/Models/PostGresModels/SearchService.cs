using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class SearchService
    {
        public int Id { get; set; }
        public int? ExpertProfileId { get; set; }
        public int? AIId { get; set; }
        public int CategoryId { get; set; }
        public int ServiceTypeId { get; set; }
        /// <summary>
        /// Precio del servicio CON IVA incluido, expresado en EUROS (no en céntimos).
        /// Moneda base implícita: EUR. Para enviar a Stripe (que usa céntimos), multiplicar por 100.
        /// Debe ser > 0. NO almacenar en céntimos ni en otra divisa sin añadir columna Currency.
        /// </summary>
        public decimal Price { get; set; }
        /// <summary>
        /// 🌍 Round 21: 3-letter ISO 4217 currency code (EUR, USD, GBP, MXN, etc).
        /// Default "EUR" para backward-compat — todos los servicios existentes y nuevos
        /// se cobran en euros salvo que explícitamente se cambie en creación.
        ///
        /// Stripe Connect Express NOTE: el currency debe coincidir con la default_currency
        /// del Stripe Account del experto, sino transfer falla. Validar en CreateSearchService.
        /// </summary>
        [MaxLength(3)]
        public string Currency { get; set; } = "EUR";
        public string Conditions { get; set; }
        public int? DurationInHours { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsActive { get; set; } // Added IsActive property
        public virtual ExpertProfile? ExpertProfile { get; set; }
        public virtual AI? AI { get; set; } // Propiedad de navegación para AI
        public virtual Category Category { get; set; }
        public virtual ServiceType ServiceType { get; set; }
        public virtual ICollection<SearchHire> SearchHires { get; set; }
        public virtual ICollection<SearchServiceImage> Images { get; set; } = new List<SearchServiceImage>();
        public virtual ICollection<SearchServiceDeliverableType> SelectedDeliverableTypes { get; set; } = new List<SearchServiceDeliverableType>();
        
        // ✅ FAVORITOS: Usuarios que marcaron este servicio como favorito
        public virtual ICollection<SearchServiceFavorite> FavoritedBy { get; set; } = new List<SearchServiceFavorite>();
    }
}