namespace newApi.DataLayer.Models.PostGresModels
{
    public class SearchHire
    {
        // Identificador único de la contratación
        public int Id { get; set; }
        // Clave foránea al usuario cliente
        public int ClientId { get; set; }
        // Clave foránea al usuario experto
        public int ExpertId { get; set; }
        // Clave foránea al servicio contratado
        public int SearchServiceId { get; set; }
        // Clave foránea a la búsqueda asociada
        public int SearchId { get; set; }
        // Estado de la contratación (Pending, InProgress, Completed, Disputed)
        public string Status { get; set; }
        // ID de la transacción de Stripe para el pago
        public string StripeTransactionId { get; set; }
        // Monto pagado por el servicio
        public decimal Amount { get; set; }
        // Fecha de creación de la contratación
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        // Fecha de finalización de la contratación (opcional)
        public DateTime? CompletedAt { get; set; }

        // Relación con el cliente
        public virtual User Client { get; set; }
        // Relación con el experto
        public virtual User Expert { get; set; }
        // Relación con el servicio contratado
        public virtual newApi.DataLayer.Models.PostGresModels.SearchService SearchService { get; set; }
        // Relación con la búsqueda asociada
        public virtual Search Search { get; set; }
        // Colección de disputas asociadas a la contratación
        public virtual ICollection<Dispute> Disputes { get; set; }
    }
}
