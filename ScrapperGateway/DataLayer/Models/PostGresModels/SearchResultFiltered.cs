using System.ComponentModel.DataAnnotations;

namespace DataLayer.Models.PostGresModels
{
    public class SearchResultFiltered
    {
        [Key]
        public int Id { get; set; }
        public int SearchId { get; set; }
        public string AdId { get; set; }
        public DateTime FilteredAt { get; set; } = DateTime.UtcNow;
        public bool IsVisible { get; set; } = true;
        public string? FilterNotes { get; set; }

        // Navigation properties
        public Search Search { get; set; }
        public Ad Ad { get; set; }
    }
}