namespace newApi.ScrapperGateway.DataLayer.Models.DTOs
{
    public class CreateSearchParameterDto
    {
        public string Keywords { get; set; }
        public string UserSearch { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
        public bool ShippingAvailable { get; set; }
        public bool StrictMatchOnly { get; set; }
        public int? Category { get; set; }
        public int? LocationRange { get; set; }
        public int? MinPrice { get; set; }
        public int? MaxPrice { get; set; }
        public int? BrandId { get; set; }
        public int? ModelId { get; set; }
        public List<int> PlatformIds { get; set; } // Nueva propiedad
    }
}
