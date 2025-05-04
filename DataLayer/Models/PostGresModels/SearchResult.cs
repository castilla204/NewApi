using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class SearchResult
    {
        [Key]
        public int Id { get; set; }
        public int SearchId { get; set; }
        public string AdId { get; set; }
        public DateTime FoundAt { get; set; } = DateTime.UtcNow;
        public Search Search { get; set; }
        public Ad Ad { get; set; }
    }
}