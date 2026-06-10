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

        /// <summary>
        /// Saldo en favor del usuario (legacy). La columna existe en la BD de producción
        /// con valor 0 para todos los usuarios; protegida por
        /// <c>CK_Users_Balance_NonNegative</c> declarado en
        /// <see cref="newApi.DataLayer.Models.AppDbContext.OnModelCreating"/>.
        /// La propiedad se reintrodujo aquí para que el modelo C# refleje el esquema
        /// real y <c>EnsureCreatedAsync</c> funcione en bases de datos nuevas (tests).
        /// </summary>
        public decimal Balance { get; set; } = 0m;

        // ✅ SOFT DELETE: Para permitir recuperación y cumplimiento legal
        public bool IsDeleted { get; set; } = false;
        public DateTime? DeletedAt { get; set; }
        /// <summary>
        /// 🌍 Round 22: 3-letter ISO currency for display. Null = auto-detect from IP/country.
        /// User can override via top-nav selector. Persisted across sessions.
        /// </summary>
        [MaxLength(3)]
        public string? PreferredCurrency { get; set; }

        /// <summary>
        /// 🛡️ Round 28 MUD-B: versión de los Términos y Condiciones que el usuario aceptó
        /// (hash sha256 truncado del HTML de los T&C en el momento de la firma).
        /// Sin esto, RGPD Art. 7 ("el consentimiento debe ser demostrable") no se cumple, y la
        /// defensa frente a chargebacks Stripe se debilita ("customer agreement evidence").
        /// LoginModal envía estos campos en el registro — el endpoint de signup debe persistirlos.
        /// </summary>
        [MaxLength(64)]
        public string? TermsVersion { get; set; }
        public DateTime? TermsAcceptedAt { get; set; }

        /// <summary>
        /// 🛡️ Round 28 MUD-B: idem para Política de Privacidad. RGPD Art. 13.3 exige notificar
        /// al usuario los cambios sustanciales — para detectarlos hay que saber qué versión firmó.
        /// </summary>
        [MaxLength(64)]
        public string? PrivacyVersion { get; set; }
        public DateTime? PrivacyAcceptedAt { get; set; }

        /// <summary>
        /// 🛡️ Round 28 MUD-G: país de RESIDENCIA FISCAL declarado por el usuario (ISO 3166-1
        /// alpha-2). Distinto de ExpertProfile.Country (geo/operativo). Necesario para DAC7
        /// reporting fiel — la AEAT reporta sellers según residencia fiscal, no según ubicación.
        /// </summary>
        [MaxLength(2)]
        public string? FiscalCountry { get; set; }
        public DateTime? FiscalCountryChangedAt { get; set; }

        /// <summary>
        /// 🛡️ Round 28 MUD-G: Tax Identification Number — NIF si fiscalCountry=ES, SSN/EIN/ITIN
        /// si fiscalCountry=US, etc. Capturable manualmente o vía Stripe Connect (Stripe lo guarda
        /// pero la plataforma puede necesitarlo para DAC7/1099 sin depender de llamada Stripe).
        /// </summary>
        [MaxLength(50)]
        public string? TaxId { get; set; }
        [MaxLength(2)]
        public string? TaxIdCountry { get; set; }
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