using Twilio.TwiML.Messaging;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class Conversation
    {
        public int Id { get; set; }
        public int? SearchHireId { get; set; } // ✅ Nullable: Ties the conversation to a specific SearchHire (null for pre-hire conversations)
        public int? SearchServiceId { get; set; } // ✅ NUEVO: Para conversaciones previas a contratar (null for post-hire conversations)
        public int? ClientId { get; set; } // ✅ Nullable para permitir anonimización en eliminación de cuentas
        public int? ExpertId { get; set; } // ✅ Nullable para permitir anonimización en eliminación de cuentas
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow; // Updated when new messages are added
        public bool IsActive { get; set; } = true; // Allows closing/disabling conversations

        // Navigation properties
        public virtual SearchHire? SearchHire { get; set; } // ✅ Nullable para conversaciones previas
        public virtual SearchService? SearchService { get; set; } // ✅ NUEVO: Para conversaciones previas a contratar
        public virtual User? Client { get; set; }
        public virtual User? Expert { get; set; }
        public virtual ICollection<Message> Messages { get; set; } = new List<Message>();
    }
}