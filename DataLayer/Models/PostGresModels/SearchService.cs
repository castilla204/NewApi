namespace newApi.DataLayer.Models.PostGresModels
{
    namespace newApi.DataLayer.Models.PostGresModels
    {
        public class SearchService
        {
            public int Id { get; set; }
            public int ExpertProfileId { get; set; }
            public int CategoryId { get; set; }
            public decimal Price { get; set; }
            public string Conditions { get; set; }
            public int DurationInHours { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

            public virtual ExpertProfile ExpertProfile { get; set; }
            public virtual Category Category { get; set; }
            public virtual ICollection<SearchHire> SearchHires { get; set; }
            public virtual ICollection<SearchServiceImage> Images { get; set; } = new List<SearchServiceImage>();
        }
    }
}
