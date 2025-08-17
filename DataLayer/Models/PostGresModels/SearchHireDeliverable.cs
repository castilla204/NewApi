using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class SearchHireDeliverable
    {
        [Key]
        public int Id { get; set; }
        public int SearchHireId { get; set; }
        public string Url { get; set; }
        public string ObjectName { get; set; }
        public string Type { get; set; } // "pdf", "video"
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public virtual SearchHire SearchHire { get; set; }
    }
}
