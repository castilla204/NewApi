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

        // 🗓️ Fase D: política de cancelación escalonada, editable desde el panel de admin.
        public int CancellationTierHighHours { get; set; } = 24;   // ≥X h antes → tramo "suave"
        public int CancellationTierLowHours { get; set; } = 6;     // ≥X h antes → tramo "medio"; por debajo → "duro"
        public int FreeCancellationsPerParty { get; set; } = 0;    // N cancelaciones con reembolso íntegro (0 = ninguna)
        public int PenaltyFreeWindowDays { get; set; } = 30;       // ventana móvil para contar las N

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public virtual AI AI { get; set; }
    }
}
