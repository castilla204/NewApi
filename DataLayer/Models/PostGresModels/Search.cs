using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class Search
    {
        [Key]
        public int Id { get; set; }
        public int UserId { get; set; }
        // Nuevo campo: Clave foránea opcional al usuario experto que realiza la búsqueda
        public int Frequency { get; set; }
        public string Title { get; set; }
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public DateTime LastExecution { get; set; }
        public DateTime NextExecution { get; set; }
        public bool IsRevised { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime StartDate { get; set; }

        public virtual User User { get; set; }

        // Nueva relación: Contratación asociada a la búsqueda
        public virtual SearchHire SearchHire { get; set; }
        public virtual ICollection<SearchParameter> SearchParameters { get; set; }
        public virtual ICollection<SearchResult> SearchResults { get; set; }
    }
}