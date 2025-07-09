namespace newApi.DataLayer.Models.PostGresModels
{
    public class Message
    {
        public int Id { get; set; }
        public int ConversationId { get; set; } // Ties the message to a conversation
        public int SenderId { get; set; } // The user (client or expert) who sent the message
        public string Content { get; set; } // The message text
        public DateTime SentAt { get; set; } = DateTime.UtcNow;
        public bool IsRead { get; set; } = false; // Tracks if the message has been read

        // Navigation properties
        public virtual Conversation Conversation { get; set; }
        public virtual User Sender { get; set; }
    }
}