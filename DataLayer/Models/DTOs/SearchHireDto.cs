using newApi.Controllers;

using newApi.DataLayer.Models.PostGresModels;

namespace newApi.DataLayer.Models.DTOs
{
    public class SearchHireDto
    {
        public int Id { get; set; }
        public int? ExpertId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string StatusTranslated { get; set; } = string.Empty; // ✅ NUEVO: Estado traducido al español
        public DateTime CreatedAt { get; set; } // ✅ NUEVO: Fecha de contratación del servicio
        public UserDto? Expert { get; set; }
        public ServiceInfo? Service { get; set; } // ✅ NUEVO: Información del servicio
        public SystemStatusDto? StatusInfo { get; set; } // ✅ NUEVO: Información completa del estado
        
        /// <summary>
        /// Monto total pagado (con IVA incluido). Este es el precio final que pagó el cliente.
        /// </summary>
        public decimal? Amount { get; set; }
        
        /// <summary>
        /// Base amount sin IVA/tax (pre-tax). Se calcula desde Stripe Tax breakdown.
        /// Si es null, significa que es un dato antiguo o no hay tax calculado.
        /// En ese caso, usar Amount como fallback.
        /// </summary>
        public decimal? BaseAmount { get; set; }
        
        /// <summary>
        /// Monto de IVA/tax calculado por Stripe Tax. Si es null o 0, no hay tax aplicado.
        /// </summary>
        public decimal? TaxAmount { get; set; }

        /// <summary>
        /// Round 24: currency en la que se realizó el cargo (ISO 4217). Default 'EUR' para
        /// hires legacy. Refunds/disputes muestran SIEMPRE este currency — nunca convertir
        /// el principal del reembolso a otra moneda.
        /// </summary>
        public string ChargeCurrency { get; set; } = "EUR";

        // ✅ INTERNACIONALIZACIÓN: Timezone y país del experto al momento de la contratación
        /// <summary>
        /// Timezone IANA del experto al momento de crear la contratación (ej: "Europe/Madrid", "America/Mexico_City")
        /// </summary>
        public string? ExpertTimezone { get; set; }
        
        /// <summary>
        /// País del experto al momento de crear la contratación (ISO 3166-1 alpha-2, ej: "ES", "MX")
        /// </summary>
        public string? ExpertCountry { get; set; }

        // 🛡️ Round 10 — P-C FIX (V8 snapshots): porcentajes congelados al momento de
        // crear la hire. Si admin cambia StatusConfigurations DESPUÉS, el snapshot
        // preserva el reparto pactado. Frontend los usa con prioridad sobre la config
        // dinámica del estado actual. Null para hires creadas pre-V8.
        public decimal? ClientPercentageSnapshot { get; set; }
        public decimal? ExpertPercentageSnapshot { get; set; }
        public decimal? PlatformPercentageSnapshot { get; set; }
    }

    public class ServiceInfo
    {
        public int Id { get; set; }
        public int ServiceTypeId { get; set; }
        public string ServiceTypeName { get; set; } = string.Empty;
        public int? ServiceTypeCategoryId { get; set; }
        public string? ServiceTypeCategoryName { get; set; }
        public bool RequiresAppointment { get; set; }

        /// <summary>
        /// 🛡️ SNAPSHOT CONTRACTUAL: precio CONTRATADO (BaseAmount del hire), no el
        /// precio actual del servicio — el experto puede haberlo cambiado después.
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// Round 24: currency original del precio del servicio (ISO 4217). Default 'EUR'.
        /// </summary>
        public string Currency { get; set; } = "EUR";

        /// <summary>🛡️ SNAPSHOT CONTRACTUAL: condiciones del servicio al contratar (fallback al dato vivo en hires legacy).</summary>
        public string? Conditions { get; set; }

        /// <summary>🛡️ SNAPSHOT CONTRACTUAL: duración (horas) al contratar.</summary>
        public int? DurationInHours { get; set; }

        // ✅ NUEVOS CAMPOS: Información de ubicación del experto para validación de citas
        public string? ExpertLatitude { get; set; } // Coordenadas del experto al momento de la contratación
        public string? ExpertLongitude { get; set; } // Coordenadas del experto al momento de la contratación
        public int? LocationRange { get; set; } // Rango máximo permitido en km

        /// <summary>
        /// Radio de trabajo del EXPERTO en km (0 = solo en su taller/punto fijo, máx 200).
        /// Distinto de LocationRange (rango de búsqueda del cliente). Con 0, la cita se
        /// realiza en el punto fijo del experto y el frontend prefija la ubicación.
        /// </summary>
        public int? ExpertWorkRadiusKm { get; set; }

