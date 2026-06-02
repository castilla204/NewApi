using TimeZoneConverter;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using System.Globalization;

namespace newApi.Services
{
    /// <summary>
    /// Servicio para manejo de conversiones de zona horaria
    /// Sigue las mejores prácticas para apps multi-país:
    /// - Todo se guarda en UTC en la BD
    /// - Las conversiones se hacen al recibir (local → UTC) y al devolver (UTC → local)
    /// - Se usan zonas IANA (Europe/Madrid, America/Mexico_City, etc.)
    /// </summary>
    public interface ITimezoneService
    {
        /// <summary>
        /// Convierte una fecha/hora local a UTC
        /// </summary>
        /// <param name="localDateTime">Fecha/hora en zona local del usuario</param>
        /// <param name="ianaTimezone">Zona horaria IANA (ej: "Europe/Madrid")</param>
        /// <returns>Fecha/hora en UTC</returns>
        DateTime ConvertToUtc(DateTime localDateTime, string ianaTimezone);
        
        /// <summary>
        /// Convierte una fecha/hora UTC a hora local del usuario
        /// </summary>
        /// <param name="utcDateTime">Fecha/hora en UTC</param>
        /// <param name="ianaTimezone">Zona horaria IANA (ej: "Europe/Madrid")</param>
        /// <returns>Fecha/hora en zona local</returns>
        DateTime ConvertFromUtc(DateTime utcDateTime, string ianaTimezone);
        
        /// <summary>
        /// Valida si una zona horaria IANA es válida
        /// </summary>
        /// <param name="ianaTimezone">Zona horaria a validar</param>
        /// <returns>True si es válida</returns>
        bool IsValidTimezone(string ianaTimezone);
        
        /// <summary>
        /// Obtiene la zona horaria efectiva del usuario
        /// Prioridad: timezone del DTO > timezone del UserSetting > UTC
        /// </summary>
        /// <param name="dtoTimezone">Timezone enviado en el DTO (puede ser null)</param>
        /// <param name="userSettingTimezone">Timezone del UserSetting (puede ser null)</param>
        /// <returns>Zona horaria a usar</returns>
        string GetEffectiveTimezone(string? dtoTimezone, string? userSettingTimezone);
        
        /// <summary>
        /// Obtiene el TimeZoneInfo para una zona IANA
        /// </summary>
        /// <param name="ianaTimezone">Zona horaria IANA</param>
        /// <returns>TimeZoneInfo o null si no existe</returns>
        TimeZoneInfo? GetTimeZoneInfo(string ianaTimezone);
        
        /// <summary>
        /// Detecta la zona horaria IANA desde coordenadas geográficas (latitud, longitud)
        /// Usa una API externa para obtener el timezone basado en la ubicación
        /// </summary>
        /// <param name="latitude">Latitud en grados decimales</param>
        /// <param name="longitude">Longitud en grados decimales</param>
        /// <returns>Zona horaria IANA (ej: "Europe/Madrid") o "UTC" si no se puede detectar</returns>
        Task<string> GetTimezoneFromCoordinatesAsync(decimal latitude, decimal longitude);
        
        /// <summary>
        /// Detecta el país desde coordenadas geográficas (latitud, longitud)
        /// Usa Mapbox Geocoding API para obtener el código de país (ISO 3166-1 alpha-2)
        /// </summary>
        /// <param name="latitude">Latitud en grados decimales</param>
        /// <param name="longitude">Longitud en grados decimales</param>
        /// <returns>Código de país ISO 3166-1 alpha-2 (ej: "ES", "US", "MX") o null si no se puede detectar</returns>
        Task<string?> GetCountryFromCoordinatesAsync(decimal latitude, decimal longitude);
        
        /// <summary>
        /// Detecta la ciudad desde coordenadas geográficas (latitud, longitud)
        /// Usa Mapbox Geocoding API para obtener el nombre de la ciudad
        /// </summary>
        /// <param name="latitude">Latitud en grados decimales</param>
        /// <param name="longitude">Longitud en grados decimales</param>
        /// <returns>Nombre de la ciudad (ej: "Madrid", "Barcelona") o null si no se puede detectar</returns>
        Task<string?> GetCityFromCoordinatesAsync(decimal latitude, decimal longitude);
        
