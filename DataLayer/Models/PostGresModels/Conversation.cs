using Twilio.TwiML.Messaging;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class Conversation
    {
        public int Id { get; set; }
        public int SearchHireId { get; set; } // Ties the conversation to a specific SearchHire
        public int ClientId { get; set; } // The client participating in the conversation
        public int ExpertId { get; set; } // The expert participating in the conversation
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow; // Updated when new messages are added
        public bool IsActive { get; set; } = true; // Allows closing/disabling conversations

        // Navigation properties
        public virtual SearchHire SearchHire { get; set; }
        public virtual User Client { get; set; }
        public virtual User Expert { get; set; }
        public virtual ICollection<Message> Messages { get; set; } = new List<Message>();
    }
}