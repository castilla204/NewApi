namespace newApi.DataLayer.Models.PostGresModels
{
    public class SearchService
    {
        public int Id { get; set; }
        public int? ExpertProfileId { get; set; }
        public int? AIId { get; set; }
        public int CategoryId { get; set; }
        public int ServiceTypeId { get; set; }
        public decimal Price { get; set; }
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