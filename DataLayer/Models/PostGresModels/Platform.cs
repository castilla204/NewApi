using System.Collections.Generic;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class Platform
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ImageUrl { get; set; }
        public string PlatformWebsiteUrl { get; set; }
        public bool IsActive { get; set; }
        public virtual ICollection<Ad> Ads { get; set; }
        public ICollection<SearchParameterPlatform> SearchParameterPlatforms { get; set; }
    }
}
