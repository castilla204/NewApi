namespace newApi.DataLayer.Models.DTOs
{
    public class SubscriptionPlanDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public decimal PriceMonthly { get; set; }
        public decimal PriceYearly { get; set; }
        public int MaxSearches { get; set; }
        public int MinSearchInterval { get; set; }
        public bool IsActive { get; set; }
    }

    public class UserSubscriptionDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public SubscriptionPlanDto Plan { get; set; }
        public string Status { get; set; }
        public DateTime CurrentPeriodStart { get; set; }
        public DateTime CurrentPeriodEnd { get; set; }
        public bool CancelAtPeriodEnd { get; set; }
    }

    public class CreateSubscriptionDto
    {
        public int PlanId { get; set; }
        public bool IsYearly { get; set; }
    }

    public class ConfirmSubscriptionDto
    {
        public int PlanId { get; set; }
        public bool IsYearly { get; set; }
        public string StripeSubscriptionId { get; set; }
        public string PaymentIntentId { get; set; }
    }

    public class UpdateSubscriptionDto
    {
        public int PlanId { get; set; }
        public bool IsYearly { get; set; }
    }

    public class SubscriptionDetailsDto
    {
        public bool IsYearly { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string Status { get; set; }
        public decimal Price { get; set; }

        /// <summary>
        /// Round 24: currency del precio de la suscripción (ISO 4217). Default 'EUR'.
        /// </summary>
        public string Currency { get; set; } = "EUR";

        public string BillingPeriod { get; set; }
        public DateTime NextBillingDate { get; set; }
    }
}