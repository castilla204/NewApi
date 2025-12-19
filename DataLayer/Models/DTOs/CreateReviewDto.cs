namespace newApi.DataLayer.Models.DTOs
{
    public class CreateReviewDto
    {
        public int Score { get; set; }
        public string Description { get; set; } = string.Empty;
        public IFormFile[]? Images { get; set; } // Explicitly nullable
    }

    public class ReviewResponseDto
    {
        public int Id { get; set; }
        public int? ReviewerId { get; set; } // ✅ Nullable para permitir anonimización completa
        public int? ExpertId { get; set; } // ✅ Nullable para permitir anonimización cuando el experto elimina su cuenta
        public int? SearchHireId { get; set; } // ✅ Nullable para permitir anonimización cuando se eliminan SearchHires
        public int Score { get; set; }
        public string Description { get; set; } = string.Empty;
        public List<string> ImageUrls { get; set; } = new List<string>();
        public DateTime CreatedAt { get; set; }
        
        // ✅ INTERNACIONALIZACIÓN: País donde se realizó la contratación
        /// <summary>
        /// País donde se realizó la contratación (ISO 3166-1 alpha-2, ej: "ES", "MX")
        /// Obtenido del SearchHire.ExpertCountry al momento de crear la contratación
        /// </summary>
        public string? Country { get; set; }
    }
}
