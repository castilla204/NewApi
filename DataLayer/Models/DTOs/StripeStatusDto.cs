using newApi.DataLayer.Models.PostGresModels;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para el estado de onboarding de Stripe
    /// </summary>
    public class OnboardingStatusDto
    {
        public bool HasStripeAccount { get; set; }
        public bool HasPendingOnboarding { get; set; }
        public bool OnboardingCompleted { get; set; }
        public string? StripeAccountId { get; set; }
        public string StripeStatus { get; set; }
        public string? StripeStatusDetails { get; set; }
        public bool CanAccessStripe { get; set; }
        // ✅ FUTURE REQUIREMENTS: Requirements que se deben completar en el futuro según Stripe
        public string? StripeFutureRequirements { get; set; }
        public DateTime? StripeFutureDueAt { get; set; }
    }

    /// <summary>
    /// DTO para el estado completo del experto en Stripe
    /// </summary>
    public class ExpertStatusDto
    {
        public bool HasStripeAccount { get; set; }
        public bool HasPendingOnboarding { get; set; }
        public bool OnboardingCompleted { get; set; }
        public string StripeStatus { get; set; }
        public string? StripeStatusDetails { get; set; }
        public string? StripeAccountId { get; set; }
        public bool CanAccessStripe { get; set; }
        public bool CanCreateServices { get; set; }
        public bool CanReceivePayments { get; set; }
        public string StatusMessage { get; set; }
        public bool CanRetryOnboarding { get; set; }
        public string? RejectionReason { get; set; }
        // ✅ FUTURE REQUIREMENTS: Requirements que se deben completar en el futuro según Stripe
        public string? StripeFutureRequirements { get; set; }
        public DateTime? StripeFutureDueAt { get; set; }
    }

    /// <summary>
    /// DTO para el estado de la cuenta de Stripe
    /// </summary>
    public class StripeAccountStatusDto
    {
        public bool ChargesEnabled { get; set; }
        public bool PayoutsEnabled { get; set; }
        public bool DetailsSubmitted { get; set; }
    }

    /// <summary>
    /// DTO para la respuesta de sincronización de estado de Stripe
    /// </summary>
    public class StripeSyncStatusDto
    {
        public bool HasStripeAccount { get; set; }
        public bool HasPendingOnboarding { get; set; }
        public bool OnboardingCompleted { get; set; }
        public string StripeStatus { get; set; }
        public string? StripeStatusDetails { get; set; }
        public string? StripeAccountId { get; set; }
        public bool CanAccessStripe { get; set; }
        public StripeAccountStatusDto StripeAccountStatus { get; set; }
    }
}

