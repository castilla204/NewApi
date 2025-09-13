namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Modelo para manejar la idempotencia de eventos de webhook de Stripe
    /// </summary>
    public class ProcessedWebhookEvent
    {
        public int Id { get; set; }
        
        /// <summary>
        /// ID del evento de Stripe o idempotency key
        /// </summary>
        public string EventId { get; set; }
        
        /// <summary>
        /// Tipo de evento (ej: account.updated, transfer.failed, etc.)
        /// </summary>
        public string EventType { get; set; }
        
        /// <summary>
        /// ID de la cuenta de Stripe asociada
        /// </summary>
        public string? StripeAccountId { get; set; }
        
        /// <summary>
        /// ID del usuario asociado (si se encuentra)
        /// </summary>
        public int? UserId { get; set; }
        
        /// <summary>
        /// Fecha y hora cuando se procesó el evento
        /// </summary>
        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Estado del procesamiento (Success, Failed, etc.)
        /// </summary>
        public string Status { get; set; } = "Success";
        
        /// <summary>
        /// Mensaje de error si el procesamiento falló
        /// </summary>
        public string? ErrorMessage { get; set; }
        
        /// <summary>
        /// Datos adicionales del evento (opcional, para debugging)
        /// </summary>
        public string? EventData { get; set; }
    }
}

