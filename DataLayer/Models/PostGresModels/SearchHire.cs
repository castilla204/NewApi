namespace newApi.DataLayer.Models.PostGresModels
{
    public class SearchHire
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public int ExpertId { get; set; }
        public int SearchServiceId { get; set; }
        public int SearchId { get; set; }
        public string Status { get; set; }
        public string? ExpertTransferId { get; set; } // ID de transferencia a experto
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public DateTime? CompletionDeadline { get; set; } // Plazo para finalización
        public bool? ClientApproved { get; set; } // Null: no revisado, True: aprobado, False: disputado
        public virtual User Client { get; set; }
        public virtual User Expert { get; set; }
        public virtual newApi.DataLayer.Models.PostGresModels.SearchService SearchService { get; set; }
        public virtual Search Search { get; set; }
        public virtual ICollection<Dispute> Disputes { get; set; }
    }
}
