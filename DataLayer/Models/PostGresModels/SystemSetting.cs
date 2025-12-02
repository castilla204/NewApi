using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class SystemSetting
    {
        [Key]
        public int Id { get; set; }
        public bool IsWhatsAppNotificationEnabled { get; set; } = true;
        public bool IsEmailNotificationEnabled { get; set; } = true;
        public string Theme { get; set; } = "light";
        public int? AIId { get; set; }
        
        // ✅ Configuración de Stripe: modo development o production
        [MaxLength(20)]
        public string StripeMode { get; set; } = "production"; // "development" o "production"
        public DateTime? StripeModeChangedAt { get; set; }
        public int? StripeModeChangedByUserId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public virtual AI AI { get; set; }
    }
}
