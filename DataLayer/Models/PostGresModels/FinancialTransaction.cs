namespace newApi.DataLayer.Models.PostGresModels
{
    public class FinancialTransaction
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public decimal Amount { get; set; }
        public string TransactionType { get; set; } // "Deposit", "ServicePayment", "Refund", "Payout"
        public string? RelatedEntityType { get; set; } // "SearchHire" para ServicePayment, NULL para Deposit
        public int? RelatedEntityId { get; set; }
        public string? StripeTransferId { get; set; } // ID de transferencia de Stripe para trazabilidad
        public string? StripePaymentIntentId { get; set; } // ID del pago original de Stripe
        public string? StripeRefundId { get; set; } // ID del refund de Stripe
        public bool IsRefunded { get; set; } = false; // Si ya fue refundado
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public virtual User User { get; set; }
    }
}
