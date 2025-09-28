using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class SearchServiceDeliverableType
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        public int SearchServiceId { get; set; }

        [Required]
        public int DeliverableTypeId { get; set; }

        public bool IsSelected { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation properties
        public virtual SearchService SearchService { get; set; }
        public virtual DeliverableType DeliverableType { get; set; }
    }
}
