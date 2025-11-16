namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para respuesta de transacciones financieras
    /// </summary>
    public class FinancialTransactionDto
    {
        public int Id { get; set; }
        public decimal Amount { get; set; }
        public string TransactionType { get; set; } = string.Empty; // "Deposit", "ServicePayment", "Refund", "Payout"
        public string? RelatedEntityType { get; set; } // "SearchHire" para ServicePayment, NULL para Deposit
        public int? RelatedEntityId { get; set; }
        public string? StripeTransferId { get; set; }
        public string? StripePaymentIntentId { get; set; }
        public string? StripeRefundId { get; set; }
        public bool IsRefunded { get; set; }
        public DateTime CreatedAt { get; set; }
        
        // Información adicional para el frontend
        public string TransactionTypeDisplay { get; set; } = string.Empty; // "Depósito", "Pago de Servicio"
        public string AmountFormatted { get; set; } = string.Empty; // "+€50.00", "-€25.00"
        public bool IsPositive { get; set; } // true para ingresos, false para gastos
    }

    /// <summary>
    /// DTO para respuesta de lista de transacciones con paginación
    /// </summary>
    public class FinancialTransactionListResponseDto
    {
        public List<FinancialTransactionDto> Transactions { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
        public bool HasNextPage { get; set; }
        public bool HasPreviousPage { get; set; }
    }
}

