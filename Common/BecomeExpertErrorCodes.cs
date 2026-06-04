namespace newApi.Common
{
    /// <summary>
    /// 🛡️ Round 28: códigos de error estables para BecomeExpert/UpdateExpertProfile.
    /// El frontend los traduce a UI; NO localizar aquí. Sustituyen al catch-all genérico
    /// del controller que mencionaba "Google Cloud Storage" (reliquia pre-Supabase).
    /// </summary>
    public static class BecomeExpertErrorCodes
    {
        // Usuario / sesión
        public const string UserNotFound = "USER_NOT_FOUND";
        public const string UserBlocked = "USER_BLOCKED";
        public const string AlreadyExpert = "ALREADY_EXPERT";
        public const string ExpertProfileAlreadyExists = "EXPERT_PROFILE_ALREADY_EXISTS";
        public const string ExpertProfileNotFound = "EXPERT_PROFILE_NOT_FOUND";
        public const string ActiveHiresAsClient = "ACTIVE_HIRES_AS_CLIENT";

        // Foto
        public const string ProfilePictureRequired = "PROFILE_PICTURE_REQUIRED";
        public const string ProfilePictureTooLarge = "PROFILE_PICTURE_TOO_LARGE";
        public const string ProfilePictureInvalidFormat = "PROFILE_PICTURE_INVALID_FORMAT";
        public const string ProfilePictureUploadFailed = "PROFILE_PICTURE_UPLOAD_FAILED";

        // Ubicación / país
        public const string MissingCoordinates = "MISSING_COORDINATES";
        public const string InvalidCoordinates = "INVALID_COORDINATES";
        public const string CountryDetectionFailed = "COUNTRY_DETECTION_FAILED";
        public const string CountryNotSupported = "COUNTRY_NOT_SUPPORTED";
        // 🛡️ Round 28: el experto ya tiene cuenta Stripe Connect y el nuevo país detectado
        // por coords difiere del país inmutable del Stripe Account. Stripe NO permite cambiar
        // Account.country post-creación → bloquear y orientar al usuario al flujo de cierre+reonboarding.
        public const string StripeCountryLocked = "STRIPE_COUNTRY_LOCKED";

        // Disponibilidad
        public const string MissingAvailability = "MISSING_AVAILABILITY";
        public const string MissingAvailabilityStartTime = "MISSING_AVAILABILITY_START_TIME";
        public const string MissingAvailabilityEndTime = "MISSING_AVAILABILITY_END_TIME";
        public const string InvalidTimeFormat = "INVALID_TIME_FORMAT";
        public const string InvalidTimeRange = "INVALID_TIME_RANGE";
        public const string InvalidDaysOfWeek = "INVALID_DAYS_OF_WEEK";

        // Persistencia / infra
        public const string AvailabilityCreationFailed = "AVAILABILITY_CREATION_FAILED";
        public const string DatabaseError = "DATABASE_ERROR";
        public const string InternalError = "INTERNAL_ERROR";
    }
}
