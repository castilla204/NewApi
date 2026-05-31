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
        // 🛡️ Round 16: ahora almacena el HASH BCrypt del password (no plain). Null si
        // el usuario solo usa OAuth (Google/Apple). Reutiliza la columna text existente.
        public string? Password { get; set; }
        public string? GoogleId { get; set; }
        // 🛡️ Round 16: Apple identifier (sub claim del JWT identityToken). Stable per
        // (user, team). Null si el usuario no usa Apple Sign In.
        public string? AppleId { get; set; }
        public string? PhoneNumber { get; set; }
        public bool PhoneVerified { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsBlocked { get; set; }
        // 🛡️ Round 16: email verification + login security tracking
        public bool EmailVerified { get; set; } = false;
        public DateTime? EmailVerifiedAt { get; set; }
        public DateTime? PasswordChangedAt { get; set; }
        public int FailedLoginAttempts { get; set; } = 0;
        public DateTime? LockedUntil { get; set; }
        public UserRole Role { get; set; }
        // ✅ SOFT DELETE: Para permitir recuperación y cumplimiento legal
        public bool IsDeleted { get; set; } = false;
        public DateTime? DeletedAt { get; set; }
        /// <summary>
        /// 🌍 Round 22: 3-letter ISO currency for display. Null = auto-detect from IP/country.
        /// User can override via top-nav selector. Persisted across sessions.
        /// </summary>
        [MaxLength(3)]
        public string? PreferredCurrency { get; set; }
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
        
        // ✅ FAVORITOS: Servicios marcados como favoritos por el usuario
        public virtual ICollection<SearchServiceFavorite> FavoriteServices { get; set; } = new List<SearchServiceFavorite>();
    }
}