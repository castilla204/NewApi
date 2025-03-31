using System.ComponentModel.DataAnnotations;

namespace DataLayer.Models.PostGresModels
{
    public class User
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string? Password { get; set; }
        public string GoogleId { get; set; }
        public string? PhoneNumber { get; set; }
        public bool PhoneVerified { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsBlocked { get; set; }
        //navigation
        public ICollection<Search> Searches { get; set; }
        public ICollection<Like> Likes { get; set; }
        public ICollection<UserSubscription> UserSubscriptions { get; set; }

        // Relación con SubscriptionPlan (Un usuario tiene un solo plan)
        public int? SubscriptionPlanId { get; set; }
        public virtual SubscriptionPlan SubscriptionPlan { get; set; }
    }
}