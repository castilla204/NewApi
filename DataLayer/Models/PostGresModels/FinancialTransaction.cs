namespace newApi.DataLayer.Models.PostGresModels
{
    public class FinancialTransaction
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public decimal Amount { get; set; }
        public string TransactionType { get; set; } // "Deposit", "ServicePayment", "Refund"
        public string? RelatedEntityType { get; set; } // Ej. "SearchHire"
        public int? RelatedEntityId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public virtual User User { get; set; }
    }
}
