using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para solicitar retiro de dinero del balance interno
    /// </summary>
    public class WithdrawMoneyRequestDto
    {
        /// <summary>
        /// Cantidad a retirar en euros
        /// </summary>
        [Required]
        [Range(0.01, double.MaxValue, ErrorMessage = "Amount must be greater than 0")]
        public decimal Amount { get; set; }

        /// <summary>
        /// ID del pago original de Stripe (opcional, si no se especifica se usa el más antiguo disponible)
        /// </summary>
        public string? PaymentIntentId { get; set; }
    }

    /// <summary>
    /// DTO para respuesta de retiro de dinero
    /// </summary>
    public class WithdrawMoneyResponseDto
    {
        /// <summary>
        /// Mensaje de confirmación
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Cantidad total refundada
        /// </summary>
        public decimal RefundedAmount { get; set; }

        /// <summary>
        /// IDs de los refunds procesados
        /// </summary>
        public List<string> RefundIds { get; set; } = new List<string>();

        /// <summary>
        /// Balance restante del usuario
        /// </summary>
        public decimal RemainingBalance { get; set; }
    }

    /// <summary>
    /// DTO para información de pagos refundables
    /// </summary>
    public class RefundablePaymentDto
    {
        /// <summary>
        /// ID de la transacción financiera
        /// </summary>
        public int TransactionId { get; set; }

        /// <summary>
        /// ID del PaymentIntent de Stripe
        /// </summary>
        public string PaymentIntentId { get; set; } = string.Empty;

        /// <summary>
        /// Cantidad que se puede refundear
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>
        /// Fecha del pago original
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Días restantes para poder hacer refund (máximo 120 días)
        /// </summary>
        public int DaysRemaining { get; set; }
    }
}
