using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class Log
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        public string Message { get; set; } = string.Empty;
        
        public string? Details { get; set; }
        
        public int? UserId { get; set; }
        
        public string? Source { get; set; }
        
        public int? LogTypeId { get; set; }
        
        public string? RelatedEntityType { get; set; } // "SearchHire", "Payment", "Transfer", etc.
        
        public int? RelatedEntityId { get; set; }
        
        public string? AdditionalData { get; set; } // JSON para datos adicionales
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public virtual User? User { get; set; }
        public virtual LogType? LogType { get; set; }
    }
}
