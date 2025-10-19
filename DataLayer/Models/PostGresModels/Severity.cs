using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class Severity
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [MaxLength(20)]
        public string Name { get; set; } = string.Empty; // "Critical", "High", "Medium", "Low"
        
        [MaxLength(100)]
        public string? Description { get; set; }
        
        public int SortOrder { get; set; } = 0; // Para ordenar por importancia
        
        public bool IsActive { get; set; } = true;
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public DateTime? UpdatedAt { get; set; }
        
        // Navigation properties
        public virtual ICollection<LogType> LogTypes { get; set; } = new List<LogType>();
    }
}
