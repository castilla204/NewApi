using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class DeliverableType
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Name { get; set; } // e.g., "PDF", "Video"

        [Required]
        [StringLength(100)]
        public string DisplayName { get; set; } // e.g., "Informe PDF", "Video de Inspección"

        [StringLength(500)]
        public string? Description { get; set; }

        public bool IsRequired { get; set; } = false; // Is this deliverable type mandatory?
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; } = 0;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation properties
        public virtual ICollection<SearchServiceDeliverableType> SearchServiceDeliverableTypes { get; set; } = new List<SearchServiceDeliverableType>();
    }
}
