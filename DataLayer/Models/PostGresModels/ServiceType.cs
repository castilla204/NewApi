namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Tipos específicos de servicio (e.g., "Revisión de Electrodomésticos", "Revisión de Coches")
    /// </summary>
    public class ServiceType
    {
        public int Id { get; set; }
        public string Name { get; set; } // e.g., "Revisión de Electrodomésticos", "Revisión de Coches", "Búsqueda de Inmuebles"
        public string Description { get; set; }
        public int? ServiceTypeCategoryId { get; set; } // Referencia al tipo real (Búsqueda + Revisión, Revisión, etc.)
        public int Position { get; set; } = 0; // Posición para ordenar en el frontend
        public bool IsActive { get; set; } = true;
        public bool RequiresAppointment { get; set; } = false; // Indica si este tipo de servicio requiere cita presencial
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public virtual ServiceTypeCategory ServiceTypeCategory { get; set; }
    }
}