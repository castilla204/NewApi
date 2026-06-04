namespace newApi.DataLayer.Models.PostGresModels
{
    public class Review
    {
        public int Id { get; set; }
        public int? ReviewerId { get; set; } // ✅ Nullable para permitir anonimización en eliminación de cuentas
        public int? ExpertId { get; set; } // ✅ Nullable para permitir anonimización cuando el experto elimina su cuenta
        public int? SearchHireId { get; set; } // ✅ Nullable para permitir anonimización cuando se eliminan SearchHires 
        public int Score { get; set; }
        public string Description { get; set; }
        public string[] Images { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 🛡️ Round 28 MUD-D: país donde se prestó el servicio cuando se creó la review
        /// (snapshot inmutable). Backfill desde SearchHire.ExpertCountry. Necesario para
        /// mostrar badge "Servicio prestado en {país}" en ReviewCard cuando el experto se
        /// muda y opera ahora desde un país distinto. Sin este campo, las reviews previas
        /// se mezclan visualmente con las nuevas y confunden al cliente.
        /// </summary>
        [System.ComponentModel.DataAnnotations.MaxLength(2)]
        public string? ReceivedInCountry { get; set; }

        public virtual User Reviewer { get; set; }
        public virtual User Expert { get; set; }
        public virtual SearchHire SearchHire { get; set; } 

        public virtual ICollection<ReviewImage> ImagesCollection { get; set; } = new List<ReviewImage>();
    }

}