        // 🏠 Detalle del punto fijo del experto (snapshot ?? dato vivo). Solo relevante con
        // ExpertWorkRadiusKm == 0 (taller). El cliente los ve para saber cómo llegar.
        public string? ExpertWorkLocationDoor { get; set; }
        public string? ExpertWorkLocationFloor { get; set; }
        public string? ExpertWorkLocationDetails { get; set; }
    }
    public class CreateSearchHireDto
    {
        public int SearchServiceId { get; set; }
        public string? SpecialRequirements { get; set; }
    }

    /// <summary>
    /// DTO for search hire response data
    /// </summary>
    public class SearchHireResponseDto
    {
        public int Id { get; set; }
        public int? ClientId { get; set; } // ✅ Nullable para permitir anonimización completa
        public int? ExpertId { get; set; }
        public int SearchServiceId { get; set; }
        public int? SearchId { get; set; }
        public string Status { get; set; }
        public string StatusTranslated { get; set; } = string.Empty; // ✅ NUEVO: Estado traducido al español
        public string? ExpertTransferId { get; set; }
        public decimal Amount { get; set; }
        
        /// <summary>
        /// Base amount sin IVA/tax (pre-tax). Se calcula desde Stripe Tax breakdown.
        /// Si es null, significa que es un dato antiguo o no hay tax calculado.
        /// En ese caso, usar Amount como fallback.
        /// </summary>
        public decimal? BaseAmount { get; set; }
        
        /// <summary>
        /// Monto de IVA/tax calculado por Stripe Tax. Si es null o 0, no hay tax aplicado.
        /// </summary>
        public decimal? TaxAmount { get; set; }
        
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        /// <summary>
        /// 🛡️ Round 28: divisa en la que se cobró este hire (ISO 4217 MAYÚSCULAS). Snapshot
        /// inmutable de SearchHire.Currency — frontend la usa para mostrar Amount/BaseAmount/
        /// TaxAmount con el símbolo correcto en listados ("Mis contrataciones") y detalles.
        /// </summary>
        public string ChargeCurrency { get; set; } = "EUR";

        public UserDto? Client { get; set; } // ✅ Nullable para manejar usuarios eliminados
        public UserDto? Expert { get; set; }
        public SearchServiceResponseDto Service { get; set; }
        public ServiceTypeDto ServiceType { get; set; }
        public SystemStatusDto? StatusInfo { get; set; } // ✅ NUEVO: Información completa del estado con colores
        
        // Nuevos campos agregados
        public string? SearchTitle { get; set; } // Título de la contratación
        public string? SearchDescription { get; set; } // Descripción de la contratación
        public int UnreadMessagesCount { get; set; } // Número de mensajes pendientes de leer
        
        // ✅ INTERNACIONALIZACIÓN: Timezone y país del experto al momento de la contratación
        /// <summary>
        /// Timezone IANA del experto al momento de crear la contratación (ej: "Europe/Madrid", "America/Mexico_City")
        /// </summary>
        public string? ExpertTimezone { get; set; }
        
        /// <summary>
        /// País del experto al momento de crear la contratación (ISO 3166-1 alpha-2, ej: "ES", "MX")
        /// </summary>
        public string? ExpertCountry { get; set; }

        // 🛡️ Round 10 — P-C FIX (V8 snapshots): mismos campos que en SearchHireDto.
        public decimal? ClientPercentageSnapshot { get; set; }
        public decimal? ExpertPercentageSnapshot { get; set; }
        public decimal? PlatformPercentageSnapshot { get; set; }
    }

    public class UpdateSearchHireStatusDto
    {
        public string Status { get; set; }
    }

    public class SearchServiceImageDto
    {
        public int Id { get; set; }
        public string Url { get; set; }
    }

    public class SearchServiceResponseDto
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public int ServiceTypeId { get; set; }
        public string ServiceTypeName { get; set; }
        public string ServiceTypeDescription { get; set; } // ✅ NUEVO
        public int? ServiceTypeCategoryId { get; set; }
        public bool RequiresAppointment { get; set; }
        public decimal Price { get; set; }

        /// <summary>
        /// 🛡️ Round 28: currency original del precio del servicio (ISO 4217 en MAYÚSCULAS,
        /// ej. "EUR", "GBP", "USD"). Default "EUR" para backward-compat. El frontend lo usa
        /// para mostrar el símbolo correcto en panel de experto y en cards de búsqueda —
        /// antes se hardcodeaba EUR en ServicesTab y aparecía € a expertos UK/CH/SE/etc.
        /// </summary>
        public string Currency { get; set; } = "EUR";

