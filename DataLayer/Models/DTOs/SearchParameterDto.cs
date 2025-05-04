using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace newApi.DataLayer.Models.DTOs
{
    public class SearchParameterDto
    {
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
        public int SearchId { get; set; }
        public List<int> PlatformIds { get; set; }
    }
}