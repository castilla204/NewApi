using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class LogType
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [MaxLength(50)]
        public string Name { get; set; } = string.Empty; // "Information", "Warning", "Error", "Debug", "Critical"
        
        [MaxLength(200)]
        public string? Description { get; set; }
        
        public int? SeverityId { get; set; } // Opcional, solo para tipos Error
        
        public bool RequiresAdminNotification { get; set; } = false;
        
        public bool RequiresEmailAlert { get; set; } = false;
        
        public bool RequiresSmsAlert { get; set; } = false;
        
        public bool IsActive { get; set; } = true;
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public DateTime? UpdatedAt { get; set; }
        
        // Navigation properties
        public virtual Severity? Severity { get; set; } // Opcional
        public virtual ICollection<Log> Logs { get; set; } = new List<Log>();
    }
}
