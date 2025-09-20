namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Categorías de tipos de servicio (tipos reales como "Búsqueda + Revisión", "Revisión")
    /// </summary>
    public class ServiceTypeCategory
    {
        public int Id { get; set; }
        public string Name { get; set; } // e.g., "Búsqueda + Revisión", "Revisión", "Búsqueda"
        public string Description { get; set; }
        public int Position { get; set; } = 0; // Posición para ordenar en el frontend
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public virtual ICollection<ServiceType> ServiceTypes { get; set; } = new List<ServiceType>();
    }
}