        public string Conditions { get; set; }
        public int DurationInHours { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsActive { get; set; }
        public List<string> ImageUrls { get; set; } // ✅ Mantener para compatibilidad
        public List<SearchServiceImageDto> Images { get; set; } = new List<SearchServiceImageDto>(); // ✅ NUEVO: Incluye IDs
        public ExpertProfileDto? Expert { get; set; }
        public List<DeliverableTypeDto> SelectedDeliverableTypes { get; set; } = new List<DeliverableTypeDto>();
    }

    public class SearchServiceDetailDto : SearchServiceResponseDto
    {
        public string CategoryName { get; set; }
        public int CompletedSearches { get; set; }
        public double AverageRating { get; set; }
        public int TotalReviews { get; set; } // ✅ NUEVO: Total de reseñas del experto
    }

    /// <summary>
    /// DTO ligero optimizado para homepage - solo datos esenciales para mostrar cards
    /// </summary>
    public class SearchServiceHomepageDto
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public string CategoryName { get; set; }
        public int ServiceTypeId { get; set; }
        public string ServiceTypeName { get; set; }
        public decimal Price { get; set; }

        /// <summary>
        /// Round 24: currency original del precio (ISO 4217). Default 'EUR' para retro-compat.
        /// </summary>
        public string Currency { get; set; } = "EUR";

