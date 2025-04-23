using System;
using System.Collections.Generic;

namespace newApi.ScrapperGateway.DataLayer.Models.DTOs
{
    public class SearchRequestDto
    {
        public int SearchId { get; set; }
        public string MessageId { get; set; } = Guid.NewGuid().ToString();
        public string Keywords { get; set; }
        public string UserSearch { get; set; }
        public int PagesToScrape { get; set; }
        public int Frequency { get; set; }
        public List<int> PlatformIds { get; set; }
        public int? Category { get; set; }
        public string? Latitude { get; set; }
        public string? Longitude { get; set; }
        public int? MinPrice { get; set; }
        public int? MaxPrice { get; set; }
        public int? BrandId { get; set; }
        public int? ModelId { get; set; }
        public bool Analyze { get; set; }
        public bool IsMultiPage { get; set; }
        public bool ShippingAvailable { get; set; }
        public bool IsProgrammed { get; set; }
    }
}