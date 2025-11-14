using System.ComponentModel.DataAnnotations;
namespace newApi.DataLayer.Models.PostGresModels
{
    public enum UserRole
    {
        Client = 0,
        Expert = 1,
        Admin = 2
    }
    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string? Password { get; set; }
        public string GoogleId { get; set; }
        public string? PhoneNumber { get; set; }
        public bool PhoneVerified { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsBlocked { get; set; }
        public UserRole Role { get; set; }
        // ✅ SOFT DELETE: Para permitir recuperación y cumplimiento legal
        public bool IsDeleted { get; set; } = false;
        public DateTime? DeletedAt { get; set; }
        public virtual ExpertProfile ExpertProfile { get; set; }
        public virtual ICollection<Search> Searches { get; set; }
        public virtual ICollection<Like> Likes { get; set; }
        public virtual ICollection<UserSubscription> UserSubscriptions { get; set; }
        public virtual ICollection<SearchHire> SearchHiresAsClient { get; set; }
        public virtual ICollection<SearchHire> SearchHiresAsExpert { get; set; }
        public virtual ICollection<Review> ReviewsGiven { get; set; }
        public virtual ICollection<Review> ReviewsReceived { get; set; }
        public virtual ICollection<Dispute> DisputesReported { get; set; }
        public virtual UserSetting Settings { get; set; }
        public int? SubscriptionPlanId { get; set; }
        public virtual SubscriptionPlan SubscriptionPlan { get; set; }
        public virtual ICollection<FinancialTransaction> FinancialTransactions { get; set; }
        public virtual ICollection<Conversation> ConversationsAsClient { get; set; } = new List<Conversation>();
        public virtual ICollection<Conversation> ConversationsAsExpert { get; set; } = new List<Conversation>(); 
        public virtual ICollection<Message> MessagesSent { get; set; } = new List<Message>(); 
    }
}