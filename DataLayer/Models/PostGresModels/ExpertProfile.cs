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
        Disabled = 11,           // Stripe deshabilitó pagos/cobros (disabled_reason crítico no rechazado)
        // 🛡️ LOTE C-16: estado separado de PendingVerification.
        // PendingVerification = Stripe está procesando docs que YA subió el experto (automático, minutos/horas).
        // UnderReview = el equipo de Stripe está revisando MANUALMENTE la cuenta (caso atípico, días). Causas
        // típicas: monto alto, MCC sensible, Radar flagged, KYC ambiguo. El experto no puede hacer nada,
        // pero merece un mensaje distinto y los admins deben verlo en métricas separadas.
        // Fuente: disabled_reason="under_review" del API de Stripe Accounts.
        // BD: columna `StripeStatus` es `integer` sin CHECK constraint → añadir un valor más NO requiere
        // migración estructural (el `Add-Migration` solo bumpea el snapshot del modelo).
        UnderReview = 12
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
        /// Radio de trabajo del experto en km desde su punto fijo (taller).
        /// 0 = solo trabaja en su taller/punto fijo (el cliente se desplaza).
        /// Máximo 200 km. Default 100 km (la cobertura que el frontend anunciaba
        /// de forma fija antes de que el radio fuera configurable).
        /// </summary>
        [System.ComponentModel.DataAnnotations.Range(0, 200)]
        public int WorkRadiusKm { get; set; } = 100;

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
        /// 🛡️ Round 28 MUD-D: si el experto se mudó (vía wizard de mudanza), guardamos el
        /// país anterior para auditoría + UI ("Antiguo experto en US"). Permite distinguir
        /// reviews recibidas en el contexto anterior de las nuevas.
        /// </summary>
        [System.ComponentModel.DataAnnotations.MaxLength(2)]
        public string? RelocatedFromCountry { get; set; }
        public DateTime? RelocatedAt { get; set; }

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