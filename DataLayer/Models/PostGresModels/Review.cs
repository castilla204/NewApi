namespace newApi.DataLayer.Models.PostGresModels
{
    public class Review
    {
        // Identificador único de la reseña
        public int Id { get; set; }
        // Clave foránea al usuario que deja la reseña
        public int ReviewerId { get; set; }
        // Clave foránea al usuario experto reseñado
        public int ExpertId { get; set; }
        // Puntuación de la reseña (ej. 1 a 5)
        public int Score { get; set; }
        // Descripción de la reseña
        public string Description { get; set; }
        // Imágenes asociadas a la reseña
        public string[] Images { get; set; }
        // Fecha de creación de la reseña
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Relación con el revisor
        public virtual User Reviewer { get; set; }
        // Relación con el experto
        public virtual User Expert { get; set; }
    }

}
