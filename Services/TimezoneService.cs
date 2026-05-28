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
        /// Usa Google Geocoding API para obtener el código de país (ISO 3166-1 alpha-2)
        /// </summary>
        /// <param name="latitude">Latitud en grados decimales</param>
        /// <param name="longitude">Longitud en grados decimales</param>
        /// <returns>Código de país ISO 3166-1 alpha-2 (ej: "ES", "US", "MX") o null si no se puede detectar</returns>
        Task<string?> GetCountryFromCoordinatesAsync(decimal latitude, decimal longitude);
        
        /// <summary>
        /// Detecta la ciudad desde coordenadas geográficas (latitud, longitud)
        /// Usa Google Geocoding API para obtener el nombre de la ciudad
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
        private readonly string? _googleApiKey;
        
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
            
            // Obtener Google API Key de la configuración (puede estar en Secret Manager o variables de entorno)
            _googleApiKey = _configuration["Google:ApiKey"] ?? _configuration["GOOGLE_API_KEY"];
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
            // 🛡️ R13 FIX: fallback a "UTC" en lugar de throw cuando API key falta.
            // Los call sites (N7-N8 en UpdateExpertProfile, AppointmentService al detectar TZ del experto)
            // asumen que este método siempre retorna un IANA TZ válido — si throw, los flows revientan
            // y bloquean creación de cita / actualización de perfil. UTC es safe fallback (Stripe acepta,
            // citas se renderizan con offset 0 — el experto puede corregirlo después).
            if (string.IsNullOrWhiteSpace(_googleApiKey))
            {
                _logger.LogWarning("R13: Google Maps API Key no configurada. Usando 'UTC' como fallback para ({Latitude}, {Longitude}).", latitude, longitude);
                return "UTC";
            }

            try
            {
                // Google Timezone API requiere un timestamp (usamos el actual)
                // ✅ CRÍTICO: Usar formato invariante (punto decimal) para las coordenadas
                // Google API rechaza comas como separador decimal
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var latStr = latitude.ToString(CultureInfo.InvariantCulture);
                var lonStr = longitude.ToString(CultureInfo.InvariantCulture);
                var url = $"https://maps.googleapis.com/maps/api/timezone/json?location={latStr},{lonStr}&timestamp={timestamp}&key={_googleApiKey}";
                
                _logger.LogDebug("Llamando a Google Timezone API para coordenadas ({Latitude}, {Longitude}) - URL: {Url}", 
                    latitude, longitude, url.Replace(_googleApiKey, "***"));
                
                var response = await _httpClient.GetAsync(url);
                var json = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("❌ Google Timezone API request failed. Status: {StatusCode}, Response: {Response}", 
                        response.StatusCode, json);
                    throw new InvalidOperationException(
                        $"Google Timezone API request failed. Status: {response.StatusCode}. " +
                        $"No se puede detectar timezone para coordenadas ({latitude}, {longitude}).");
                }
                
                var jsonDoc = JsonDocument.Parse(json);
                
                // Google API devuelve: { "status": "OK", "timeZoneId": "Europe/Madrid" }
                // O errores: { "status": "REQUEST_DENIED", "errorMessage": "..." }
                if (jsonDoc.RootElement.TryGetProperty("status", out var status))
                {
                    var statusValue = status.GetString();
                    
                    if (statusValue == "OK")
                    {
                        if (jsonDoc.RootElement.TryGetProperty("timeZoneId", out var timeZoneId))
                        {
                            var ianaTimezone = timeZoneId.GetString();
                            
                            // Validar que el timezone sea válido
                            if (!string.IsNullOrWhiteSpace(ianaTimezone) && IsValidTimezone(ianaTimezone))
                            {
                                _logger.LogInformation("✅ Timezone detectado: '{Timezone}' desde Google API para coordenadas ({Latitude}, {Longitude})", 
                                    ianaTimezone, latitude, longitude);
                                return ianaTimezone;
                            }
                            else
                            {
                                _logger.LogError("❌ Timezone inválido recibido de Google API: '{Timezone}' para coordenadas ({Latitude}, {Longitude})", 
                                    ianaTimezone, latitude, longitude);
                                throw new InvalidOperationException(
                                    $"Timezone inválido recibido de Google API: '{ianaTimezone}' para coordenadas ({latitude}, {longitude}).");
                            }
                        }
                        else
                        {
                            _logger.LogError("❌ Google API devolvió status OK pero sin timeZoneId. Response: {Response}", json);
                            throw new InvalidOperationException(
                                $"Google API devolvió status OK pero sin timeZoneId para coordenadas ({latitude}, {longitude}).");
                        }
                    }
                    else
                    {
                        // Error de Google API
                        var errorMessage = jsonDoc.RootElement.TryGetProperty("errorMessage", out var errorMsg) 
                            ? errorMsg.GetString() 
                            : "Sin mensaje de error";
                        
                        _logger.LogWarning("R13: Google Timezone API error. Status: {Status}, Message: {Message}. Usando UTC fallback.",
                            statusValue, errorMessage);
                        // 🛡️ R13 FIX: fallback "UTC" en vez de throw (ver razón en el guard inicial del método).
                        return "UTC";
                    }
                }
                else
                {
                    _logger.LogWarning("R13: Respuesta Google API sin campo 'status'. Usando UTC fallback.");
                    return "UTC";
                }
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "R13: Timeout Google Timezone API para ({Latitude}, {Longitude}). Usando UTC fallback.",
                    latitude, longitude);
                return "UTC";
            }
            catch (Exception ex)
            {
                // 🛡️ R13 FIX: fallback unificado UTC. No re-lanzar InvalidOperationException — antes
                // los callers asumían que el método siempre retornaba string válido y un throw
                // rompía flows críticos (UpdateExpertProfile, AppointmentService TZ detection).
                _logger.LogWarning(ex, "R13: Error Google Timezone API para ({Latitude}, {Longitude}). Usando UTC fallback.",
                    latitude, longitude);
                return "UTC";
            }
        }
        
        public async Task<string?> GetCountryFromCoordinatesAsync(decimal latitude, decimal longitude)
        {
            // ✅ SOLO Google Geocoding API - Sin fallbacks externos
            if (string.IsNullOrWhiteSpace(_googleApiKey))
            {
                _logger.LogError("❌ Google Maps API Key no configurada. No se puede detectar país para coordenadas ({Latitude}, {Longitude}). " +
                    "Configura 'google-maps-api-key' en Google Cloud Secret Manager.", latitude, longitude);
                throw new InvalidOperationException(
                    $"Google Maps API Key no configurada. No se puede detectar país para coordenadas ({latitude}, {longitude}). " +
                    "Configura 'google-maps-api-key' en Google Cloud Secret Manager.");
            }

            try
            {
                // ✅ CRÍTICO: Usar formato invariante (punto decimal) para las coordenadas
                // Google API rechaza comas como separador decimal
                var latStr = latitude.ToString(CultureInfo.InvariantCulture);
                var lonStr = longitude.ToString(CultureInfo.InvariantCulture);
                var url = $"https://maps.googleapis.com/maps/api/geocode/json?latlng={latStr},{lonStr}&result_type=country&key={_googleApiKey}";
                
                _logger.LogDebug("Llamando a Google Geocoding API para detectar país en coordenadas ({Latitude}, {Longitude})", 
                    latitude, longitude);
                
                var response = await _httpClient.GetAsync(url);
                var json = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("❌ Google Geocoding API request failed. Status: {StatusCode}, Response: {Response}", 
                        response.StatusCode, json);
                    throw new InvalidOperationException(
                        $"Google Geocoding API request failed. Status: {response.StatusCode}. " +
                        $"No se puede detectar país para coordenadas ({latitude}, {longitude}).");
                }
                
                var jsonDoc = JsonDocument.Parse(json);
                
                // Google Geocoding API devuelve: { "status": "OK", "results": [...] }
                // O errores: { "status": "REQUEST_DENIED", "error_message": "..." }
                if (jsonDoc.RootElement.TryGetProperty("status", out var status))
                {
                    var statusValue = status.GetString();
                    
                    if (statusValue == "OK")
                    {
                        if (jsonDoc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                        {
                            // Buscar el componente de tipo "country" en los resultados
                            foreach (var result in results.EnumerateArray())
                            {
                                if (result.TryGetProperty("address_components", out var addressComponents) && 
                                    addressComponents.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var component in addressComponents.EnumerateArray())
                                    {
                                        if (component.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
                                        {
                                            // Verificar si este componente es de tipo "country"
                                            bool isCountry = false;
                                            foreach (var type in types.EnumerateArray())
                                            {
                                                if (type.GetString() == "country")
                                                {
                                                    isCountry = true;
                                                    break;
                                                }
                                            }
                                            
                                            if (isCountry && component.TryGetProperty("short_name", out var shortName))
                                            {
                                                var countryCode = shortName.GetString();
                                                
                                                if (!string.IsNullOrWhiteSpace(countryCode) && countryCode.Length == 2)
                                                {
                                                    _logger.LogInformation("✅ País detectado: '{Country}' desde Google API para coordenadas ({Latitude}, {Longitude})", 
                                                        countryCode, latitude, longitude);
                                                    return countryCode.ToUpperInvariant();
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            
                            // Si llegamos aquí, no encontramos un país en los resultados
                            _logger.LogWarning("⚠️ Google API devolvió status OK pero no se encontró componente de tipo 'country' en los resultados. Response: {Response}", json);
                            return null;
                        }
                        else
                        {
                            _logger.LogError("❌ Google API devolvió status OK pero sin campo 'results' o no es un array. Response: {Response}", json);
                            return null;
                        }
                    }
                    else
                    {
                        // Error de Google API
                        var errorMessage = jsonDoc.RootElement.TryGetProperty("error_message", out var errorMsg) 
                            ? errorMsg.GetString() 
                            : "Sin mensaje de error";
                        
                        _logger.LogError("❌ Google Geocoding API error. Status: {Status}, Message: {Message}, Response: {Response}", 
                            statusValue, errorMessage, json);
                        
                        throw new InvalidOperationException(
                            $"Google Geocoding API error: {statusValue}. {errorMessage}. " +
                            $"No se puede detectar país para coordenadas ({latitude}, {longitude}).");
                    }
                }
                else
                {
                    _logger.LogError("❌ Respuesta de Google API sin campo 'status'. Response: {Response}", json);
                    throw new InvalidOperationException(
                        $"Respuesta de Google API sin campo 'status' para coordenadas ({latitude}, {longitude}).");
                }
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "❌ Timeout llamando a Google Geocoding API para coordenadas ({Latitude}, {Longitude})", 
                    latitude, longitude);
                throw new InvalidOperationException(
                    $"Timeout llamando a Google Geocoding API para coordenadas ({latitude}, {longitude}). " +
                    "Verifica tu conexión a internet y la configuración de la API key.");
            }
            catch (InvalidOperationException)
            {
                // Re-lanzar excepciones de InvalidOperationException (ya tienen mensajes claros)
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error inesperado llamando a Google Geocoding API para coordenadas ({Latitude}, {Longitude})", 
                    latitude, longitude);
                throw new InvalidOperationException(
                    $"Error inesperado llamando a Google Geocoding API para coordenadas ({latitude}, {longitude}): {ex.Message}");
            }
        }
        
        public async Task<string?> GetCityFromCoordinatesAsync(decimal latitude, decimal longitude)
        {
            // ✅ Usar Google Geocoding API para obtener la ciudad
            if (string.IsNullOrWhiteSpace(_googleApiKey))
            {
                _logger.LogError("❌ Google Maps API Key no configurada. No se puede detectar ciudad para coordenadas ({Latitude}, {Longitude}). " +
                    "Configura 'google-maps-api-key' en Google Cloud Secret Manager.", latitude, longitude);
                throw new InvalidOperationException(
                    $"Google Maps API Key no configurada. No se puede detectar ciudad para coordenadas ({latitude}, {longitude}). " +
                    "Configura 'google-maps-api-key' en Google Cloud Secret Manager.");
            }

            try
            {
                // ✅ CRÍTICO: Usar formato invariante (punto decimal) para las coordenadas
                var latStr = latitude.ToString(CultureInfo.InvariantCulture);
                var lonStr = longitude.ToString(CultureInfo.InvariantCulture);
                // No filtrar por result_type para obtener información completa de la ubicación
                var url = $"https://maps.googleapis.com/maps/api/geocode/json?latlng={latStr},{lonStr}&key={_googleApiKey}";
                
                _logger.LogDebug("Llamando a Google Geocoding API para detectar ciudad en coordenadas ({Latitude}, {Longitude})", 
                    latitude, longitude);
                
                var response = await _httpClient.GetAsync(url);
                var json = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("❌ Google Geocoding API request failed. Status: {StatusCode}, Response: {Response}", 
                        response.StatusCode, json);
                    throw new InvalidOperationException(
                        $"Google Geocoding API request failed. Status: {response.StatusCode}. " +
                        $"No se puede detectar ciudad para coordenadas ({latitude}, {longitude}).");
                }
                
                var jsonDoc = JsonDocument.Parse(json);
                
                if (jsonDoc.RootElement.TryGetProperty("status", out var status))
                {
                    var statusValue = status.GetString();
                    
                    if (statusValue == "OK")
                    {
                        if (jsonDoc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                        {
                            // Buscar el componente de tipo "locality" (ciudad) en los resultados
                            foreach (var result in results.EnumerateArray())
                            {
                                if (result.TryGetProperty("address_components", out var addressComponents) && 
                                    addressComponents.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var component in addressComponents.EnumerateArray())
                                    {
                                        if (component.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
                                        {
                                            // Verificar si este componente es de tipo "locality" (ciudad)
                                            bool isLocality = false;
                                            foreach (var type in types.EnumerateArray())
                                            {
                                                if (type.GetString() == "locality")
                                                {
                                                    isLocality = true;
                                                    break;
                                                }
                                            }
                                            
                                            if (isLocality && component.TryGetProperty("long_name", out var longName))
                                            {
                                                var cityName = longName.GetString();
                                                
                                                if (!string.IsNullOrWhiteSpace(cityName))
                                                {
                                                    _logger.LogInformation("✅ Ciudad detectada: '{City}' desde Google API para coordenadas ({Latitude}, {Longitude})", 
                                                        cityName, latitude, longitude);
                                                    return cityName;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            
                            // Si no encontramos "locality", intentar con "administrative_area_level_2" o "administrative_area_level_1"
                            foreach (var result in results.EnumerateArray())
                            {
                                if (result.TryGetProperty("address_components", out var addressComponents) && 
                                    addressComponents.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var component in addressComponents.EnumerateArray())
                                    {
                                        if (component.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
                                        {
                                            bool isAdminArea = false;
                                            foreach (var type in types.EnumerateArray())
                                            {
                                                var typeStr = type.GetString();
                                                if (typeStr == "administrative_area_level_2" || typeStr == "administrative_area_level_1")
                                                {
                                                    isAdminArea = true;
                                                    break;
                                                }
                                            }
                                            
                                            if (isAdminArea && component.TryGetProperty("long_name", out var longName))
                                            {
                                                var areaName = longName.GetString();
                                                
                                                if (!string.IsNullOrWhiteSpace(areaName))
                                                {
                                                    _logger.LogInformation("✅ Área administrativa detectada como ciudad: '{City}' desde Google API para coordenadas ({Latitude}, {Longitude})", 
                                                        areaName, latitude, longitude);
                                                    return areaName;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            
                            // Si llegamos aquí, no encontramos una ciudad en los resultados
                            _logger.LogWarning("⚠️ Google API devolvió status OK pero no se encontró componente de tipo 'locality' en los resultados. Response: {Response}", json);
                            return null;
                        }
                        else
                        {
                            _logger.LogError("❌ Google API devolvió status OK pero sin campo 'results' o no es un array. Response: {Response}", json);
                            return null;
                        }
                    }
                    else
                    {
                        // Error de Google API
                        var errorMessage = jsonDoc.RootElement.TryGetProperty("error_message", out var errorMsg) 
                            ? errorMsg.GetString() 
                            : "Sin mensaje de error";
                        
                        _logger.LogError("❌ Google Geocoding API error. Status: {Status}, Message: {Message}, Response: {Response}", 
                            statusValue, errorMessage, json);
                        
                        throw new InvalidOperationException(
                            $"Google Geocoding API error: {statusValue}. {errorMessage}. " +
                            $"No se puede detectar ciudad para coordenadas ({latitude}, {longitude}).");
                    }
                }
                else
                {
                    _logger.LogError("❌ Respuesta de Google API sin campo 'status'. Response: {Response}", json);
                    throw new InvalidOperationException(
                        $"Respuesta de Google API sin campo 'status' para coordenadas ({latitude}, {longitude}).");
                }
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "❌ Timeout llamando a Google Geocoding API para coordenadas ({Latitude}, {Longitude})", 
                    latitude, longitude);
                throw new InvalidOperationException(
                    $"Timeout llamando a Google Geocoding API para coordenadas ({latitude}, {longitude}). " +
                    "Verifica tu conexión a internet y la configuración de la API key.");
            }
            catch (InvalidOperationException)
            {
                // Re-lanzar excepciones de InvalidOperationException (ya tienen mensajes claros)
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error inesperado llamando a Google Geocoding API para coordenadas ({Latitude}, {Longitude})", 
                    latitude, longitude);
                throw new InvalidOperationException(
                    $"Error inesperado llamando a Google Geocoding API para coordenadas ({latitude}, {longitude}): {ex.Message}");
            }
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

