namespace newApi.DataLayer.Models.DTOs
{
    public class CreateSearchParameterDto
    {
        public string Keywords { get; set; }
        public string UserSearch { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
        public string? LocationName { get; set; } // Nombre de la ubicación (ej: "Calle Juan Sadar, Soria")
        public bool ShippingAvailable { get; set; }
        public bool StrictMatchOnly { get; set; }
        public int? Category { get; set; }
        public int? LocationRange { get; set; }
        public int? MinPrice { get; set; }
        public int? MaxPrice { get; set; }
        public int? BrandId { get; set; }
        public int? ModelId { get; set; }
        public int? ServiceTypeId { get; set; } // Added: ServiceTypeId
        public List<int> PlatformIds { get; set; }
    }
}