namespace newApi.DataLayer.Models.PostGresModels
{
    public class Review
    {
        public int Id { get; set; }
        public int ReviewerId { get; set; }
        public int ExpertId { get; set; }
        public int SearchHireId { get; set; } // Clave foránea hacia SearchHire
        public int Score { get; set; }
        public string Description { get; set; }
        public string[] Images { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public virtual User Reviewer { get; set; }
        public virtual User Expert { get; set; }
        public virtual SearchHire SearchHire { get; set; } // Propiedad de navegación

        public virtual ICollection<ReviewImage> ImagesCollection { get; set; } = new List<ReviewImage>();
    }

}
