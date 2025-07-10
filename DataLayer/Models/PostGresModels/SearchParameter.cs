using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class SearchParameter
    {
        [Key]
        public int SearchParameterId { get; set; }
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
        public int? ServiceTypeId { get; set; } // Added: Foreign key to ServiceType

        public int SearchId { get; set; }
        public virtual Search Search { get; set; }
        public virtual ServiceType? ServiceType { get; set; } // Added: Navigation to ServiceType
        public virtual ICollection<SearchParameterPlatform> SearchParameterPlatforms { get; set; }
    }
}