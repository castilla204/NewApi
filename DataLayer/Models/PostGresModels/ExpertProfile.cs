namespace newApi.DataLayer.Models.PostGresModels
{
    public enum StripeStatus
    {
        NotRequested = 0,    // Usuario nunca ha solicitado cuenta Stripe
        Pending = 1,         // Solicitud enviada, esperando aprobación
        Approved = 2,        // Cuenta aprobada por Stripe
        Rejected = 3,        // Cuenta rechazada por Stripe
        Deauthorized = 4     // Cuenta desautorizada después de ser aprobada
    }

    public class ExpertProfile
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string ProfilePictureUrl { get; set; }
        public string ProfilePictureObjectName { get; set; }
        public string Description { get; set; }
        public string? StripeAccountId { get; set; }
        public string? PendingStripeAccountId { get; set; } // Cuenta temporal hasta completar onboarding
        public bool OnboardingCompleted { get; set; } = false; // Estado del onboarding
        public StripeStatus StripeStatus { get; set; } = StripeStatus.NotRequested; // Estado de la solicitud Stripe
        public string? StripeStatusDetails { get; set; } // Detalles específicos del estado para el frontend
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string Latitude { get; set; }
        public string Longitude { get; set; }
        public virtual User User { get; set; }
        public virtual ICollection<newApi.DataLayer.Models.PostGresModels.SearchService> SearchServices { get; set; }
    }
}