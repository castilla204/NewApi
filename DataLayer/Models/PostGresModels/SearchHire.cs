

namespace newApi.DataLayer.Models.PostGresModels
{
    public class SearchHire
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public int? ExpertId { get; set; }
        public int SearchServiceId { get; set; }
        public int SearchId { get; set; }
        public string Status { get; set; }
        public string? ExpertTransferId { get; set; }
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
        public DateTime? CompletionDeadline { get; set; }
        public bool? ClientApproved { get; set; }
        public virtual User Client { get; set; }
        public virtual User? Expert { get; set; }
        public virtual SearchService SearchService { get; set; }
        public virtual Search Search { get; set; }
        public virtual ICollection<Dispute> Disputes { get; set; }
        public virtual ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
        public virtual ICollection<SearchHireDeliverable> Deliverables { get; set; } = new List<SearchHireDeliverable>();
    }

}