        public List<string> ImageUrls { get; set; } = new List<string>(); // Solo primeras 2-3 imágenes
        public HomepageExpertDto Expert { get; set; }
        public int CompletedSearches { get; set; }
        public double AverageRating { get; set; }
        public bool IsFavorite { get; set; } // ✅ NUEVO: Indica si el servicio es favorito del usuario autenticado
    }

    /// <summary>
    /// DTO ligero del experto para homepage
    /// </summary>
    public class HomepageExpertDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string ProfilePictureUrl { get; set; }
        public string? Country { get; set; }
        /// <summary>
        /// Nombre de la ciudad del experto (ej: "Madrid", "Barcelona")
        /// </summary>
        public string? City { get; set; }
        /// <summary>
        /// Radio de trabajo del experto en km (0 = solo en su taller/punto fijo, máx 200).
        /// </summary>
        public int WorkRadiusKm { get; set; }
        /// <summary>
        /// Horario de disponibilidad del experto (opcional - solo si tiene disponibilidad configurada)
        /// </summary>
        public HomepageExpertAvailabilityDto? Availability { get; set; }
    }

    /// <summary>
    /// DTO ligero de disponibilidad del experto para homepage
    /// </summary>
    public class HomepageExpertAvailabilityDto
    {
        public List<string> DaysOfWeek { get; set; } = new List<string>();
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
    }

    public class ExpertProfileDto
    {
        public int Id { get; set; }
        public string ProfilePictureUrl { get; set; }
        public string Description { get; set; }
        /// <summary>Formación opcional (JSON de items). Se muestra al cliente como señal positiva.</summary>
        public string? Formacion { get; set; }
        public string? StripeAccountId { get; set; }
        public DateTime CreatedAt { get; set; }
        public UserDto User { get; set; }
        public List<ReviewDto> Reviews { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }

        /// <summary>
        /// Radio de trabajo del experto en km (0 = solo en su taller/punto fijo, máx 200).
        /// </summary>
        public int WorkRadiusKm { get; set; }

        /// <summary>Puerta/garaje del taller (solo modo fijo). Null si no aplica.</summary>
        public string? WorkLocationDoor { get; set; }
        /// <summary>Piso/planta del taller (solo modo fijo). Null si no aplica.</summary>
        public string? WorkLocationFloor { get; set; }
        /// <summary>Observaciones de acceso al taller (solo modo fijo). Null si no aplica.</summary>
        public string? WorkLocationDetails { get; set; }
        // 🛡️ C2: serializar como string (nombre del enum) para que el frontend reciba
        // "Approved"/"Pending"/... en vez del entero. Sin JsonStringEnumConverter global,
        // un campo tipado enum se emitía como número y rompía el contrato con ReactWeb.
        public string StripeStatus { get; set; }
        public string? StripeStatusDetails { get; set; }
        public bool OnboardingCompleted { get; set; }
        public bool IsOnVacation { get; set; }
        public CurrentExpertAvailabilityDto? CurrentAvailability { get; set; }
        // ✅ FUTURE REQUIREMENTS: Requirements que se deben completar en el futuro según Stripe
        public string? StripeFutureRequirements { get; set; }
        public DateTime? StripeFutureDueAt { get; set; }
        
        // ✅ INTERNACIONALIZACIÓN: Timezone, país y ciudad del experto
        /// <summary>
        /// Timezone IANA del experto (ej: "Europe/Madrid", "America/Mexico_City")
        /// </summary>
        public string? Timezone { get; set; }
        
        /// <summary>
        /// País del experto (ISO 3166-1 alpha-2, ej: "ES", "MX")
        /// </summary>
        public string? Country { get; set; }
        
        /// <summary>
        /// Ciudad del experto (ej: "Madrid", "Barcelona")
        /// </summary>
        public string? City { get; set; }

        /// <summary>
        /// 🛡️ Round 28 MUD-W: si el experto vino de una mudanza, contiene el ISO
        /// del país previo (ej "ES") para que el frontend (BecomeExpertPage,
        /// ExpertPanelPage) sepa que necesita re-selección de país nuevo. null si
        /// nunca se mudó.
        /// </summary>
        public string? RelocatedFromCountry { get; set; }

        /// <summary>Timestamp UTC del momento de la mudanza.</summary>
        public DateTime? RelocatedAt { get; set; }
    }

    public class UserDto
    {
        public int Id { get; set; }
        public string Email { get; set; }
        public string Name { get; set; }
        public string? ProfilePictureUrl { get; set; }
    }

    public class ReviewDto
    {
        public int Id { get; set; }
        public int Score { get; set; }
        public string Description { get; set; } // Changed from Comment to Description
        public DateTime CreatedAt { get; set; }
        public UserDto Reviewer { get; set; } // ✅ NUEVO: Información del revisor
        public List<string> ImageUrls { get; set; } = new List<string>(); // ✅ NUEVO: URLs de las imágenes
        
        // ✅ INTERNACIONALIZACIÓN: País donde se realizó la contratación
        /// <summary>
        /// País donde se realizó la contratación (ISO 3166-1 alpha-2, ej: "ES", "MX")
        /// Obtenido del SearchHire.ExpertCountry al momento de crear la contratación
        /// </summary>
        public string? Country { get; set; }
    }

    public class CreateSearchServiceRequestDto
    {
        public int ExpertProfileId { get; set; }
        public int CategoryId { get; set; }
        public int ServiceTypeId { get; set; }

        /// <summary>
        /// Precio del servicio CON IVA incluido.
        /// Este es el precio final que pagará el cliente.
        /// Stripe calculará automáticamente el IVA según la ubicación del comprador.
        /// Ejemplo: Si quieres que el cliente pague €110, establece Price = 110.
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// 🌍 Round 21: ISO 4217 currency code (EUR, USD, GBP, MXN, etc).
        /// Default "EUR" para backward-compat — si el front no lo envía, se cobra en euros.
        /// Debe coincidir con la default_currency del Stripe Account del experto.
        /// </summary>
        public string Currency { get; set; } = "EUR";

        public string Conditions { get; set; }
        public int? DurationInHours { get; set; }
        public List<IFormFile> Images { get; set; }
        public string? SelectedDeliverableTypes { get; set; } // JSON string from FormData
    }

    public class UpdateSearchServiceRequestDto
    {
        public int ServiceId { get; set; }
        public int CategoryId { get; set; }
        public int ServiceTypeId { get; set; }

        /// <summary>
        /// Precio del servicio CON IVA incluido.
        /// Este es el precio final que pagará el cliente.
        /// Stripe calculará automáticamente el IVA según la ubicación del comprador.
        /// Ejemplo: Si quieres que el cliente pague €110, establece Price = 110.
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// 🛡️ Round 28: ISO 4217 currency code. Opcional — si null o vacío, el backend
        /// preserva la moneda actual del servicio. Si se envía, debe ser válido en
        /// SupportedCurrenciesList y coincidir con la divisa Stripe del experto.
        /// </summary>
        public string? Currency { get; set; }

        public string Conditions { get; set; }
        public int? DurationInHours { get; set; }
        
        /// <summary>
        /// Nuevas imágenes a agregar al servicio.
        /// Si se proporcionan, se agregarán a las imágenes existentes (a menos que se eliminen explícitamente).
        /// </summary>
        public List<IFormFile>? Images { get; set; }
        
        /// <summary>
        /// IDs de las imágenes a eliminar explícitamente.
        /// Formato: JSON array de enteros, ej: "[1, 2, 3]"
        /// Si se proporciona, se eliminarán estas imágenes del servicio antes de agregar las nuevas.
        /// Si no se proporciona o está vacío, se conservarán todas las imágenes existentes.
        /// </summary>
        public string? ImagesToDelete { get; set; } // JSON string from FormData: array de IDs de SearchServiceImage
        
        public string? SelectedDeliverableTypes { get; set; } // JSON string from FormData
    }



}
