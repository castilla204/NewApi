using DataLayer.Models.PostGresModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataLayer.Models.DTOs
{
    public class PlatformDTO
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ImageUrl { get; set; }
        public string PlatformWebsiteUrl { get; set; }
        public bool IsActive { get; set; }
    }
}
