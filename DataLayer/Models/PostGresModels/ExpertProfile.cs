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
        
        /// <summary>
        /// Zona horaria del experto en formato IANA (ej: "Europe/Madrid", "America/Mexico_City")
        /// Se usa para determinar el timezone de todos los servicios del experto
        /// Por defecto: UTC si no se especifica
        /// </summary>
        public string Timezone { get; set; } = "UTC";
        
        /// <summary>
        /// Código de país del experto en formato ISO 3166-1 alpha-2 (ej: "ES", "US", "MX")
        /// Se detecta automáticamente desde las coordenadas al registrarse o actualizar ubicación
        /// </summary>
        public string? Country { get; set; }

        /// <summary>
        /// Nombre de la ciudad del experto (ej: "Madrid", "Barcelona", "México City")
        /// Se detecta automáticamente desde las coordenadas usando Mapbox Geocoding API
        /// </summary>
        public string? City { get; set; }

        /// <summary>
        /// 🌍 Round 21: timestamp del último payout exitoso (payout.paid webhook).
        /// Permite al experto ver en dashboard cuándo recibió pago la última vez.
        /// Nullable porque expertos nuevos no lo tienen aún.
        /// </summary>
        public DateTime? LastPayoutDate { get; set; }
        
        public virtual User User { get; set; }
        public virtual ICollection<newApi.DataLayer.Models.PostGresModels.SearchService> SearchServices { get; set; }
    }
}