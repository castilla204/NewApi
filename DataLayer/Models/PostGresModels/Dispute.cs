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
        public string Reason { get; set; } = string.Empty;
        // Estado de la disputa (Pending, Resolved)
        public string Status { get; set; } = string.Empty;
        // Resolución de la disputa (Refunded, PaidToExpert)
        public string? ResolutionComments { get; set; }
        // Comentarios del administrador sobre la resolución
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Respuesta del experto a la disputa del cliente
        public string? ExpertResponse { get; set; }
        // Fecha límite para que el experto responda (48h después de la disputa)
        public DateTime? ExpertResponseDeadline { get; set; }
        // Fecha cuando el experto respondió
        public DateTime? ExpertResponseAt { get; set; }
        // Indica si el experto puede aún responder (dentro de las 48h)
        public bool CanExpertRespond => ExpertResponseDeadline.HasValue && DateTime.UtcNow <= ExpertResponseDeadline.Value && ExpertResponseAt == null;

        // Relación con la contratación
        public virtual SearchHire SearchHire { get; set; } = null!;
        // Relación con el usuario que reporta
        public virtual User Reporter { get; set; } = null!;
        // Relación con los archivos adjuntos
        public virtual ICollection<DisputeFile> Files { get; set; } = new List<DisputeFile>();
    }
}
