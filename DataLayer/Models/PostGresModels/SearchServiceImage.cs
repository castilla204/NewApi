

namespace newApi.DataLayer.Models.PostGresModels
{
    public class SearchServiceImage
    {
        public int Id { get; set; }
        public int SearchServiceId { get; set; }
        public string ImageUrl { get; set; }
        public string ImageObjectName { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public virtual SearchService SearchService { get; set; }
    }
}
