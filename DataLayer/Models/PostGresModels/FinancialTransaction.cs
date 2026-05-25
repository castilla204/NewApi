namespace newApi.DataLayer.Models.PostGresModels
{
    public class FinancialTransaction
    {
        public int Id { get; set; }
        public int? UserId { get; set; } // ✅ Nullable para permitir anonimización en eliminación de cuentas

        /// <summary>
        /// P3-10 (versión mínima): mantenido por compatibilidad. Se considera DEPRECATED
        /// frente a <see cref="AmountCents"/> (entero en céntimos) para evitar pérdidas por
        /// redondeo decimal. Mientras la migración esté en curso ambos campos coexisten.
        /// NOTA: el atributo [Obsolete] se difiere (DEFERRED) — habilitar tras actualizar
        /// todos los escritores y readers (ver RESUMEN_P3_PENDIENTES.md).
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>
        /// P3-10 (versión mínima): importe en céntimos. Backfill: AmountCents = ROUND(Amount * 100).
        /// La actualización paralela en TODOS los escritores se difiere (DEFERRED) — ver
        /// RESUMEN_P3_PENDIENTES.md para la lista de archivos pendientes de tocar.
        /// </summary>
        public long AmountCents { get; set; }

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
