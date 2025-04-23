using System.ComponentModel.DataAnnotations;

namespace newApi.ScrapperGateway.DataLayer.Models.PostGresModels
{
    public class Search
    {
        [Key]
        public int Id { get; set; }
        public int UserId { get; set; }
        public int Frequency { get; set; }
        public string Title { get; set; }
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public DateTime LastExecution { get; set; }
        public DateTime NextExecution { get; set; }
        public bool isRevised { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime StartDate { get; set; }
        public ICollection<SearchParameter> SearchParameters { get; set; }
        public ICollection<SearchResult> SearchResults { get; set; }
        public User User { get; set; }
    }
}