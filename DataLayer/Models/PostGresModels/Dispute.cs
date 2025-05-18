namespace newApi.DataLayer.Models.PostGresModels
{
    public class Dispute
    {
        // Identificador único de la disputa
        public int Id { get; set; }
        // Clave foránea a la contratación asociada
        public int SearchHireId { get; set; }
        // Clave foránea al usuario que reporta
        public int ReporterId { get; set; }
        // Razón del reporte
        public string Reason { get; set; }
        // Estado de la disputa (Pending, Resolved)
        public string Status { get; set; }
        // Resolución de la disputa (Refunded, PaidToExpert)
        public string? ResolutionComments { get; set; }
        // Comentarios del administrador sobre la resolución
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Relación con la contratación
        public virtual SearchHire SearchHire { get; set; }
        // Relación con el usuario que reporta
        public virtual User Reporter { get; set; }
    }
}
