namespace newApi.DataLayer.Models.PostGresModels
{
    public enum StripeStatus
    {
        NotRequested = 0,        // Usuario nunca ha solicitado cuenta Stripe
        Pending = 1,             // Onboarding inicial en Stripe Connect
        Approved = 2,            // Cuenta aprobada por Stripe (charges/payouts activos)
        Rejected = 3,            // Cuenta rechazada por Stripe (rejection definitiva)
        Deauthorized = 4,        // Cuenta desautorizada después de ser aprobada
        ActionRequired = 5,      // Stripe requiere datos adicionales (requirements.currently_due)
        PendingVerification = 6, // Stripe está verificando documentación enviada (requirements.pending_verification)
        RequirementsDue = 7,     // Hay requisitos con fecha próxima de vencimiento (future_requirements.eventually_due)
        RequirementsPastDue = 8, // Hay requisitos vencidos (requirements.past_due o future_requirements.past_due)
        RestrictedSoon = 9,      // Stripe marcará la cuenta como limitada pronto (restricted soon / future restriction)
        Restricted = 10,         // Cuenta restringida (restricted_forever, disabled_reason no fatal)
        Disabled = 11            // Stripe deshabilitó pagos/cobros (disabled_reason crítico no rechazado)
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
        public string? StripeFutureRequirements { get; set; } // ✅ FUTURE REQUIREMENTS: Requirements que se deben completar en el futuro (eventually_due, past_due)
        public DateTime? StripeFutureDueAt { get; set; } // ✅ FUTURE REQUIREMENTS: Fecha estimada de vencimiento de future requirements
        public bool IsOnVacation { get; set; } = false; // Modo vacaciones - oculta servicios del experto
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string Latitude { get; set; }
        public string Longitude { get; set; }
        public virtual User User { get; set; }
        public virtual ICollection<newApi.DataLayer.Models.PostGresModels.SearchService> SearchServices { get; set; }
    }
}