        /// <summary>
        /// Detecta el país desde la dirección IP del cliente
        /// Usa ip-api.com (servicio gratuito) para obtener el código de país (ISO 3166-1 alpha-2)
        /// </summary>
        /// <param name="ipAddress">Dirección IP del cliente</param>
        /// <returns>Código de país ISO 3166-1 alpha-2 (ej: "ES", "US", "MX") o "ES" por defecto si no se puede detectar</returns>
        Task<string> GetCountryFromIpAddressAsync(string ipAddress);
    }

    public class TimezoneService : ITimezoneService
    {
        private readonly ILogger<TimezoneService> _logger;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly string? _mapboxPublicToken;
        
        // Zonas horarias más comunes para validación rápida
        private static readonly HashSet<string> CommonTimezones = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UTC",
            "Europe/Madrid",
            "Europe/London",
            "Europe/Paris",
            "Europe/Berlin",
            "Europe/Rome",
            "America/New_York",
            "America/Los_Angeles",
            "America/Chicago",
            "America/Mexico_City",
            "America/Bogota",
            "America/Lima",
            "America/Santiago",
            "America/Buenos_Aires",
            "America/Sao_Paulo",
            "Asia/Tokyo",
            "Asia/Shanghai",
            "Asia/Singapore",
            "Asia/Dubai",
            "Australia/Sydney",
            "Pacific/Auckland"
        };

        public TimezoneService(ILogger<TimezoneService> logger, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _httpClient = httpClientFactory.CreateClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(5); // Timeout corto para evitar bloqueos
            
            // Obtener token público de Mapbox desde configuración/entorno.
            _mapboxPublicToken = _configuration["Mapbox:PublicToken"] ?? _configuration["MAPBOX_PUBLIC_TOKEN"];
        }

        public DateTime ConvertToUtc(DateTime localDateTime, string ianaTimezone)
        {
            if (string.IsNullOrWhiteSpace(ianaTimezone) || ianaTimezone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            {
                // Si es UTC, solo marcar como UTC sin convertir
                return DateTime.SpecifyKind(localDateTime, DateTimeKind.Utc);
            }

            try
            {
                var tzInfo = GetTimeZoneInfo(ianaTimezone);
                if (tzInfo == null)
                {
                    _logger.LogError("❌ No se pudo obtener TimeZoneInfo para '{Timezone}'. " +
                        "Esto puede indicar que Google Maps API devolvió un timezone no soportado. " +
                        "Usando UTC como fallback (puede causar errores en las citas).", ianaTimezone);
                    return DateTime.SpecifyKind(localDateTime, DateTimeKind.Utc);
                }

                // ✅ Asegurar que el DateTime no tenga Kind=Utc para la conversión
                // TimeZoneInfo.ConvertTimeToUtc requiere DateTimeKind.Unspecified o DateTimeKind.Local
                var unspecifiedDateTime = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);

                // 🛡️ E2 FIX: spring-forward — la hora 02:30 (Europa/Madrid el último domingo de marzo,
                // p.ej.) NO EXISTE porque el reloj salta de 02:00 a 03:00. ConvertTimeToUtc lanza
                // ArgumentException ante esto y el catch hacía fallback a "tratar como UTC" → la cita
                // quedaba 1h desplazada en BD. Detectamos la hora inválida y la avanzamos al siguiente
                // instante válido (+1h: cubre el delta DST estándar de todas las zonas que usan DST).
                if (tzInfo.IsInvalidTime(unspecifiedDateTime))
                {
                    _logger.LogWarning("⚠️ E2: hora inválida {LocalTime} en timezone '{Timezone}' (spring-forward). Avanzada 1h a {AdjustedTime}.",
                        localDateTime, ianaTimezone, unspecifiedDateTime.AddHours(1));
                    unspecifiedDateTime = unspecifiedDateTime.AddHours(1);
                }
                // Nota: el caso ambiguo (fall-back, hora que ocurre 2 veces) no necesita ajuste —
                // ConvertTimeToUtc por defecto toma la interpretación standard time (post-fallback),
                // que es la convención más común y predecible para citas programadas.

                // ✅ Convertir usando TimeZoneInfo (maneja automáticamente DST y todos los offsets)
                // Esto funciona para TODOS los timezones IANA que TimeZoneConverter puede convertir
                // TimeZoneConverter v6.1.0 soporta TODOS los timezones IANA estándar que Google Maps API devuelve
                var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(unspecifiedDateTime, tzInfo);
                
                _logger.LogDebug("✅ Convertido {LocalTime} ({Timezone}) → {UtcTime} UTC", 
                    localDateTime, ianaTimezone, utcDateTime);
                
                return utcDateTime;
            }
            catch (TimeZoneNotFoundException ex)
            {
                _logger.LogError(ex, "❌ TimeZoneNotFoundException al convertir a UTC desde '{Timezone}'. " +
                    "El timezone no existe en el sistema. Usando UTC como fallback.", ianaTimezone);
                return DateTime.SpecifyKind(localDateTime, DateTimeKind.Utc);
            }
            catch (ArgumentException ex)
            {
                _logger.LogError(ex, "❌ ArgumentException al convertir a UTC desde '{Timezone}'. " +
                    "El DateTime o TimeZoneInfo es inválido. Usando UTC como fallback.", ianaTimezone);
                return DateTime.SpecifyKind(localDateTime, DateTimeKind.Utc);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error inesperado convirtiendo a UTC desde timezone '{Timezone}': {Message}", 
                    ianaTimezone, ex.Message);
                return DateTime.SpecifyKind(localDateTime, DateTimeKind.Utc);
            }
        }

        public DateTime ConvertFromUtc(DateTime utcDateTime, string ianaTimezone)
        {
            if (string.IsNullOrWhiteSpace(ianaTimezone) || ianaTimezone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            {
                return DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
            }

            try
            {
                var tzInfo = GetTimeZoneInfo(ianaTimezone);
                if (tzInfo == null)
                {
                    _logger.LogWarning("Invalid timezone '{Timezone}', returning UTC", ianaTimezone);
                    return DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
                }

                // Asegurar que el DateTime tenga Kind=Utc para la conversión
                var utcSpecified = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
                var localDateTime = TimeZoneInfo.ConvertTimeFromUtc(utcSpecified, tzInfo);
                
                return localDateTime;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error converting from UTC to timezone '{Timezone}'", ianaTimezone);
                return DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
            }
        }

        public bool IsValidTimezone(string ianaTimezone)
        {
            if (string.IsNullOrWhiteSpace(ianaTimezone))
                return false;

            if (ianaTimezone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
                return true;

            // Verificación rápida para zonas comunes
            if (CommonTimezones.Contains(ianaTimezone))
                return true;

            // Verificación completa
            return GetTimeZoneInfo(ianaTimezone) != null;
        }

        public string GetEffectiveTimezone(string? dtoTimezone, string? userSettingTimezone)
        {
            // Prioridad: DTO > UserSetting > UTC
            if (!string.IsNullOrWhiteSpace(dtoTimezone) && IsValidTimezone(dtoTimezone))
            {
                return dtoTimezone;
            }
            
            if (!string.IsNullOrWhiteSpace(userSettingTimezone) && IsValidTimezone(userSettingTimezone))
            {
                return userSettingTimezone;
            }
            
            return "UTC";
        }

        public TimeZoneInfo? GetTimeZoneInfo(string ianaTimezone)
        {
            if (string.IsNullOrWhiteSpace(ianaTimezone))
                return null;

            if (ianaTimezone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
                return TimeZoneInfo.Utc;

            try
            {
                // ✅ Usar TimeZoneConverter para convertir IANA a TimeZoneInfo
                // TimeZoneConverter v6.1.0 soporta TODOS los timezones IANA estándar
                // Incluye todos los timezones que Google Maps API puede devolver
                // Funciona en Windows, Linux y macOS
                var tzInfo = TZConvert.GetTimeZoneInfo(ianaTimezone);
                
                // ✅ Validación adicional: Verificar que el TimeZoneInfo sea válido
                if (tzInfo != null)
                {
                    _logger.LogDebug("Timezone '{Timezone}' convertido exitosamente a TimeZoneInfo (Id: {Id})", 
                        ianaTimezone, tzInfo.Id);
                    return tzInfo;
                }
                
                _logger.LogWarning("TZConvert devolvió null para timezone '{Timezone}'", ianaTimezone);
                return null;
            }
            catch (TimeZoneNotFoundException ex)
            {
                // ❌ Timezone IANA no encontrado en la base de datos de TimeZoneConverter
                // Esto NO debería pasar con timezones válidos de Google Maps API
                _logger.LogError(ex, "❌ Timezone IANA '{Timezone}' no encontrado en TimeZoneConverter. " +
                    "Esto puede indicar un timezone no estándar o una versión desactualizada de TimeZoneConverter.", ianaTimezone);
                return null;
            }
            catch (InvalidTimeZoneException ex)
            {
                // ❌ Timezone inválido
                _logger.LogError(ex, "❌ Timezone IANA '{Timezone}' es inválido según TimeZoneConverter", ianaTimezone);
                return null;
            }
            catch (Exception ex)
            {
                // ❌ Error inesperado
                _logger.LogError(ex, "❌ Error inesperado obteniendo TimeZoneInfo para '{Timezone}': {Message}", 
                    ianaTimezone, ex.Message);
                return null;
            }
        }
        
        public async Task<string> GetTimezoneFromCoordinatesAsync(decimal latitude, decimal longitude)
        {
            // Fallback a UTC cuando falta token de Mapbox.
            if (string.IsNullOrWhiteSpace(_mapboxPublicToken))
            {
                _logger.LogWarning("Mapbox public token no configurado. Usando 'UTC' como fallback para ({Latitude}, {Longitude}).", latitude, longitude);
                return "UTC";
            }

            try
            {
                // Mapbox no expone Timezone API directa. Derivamos un TZ base por país detectado.
                var countryCode = await GetCountryFromCoordinatesAsync(latitude, longitude);
                var fallbackTimezone = GetFallbackTimezoneFromCountry(countryCode);
                _logger.LogInformation("Timezone derivado por país ({Country}) => {Timezone} para coordenadas ({Latitude}, {Longitude})",
                    countryCode ?? "unknown", fallbackTimezone, latitude, longitude);
                return fallbackTimezone;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Timeout detectando timezone con Mapbox ({Timeout}s) para ({Latitude}, {Longitude}). Usando UTC fallback.",
                    _httpClient.Timeout.TotalSeconds, latitude, longitude);
                return "UTC";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error detectando timezone con Mapbox para ({Latitude}, {Longitude}). Usando UTC fallback.",
                    latitude, longitude);
                return "UTC";
            }
        }
        
        // 🛡️ Round 28: orquestador v6→v5. Mapbox v6 (Search Geocode) primario; si falla, v5 (legacy).
        // V6 sigue formato distinto: types=country puede no filtrar estrictamente y el code SIEMPRE
        // está en properties.context.country.country_code (verificado empíricamente por Agente 4-B).
        public async Task<string?> GetCountryFromCoordinatesAsync(decimal latitude, decimal longitude)
        {
            if (string.IsNullOrWhiteSpace(_mapboxPublicToken))
            {
                _logger.LogError("Mapbox public token no configurado. No se puede detectar país para coordenadas ({Latitude}, {Longitude}). " +
                    "Configura 'mapbox-public-token' en secrets/env.", latitude, longitude);
                throw new InvalidOperationException(
                    $"Mapbox public token no configurado. No se puede detectar país para coordenadas ({latitude}, {longitude}).");
            }

            var latStr = latitude.ToString(CultureInfo.InvariantCulture);
            var lonStr = longitude.ToString(CultureInfo.InvariantCulture);

            // 1) Intento primario: Mapbox v6 (Search Geocode reverse).
            try
            {
                var country = await TryGetCountryV6Async(latStr, lonStr, latitude, longitude);
                if (!string.IsNullOrEmpty(country))
                {
                    _logger.LogInformation("País detectado vía Mapbox v6: '{Country}' para coordenadas ({Latitude}, {Longitude})",
                        country, latitude, longitude);
                    return country;
                }
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Timeout en Mapbox v6 country lookup para ({Latitude}, {Longitude}). Probando fallback v5.",
                    latitude, longitude);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mapbox v6 country lookup falló para ({Latitude}, {Longitude}). Probando fallback v5.",
                    latitude, longitude);
            }

            // 2) Fallback: Mapbox v5 (legacy mapbox.places).
            try
            {
                var country = await TryGetCountryV5Async(latStr, lonStr, latitude, longitude);
                if (!string.IsNullOrEmpty(country))
                {
                    _logger.LogInformation("País detectado vía Mapbox v5 (fallback): '{Country}' para coordenadas ({Latitude}, {Longitude})",
                        country, latitude, longitude);
                    return country;
                }
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Timeout en Mapbox v5 (fallback) country lookup para ({Latitude}, {Longitude}).",
                    latitude, longitude);
                throw new InvalidOperationException(
                    $"Timeout llamando a Mapbox Geocoding API para coordenadas ({latitude}, {longitude}). " +
                    "Verifica tu conexión a internet y la configuración del token.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mapbox v5 (fallback) country lookup TAMBIÉN falló para ({Latitude}, {Longitude}).",
                    latitude, longitude);
                throw new InvalidOperationException(
                    $"Mapbox v6 y v5 fallaron para ({latitude}, {longitude}): {ex.Message}", ex);
            }

            _logger.LogWarning("Mapbox v6 y v5 ambos devolvieron features sin código de país para ({Latitude}, {Longitude}).",
                latitude, longitude);
            return null;
        }

        // 🛡️ v6 (Search Geocode) — primario. Devuelve null en error HTTP para que el orquestador caiga a v5.
        private async Task<string?> TryGetCountryV6Async(string latStr, string lonStr, decimal latitude, decimal longitude)
        {
            var url = $"https://api.mapbox.com/search/geocode/v6/reverse?longitude={lonStr}&latitude={latStr}&types=country&language=es&access_token={_mapboxPublicToken}";
            _logger.LogDebug("Mapbox v6: detectando país en ({Latitude}, {Longitude})", latitude, longitude);

            var response = await _httpClient.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Mapbox v6 country devolvió {Status}: {Body}", response.StatusCode, json);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                return null;

            // V6 path canónico (verificado por Agente 4-B con 12 llamadas reales):
            // features[i].properties.context.country.country_code — uppercase ISO 3166-1 alpha-2.
            // Iteramos porque types=country puede devolver tipos mixtos (bug intermitente del backend).
            foreach (var feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("properties", out var properties)) continue;

                if (properties.TryGetProperty("context", out var ctx)
                    && ctx.ValueKind == JsonValueKind.Object
                    && ctx.TryGetProperty("country", out var ctxCountry)
                    && ctxCountry.ValueKind == JsonValueKind.Object
                    && ctxCountry.TryGetProperty("country_code", out var ctxCc)
                    && ctxCc.ValueKind == JsonValueKind.String)
                {
                    var code = ctxCc.GetString();
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        var normalized = code.Trim().ToUpperInvariant();
                        if (normalized.Length == 2) return normalized;
                    }
                }
            }
            return null;
        }

        // 🛡️ v5 (legacy mapbox.places) — fallback. Lowercase short_code.
        private async Task<string?> TryGetCountryV5Async(string latStr, string lonStr, decimal latitude, decimal longitude)
        {
            var url = $"https://api.mapbox.com/geocoding/v5/mapbox.places/{lonStr},{latStr}.json?types=country&language=es&access_token={_mapboxPublicToken}";
            _logger.LogDebug("Mapbox v5 (fallback): detectando país en ({Latitude}, {Longitude})", latitude, longitude);

            var response = await _httpClient.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Mapbox v5 country devolvió {Status}: {Body}", response.StatusCode, json);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var feature in features.EnumerateArray())
            {
                if (feature.TryGetProperty("properties", out var properties)
                    && properties.TryGetProperty("short_code", out var shortCode)
                    && shortCode.ValueKind == JsonValueKind.String)
                {
                    var code = shortCode.GetString();
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        var normalized = code.Split('-')[0].Trim().ToUpperInvariant();
                        if (normalized.Length == 2) return normalized;
                    }
                }
            }
            return null;
        }

        // 🛡️ Round 28: orquestador ciudad v6→v5. Mismo patrón que country.
        public async Task<string?> GetCityFromCoordinatesAsync(decimal latitude, decimal longitude)
        {
            if (string.IsNullOrWhiteSpace(_mapboxPublicToken))
            {
                _logger.LogError("Mapbox public token no configurado. No se puede detectar ciudad para coordenadas ({Latitude}, {Longitude}). " +
                    "Configura 'mapbox-public-token' en secrets/env.", latitude, longitude);
                throw new InvalidOperationException(
                    $"Mapbox public token no configurado. No se puede detectar ciudad para coordenadas ({latitude}, {longitude}).");
            }

            var latStr = latitude.ToString(CultureInfo.InvariantCulture);
            var lonStr = longitude.ToString(CultureInfo.InvariantCulture);

            try
            {
                var city = await TryGetCityV6Async(latStr, lonStr, latitude, longitude);
                if (!string.IsNullOrEmpty(city))
                {
                    _logger.LogInformation("Ciudad detectada vía Mapbox v6: '{City}' para coordenadas ({Latitude}, {Longitude})",
                        city, latitude, longitude);
                    return city;
                }
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Timeout en Mapbox v6 city lookup para ({Latitude}, {Longitude}). Probando fallback v5.",
                    latitude, longitude);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mapbox v6 city lookup falló para ({Latitude}, {Longitude}). Probando fallback v5.",
                    latitude, longitude);
            }

            try
            {
                var city = await TryGetCityV5Async(latStr, lonStr, latitude, longitude);
                if (!string.IsNullOrEmpty(city))
                {
                    _logger.LogInformation("Ciudad detectada vía Mapbox v5 (fallback): '{City}' para coordenadas ({Latitude}, {Longitude})",
                        city, latitude, longitude);
                    return city;
                }
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Timeout en Mapbox v5 (fallback) city lookup para ({Latitude}, {Longitude}).",
                    latitude, longitude);
                throw new InvalidOperationException(
                    $"Timeout llamando a Mapbox Geocoding API para coordenadas ({latitude}, {longitude}). " +
                    "Verifica tu conexión a internet y la configuración del token.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mapbox v5 (fallback) city lookup TAMBIÉN falló para ({Latitude}, {Longitude}).",
                    latitude, longitude);
                throw new InvalidOperationException(
                    $"Mapbox v6 y v5 fallaron para ({latitude}, {longitude}): {ex.Message}", ex);
            }

            _logger.LogWarning("Mapbox v6 y v5 ambos devolvieron features sin ciudad interpretable para ({Latitude}, {Longitude}).",
                latitude, longitude);
            return null;
        }

        // 🛡️ v6 ciudad — primario. properties.name + fallback a context.place.name / context.locality.name.
        private async Task<string?> TryGetCityV6Async(string latStr, string lonStr, decimal latitude, decimal longitude)
        {
            var url = $"https://api.mapbox.com/search/geocode/v6/reverse?longitude={lonStr}&latitude={latStr}&types=place,locality&language=es&access_token={_mapboxPublicToken}";
            _logger.LogDebug("Mapbox v6: detectando ciudad en ({Latitude}, {Longitude})", latitude, longitude);

            var response = await _httpClient.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Mapbox v6 city devolvió {Status}: {Body}", response.StatusCode, json);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("properties", out var properties)) continue;

                if (properties.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    var cityName = name.GetString();
                    if (!string.IsNullOrWhiteSpace(cityName)) return cityName;
                }

                if (properties.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.Object)
                {
                    if (ctx.TryGetProperty("place", out var place)
                        && place.ValueKind == JsonValueKind.Object
                        && place.TryGetProperty("name", out var placeName)
                        && placeName.ValueKind == JsonValueKind.String)
                    {
                        var cityName = placeName.GetString();
                        if (!string.IsNullOrWhiteSpace(cityName)) return cityName;
                    }
                    if (ctx.TryGetProperty("locality", out var locality)
                        && locality.ValueKind == JsonValueKind.Object
                        && locality.TryGetProperty("name", out var localityName)
                        && localityName.ValueKind == JsonValueKind.String)
                    {
                        var cityName = localityName.GetString();
                        if (!string.IsNullOrWhiteSpace(cityName)) return cityName;
                    }
                }
            }
            return null;
        }

        // 🛡️ v5 ciudad — fallback. feature.text.
        private async Task<string?> TryGetCityV5Async(string latStr, string lonStr, decimal latitude, decimal longitude)
        {
            var url = $"https://api.mapbox.com/geocoding/v5/mapbox.places/{lonStr},{latStr}.json?types=place,locality&language=es&access_token={_mapboxPublicToken}";
            _logger.LogDebug("Mapbox v5 (fallback): detectando ciudad en ({Latitude}, {Longitude})", latitude, longitude);

            var response = await _httpClient.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Mapbox v5 city devolvió {Status}: {Body}", response.StatusCode, json);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var feature in features.EnumerateArray())
            {
                if (feature.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    var cityName = text.GetString();
                    if (!string.IsNullOrWhiteSpace(cityName)) return cityName;
                }
            }
            return null;
        }

        private static string GetFallbackTimezoneFromCountry(string? countryCode)
        {
            return countryCode?.ToUpperInvariant() switch
            {
                "ES" => "Europe/Madrid",
                "GB" => "Europe/London",
                "FR" => "Europe/Paris",
                "DE" => "Europe/Berlin",
                "IT" => "Europe/Rome",
                "PT" => "Europe/Lisbon",
                "US" => "America/New_York",
                "MX" => "America/Mexico_City",
                "CO" => "America/Bogota",
                "AR" => "America/Argentina/Buenos_Aires",
                "BR" => "America/Sao_Paulo",
                "CL" => "America/Santiago",
                "PE" => "America/Lima",
                "JP" => "Asia/Tokyo",
                "CN" => "Asia/Shanghai",
                "SG" => "Asia/Singapore",
                "AE" => "Asia/Dubai",
                "AU" => "Australia/Sydney",
                "NZ" => "Pacific/Auckland",
                _ => "UTC"
            };
        }
        
        /// <summary>
        /// Detecta el país desde la dirección IP del cliente usando ip-api.com
        /// Servicio gratuito, sin API key necesario (límite: 45 requests/minuto)
        /// </summary>
        public async Task<string> GetCountryFromIpAddressAsync(string ipAddress)
        {
            // Validar IP
            if (string.IsNullOrWhiteSpace(ipAddress) || ipAddress == "unknown" || ipAddress == "::1" || ipAddress == "127.0.0.1")
            {
                _logger.LogWarning("⚠️ IP inválida o localhost: '{IpAddress}', usando 'ES' por defecto", ipAddress);
                return "ES"; // España por defecto
            }
            
            try
            {
                // ✅ Usar ip-api.com (gratuito, sin API key, límite 45 req/min)
                // Formato JSON: http://ip-api.com/json/{ip}?fields=status,message,countryCode
                var url = $"http://ip-api.com/json/{ipAddress}?fields=status,message,countryCode";
                
                _logger.LogDebug("Llamando a ip-api.com para detectar país desde IP: {IpAddress}", ipAddress);
                
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // Timeout de 5 segundos
                var response = await _httpClient.GetAsync(url, cts.Token);
                var json = await response.Content.ReadAsStringAsync(cts.Token);
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("⚠️ ip-api.com request failed. Status: {StatusCode}, usando 'ES' por defecto", response.StatusCode);
                    return "ES";
                }
                
                var jsonDoc = JsonDocument.Parse(json);
                
                // ip-api.com devuelve: { "status": "success", "countryCode": "ES" }
                // O errores: { "status": "fail", "message": "..." }
                if (jsonDoc.RootElement.TryGetProperty("status", out var status))
                {
                    var statusValue = status.GetString();
                    
                    if (statusValue == "success" && jsonDoc.RootElement.TryGetProperty("countryCode", out var countryCode))
                    {
                        var country = countryCode.GetString();
                        
                        if (!string.IsNullOrWhiteSpace(country) && country.Length == 2)
                        {
                            _logger.LogInformation("✅ País detectado: '{Country}' desde IP: {IpAddress}", country.ToUpperInvariant(), ipAddress);
                            return country.ToUpperInvariant();
                        }
                    }
                    else if (statusValue == "fail" && jsonDoc.RootElement.TryGetProperty("message", out var message))
                    {
                        _logger.LogWarning("⚠️ ip-api.com error: {Message}, usando 'ES' por defecto", message.GetString());
                    }
                }
                
                _logger.LogWarning("⚠️ No se pudo obtener país desde IP: {IpAddress}, usando 'ES' por defecto", ipAddress);
                return "ES";
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("⚠️ Timeout llamando a ip-api.com para IP: {IpAddress}, usando 'ES' por defecto", ipAddress);
                return "ES";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error inesperado llamando a ip-api.com para IP: {IpAddress}, usando 'ES' por defecto", ipAddress);
                return "ES";
            }
        }
    }
}

