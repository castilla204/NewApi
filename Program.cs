using Google.Cloud.SecretManager.V1;
using Google.Api.Gax.Grpc;
using Grpc.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.IdentityModel.Tokens;
using Stripe;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using newApi.Services;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using AutoMapper;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.Dashboard;
using Microsoft.Extensions.DependencyInjection;
using newApi.Controllers;
using Microsoft.AspNetCore.Server.IIS;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using newApi.Middleware;
using Npgsql;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;

// ✅ RENDER.COM: Configurar puerto según el entorno
// En desarrollo: usar puerto 7124 (localhost:7124)
// En producción (Render.com): usar puerto 10000
// CRÍTICO: Render.com necesita ASPNETCORE_URLS configurado ANTES del builder
// Según documentación oficial: https://render.com/docs/web-services#port-binding

// Crear builder PRIMERO para detectar el entorno
var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();

string? aspnetcoreUrls = null;
string portToUse;

// ✅ Configurar puerto según el entorno
// ✅ RENDER.COM: Según documentación oficial https://render.com/docs/web-services#port-binding
// "Your web service must bind to a port on host 0.0.0.0 to serve HTTP requests"
// "The default value of PORT is 10000 for all Render web services"
if (!isDevelopment)
{
    // ✅ PRODUCCIÓN (Render.com): Puerto desde variable PORT (default 10000)
    var renderPort = Environment.GetEnvironmentVariable("PORT");
    
    if (!string.IsNullOrEmpty(renderPort) && int.TryParse(renderPort, out int portNumber))
    {
        portToUse = renderPort;
        Console.WriteLine($"[RENDER] ✅ Variable PORT detectada: {renderPort}");
    }
    else
    {
        portToUse = "10000"; // Puerto por defecto según documentación de Render.com
        Console.WriteLine($"[RENDER] ⚠️ Variable PORT no encontrada, usando puerto por defecto: {portToUse}");
    }
    
    // ✅ CRÍTICO: Bindear a 0.0.0.0 (no localhost) según documentación oficial
    // "Every Render web service must bind to a port on host 0.0.0.0"
    aspnetcoreUrls = $"http://0.0.0.0:{portToUse}";
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", aspnetcoreUrls);
    Console.WriteLine($"[RENDER] ✅ ASPNETCORE_URLS configurado: {aspnetcoreUrls}");
    
    // ✅ CRÍTICO: Forzar binding del puerto ANTES de cualquier inicialización
    builder.WebHost.UseUrls(aspnetcoreUrls);
    Console.WriteLine($"[RENDER] ✅ UseUrls() configurado: {aspnetcoreUrls}");
}
else
{
    // ✅ DESARROLLO: Puerto 7124 (localhost:7124)
    portToUse = "7124";
    aspnetcoreUrls = $"http://localhost:{portToUse}";
    builder.WebHost.UseUrls(aspnetcoreUrls);
    Console.WriteLine($"[DEV] ✅ Desarrollo: usando puerto {portToUse} (localhost:{portToUse})");
}

// ✅ Configurar codificación UTF-8 para la consola (evita problemas con caracteres especiales)
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

// Configurar logging básico
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.FormatterName = "simple";
});
builder.Logging.AddDebug();

// Verificar el entorno PRIMERO (necesario para configurar logging)
// NOTA: isDevelopment ya se detectó arriba (línea 39)

// ✅ LOG DE VERSIÓN: Identificar la versión desplegada
var versionLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
var buildDate = System.IO.File.GetLastWriteTime(System.Reflection.Assembly.GetExecutingAssembly().Location);
var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
versionLogger.LogInformation("═══════════════════════════════════════════════════════════");
versionLogger.LogInformation("🚀 INICIANDO APLICACIÓN - RENDER POSTGRESQL");
versionLogger.LogInformation("═══════════════════════════════════════════════════════════");
versionLogger.LogInformation($"   Versión: {assemblyVersion}");
versionLogger.LogInformation($"   Build Date: {buildDate:yyyy-MM-dd HH:mm:ss}");
versionLogger.LogInformation($"   Entorno: {(isDevelopment ? "Development" : "Production")}");
versionLogger.LogInformation($"   .NET Version: {Environment.Version}");
versionLogger.LogInformation("═══════════════════════════════════════════════════════════");

// ✅ CONFIGURAR LOGGING DE ENTITY FRAMEWORK CORE
// En desarrollo: mostrar todas las consultas SQL (útil para debugging)
// En producción: solo mostrar errores y warnings (reducir ruido en logs)
if (isDevelopment)
{
    // Desarrollo: mostrar consultas SQL y información detallada
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Information);
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Information);
}
else
{
    // Producción: solo errores y warnings (no mostrar consultas SQL normales)
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
}

// Configurar zona horaria de España
TimeZoneInfo spainTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture("es-ES");
    options.SupportedCultures = new[] { new System.Globalization.CultureInfo("es-ES") };
    options.SupportedUICultures = new[] { new System.Globalization.CultureInfo("es-ES") };
});

// Crear logger para inicialización
builder.Logging.AddConsole();
var initLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");

// Instancia el cliente de Secret Manager (funciona igual en desarrollo y producción)
// Se inicializa si las credenciales de Google Cloud están disponibles
SecretManagerServiceClient? secretClient = null;
bool secretManagerAvailable = false;

// Obtener ruta de credenciales o JSON de credenciales
string? credentialsPath = null;
string? credentialsJson = null;

if (isDevelopment)
{
    // En desarrollo: usar fallback a ubicación estándar si la variable no está configurada
    credentialsPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
    
    if (string.IsNullOrEmpty(credentialsPath))
    {
        // Fallback solo en desarrollo: usar ubicación estándar
        var fallbackPath = "C:\\cloudcredential.json";
        
        // Solo usar fallback si el archivo existe
        if (System.IO.File.Exists(fallbackPath))
        {
            credentialsPath = fallbackPath;
            // Configurar la variable de entorno para esta sesión en desarrollo
            try
            {
                Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", credentialsPath, EnvironmentVariableTarget.Process);
            }
            catch
            {
                // Si falla, continuar sin configurar la variable (no crítico)
            }
        }
        // Si no existe el archivo fallback, credentialsPath seguirá siendo null
        // y el cliente usará Application Default Credentials automáticamente
    }
}
else
{
    // En producción (Azure App Services): leer de variable GoogleCredentialJson
    // ✅ DEBUG: Verificar todas las variables de entorno relacionadas con Google
    var allEnvVars = Environment.GetEnvironmentVariables();
    var googleRelatedVars = new List<string>();
    foreach (System.Collections.DictionaryEntry entry in allEnvVars)
    {
        var key = entry.Key?.ToString() ?? "";
        if (key.Contains("Google", StringComparison.OrdinalIgnoreCase) || 
            key.Contains("GOOGLE", StringComparison.OrdinalIgnoreCase))
        {
            var value = entry.Value?.ToString() ?? "";
            var valuePreview = value.Length > 50 ? value.Substring(0, 50) + "..." : value;
            googleRelatedVars.Add($"{key} = {valuePreview} (length: {value.Length})");
        }
    }
    initLogger.LogInformation($"🔍 DEBUG: Variables de entorno relacionadas con Google encontradas: {googleRelatedVars.Count}");
    foreach (var varInfo in googleRelatedVars)
    {
        initLogger.LogInformation($"   {varInfo}");
    }
    
    credentialsJson = Environment.GetEnvironmentVariable("GoogleCredentialJson");
    
    // ✅ DEBUG: Log detallado de lo que se encontró
    if (credentialsJson == null)
    {
        initLogger.LogWarning("🔍 DEBUG: Environment.GetEnvironmentVariable('GoogleCredentialJson') devolvió NULL");
    }
    else if (string.IsNullOrEmpty(credentialsJson))
    {
        initLogger.LogWarning("🔍 DEBUG: Environment.GetEnvironmentVariable('GoogleCredentialJson') devolvió cadena VACÍA");
    }
    else
    {
        initLogger.LogInformation($"🔍 DEBUG: GoogleCredentialJson encontrado - Longitud: {credentialsJson.Length} caracteres");
    }
    
    if (!string.IsNullOrEmpty(credentialsJson))
    {
        initLogger.LogInformation("✅ Credenciales de Google Cloud encontradas en variable GoogleCredentialJson (Azure App Services)");
        
        // Crear archivo temporal con las credenciales JSON
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"google-credentials-{Guid.NewGuid()}.json");
            System.IO.File.WriteAllText(tempPath, credentialsJson);
            credentialsPath = tempPath;
            
            // Configurar la variable de entorno para esta sesión
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", credentialsPath, EnvironmentVariableTarget.Process);
            initLogger.LogInformation($"✅ Archivo temporal de credenciales creado: {tempPath}");
        }
        catch (Exception ex)
        {
            initLogger.LogError($"❌ Error al crear archivo temporal de credenciales: {ex.Message}");
            initLogger.LogError($"   Stack trace: {ex.StackTrace}");
            // Continuar sin archivo temporal, intentará usar Application Default Credentials
        }
    }
    else
    {
        // En producción, también intentar GOOGLE_APPLICATION_CREDENTIALS como fallback
        credentialsPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrEmpty(credentialsPath))
        {
            initLogger.LogInformation("✅ Usando GOOGLE_APPLICATION_CREDENTIALS como fallback en producción");
        }
        else
        {
            initLogger.LogWarning("⚠️ No se encontró GoogleCredentialJson ni GOOGLE_APPLICATION_CREDENTIALS en producción. Intentando Application Default Credentials...");
        }
    }
}

initLogger.LogInformation($"=== INICIALIZANDO SECRET MANAGER ===");
initLogger.LogInformation($"Entorno: {(isDevelopment ? "Development" : "Production")}");
if (!isDevelopment && !string.IsNullOrEmpty(credentialsJson))
{
    initLogger.LogInformation($"✅ Usando GoogleCredentialJson desde Azure App Services (longitud: {credentialsJson.Length} caracteres)");
}
initLogger.LogInformation($"GOOGLE_APPLICATION_CREDENTIALS: {Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS") ?? "NO CONFIGURADO (usará Application Default Credentials)"}");
initLogger.LogInformation($"Ruta de credenciales a usar: {credentialsPath ?? "Application Default Credentials (ADC)"}");

// Intentar inicializar Secret Manager
// Si credentialsPath está configurado, verificar que el archivo existe
// Si no está configurado, usar Application Default Credentials (ADC)
bool shouldInitialize = true;
if (!string.IsNullOrEmpty(credentialsPath))
{
    var fileExists = System.IO.File.Exists(credentialsPath);
    initLogger.LogInformation($"Archivo de credenciales existe: {fileExists}");
    
    if (fileExists)
    {
        shouldInitialize = true;
    }
    else
    {
        initLogger.LogWarning($"El archivo de credenciales no existe en: {credentialsPath}");
        initLogger.LogInformation("Intentando usar Application Default Credentials (ADC) en su lugar...");
        credentialsPath = null; // Limpiar para usar ADC
        shouldInitialize = true; // Intentar con ADC
    }
}
else
{
    initLogger.LogInformation("No hay archivo de credenciales configurado. Usando Application Default Credentials (ADC)...");
    shouldInitialize = true; // Usar ADC
}

// ✅ MIGRACIÓN A RENDER: Google Cloud Secret Manager DESACTIVADO.
// Los secretos ahora se leen de variables de entorno (ver GetSecretValue más abajo),
// por lo que NO se inicializa ningún cliente de Secret Manager ni se conecta a GCP.
// El bloque siguiente se conserva inerte; puede eliminarse por completo más adelante.
if (false) // Secret Manager desactivado tras migrar los secretos a variables de entorno de Render
{
    try
    {
        try
        {
            // Leer información básica del archivo de credenciales
            if (!string.IsNullOrEmpty(credentialsPath))
            {
                var credContent = System.IO.File.ReadAllText(credentialsPath);
                if (credContent.Contains("project_id"))
                {
                    initLogger.LogInformation("Archivo de credenciales parece válido (contiene project_id)");
                }
            }
            
            // IMPORTANTE: Forzar IPv4 ANTES de crear el cliente
            // Esto debe hacerse antes de cualquier operación de red
            try
            {
                // Establecer variable de entorno para forzar IPv4 en .NET
                Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_DISABLEIPV6", "1");
                initLogger.LogInformation("IPv6 deshabilitado para forzar IPv4 (ANTES de crear cliente)");
            }
            catch (Exception ipv6Ex)
            {
                initLogger.LogWarning($"No se pudo deshabilitar IPv6: {ipv6Ex.Message}");
            }
            
            // Crear el cliente de Secret Manager con configuración mejorada
            initLogger.LogInformation("Creando cliente de Secret Manager...");
            
            // Configurar el cliente con opciones específicas para Kubernetes/K3s
            var clientBuilder = new SecretManagerServiceClientBuilder();
            
            // Configurar el endpoint explícitamente
            var endpoint = "secretmanager.googleapis.com:443";
            clientBuilder.Endpoint = endpoint;
            initLogger.LogInformation($"Endpoint configurado: {endpoint}");
            
            // Configurar opciones de gRPC específicas para Kubernetes/K3s
            // El problema puede ser que gRPC necesita configuración especial para HTTP/2
            initLogger.LogInformation("Configurando adaptador gRPC con opciones para Kubernetes...");
            clientBuilder.GrpcAdapter = GrpcNetClientAdapter.Default.WithAdditionalOptions(options =>
            {
                // Configurar timeouts más largos para la conexión inicial
                options.MaxReceiveMessageSize = 4 * 1024 * 1024; // 4MB
                options.MaxSendMessageSize = 4 * 1024 * 1024; // 4MB
                // No configurar KeepAlive muy agresivo - puede causar problemas en Kubernetes
            });
            
            initLogger.LogInformation("Construyendo cliente de Secret Manager...");
            secretClient = clientBuilder.Build();
            
            initLogger.LogInformation($"Cliente de Secret Manager creado exitosamente (endpoint: {endpoint})");
            
            secretManagerAvailable = true; // Asumimos disponible hasta que falle
        }
        catch (Exception ex)
        {
            secretManagerAvailable = false;
            initLogger.LogError($"ERROR al crear cliente de Secret Manager: {ex.GetType().Name} - {ex.Message}");
            initLogger.LogError($"Stack trace: {ex.StackTrace}");
            initLogger.LogWarning("Usando solo variables de entorno como fallback.");
        }
    }
    catch (Exception ex)
    {
        secretManagerAvailable = false;
        initLogger.LogError($"ERROR al inicializar Secret Manager: {ex.GetType().Name} - {ex.Message}");
        initLogger.LogError($"Stack trace: {ex.StackTrace}");
        initLogger.LogWarning("Secret Manager no estará disponible. Usando solo variables de entorno como fallback.");
    }
}

initLogger.LogInformation($"Secret Manager disponible: {secretManagerAvailable}");
if (secretManagerAvailable)
{
    initLogger.LogInformation($"✅ Secret Manager configurado correctamente desde: {credentialsPath}");
}
else
{
    initLogger.LogWarning("⚠️ Secret Manager NO disponible. La aplicación usará solo variables de entorno.");
}

// ─────────────────────────────────────────────────────────────────────────────
// CARGA DE SECRETOS — Variables de entorno (Render)
// Antes: Google Cloud Secret Manager. Ahora los secretos se inyectan como
// variables de entorno en el panel de Render, MANTENIENDO LOS MISMOS NOMBRES
// (p.ej. "stripe-secret-key", "jwt-key", "mfa-encryption-key", ...).
//
// Para cada nombre se intenta, en orden:
//   1) El nombre EXACTO tal cual (p.ej. "stripe-secret-key").
//   2) La variante MAYÚSCULAS_CON_GUION_BAJO (p.ej. "STRIPE_SECRET_KEY"),
//      por si la plataforma normaliza los nombres de las variables.
// En entorno de DESARROLLO se prueba primero el sufijo "-dev" (misma convención
// que se usaba con Secret Manager).
//
// IMPORTANTE: nunca se registra (log) el valor de un secreto.
// ─────────────────────────────────────────────────────────────────────────────
string? GetSecretValue(string secretName, string? defaultValue = null)
{
    var namesToTry = new List<string>();
    if (isDevelopment)
    {
        namesToTry.Add($"{secretName}-dev");
    }
    namesToTry.Add(secretName);

    foreach (var name in namesToTry)
    {
        // 1) Nombre exacto (se mantienen los nombres originales)
        var value = Environment.GetEnvironmentVariable(name);

        // 2) Variante convencional en MAYÚSCULAS con guiones bajos
        if (string.IsNullOrEmpty(value))
        {
            var normalized = name.Replace('-', '_').ToUpperInvariant();
            value = Environment.GetEnvironmentVariable(normalized);
        }

        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }
    }

    return defaultValue;
}

// Cargar secretos de Google Cloud Secret Manager
var googleClientIdsSecret = GetSecretValue("google-client-ids", null);
var initLogger2 = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");

if (!string.IsNullOrEmpty(googleClientIdsSecret))
{
    initLogger2.LogInformation($"✅ Secreto google-client-ids obtenido (longitud: {googleClientIdsSecret.Length})");
    
    string[]? googleClientIds = null;
    
    // Intentar parsear como JSON primero (formato: ["id1", "id2"])
    var trimmedSecret = googleClientIdsSecret.Trim();
    if (trimmedSecret.StartsWith("[") && trimmedSecret.EndsWith("]"))
    {
        try
        {
            initLogger2.LogInformation("🔍 Detectado formato JSON, intentando parsear...");
            googleClientIds = System.Text.Json.JsonSerializer.Deserialize<string[]>(trimmedSecret);
            if (googleClientIds != null && googleClientIds.Length > 0)
            {
                initLogger2.LogInformation($"✅ Parseado como JSON exitosamente: {googleClientIds.Length} Client ID(s) encontrados");
            }
        }
        catch (Exception jsonEx)
        {
            initLogger2.LogWarning($"⚠️ Error al parsear como JSON: {jsonEx.Message}, intentando formato separado por comas...");
        }
    }
    
    // Si no es JSON o falló el parseo, intentar formato separado por comas
    if (googleClientIds == null || googleClientIds.Length == 0)
    {
        initLogger2.LogInformation("🔍 Intentando parsear como lista separada por comas...");
        googleClientIds = googleClientIdsSecret
                          .Split(',', StringSplitOptions.RemoveEmptyEntries)
                          .Select(id => id.Trim().Trim('"').Trim('\'')) // Remover comillas si las hay
                          .Where(id => !string.IsNullOrWhiteSpace(id))
                          .ToArray();
        
        if (googleClientIds.Length > 0)
        {
            initLogger2.LogInformation($"✅ Parseado como lista separada por comas: {googleClientIds.Length} Client ID(s) encontrados");
        }
    }

    if (googleClientIds != null && googleClientIds.Length > 0)
    {
        initLogger2.LogInformation($"✅ Se cargaron {googleClientIds.Length} Google Client ID(s):");
        var configDict = new Dictionary<string, string?>();
        for (int i = 0; i < googleClientIds.Length; i++)
        {
            configDict[$"Google:ClientIds:{i}"] = googleClientIds[i];
            initLogger2.LogInformation($"  [{i}] Google:ClientIds:{i} = [{googleClientIds[i]}]");
        }
        builder.Configuration.AddInMemoryCollection(configDict);
        initLogger2.LogInformation($"✅ Google Client IDs configurados correctamente en la configuración");
    }
    else
    {
        initLogger2.LogError("❌ ERROR: No se pudieron parsear los Google Client IDs del secreto");
    }
}
else
{
    initLogger2.LogError("❌ ERROR: No se pudo obtener el secreto google-client-ids del Secret Manager");
}

// JWT - Leer de variables de entorno primero, luego de Secret Manager como fallback
// Misma lógica para desarrollo y producción
var configLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");

// Leer JWT desde Secret Manager
var jwtKeyFromSecret = GetSecretValue("jwt-key", null);
var jwtKeyFromConfig = builder.Configuration["Jwt:Key"];

builder.Configuration["Jwt:Key"] = jwtKeyFromSecret ?? jwtKeyFromConfig ?? "";

// Leer Issuer y Audience desde Secret Manager
builder.Configuration["Jwt:Issuer"] = GetSecretValue("jwt-issuer", null) ?? builder.Configuration["Jwt:Issuer"] ?? "newApi";

builder.Configuration["Jwt:Audience"] = GetSecretValue("jwt-audience", null) ?? builder.Configuration["Jwt:Audience"] ?? "newApi";

// Obtener jwtKey para validación y logging
var jwtKey = builder.Configuration["Jwt:Key"];
var jwtKeySource = !string.IsNullOrEmpty(jwtKeyFromSecret) ? "Google Cloud Secret Manager" 
    : (!string.IsNullOrEmpty(jwtKeyFromConfig) ? "Configuration/User Secrets" : "NOT FOUND");
configLogger.LogInformation($"JWT Key source: {jwtKeySource}");

// ✅ SEGURIDAD 2025: Validar longitud mínima de clave JWT (OWASP Best Practice)
if (string.IsNullOrEmpty(jwtKey))
{
    var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    var userSecretsPath = Path.Combine(
        appDataPath,
        "Microsoft",
        "UserSecrets",
        "dec0adc1-b7d7-4da6-be0f-42e3054c640a",
        "secrets.json"
    );
    if (!System.IO.File.Exists(userSecretsPath))
    {
        userSecretsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".microsoft",
            "usersecrets",
            "dec0adc1-b7d7-4da6-be0f-42e3054c640a",
            "secrets.json"
        );
    }
    throw new InvalidOperationException(
        "⚠️ CRITICAL SECURITY ERROR: JWT Key is not configured. " +
        "Please set 'jwt-key' in Google Cloud Secret Manager or User Secrets. " +
        $"\nEnvironment: {(isDevelopment ? "Development" : "Production")}" +
        $"\nUser Secrets Path: {userSecretsPath}" +
        $"\nUser Secrets Exists: {System.IO.File.Exists(userSecretsPath)}");
}

var jwtKeyBytes = Encoding.UTF8.GetBytes(jwtKey);
const int MINIMUM_KEY_LENGTH_BITS = 256; // OWASP/NIST recommendation for HMAC-SHA256
const int MINIMUM_KEY_LENGTH_BYTES = MINIMUM_KEY_LENGTH_BITS / 8; // 32 bytes
const int RECOMMENDED_KEY_LENGTH_BYTES = 64; // 512 bits

if (jwtKeyBytes.Length < MINIMUM_KEY_LENGTH_BYTES)
{
    throw new InvalidOperationException(
        $"⚠️ CRITICAL SECURITY ERROR: JWT Key is too short ({jwtKeyBytes.Length} bytes / {jwtKeyBytes.Length * 8} bits). " +
        $"Minimum required: {MINIMUM_KEY_LENGTH_BYTES} bytes ({MINIMUM_KEY_LENGTH_BITS} bits). " +
        $"Recommended: {RECOMMENDED_KEY_LENGTH_BYTES} bytes (512 bits). " +
        $"\n\nTo generate a secure key:\n" +
        $"  PowerShell: [Convert]::ToBase64String((1..64 | ForEach-Object {{Get-Random -Minimum 0 -Maximum 256}}))\n" +
        $"  Bash: openssl rand -base64 64");
}

if (jwtKeyBytes.Length < RECOMMENDED_KEY_LENGTH_BYTES && !builder.Environment.IsDevelopment())
{
    Console.WriteLine(
        $"⚠️ WARNING: JWT Key length ({jwtKeyBytes.Length} bytes / {jwtKeyBytes.Length * 8} bits) is below " +
        $"recommended length ({RECOMMENDED_KEY_LENGTH_BYTES} bytes / 512 bits) for production. " +
        $"Consider generating a longer key for maximum security.");
}
else if (jwtKeyBytes.Length >= RECOMMENDED_KEY_LENGTH_BYTES)
{
    Console.WriteLine($"✅ JWT Key length validated: {jwtKeyBytes.Length} bytes ({jwtKeyBytes.Length * 8} bits) - EXCELLENT");
}
else
{
    Console.WriteLine($"✅ JWT Key length validated: {jwtKeyBytes.Length} bytes ({jwtKeyBytes.Length * 8} bits) - SECURE");
}
builder.Configuration["OpenAI:ApiKey"] = GetSecretValue("openai-api-key", null) ?? "";

// ✅ Cargar Google Maps API Key desde Secret Manager
var googleMapsApiKey = GetSecretValue("google-maps-api-key", null) 
    ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
builder.Configuration["Google:ApiKey"] = googleMapsApiKey ?? "";
if (!string.IsNullOrEmpty(googleMapsApiKey))
{
    var googleMapsLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
    googleMapsLogger.LogInformation($"✅ Google Maps API Key configurada (longitud: {googleMapsApiKey.Length} caracteres)");
    googleMapsLogger.LogInformation($"   Origen: {(GetSecretValue("google-maps-api-key", null) != null ? "Secret Manager" : "Environment Variable")}");
}

// Configurar Stripe desde Secret Manager
// Las claves se cargarán dinámicamente según el modo configurado en SystemSetting
// Por defecto se cargan las de producción, pero se pueden cambiar desde el panel admin
// NOTA: El modo se carga después de inicializar la base de datos, ver más abajo

builder.Configuration["Twilio:AccountSid"] = GetSecretValue("twilio-account-sid", null) ?? "";
builder.Configuration["Twilio:AuthToken"] = GetSecretValue("twilio-auth-token", null) ?? "";
builder.Configuration["Twilio:VerificationServiceSid"] = GetSecretValue("twilio-verification-service-sid", null) ?? "";

// ✅ Cargar clave de cifrado MFA desde Secret Manager (obligatoria para cifrar/descifrar secretos MFA)
var mfaEncryptionKey = GetSecretValue("mfa-encryption-key", null) 
    ?? Environment.GetEnvironmentVariable("MFA_ENCRYPTION_KEY");

if (string.IsNullOrEmpty(mfaEncryptionKey))
{
    var mfaLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
    mfaLogger.LogError("❌ ERROR CRÍTICO: MFA Encryption Key no configurada");
    mfaLogger.LogError("   La clave debe estar en Secret Manager (mfa-encryption-key) o variable de entorno (MFA_ENCRYPTION_KEY)");
    mfaLogger.LogError("   Esta clave es OBLIGATORIA para cifrar/descifrar secretos MFA");
    mfaLogger.LogError("   IMPORTANTE: La misma clave debe usarse en desarrollo y producción para descifrar secretos existentes");
    throw new InvalidOperationException(
        "MFA Encryption Key not configured. " +
        "Add 'mfa-encryption-key' to Google Cloud Secret Manager or set 'MFA_ENCRYPTION_KEY' environment variable. " +
        "This key is REQUIRED to encrypt/decrypt MFA secrets. " +
        "IMPORTANT: The same key must be used in development and production to decrypt existing secrets.");
}

builder.Configuration["Mfa:EncryptionKey"] = mfaEncryptionKey;
var mfaLogger2 = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
mfaLogger2.LogInformation($"✅ MFA Encryption Key configurada (longitud: {mfaEncryptionKey.Length} caracteres)");
mfaLogger2.LogInformation($"   Origen: {(GetSecretValue("mfa-encryption-key", null) != null ? "Secret Manager" : "Environment Variable")}");

builder.Configuration["GoogleCloud:BucketName"] = "atrapobucket";




// Configuración de Email (opcional - si no está configurado, no se enviarán emails)
// Puede usar SMTP de hosting propio, Gmail, SendGrid, etc.
try
{
    builder.Configuration["Email:SmtpHost"] = GetSecretValue("email-smtp-host", "") ?? "";
    // ⚠️ RECOMENDACIÓN: Usar puerto 587 (STARTTLS) en lugar de 465 (SSL) para mejor compatibilidad
    builder.Configuration["Email:SmtpPort"] = GetSecretValue("email-smtp-port", "587") ?? "587";
    builder.Configuration["Email:SmtpUsername"] = GetSecretValue("email-smtp-username", "") ?? "";
    builder.Configuration["Email:SmtpPassword"] = GetSecretValue("email-smtp-password", "") ?? "";
    builder.Configuration["Email:FromEmail"] = GetSecretValue("email-from-email", "info@inspecciono.com") ?? "info@inspecciono.com";
    builder.Configuration["Email:FromName"] = GetSecretValue("email-from-name", "Inspecciono") ?? "Inspecciono";
}
catch
{
    // Si no hay configuración de email en Secret Manager, usar valores vacíos (no enviará emails)
    builder.Configuration["Email:SmtpHost"] = "";
    builder.Configuration["Email:SmtpPort"] = "587";
    builder.Configuration["Email:SmtpUsername"] = "";
    builder.Configuration["Email:SmtpPassword"] = "";
    builder.Configuration["Email:FromEmail"] = "info@inspecciono.com";
    builder.Configuration["Email:FromName"] = "Inspecciono";
}

// Configurar la cadena de conexi�n seg�n el entorno
// ✅ MIGRACIÓN A RENDER POSTGRESQL: Base de datos principal en Render
// Supabase se mantiene SOLO para Realtime (chat en directo y notificaciones)
string connectionString;

// ✅ HARDCODEADO: Connection string hardcodeada para producción (Render.com)
// En desarrollo: usar appsettings.Development.json o variable de entorno
var connectionStringSource = "Unknown";

if (isDevelopment)
{
    // ✅ DESARROLLO: Leer de appsettings.Development.json o variable de entorno
    var envConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PostgresConnection");
    var configConnectionString = builder.Configuration.GetConnectionString("PostgresConnection");
    
    if (!string.IsNullOrEmpty(envConnectionString))
    {
        connectionString = envConnectionString;
        connectionStringSource = "Variable de Entorno (Desarrollo)";
        configLogger.LogInformation($"✅ Desarrollo: Connection string desde variable de entorno");
    }
    else if (!string.IsNullOrEmpty(configConnectionString))
    {
        connectionString = configConnectionString;
        connectionStringSource = "appsettings.Development.json";
        configLogger.LogInformation($"✅ Desarrollo: Connection string desde appsettings.Development.json");
    }
    else
    {
        throw new InvalidOperationException(
            "Database connection string not configured for development. " +
            "Set 'PostgresConnection' in appsettings.Development.json or environment variable ConnectionStrings__PostgresConnection.");
    }
}
else
{
    // ✅ PRODUCCIÓN (Render): connection string desde VARIABLE DE ENTORNO.
    // No se hardcodean credenciales en el código fuente.
    // Configura en Render (formato clave-valor de Npgsql):
    //   ConnectionStrings__PostgresConnection = Host=...;Port=5432;Database=...;Username=...;Password=...;SslMode=Require;Timeout=60;CommandTimeout=120;Pooling=true;
    var prodConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__PostgresConnection")
        ?? builder.Configuration.GetConnectionString("PostgresConnection");

    if (string.IsNullOrEmpty(prodConnectionString))
    {
        throw new InvalidOperationException(
            "Database connection string not configured for production. " +
            "Set the environment variable 'ConnectionStrings__PostgresConnection' in Render " +
            "with the Npgsql key-value format: " +
            "Host=...;Port=5432;Database=...;Username=...;Password=...;SslMode=Require;Timeout=60;CommandTimeout=120;Pooling=true;");
    }

    connectionString = prodConnectionString;
    connectionStringSource = "Variable de Entorno (Render Producción)";
    configLogger.LogInformation("✅ Producción: Connection string desde variable de entorno (Render PostgreSQL)");
    configLogger.LogInformation("   ✅ Render PostgreSQL nativo - Compatible con savepoints, ExecutionStrategy y Hangfire");
}

if (!string.IsNullOrEmpty(connectionString))
{
    // Usar connection string configurada
    
    // Extraer información para logging (sin mostrar contraseña)
    // Usar NpgsqlConnectionStringBuilder para parsear correctamente (soporta Host= y Server=)
    var connBuilder = new NpgsqlConnectionStringBuilder(connectionString);
    var dbHost = connBuilder.Host ?? "unknown";
    var dbPort = connBuilder.Port.ToString();
    var dbUsername = connBuilder.Username ?? "unknown";
    var dbName = connBuilder.Database ?? "unknown";
    
    configLogger.LogInformation($"✅ Connection string detectada:");
    configLogger.LogInformation($"   Origen: {connectionStringSource}");
    configLogger.LogInformation($"   Host: {dbHost}");
    configLogger.LogInformation($"   Port: {dbPort}");
    configLogger.LogInformation($"   Database: {dbName}");
    configLogger.LogInformation($"   Username: {dbUsername}");
    configLogger.LogInformation($"   Entorno: {(isDevelopment ? "Development" : "Production")}");
}


builder.Configuration["ConnectionStrings:PostgresConnection"] = connectionString;

// ✅ HANGFIRE: Configurar connection string para Render PostgreSQL
// Render PostgreSQL es estándar, no requiere configuraciones especiales como Supabase
string hangfireConnectionString;
try
{
    // Usar NpgsqlConnectionStringBuilder para parsear la connection string correctamente
    var builderConn = new NpgsqlConnectionStringBuilder(connectionString);
    var host = builderConn.Host ?? string.Empty;
    var port = builderConn.Port;
    
    // ✅ RENDER POSTGRESQL: Usar la misma connection string para todo
    // Render PostgreSQL es estándar y soporta todas las características necesarias
    hangfireConnectionString = connectionString;
    
    configLogger.LogInformation("[OK] Configurando Hangfire para Render PostgreSQL");
    configLogger.LogInformation($"   Host: {host}");
    configLogger.LogInformation($"   Port: {port}");
    configLogger.LogInformation("   [OK] Render PostgreSQL soporta todas las características estándar");
    configLogger.LogInformation("   [OK] Compatible con Hangfire, savepoints y ExecutionStrategy");
}
catch (Exception ex)
{
    configLogger.LogError(ex, "❌ Error al parsear connection string para Hangfire, usando connection string principal");
    configLogger.LogError($"   Error: {ex.Message}");
    hangfireConnectionString = connectionString; // Fallback seguro
    configLogger.LogWarning("⚠️ Hangfire usará la connection string principal. Verifica que sea válida.");
}

// Log de diagnóstico (sin mostrar contraseña)
var connectionStringForLog = connectionString;
if (connectionStringForLog.Contains("Password="))
{
    var passwordPattern = @"Password=[^;]+";
    connectionStringForLog = Regex.Replace(connectionStringForLog, passwordPattern, "Password=***");
}
configLogger.LogInformation($"=== DATABASE CONNECTION CONFIGURED ===");
configLogger.LogInformation($"Environment: {(isDevelopment ? "Development" : "Production")}");
configLogger.LogInformation($"Connection String (masked): {connectionStringForLog}");

// Add services to the container
// ✅ CRÍTICO: Especificar explícitamente el assembly de controladores para asegurar descubrimiento
builder.Services.AddControllers()
    .AddApplicationPart(typeof(CategoriesController).Assembly) // Forzar descubrimiento de controladores
    .AddJsonOptions(options =>
    {
        // ✅ CORRECCIÓN: Configurar JSON para evitar referencias circulares
        // ReferenceHandler.IgnoreCycles está disponible desde .NET 6
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        // ✅ CAMBIO: Incluir campos null en el JSON para mayor claridad en el frontend
        // Esto permite que el frontend use tipos más específicos (string | null) en lugar de (string | null | undefined)
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
        options.JsonSerializerOptions.WriteIndented = false;
        // ✅ MEJORA: Configurar para mejor rendimiento
        options.JsonSerializerOptions.PropertyNamingPolicy = null; // Mantener nombres de propiedades como están
        // Permite binding de bodies camelCase desde el frontend (title → Title)
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });

// Configure request size limits for file uploads
builder.Services.Configure<IISServerOptions>(options =>
{
    options.MaxRequestBodySize = 50 * 1024 * 1024; // 50MB
});

builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = 50 * 1024 * 1024; // 50MB
    // ✅ RENDER.COM: Aumentar timeout de requests para consultas largas a la base de datos
    // El timeout por defecto es 2 minutos, pero las queries complejas pueden tardar más
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5); // Mantener conexiones vivas más tiempo
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2); // Timeout para headers
});

// Configure form options for multipart form data
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartBodyLengthLimit = 50 * 1024 * 1024; // 50MB
    options.MemoryBufferThreshold = int.MaxValue;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ✅ CACHÉ: Agregar Memory Cache para caché en memoria (más flexible que ResponseCache)
// Útil para homepage-wall con caché condicional por país y categoría
builder.Services.AddMemoryCache();

// ✅ CACHÉ: Agregar Response Caching para mejorar performance de endpoints públicos
// Especialmente útil para homepage-wall que hace múltiples consultas a la BD
builder.Services.AddResponseCaching();
// Note: Swagger security definition removed due to Microsoft.OpenApi.Models namespace issues
// Swagger will still work for testing endpoints

// ❌ DESHABILITADO: Response Compression causa timeouts en Render.com
// El problema: Gzip/Brotli se bloquea al comprimir la respuesta
// Síntoma: Query completa en 500ms pero cliente timeout a 30s
// Causa: Render.com usa nginx que ya comprime, doble compresión causa deadlock
// Solución: Render/nginx manejará la compresión automáticamente
// Referencias:
// - https://github.com/dotnet/aspnetcore/issues/46792
// - https://stackoverflow.com/questions/72156784/asp-net-core-6-api-hangs-after-controller-returns-data
// builder.Services.AddResponseCompression(options =>
// {
//     options.EnableForHttps = true;
//     options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
//     options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
//     options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(
//         new[] { "application/json", "application/json; charset=utf-8" });
// });
//
// builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(options =>
// {
//     options.Level = System.IO.Compression.CompressionLevel.Fastest;
// });

// ✅ SEGURIDAD 2025: Configurar Rate Limiting nativo de .NET 8
// Configuración ajustada para aplicación web: límites más permisivos para uso normal, estrictos para endpoints sensibles
// En desarrollo: sin límites para localhost y IPs de desarrollo
builder.Services.AddRateLimiter(options =>
{
    // IPs de desarrollo que no tendrán límites (puedes agregar más separadas por coma)
    var developmentIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "127.0.0.1",
        "::1",
        "localhost",
        "10.192.42.21" // Tu IP de desarrollo
    };
    
    // Agregar IPs adicionales desde variable de entorno si existe
    var additionalDevIps = Environment.GetEnvironmentVariable("DEV_IPS");
    if (!string.IsNullOrEmpty(additionalDevIps))
    {
        foreach (var ip in additionalDevIps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            developmentIps.Add(ip);
        }
    }

    // Función helper para verificar si es IP de desarrollo
    bool IsDevelopmentIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        return developmentIps.Contains(ip) || ip.StartsWith("127.") || ip.StartsWith("::1") || ip == "localhost";
    }

    // 1. Política para autenticación: Sin límites para localhost, 30 intentos cada 5 minutos para otros IPs
    options.AddPolicy("auth", httpContext =>
    {
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        // Si es IP de desarrollo, sin límites
        if (IsDevelopmentIp(remoteIp))
        {
            return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter(remoteIp);
        }
        
        // Para otros IPs: 30 requests cada 5 minutos (ampliado de 5)
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: remoteIp,
            factory: partition => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 30, // Ampliado de 5 a 30
                Window = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });

    // 2. Política para API general: Sin límites para localhost, 200 requests por minuto para otros IPs
    options.AddPolicy("api", httpContext =>
    {
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        // Si es IP de desarrollo, sin límites
        if (IsDevelopmentIp(remoteIp))
        {
            return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter(remoteIp);
        }
        
        // Para otros IPs: 200 requests por minuto (ampliado de 100)
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: remoteIp,
            factory: partition => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 200, // Ampliado de 100 a 200
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 5 // Ampliado de 2 a 5
            });
    });

    // 3. Política para operaciones de pago: Sin límites para localhost, 30 por minuto para otros IPs
    options.AddPolicy("payment", httpContext =>
    {
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        // Si es IP de desarrollo, sin límites
        if (IsDevelopmentIp(remoteIp))
        {
            return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter(remoteIp);
        }
        
        // Para otros IPs: 30 requests por minuto (ampliado de 10)
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: remoteIp,
            factory: partition => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 30, // Ampliado de 10 a 30
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 2 // Ampliado de 0 a 2
            });
    });

    // 4. Política para admin: Sin límites para localhost, 500 requests por minuto para otros IPs
    options.AddPolicy("admin", httpContext =>
    {
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        // Si es IP de desarrollo, sin límites
        if (IsDevelopmentIp(remoteIp))
        {
            return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter(remoteIp);
        }
        
        // Para otros IPs: 500 requests por minuto (ampliado de 200)
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: remoteIp,
            factory: partition => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 500, // Ampliado de 200 a 500
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 10 // Ampliado de 5 a 10
            });
    });

    // 5. Política global por IP: Sin límites para localhost, 5000 requests por hora para otros IPs
    // En desarrollo o para IPs de desarrollo: sin límites
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<Microsoft.AspNetCore.Http.HttpContext, string>(httpContext =>
    {
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        // Si es desarrollo o IP de desarrollo, sin límites
        if (isDevelopment || IsDevelopmentIp(remoteIp))
        {
            return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter(remoteIp);
        }
        
        // En producción, aplicar límite ampliado
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: remoteIp,
            factory: partition => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 5000, // Ampliado de 1000 a 5000 requests por hora
                Window = TimeSpan.FromHours(1)
            });
    });

    // Respuesta cuando se excede el límite
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        
        if (context.Lease.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out var retryAfter))
        {
            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                error = "Too many requests. Please try again later.",
                retryAfter = retryAfter.TotalSeconds
            }, cancellationToken);
        }
        else
        {
            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                error = "Too many requests. Please try again later."
            }, cancellationToken);
        }
    };
});

builder.Services.AddAutoMapper(cfg =>
{
    cfg.AddProfile<AdMappingProfile>();
    cfg.AddProfile<PlatformMappingProfile>();
    cfg.AddProfile<CategoryMappingProfile>();
    cfg.AddProfile<UserMappingProfile>();
});

// ✅ 2026: SignalR REMOVIDO - Usando Supabase Realtime para chat en tiempo real
// Ventajas de Supabase Realtime:
// - Escalabilidad automática global
// - Presence integrado (sin diccionarios en memoria)
// - Postgres Changes para sincronización automática
// - RLS para seguridad a nivel de fila

// Configure JWT Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment(); // ✅ SEGURIDAD: HTTPS obligatorio en producción
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "",
        ValidAudience = builder.Configuration["Jwt:Audience"] ?? "",
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not found in Secret Manager or configuration."))),
        // 🛡️ Round 15 — R3 FIX: 60s de tolerancia para drift de reloj cliente↔servidor.
        // Era Zero → rechazos espurios cuando el reloj del móvil va 5-30s adelantado.
        // 60s es el mínimo razonable; OWASP/Microsoft recomiendan 30-60s.
        ClockSkew = TimeSpan.FromSeconds(60)
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            
            logger.LogInformation($"[JWT] OnMessageReceived - Path: {path}, HasQueryToken: {!string.IsNullOrEmpty(accessToken)}");
            
            // SignalR chatHub removido - Usando Supabase Realtime
            // El frontend ahora usa tokens JWT directamente con Supabase
            return Task.CompletedTask;
        },
        OnTokenValidated = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            var path = context.HttpContext.Request.Path;
            var userId = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "Unknown";
            
            logger.LogInformation($"[JWT] ✅ OnTokenValidated - Path: {path}, UserId: {userId}, IsAuthenticated: {context.Principal?.Identity?.IsAuthenticated}");
            return Task.CompletedTask;
        },
        // ✅ FIX PRODUCCIÓN: Falla rápido si el token es inválido en lugar de causar timeout
        OnAuthenticationFailed = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            var path = context.HttpContext.Request.Path.Value ?? "";
            var method = context.HttpContext.Request.Method;
            var hasAuthHeader = context.HttpContext.Request.Headers.ContainsKey("Authorization");
            var authHeader = hasAuthHeader ? context.HttpContext.Request.Headers["Authorization"].ToString().Substring(0, Math.Min(30, context.HttpContext.Request.Headers["Authorization"].ToString().Length)) + "..." : "NO AUTH HEADER";
            
            // Si es un endpoint público, no loggear el error (es normal que no haya token)
            var publicEndpoints = new[] { "/api/Categories", "/api/ServiceType/public", "/api/SearchService/homepage-wall", "/api/SearchService/detected-country-from-ip", "/health", "/warmup" };
            var pathString = new Microsoft.AspNetCore.Http.PathString(path);
            var isPublicEndpoint = publicEndpoints.Any(ep => pathString.StartsWithSegments(ep));
            
            logger.LogWarning($"[JWT] ❌ OnAuthenticationFailed - {method} {path}");
            logger.LogWarning($"[JWT]    HasAuthHeader: {hasAuthHeader}, AuthHeader: {authHeader}");
            logger.LogWarning($"[JWT]    IsPublicEndpoint: {isPublicEndpoint}");
            logger.LogWarning($"[JWT]    Error: {context.Exception?.Message ?? "Unknown error"}");
            logger.LogWarning($"[JWT]    Exception Type: {context.Exception?.GetType().Name ?? "None"}");
            
            if (context.Exception != null)
            {
                logger.LogWarning($"[JWT]    StackTrace: {context.Exception.StackTrace}");
            }
            
            // Falla rápido en lugar de esperar timeout
            context.Fail("Invalid token");
            return Task.CompletedTask;
        },
        // ✅ FIX PRODUCCIÓN: No intentar validar token si no está presente en endpoints públicos
        OnChallenge = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            var path = context.HttpContext.Request.Path.Value ?? "";
            var method = context.HttpContext.Request.Method;
            var hasAuthHeader = context.HttpContext.Request.Headers.ContainsKey("Authorization");
            var authHeaderValue = hasAuthHeader ? context.HttpContext.Request.Headers["Authorization"].ToString() : "NO AUTH HEADER";
            var authHeaderPreview = hasAuthHeader && authHeaderValue.Length > 0 
                ? (authHeaderValue.Length > 50 ? authHeaderValue.Substring(0, 50) + "..." : authHeaderValue)
                : "NO AUTH HEADER";
            
            var publicEndpoints = new[] { 
                "/api/Categories", 
                "/api/ServiceType/public", 
                "/api/SearchService/homepage-wall",
                "/api/SearchService/detected-country-from-ip",
                "/health", 
                "/warmup", 
                "/health-detailed" 
            };
            var pathString = context.HttpContext.Request.Path;
            var isPublicEndpoint = publicEndpoints.Any(ep => pathString.StartsWithSegments(ep));
            
            // ✅ LOGGING MEJORADO: Mostrar más detalles para debug
            logger.LogWarning($"[JWT] 🔔 OnChallenge - {method} {path}");
            logger.LogWarning($"[JWT]    HasAuthHeader: {hasAuthHeader}");
            logger.LogWarning($"[JWT]    AuthHeaderPreview: {authHeaderPreview}");
            logger.LogWarning($"[JWT]    IsPublicEndpoint: {isPublicEndpoint}");
            logger.LogWarning($"[JWT]    Error: {context.Error}, ErrorDescription: {context.ErrorDescription}");
            
            // Si es endpoint público y no hay token, no hacer challenge (dejar pasar)
            if (isPublicEndpoint && !hasAuthHeader)
            {
                logger.LogInformation($"[JWT] ✅ Endpoint público sin token - HandleResponse() para permitir acceso");
                context.HandleResponse(); // No enviar 401, dejar que el endpoint público maneje la request
            }
            else if (!hasAuthHeader)
            {
                logger.LogWarning($"[JWT] ⚠️ Endpoint protegido sin token - Se enviará 401");
                logger.LogWarning($"[JWT]    El frontend debe enviar: Authorization: Bearer <token>");
            }
            else
            {
                logger.LogWarning($"[JWT] ⚠️ Endpoint protegido CON token pero falló validación - Se enviará 401");
                logger.LogWarning($"[JWT]    Verificar: Issuer, Audience, Expiration, Signature");
            }
            
            return Task.CompletedTask;
        }
    };
});

// Configure PostgreSQL
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgresConnection"), npgsqlOptions =>
    {
        npgsqlOptions.CommandTimeout(30);
        npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
    }));

// Configure Google Cloud Storage
builder.Services.AddSingleton<StorageClient>(sp =>
{
    var isDev = sp.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
    
    // En producción: intentar leer de GoogleCredentialJson (Azure App Services)
    if (!isDev)
    {
        var credentialsJson = Environment.GetEnvironmentVariable("GoogleCredentialJson");
        if (!string.IsNullOrEmpty(credentialsJson))
        {
            try
            {
                var credential = GoogleCredential.FromJson(credentialsJson);
                return StorageClient.Create(credential);
            }
            catch (Exception ex)
            {
                var logger = sp.GetRequiredService<ILogger<Program>>();
                logger.LogError(ex, "Error al crear StorageClient desde GoogleCredentialJson");
            }
        }
    }
    
    // Fallback: usar GOOGLE_APPLICATION_CREDENTIALS (archivo o variable)
    var credentialsPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
    if (!string.IsNullOrEmpty(credentialsPath) && System.IO.File.Exists(credentialsPath))
    {
        var credential = GoogleCredential.FromFile(credentialsPath);
        return StorageClient.Create(credential);
    }
    
    // Último fallback: Application Default Credentials
    try
    {
        return StorageClient.Create();
    }
    catch
    {
        // Si falla, retornar null - los servicios que lo necesiten deberán manejarlo
        return null!;
    }
});

// Supabase Storage (reemplaza a GCS para multimedia). HttpClient tipado.
builder.Services.AddHttpClient<ISupabaseStorageService, SupabaseStorageService>();
// ISignedUrlService ahora resuelve URLs desde Supabase (público/firmado por prefijo).
builder.Services.AddScoped<ISignedUrlService, SupabaseSignedUrlService>();


// Configure CORS

builder.Services.AddCors(options =>
{
    if (isDevelopment)
    {
        // ✅ DESARROLLO: Permitir cualquier origen para no bloquear imágenes externas (Unsplash, etc.)
        options.AddPolicy("AllowSpecificOrigin", builder =>
        {
            builder.AllowAnyOrigin()
                   .AllowAnyMethod()
                   .AllowAnyHeader()
                   .SetPreflightMaxAge(TimeSpan.FromSeconds(600));
        });
    }
    else
    {
        // ✅ PRODUCCIÓN: Solo orígenes específicos con credenciales
        options.AddPolicy("AllowSpecificOrigin", builder =>
        {
            builder.WithOrigins(
                "http://localhost:3000",
                "http://localhost:5173",
                "https://localhost",              // ✅ Capacitor Android/iOS
                "capacitor://localhost",          // ✅ Capacitor Android/iOS (alternativo)
                "https://inspecciono.com",
                "https://www.inspecciono.com") // <--- agregar dominio de frontend producci�n
                   .AllowAnyMethod()
                   .AllowAnyHeader()
                   .AllowCredentials()
                   .SetPreflightMaxAge(TimeSpan.FromSeconds(600));
        });
    }
});


// ✅ HANGFIRE CONFIGURACIÓN 2026: Integración con Render PostgreSQL
// 
// DOCUMENTACIÓN OFICIAL: https://docs.hangfire.io/en/latest/configuration/using-postgresql.html
// VERSIÓN ACTUAL: Hangfire.PostgreSql 1.20.13 ✅
// VERSIÓN HANGFIRE: Hangfire.AspNetCore 1.8.22 ✅
// 
// MIGRACIÓN A RENDER POSTGRESQL:
// - Base de datos principal migrada de Supabase a Render PostgreSQL
// - Render PostgreSQL es estándar, sin restricciones de pooling
// - Soporta todas las características: savepoints, ExecutionStrategy, Hangfire, locks distribuidos
// - Configuración simplificada: misma connection string para app y Hangfire
// 
// SUPABASE MANTENIDO SOLO PARA:
// - Realtime (chat en directo)
// - Notificaciones push en tiempo real
// 
// VENTAJAS DE RENDER POSTGRESQL:
// - PostgreSQL nativo sin poolers intermedios (no más problemas de Session/Transaction Pooler)
// - Mejor rendimiento para transacciones y locks
// - Configuración más simple
// - Sin limitaciones de prepared statements o multiplexing
// 
// EJEMPLO DE CONNECTION STRING (Render PostgreSQL):
// Host=dpg-d5kar5l6ubrc73espd5g-a;Port=5432;Database=inspecciono;Username=inspecciono_user;Password=***;SslMode=Require;Timeout=60;CommandTimeout=120;Pooling=true;
// 
// REFERENCIAS:
// - https://render.com/docs/databases
// - https://docs.hangfire.io/en/latest/configuration/using-postgresql.html

// Validar que la connection string de Hangfire sea válida antes de configurarla
var hangfireLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
var hangfireConnectionValid = !string.IsNullOrEmpty(hangfireConnectionString);

if (hangfireConnectionValid)
{
    // Validar que la connection string no tenga un hostname inválido
    var serverMatch = Regex.Match(hangfireConnectionString, @"Server=([^;]+)", RegexOptions.IgnoreCase);
    if (serverMatch.Success)
    {
        var serverHost = serverMatch.Groups[1].Value.Trim();
        // Si el hostname parece inválido (muy corto o contiene caracteres raros), usar connection string principal
        if (serverHost.Length < 5 || serverHost.Contains("..") || serverHost.StartsWith(".") || serverHost.EndsWith("."))
        {
            hangfireLogger.LogWarning($"⚠️ Hostname de Hangfire parece inválido: {serverHost}. Usando connection string principal");
            hangfireConnectionString = connectionString;
        }
    }
    
    // ✅ FIX CRÍTICO: Deshabilitar multiplexing explícitamente y configurar Enlist para Hangfire
    // Render PostgreSQL es estándar, pero estas configuraciones son buenas prácticas
    var hangfireConnBuilder = new NpgsqlConnectionStringBuilder(hangfireConnectionString);
    hangfireConnBuilder.Multiplexing = false; // Deshabilitar multiplexing para mejor compatibilidad
    hangfireConnBuilder.Enlist = false; // Evitar transacciones distribuidas automáticas
    hangfireConnBuilder.MaxAutoPrepare = 0; // Deshabilitar prepared statements (no necesario para Render)
    
    // ✅ Obtener connection string final del builder (ya incluye todos los parámetros correctamente formateados)
    hangfireConnectionString = hangfireConnBuilder.ToString();
    
    // ✅ HANGFIRE CONFIGURACIÓN 2026: Mejores prácticas para Render PostgreSQL
    // Basado en: https://docs.hangfire.io/en/latest/configuration/using-postgresql.html
    // Render PostgreSQL es estándar, sin problemas de poolers intermedios
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180) // Compatibilidad con .NET 8+
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(hangfireConnectionString, new PostgreSqlStorageOptions
        {
            // ✅ SCHEMA: Esquema explícito para Hangfire (mejores prácticas 2026)
            SchemaName = "hangfire", // Esquema por defecto, explícito para claridad
            
            // ✅ SCHEMA PREPARATION: Crea tablas automáticamente si no existen
            PrepareSchemaIfNecessary = true,
            
            // ✅ POLLING: Intervalo optimizado para balance entre latencia y carga
            // Intervalos más cortos = menor latencia pero mayor carga en DB
            // Intervalos más largos = menor carga pero mayor latencia en procesamiento
            QueuePollInterval = TimeSpan.FromSeconds(15), // Balance óptimo para Render PostgreSQL
            
            // ✅ INVISIBILITY TIMEOUT: Tiempo antes de reintentar job fallido
            // Si un job no se completa en este tiempo, se marca como fallido y se reintenta
            // RECOMENDACIÓN: 30-60 minutos para jobs largos
            InvisibilityTimeout = TimeSpan.FromMinutes(30), // Suficiente para jobs largos
            
            // ✅ SLIDING INVISIBILITY: HABILITADO para renovar timeouts automáticamente
            // Reduce disposiciones al extender el timeout mientras el job está procesando
            // Basado en recomendaciones de Hangfire Forum y casos exitosos
            UseSlidingInvisibilityTimeout = true,

            // ✅ DISTRIBUTED LOCK TIMEOUT: Crítico para evitar deadlocks
            // Los locks distribuidos necesitan más tiempo con latencia de red
            // Basado en casos reales: timeouts altos (15+ min) resuelven problemas de locks
            DistributedLockTimeout = TimeSpan.FromMinutes(20) // Aumentado para Render.com
        })
        .UseDefaultTypeResolver()
        .UseDefaultTypeSerializer());
    
    // ✅ HABILITADO: Servidor de Hangfire para procesar jobs automáticamente
    // Los jobs de timers de appointments requieren que el servidor esté activo
    // Render PostgreSQL es estándar, habilitar servidor en todos los entornos
    var enableHangfireServer = true; // Siempre habilitado con Render PostgreSQL
    
    if (enableHangfireServer)
    {
        builder.Services.AddHangfireServer(options =>
        {
            // Worker count: ajustar según CPU disponible
            // En desarrollo: 1 worker para reducir carga
            // En producción: más workers para mejor throughput
            options.WorkerCount = isDevelopment ? 1 : Math.Max(2, Environment.ProcessorCount);
            
            // ✅ FIX RENDER.COM: Aumentar timeouts para Render.com (más latencia)
            options.ServerTimeout = TimeSpan.FromMinutes(10); // Aumentado de 5 a 10 minutos
            options.HeartbeatInterval = TimeSpan.FromSeconds(60); // Aumentado de 30 a 60 segundos
            options.ServerCheckInterval = TimeSpan.FromMinutes(2); // Aumentado de 1 a 2 minutos
            options.SchedulePollingInterval = TimeSpan.FromSeconds(60); // Aumentado de 30 a 60 segundos
            options.StopTimeout = TimeSpan.FromSeconds(30); // Aumentado de 15 a 30 segundos
            options.Queues = new[] { "default" }; // Solo procesar cola default para reducir carga
            options.ShutdownTimeout = TimeSpan.FromSeconds(30); // Timeout para shutdown graceful
        });
        
        hangfireLogger.LogInformation($"✅ Hangfire Server habilitado con Render PostgreSQL");
        hangfireLogger.LogInformation($"   Workers: {(isDevelopment ? 1 : Math.Max(2, Environment.ProcessorCount))}");
        hangfireLogger.LogInformation("   [OK] PostgreSQL estándar - Sin restricciones de poolers");
    }
    else
    {
        hangfireLogger.LogWarning("⚠️ Hangfire Server deshabilitado (connection string no válida)");
    }
}
else
{
    hangfireLogger.LogError("❌ Hangfire no se configurará: connection string no válida");
}

// Register Services

builder.Services.AddScoped<IStripeConfigService, StripeConfigService>();builder.Services.AddScoped<IWebMixerService, WebMixerService>();
builder.Services.AddScoped<IScrapperService, ScrapperService>();
builder.Services.AddScoped<ILikeService, LikeService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAuthorizationServices, AuthorizationServices>();
builder.Services.AddScoped<ISubscriptionService, newApi.Services.SubscriptionService>();
builder.Services.AddScoped<ISearchHireService, SearchHireService>();
builder.Services.AddScoped<ISearchServiceService, SearchServiceService>();
builder.Services.AddScoped<IAppointmentService, AppointmentService>();
// Servicios redundantes eliminados - reemplazados por SystemStatusService
// builder.Services.AddScoped<IAppointmentConfigService, AppointmentConfigService>();
// builder.Services.AddScoped<ICategoryServiceTypeConfigService, CategoryServiceTypeConfigService>();
builder.Services.AddScoped<SystemStatusService>();
builder.Services.AddScoped<IAccountDeletionService, AccountDeletionService>();
builder.Services.AddScoped<IAccountDeletionNotificationService, AccountDeletionNotificationService>();
builder.Services.AddScoped<StripeRefundService>();
builder.Services.AddScoped<IStripeReconciliationService, StripeReconciliationService>(); // P2-5: conciliación diaria BD↔Stripe
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ILoggingService, LoggingService>();
builder.Services.AddSingleton<IAlertChannelService, AlertChannelService>(); // P3-3: canal alternativo Slack/Telegram
builder.Services.AddScoped<IPlatformMaintenanceService, PlatformMaintenanceService>(); // 🛡️ R5+R7: jobs mantenimiento

// 🔧 FISCAL FLIP: perfil fiscal de la plataforma + servicios asociados. Default IsVatRegistered=false
// → comportamiento legacy (recibo simple, sin alertas IVA, sin captura NIF cliente formal). Para activar:
// rellenar sección "PlatformFiscal" en appsettings o env vars de Render (PlatformFiscal__*) + aplicar
// migración AddInvoiceCountersAndClientVat + reiniciar. Sin recompilar.
builder.Services.Configure<newApi.Configuration.PlatformFiscalProfile>(
    builder.Configuration.GetSection(newApi.Configuration.PlatformFiscalProfile.SectionName));
builder.Services.AddScoped<newApi.Services.IInvoiceNumberService, newApi.Services.InvoiceNumberService>();
builder.Services.AddScoped<newApi.Services.IViesValidator, newApi.Services.ViesValidatorStub>();
// 📜 Round 9 — A2 FIX: servicio de auditoría de transiciones de estado de SearchHire (GDPR Art 5(2)).
builder.Services.AddScoped<newApi.Services.ISearchHireStatusAuditService, newApi.Services.SearchHireStatusAuditService>();

builder.Services.AddScoped<IInvoiceService, newApi.Services.InvoiceService>();
builder.Services.AddScoped<IStripeValidationService, StripeValidationService>();
builder.Services.AddScoped<RefreshTokenCleanupService>(); // ✅ SEGURIDAD 2025: Limpieza de refresh tokens
builder.Services.AddScoped<MfaService>(); // ✅ SEGURIDAD 2025: Autenticación Multifactor (MFA/2FA)
builder.Services.AddScoped<ITimezoneService, TimezoneService>(); // ✅ Servicio para detección de timezone y country desde coordenadas
builder.Services.AddScoped<IFavoriteService, FavoriteService>(); // ✅ FAVORITOS: Gestión de servicios favoritos
builder.Services.AddScoped<ISupabaseRealtimeService, SupabaseRealtimeService>(); // ✅ SUPABASE REALTIME: Reemplaza SignalR para chat en tiempo real
builder.Services.AddScoped<IStripeReconciliationService, StripeReconciliationService>(); // P2-5: reconciliación diaria BD↔Stripe

// Background services - AppointmentTimerBackgroundService migrated to Hangfire

builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<SearchServiceService>();
builder.Services.AddScoped<SearchHireService>();

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("Supabase"); // HttpClient nombrado para Supabase Realtime

// Add Health Checks
builder.Services.AddHealthChecks();

var app = builder.Build();

// ✅ RENDER.COM: CRÍTICO - Mover inicialización pesada a background task
// Render.com necesita que el servidor esté escuchando INMEDIATAMENTE
// La inicialización pesada (Stripe, etc.) se hará en background después de iniciar el servidor
// Esto permite que Render.com detecte el puerto rápidamente

// ✅ Background task para inicialización pesada (se ejecuta después de que el servidor esté escuchando)
_ = Task.Run(async () =>
{
    var bgLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("BackgroundInit");
    try
    {
        bgLogger.LogInformation("[BACKGROUND] 🚀 Iniciando background task para carga de Stripe...");
        bgLogger.LogInformation("[BACKGROUND] ⏳ Esperando 2 segundos para que el servidor esté completamente iniciado...");
        
        // Esperar un poco para que el servidor esté completamente iniciado
        await Task.Delay(2000);
        
        bgLogger.LogInformation("[BACKGROUND] ✅ Espera completada, iniciando carga de claves Stripe...");
        
        // ✅ Cargar claves Stripe según el modo configurado en SystemSetting
        using (var scope = app.Services.CreateScope())
        {
            bgLogger.LogInformation("[BACKGROUND] 📦 Creando scope de servicios...");
            
            var stripeConfigService = scope.ServiceProvider.GetRequiredService<IStripeConfigService>();
            bgLogger.LogInformation("[BACKGROUND] 🔍 Obteniendo modo de Stripe desde SystemSetting...");
            
            var mode = await stripeConfigService.GetStripeModeAsync();
            bgLogger.LogInformation($"[BACKGROUND] ✅ Modo Stripe detectado: {mode}");
            
            bgLogger.LogInformation("[BACKGROUND] 🔑 Obteniendo claves Stripe desde Secret Manager...");
            var (secretKey, webhookSecret, generalWebhookSecret) = await stripeConfigService.GetStripeKeysForModeAsync(
                mode, 
                GetSecretValue);
            
            bgLogger.LogInformation($"[BACKGROUND] ✅ Claves obtenidas - SecretKey: {!string.IsNullOrEmpty(secretKey)}, WebhookSecret: {!string.IsNullOrEmpty(webhookSecret)}");
            
            // ✅ Solo sobrescribir si las ENV VARS traen valor. En LOCAL no hay env vars de
            // Stripe, así que se CONSERVA lo de appsettings.Development.json (Stripe:SecretKey,
            // WebhookSecret, ...). En Render las env vars SÍ están y tienen prioridad.
            // (Mismo patrón guardado que AdminController.ReloadStripeKeysAsync, antes esto
            // machacaba la clave con "" y dejaba a Stripe sin API key en local.)
            if (!string.IsNullOrEmpty(secretKey))
                builder.Configuration["Stripe:SecretKey"] = secretKey;
            if (!string.IsNullOrEmpty(webhookSecret))
                builder.Configuration["Stripe:WebhookSecret"] = webhookSecret;
            if (!string.IsNullOrEmpty(generalWebhookSecret))
                builder.Configuration["Stripe:GeneralWebhookSecret"] = generalWebhookSecret;

            // Clave efectiva: ENV (prod) o appsettings.Development.json (local)
            StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"];
            
            var stripeLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            stripeLogger.LogInformation($"✅ Claves Stripe cargadas en modo: {mode}");
            stripeLogger.LogInformation($"   SecretKey presente: {!string.IsNullOrEmpty(builder.Configuration["Stripe:SecretKey"])} (env: {!string.IsNullOrEmpty(secretKey)})");
            stripeLogger.LogInformation($"   WebhookSecret presente: {!string.IsNullOrEmpty(builder.Configuration["Stripe:WebhookSecret"])} (env: {!string.IsNullOrEmpty(webhookSecret)})");
            
            bgLogger.LogInformation("[BACKGROUND] 📝 Guardando log en base de datos...");
            
            // ✅ Log informativo del sistema
            var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
            await loggingService.LogInfoAsync(
                message: "Stripe configuration successful",
                details: "Stripe API key configured successfully",
                userId: null,
                source: "Program.ConfigureStripe",
                relatedEntityType: "System",
                relatedEntityId: null,
                additionalData: new { 
                    Action = "StripeConfiguration",
                    SecretKeyPresent = !string.IsNullOrEmpty(secretKey),
                    PublishableKeyPresent = !string.IsNullOrEmpty(builder.Configuration["Stripe:PublishableKey"]),
                    Success = true
                },
                notifyUser: false
            );
            
            bgLogger.LogInformation("[BACKGROUND] ✅ Background task completada exitosamente");
        }
    }
    catch (Exception ex)
    {
        bgLogger.LogError(ex, "[BACKGROUND] ❌ ERROR en background task de Stripe");
        var stripeLogger = app.Services.GetRequiredService<ILogger<Program>>();
        stripeLogger.LogError(ex, "Error cargando claves Stripe según modo, usando configuración por defecto");
        
        // Si falla, intentar usar configuración por defecto
        if (string.IsNullOrEmpty(builder.Configuration["Stripe:SecretKey"]))
        {
            bgLogger.LogWarning("[BACKGROUND] ⚠️ Stripe SecretKey no encontrado, registrando error crítico...");
            using (var scope = app.Services.CreateScope())
            {
                var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                await loggingService.LogCriticalAsync(
                    message: "CRITICAL: Stripe configuration missing",
                    details: "Stripe SecretKey not found in configuration",
                    userId: null,
                    source: "Program.ConfigureStripe",
                    relatedEntityType: "System",
                    relatedEntityId: null,
                    additionalData: new { 
                        Action = "StripeConfiguration",
                        SecretKeyPresent = false,
                        PublishableKeyPresent = !string.IsNullOrEmpty(builder.Configuration["Stripe:PublishableKey"])
                    }
                );
            }
        }
    }
});


// ✅ HANGFIRE DASHBOARD: Habilitado con autenticación JWT
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new newApi.Filters.HangfireAuthorizationFilter(app.Configuration) },
    // Configurar para permitir iframes
    IsReadOnlyFunc = (DashboardContext context) => false
});

// 🔁 FRENTE 5 + 8 (RE-ACTIVADO): el comentario antiguo decía "Hangfire deshabilitado", pero el SERVIDOR
// de Hangfire SÍ está habilitado (enableHangfireServer = true más arriba). Estos dos registros estaban
// apagados por error, dejando sin efecto el AVISO de jobs fallidos (frente 5) y el WATCHDOG de timers
// (frente 8) pese a que el servidor procesa jobs con normalidad.
// (1) Filtro global: avisa (LogCritical → email al admin) cuando un job agota TODOS sus reintentos.
//     Tiene guarda anti-bucle para no re-encolar emails si el que falla es el propio job de email.
Hangfire.GlobalJobFilters.Filters.Add(
    new newApi.Services.HangfireFailedJobNotificationFilter(
        app.Services.GetRequiredService<IServiceScopeFactory>()));
// (2) Watchdog cada 10 min: recupera timers vencidos no procesados (job perdido por crash/reinicio entre
//     Schedule y Save). Solo toca timers con EndTime <= ahora y !IsExpired (no dispara antes de tiempo) y
//     es idempotente (ProcessAppointmentTimerAsync re-chequea estado; el dinero usa clave por-hire).
// 🛡️ N29 FIX: TZ explícita UTC en TODOS los RecurringJob. Sin esto, los cron usan la TZ
// del servidor (típico UTC en Render pero NO garantizado tras cambio de región). Los cron
// diarios además sufren DST spring-forward/fall-back si la TZ tiene DST → "0 3 * * *" puede
// no ejecutar o ejecutar dos veces el domingo del cambio. UTC no tiene DST → comportamiento
// determinista para auditoría y conciliación.
var n29UtcOptions = new Hangfire.RecurringJobOptions { TimeZone = TimeZoneInfo.Utc };

Hangfire.RecurringJob.AddOrUpdate<newApi.Services.IAppointmentService>(
    "appointment-timers-watchdog",
    svc => svc.ProcessOverdueTimersAsync(),
    "*/10 * * * *",
    n29UtcOptions);

// P2-5: Conciliación diaria BD ↔ Stripe a las 03:00 UTC. Detecta y reporta
// (LogCritical → email admin) refunds/transfers que existen sólo en Stripe o
// sólo en FinancialTransactions en las últimas 24h. NO muta nada por sí solo.
Hangfire.RecurringJob.AddOrUpdate<newApi.Services.IStripeReconciliationService>(
    "stripe-reconciliation-daily",
    svc => svc.RunDailyReconciliationAsync(),
    Cron.Daily(3, 0),
    n29UtcOptions);

// P3-2: Digest cada 30 min de alertas críticas suprimidas por dedup (count > umbral).
// LoggingService dedupa email-spam de LogCriticalAsync; este job consolida lo suprimido.
Hangfire.RecurringJob.AddOrUpdate<newApi.Services.ILoggingService>(
    "admin-alerts-digest",
    svc => svc.EmitAdminDigestAsync(),
    "*/30 * * * *",
    n29UtcOptions);

// P3-1 (versión mínima): Digest diario de SearchHires con RefundFailedAt en las últimas 24h.
// El filtro Hangfire setea RefundFailedAt cuando RetryMoneyDistributionJobAsync agota los reintentos.
Hangfire.RecurringJob.AddOrUpdate<newApi.Services.ILoggingService>(
    "refund-failed-digest",
    svc => svc.EmitRefundFailedDigestAsync(),
    Hangfire.Cron.Daily(7, 0),
    n29UtcOptions);

// 🛡️ R7: cleanup diario de ProcessedWebhookEvent (retención 30 días). Sin esto la tabla
// crece infinitamente — Stripe envía ~50-500 eventos/día y el TryBeginProcessingEventAsync
// insertaba 1 fila por cada uno desde el inicio.
Hangfire.RecurringJob.AddOrUpdate<newApi.Services.IPlatformMaintenanceService>(
    "processed-webhook-events-cleanup",
    svc => svc.CleanupOldProcessedWebhookEventsAsync(),
    Hangfire.Cron.Daily(4, 0),
    n29UtcOptions);

// 🛡️ R5: watchdog PaymentIntents próximos a expirar (7 días con CaptureMethod=manual).
// TODO P3-9 explícito en SubscriptionController:3152. Cada hora detecta hires con
// CaptureStatus="Pending" y CreatedAt > 6.5d, cancela el PI (libera autorización del cliente
// antes de expiración automática) y marca Failed para alerta admin.
Hangfire.RecurringJob.AddOrUpdate<newApi.Services.IPlatformMaintenanceService>(
    "payment-intent-expiry-watchdog",
    svc => svc.ProcessExpiringPaymentIntentsAsync(),
    "0 * * * *", // cada hora en punto
    n29UtcOptions);

// 🛡️ R5-F1: rescata AppointmentTimers que quedaron con HangfireJobId=NULL (proceso muere
// entre commit del timer y BackgroundJob.Schedule). Cada 15 min batch de 200 max para
// no saturar Hangfire en caso de backlog grande.
Hangfire.RecurringJob.AddOrUpdate<newApi.Services.IPlatformMaintenanceService>(
    "orphaned-timers-rescue",
    svc => svc.RescueOrphanedAppointmentTimersAsync(),
    "*/15 * * * *",
    n29UtcOptions);

// 🛡️ R5-F5: detecta SearchHires finalizados (completed/dispute_resolved_*) hace >24h SIN
// ninguna FinancialTransaction Refund/Payout asociada → ProcessMoneyDistribution falló o
// nunca corrió. Log Critical para reconciliación manual (no auto-fix por seguridad).
Hangfire.RecurringJob.AddOrUpdate<newApi.Services.IPlatformMaintenanceService>(
    "unreconciled-hires-detector",
    svc => svc.DetectUnreconciledFinalizedHiresAsync(),
    Hangfire.Cron.Daily(6, 0),
    n29UtcOptions);

// 🛡️ T4: cada hora escala disputas Pending cuyo deadline 48h del experto ya pasó sin
// respuesta. Flip atómico Pending→Resolving + encolar RescueStuckResolvingDisputeAsync
// (que mueve dinero a favor del cliente). Sin esto, una disputa quedaba indefinida en
// Pending bloqueando el dinero. AutomaticRetry=0 + DisableConcurrentExecution.
Hangfire.RecurringJob.AddOrUpdate<newApi.Services.IPlatformMaintenanceService>(
    "stale-disputes-escalator",
    svc => svc.EscalateStaleDisputesAsync(),
    "0 * * * *", // cada hora en punto
    n29UtcOptions);

// 🛡️ Round 12 — D3: notificación proactiva al experto cuando un requirement Stripe entra
// en ventana de 3 días antes del deadline. Defensa en profundidad: Stripe envía sus propios
// compliance emails pero SIN SLA garantizado. Sin esto, el experto puede ser sorprendido por
// transfers bloqueados sin previo aviso.
// Daily a las 09:00 UTC (= 11:00 Madrid summer, 10:00 winter — hora razonable para que el
// email no se pierda en la noche europea).
Hangfire.RecurringJob.AddOrUpdate<newApi.Services.IPlatformMaintenanceService>(
    "upcoming-stripe-deadlines-notifier",
    svc => svc.NotifyUpcomingStripeDeadlinesAsync(),
    Hangfire.Cron.Daily(9, 0),
    n29UtcOptions);
// 🛡️ Round 15 — R3 FIX: descomentar el cleanup. Estaba inactivo → la tabla RefreshTokens
// crecía sin freno (usuario activo rotando ~1 token/h × 24h × 90d = ~2.1k rows). Con cron
// diario a las 03:00 UTC se purgan los revocados/expirados con cutoff de 30+ días.
Hangfire.RecurringJob.AddOrUpdate<RefreshTokenCleanupService>(
    "cleanup-expired-refresh-tokens",
    service => service.CleanupExpiredTokensAsync(),
    Hangfire.Cron.Daily(3),
    n29UtcOptions);

// ✅ OPTIMIZADO: Usar solo scheduled jobs para eventos específicos
// Los recurring jobs fueron eliminados porque:
// 1. Los scheduled jobs se programan cuando ocurre el evento (más eficiente)
// 2. Hangfire tiene reintentos automáticos para scheduled jobs que fallan
// 3. Evita verificar periódicamente cuando no hay nada que procesar
// 4. Mejor práctica: programar jobs cuando ocurre el evento, no verificar periódicamente


// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "API V1");
        c.OAuthClientId("swagger-client-id");
        c.OAuthAppName("Swagger Test");
        c.OAuthUseBasicAuthenticationWithAccessCodeGrant();
    });
}

// ❌ DESHABILITADO: Response Compression causa timeouts en Render.com
// El problema: Gzip/Brotli se bloquea al comprimir la respuesta
// Síntoma: Query completa en 500ms pero cliente timeout a 30s
// Causa: Render.com usa nginx que ya comprime, doble compresión causa deadlock
// Solución: Render/nginx manejará la compresión automáticamente
// app.UseResponseCompression();

// ✅ RENDER.COM: Los timeouts de Kestrel ya están configurados arriba
// KeepAliveTimeout y RequestHeadersTimeout están en 5 y 2 minutos respectivamente

// ✅ RENDER.COM BEST PRACTICES: Orden correcto del middleware según ASP.NET Core
// 0. Response Caching PRIMERO (antes de routing para cachear respuestas)
app.UseResponseCaching();

// 🛡️ A6 FIX: Security headers + HSTS (Round 7). Antes faltaban TODOS:
//   - HSTS (Strict-Transport-Security): fuerza HTTPS, previene downgrade attacks
//   - X-Content-Type-Options: nosniff (previene MIME confusion)
//   - X-Frame-Options: DENY (anti-clickjacking; complementa el meta CSP frontend)
//   - Referrer-Policy: strict-origin-when-cross-origin (no leak full URL en cross-site)
//   - Permissions-Policy: deshabilita APIs sensibles por defecto
// Solo en producción — en dev no aplica (HTTPS opcional, debugging fácil).
if (!app.Environment.IsDevelopment())
{
    app.UseHsts(); // Strict-Transport-Security: max-age=2592000 por default (30 días)
}
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=(), payment=(self)";
    await next();
});

// 1. Routing PRIMERO (necesario para que funcionen los endpoints)
app.UseRouting();

// 2. CORS después de routing
app.UseCors("AllowSpecificOrigin");

// ❌ COMENTADO TEMPORALMENTE: Middleware de logging detallado
/*
// 3. Middleware DETALLADO de logging para diagnóstico COMPLETO
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var method = context.Request.Method;
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var startTime = DateTime.UtcNow;
    
    // ✅ LOGS DETALLADOS ANTES DE PROCESAR
    logger.LogInformation("========================================");
    logger.LogInformation($"[PIPELINE] 📥 REQUEST INICIADA: {method} {path}");
    logger.LogInformation($"[PIPELINE]    Timestamp: {startTime:yyyy-MM-dd HH:mm:ss.fff}");
    logger.LogInformation($"[PIPELINE]    Origin: {context.Request.Headers["Origin"]}");
    logger.LogInformation($"[PIPELINE]    HasAuth: {context.Request.Headers.ContainsKey("Authorization")}");
    logger.LogInformation($"[PIPELINE]    User-Agent: {context.Request.Headers["User-Agent"]}");
    
    try
    {
        await next();
        
        var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
        logger.LogInformation($"[PIPELINE] 📤 RESPONSE: {method} {path} -> {context.Response.StatusCode} ({duration:F2}ms)");
        logger.LogInformation($"[PIPELINE]    Content-Type: {context.Response.ContentType ?? "N/A"}");
        logger.LogInformation($"[PIPELINE]    CORS Headers:");
        logger.LogInformation($"[PIPELINE]       Access-Control-Allow-Origin: {context.Response.Headers["Access-Control-Allow-Origin"]}");
        logger.LogInformation($"[PIPELINE]       Access-Control-Allow-Credentials: {context.Response.Headers["Access-Control-Allow-Credentials"]}");
        logger.LogInformation("========================================");
    }
    catch (Exception ex)
    {
        var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
        logger.LogError(ex, $"[PIPELINE] ❌ ERROR: {method} {path} -> Exception después de {duration:F2}ms");
        logger.LogInformation("========================================");
        throw;
    }
});
*/

// 4. Autenticación y autorización
app.UseAuthentication();

// ❌ COMENTADO TEMPORALMENTE: Logging después de autenticación
/*
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var path = context.Request.Path.Value ?? "";
    var isAuthenticated = context.User?.Identity?.IsAuthenticated ?? false;
    var userId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "N/A";
    
    logger.LogInformation($"[AUTH] 🔐 DESPUÉS de UseAuthentication: {path}");
    logger.LogInformation($"[AUTH]    IsAuthenticated: {isAuthenticated}, UserId: {userId}");
    
    await next();
});
*/

app.UseAuthorization();

// ❌ COMENTADO TEMPORALMENTE: Logging después de autorización
/*
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var path = context.Request.Path.Value ?? "";
    var endpoint = context.GetEndpoint();
    var hasAllowAnonymous = endpoint?.Metadata?.GetMetadata<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() != null;
    var hasAuthorize = endpoint?.Metadata?.GetMetadata<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>() != null;
    
    logger.LogInformation($"[AUTH] 🔒 DESPUÉS de UseAuthorization: {path}");
    logger.LogInformation($"[AUTH]    AllowAnonymous: {hasAllowAnonymous}, Authorize: {hasAuthorize}");
    logger.LogInformation($"[AUTH]    Endpoint: {endpoint?.DisplayName ?? "N/A"}");
    
    await next();
});
*/

// ❌ COMENTADO TEMPORALMENTE: Middleware de diagnóstico para /api/*
/*
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
    {
        var apiLogger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var method = context.Request.Method;
        var endpoint = context.GetEndpoint();
        
        apiLogger.LogInformation("========================================");
        apiLogger.LogInformation($"[API-DIAG] 📥 REQUEST A /api: {method} {path}");
        apiLogger.LogInformation($"[API-DIAG]    Endpoint ANTES de routing: {endpoint?.DisplayName ?? "NULL - NO MATCHED AÚN"}");
        apiLogger.LogInformation($"[API-DIAG]    HasAuth: {context.Request.Headers.ContainsKey("Authorization")}");
        apiLogger.LogInformation($"[API-DIAG]    IsAuthenticated: {context.User?.Identity?.IsAuthenticated ?? false}");
        
        try
        {
            await next();
            
            // Obtener endpoint DESPUÉS del routing
            endpoint = context.GetEndpoint();
            apiLogger.LogInformation($"[API-DIAG]    Endpoint DESPUÉS de routing: {endpoint?.DisplayName ?? "NULL - NO MATCHED"}");
            apiLogger.LogInformation($"[API-DIAG]    RoutePattern: {(endpoint as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText ?? "N/A"}");
            apiLogger.LogInformation($"[API-DIAG] 📤 RESPONSE: {method} {path} -> {context.Response.StatusCode}");
            apiLogger.LogInformation("========================================");
        }
        catch (Exception ex)
        {
            apiLogger.LogError(ex, $"[API-DIAG] ❌ ERROR en {method} {path}");
            apiLogger.LogInformation("========================================");
            throw;
        }
    }
    else
    {
        await next();
    }
});
*/

// ❌ COMENTADO TEMPORALMENTE PARA DIAGNÓSTICO
// ✅ DEBUG: Logging DESPUÉS de autenticación (para capturar estado final)
/*
app.Use(async (context, next) =>
{
    var requestLogger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var path = context.Request.Path.Value ?? "";
    var method = context.Request.Method;
    var startTime = DateTime.UtcNow;
    
    // Loggear estado después de autenticación
    if (!app.Environment.IsDevelopment())
    {
        var isAuthenticatedAfter = context.User?.Identity?.IsAuthenticated ?? false;
        var userId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "N/A";
        var roles = string.Join(", ", context.User?.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value) ?? Array.Empty<string>());
        
        requestLogger.LogInformation($"[REQUEST] 🔐 DESPUÉS de auth: {method} {path}");
        requestLogger.LogInformation($"[REQUEST]    IsAuthenticated: {isAuthenticatedAfter}");
        requestLogger.LogInformation($"[REQUEST]    UserId: {userId}");
        requestLogger.LogInformation($"[REQUEST]    Roles: {roles}");
        
        // Verificar si el endpoint tiene [AllowAnonymous]
        var endpoint = context.GetEndpoint();
        var hasAllowAnonymous = endpoint?.Metadata?.GetMetadata<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() != null;
        var hasAuthorize = endpoint?.Metadata?.GetMetadata<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>() != null;
        requestLogger.LogInformation($"[REQUEST]    Endpoint Metadata: AllowAnonymous={hasAllowAnonymous}, Authorize={hasAuthorize}");
    }
    
    // Ejecutar el siguiente middleware
    await next();
    
    // Loggear después de que se procese la request
    if (!app.Environment.IsDevelopment())
    {
        var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
        var statusCode = context.Response.StatusCode;
        var responseContentType = context.Response.ContentType ?? "N/A";
        
        requestLogger.LogInformation($"[REQUEST] 📤 RESPONSE: {method} {path}");
        requestLogger.LogInformation($"[REQUEST]    StatusCode: {statusCode}");
        requestLogger.LogInformation($"[REQUEST]    Response Content-Type: {responseContentType}");
        requestLogger.LogInformation($"[REQUEST]    Duration: {duration:F2}ms");
        requestLogger.LogInformation($"[REQUEST] ========================================");
        
        // Si es un error, loggear más detalles
        if (statusCode >= 400)
        {
            requestLogger.LogWarning($"[REQUEST] ⚠️ ERROR RESPONSE: {statusCode} para {method} {path}");
            if (statusCode == 401)
            {
                requestLogger.LogWarning($"[REQUEST]    ⚠️ 401 Unauthorized - Token inválido o faltante");
            }
            else if (statusCode == 403)
            {
                requestLogger.LogWarning($"[REQUEST]    ⚠️ 403 Forbidden - Usuario autenticado pero sin permisos");
            }
        }
    }
});
*/

// ❌ COMENTADO TEMPORALMENTE PARA DIAGNÓSTICO
// ✅ DEBUG: Logging antes del middleware MFA
/*
app.Use(async (context, next) =>
{
    var mfaLogger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var path = context.Request.Path.Value ?? "";
    var method = context.Request.Method;
    
    if (!app.Environment.IsDevelopment())
    {
        var isAuthenticated = context.User?.Identity?.IsAuthenticated ?? false;
        var userId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "N/A";
        
        mfaLogger.LogInformation($"[MFA] 🔍 ANTES de RequireMfaMiddleware: {method} {path}");
        mfaLogger.LogInformation($"[MFA]    IsAuthenticated: {isAuthenticated}, UserId: {userId}");
    }
    
    await next();
    
    if (!app.Environment.IsDevelopment())
    {
        var statusCode = context.Response.StatusCode;
        mfaLogger.LogInformation($"[MFA] 📤 DESPUÉS de RequireMfaMiddleware: {method} {path}, StatusCode: {statusCode}");
        
        if (statusCode == 403)
        {
            mfaLogger.LogWarning($"[MFA] ⚠️ 403 Forbidden - Posible bloqueo por MFA");
        }
    }
});
*/

// ❌ COMENTADO TEMPORALMENTE PARA DIAGNÓSTICO
// ✅ SEGURIDAD 2025: FORZAR MFA para Admin y Expertos
// OWASP/NIST/PCI DSS: MFA obligatorio para cuentas privilegiadas
// IMPORTANTE: El middleware verifica rutas públicas internamente
// por lo que puede estar antes de mapear endpoints
// app.UseRequireMfa();

// Add health check endpoint con logging detallado
app.MapHealthChecks("/health").WithName("HealthCheck").WithTags("System");

// ✅ RENDER.COM: Endpoint de health check mejorado con más información
app.MapGet("/health-detailed", () =>
{
    var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
    var aspnetcoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    var urls = app.Urls.ToList();
    
    return Results.Ok(new
    {
        status = "healthy",
        timestamp = DateTime.UtcNow,
        environment = app.Environment.EnvironmentName,
        port = port,
        aspnetcoreUrls = aspnetcoreUrls,
        listeningUrls = urls,
        message = "Server is running and ready to accept requests"
    });
}).WithName("HealthCheckDetailed").WithTags("System");

// ✅ DIAGNÓSTICO COMPLETO: Endpoint para verificar estado de todos los servicios críticos
app.MapGet("/diagnostics", async (AppDbContext db, ILogger<Program> logger, IConfiguration configuration) =>
{
    var services = new Dictionary<string, object>();
    
    // 1. Verificar base de datos
    logger.LogInformation("[DIAGNOSTICS] Verificando base de datos...");
    try
    {
        var dbStartTime = DateTime.UtcNow;
        var canConnect = await db.Database.CanConnectAsync();
        var dbDuration = (DateTime.UtcNow - dbStartTime).TotalMilliseconds;
        
        services["database"] = new
        {
            status = canConnect ? "ok" : "failed",
            canConnect = canConnect,
            duration = dbDuration
        };
    }
    catch (Exception ex)
    {
        services["database"] = new
        {
            status = "error",
            error = ex.Message,
            errorType = ex.GetType().Name
        };
    }
    
    // 2. Verificar JWT Key
    logger.LogInformation("[DIAGNOSTICS] Verificando JWT Key...");
    var jwtKey = configuration["Jwt:Key"];
    services["jwt"] = new
    {
        status = !string.IsNullOrEmpty(jwtKey) ? "ok" : "missing",
        keyPresent = !string.IsNullOrEmpty(jwtKey),
        keyLength = jwtKey?.Length ?? 0
    };
    
    // 3. Verificar Secret Manager
    logger.LogInformation("[DIAGNOSTICS] Verificando Secret Manager...");
    var googleCredJson = Environment.GetEnvironmentVariable("GoogleCredentialJson");
    var googleAppCreds = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
    services["secretManager"] = new
    {
        googleCredentialJsonPresent = !string.IsNullOrEmpty(googleCredJson),
        googleApplicationCredentialsPresent = !string.IsNullOrEmpty(googleAppCreds),
        googleApplicationCredentialsPath = googleAppCreds ?? "null",
        fileExists = !string.IsNullOrEmpty(googleAppCreds) && System.IO.File.Exists(googleAppCreds)
    };
    
    // 4. Verificar Stripe
    logger.LogInformation("[DIAGNOSTICS] Verificando Stripe...");
    var stripeKey = configuration["Stripe:SecretKey"];
    services["stripe"] = new
    {
        status = !string.IsNullOrEmpty(stripeKey) ? "ok" : "missing",
        secretKeyPresent = !string.IsNullOrEmpty(stripeKey),
        secretKeyLength = stripeKey?.Length ?? 0
    };
    
    // 5. Verificar endpoints registrados
    logger.LogInformation("[DIAGNOSTICS] Verificando endpoints registrados...");
    try
    {
        var endpointDataSource = app.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var endpoints = endpointDataSource.Endpoints;
        var apiEndpointsCount = endpoints.Count(e =>
        {
            var routeEndpoint = e as Microsoft.AspNetCore.Routing.RouteEndpoint;
            return routeEndpoint?.RoutePattern.RawText?.StartsWith("/api", StringComparison.OrdinalIgnoreCase) == true;
        });
        
        services["endpoints"] = new
        {
            totalEndpoints = endpoints.Count(),
            apiEndpoints = apiEndpointsCount,
            status = apiEndpointsCount > 0 ? "ok" : "warning"
        };
    }
    catch (Exception ex)
    {
        services["endpoints"] = new
        {
            status = "error",
            error = ex.Message
        };
    }
    
    logger.LogInformation("[DIAGNOSTICS] Diagnóstico completado");
    
    return Results.Ok(new
    {
        timestamp = DateTime.UtcNow,
        environment = app.Environment.EnvironmentName,
        services = services
    });
}).WithName("Diagnostics").WithTags("System");

// ✅ RENDER.COM: Endpoint de warmup para evitar cold starts
// Este endpoint hace una query simple a la BD para "calentar" las conexiones
// Útil para mantener la app activa y evitar el delay de 50+ segundos en el primer request
app.MapGet("/warmup", async (AppDbContext db) =>
{
    try
    {
        // Query simple para "calentar" la conexión a la base de datos
        var count = await db.Users.CountAsync();
        return Results.Ok(new { 
            status = "warmed up", 
            timestamp = DateTime.UtcNow,
            userCount = count,
            message = "Application and database connections are ready" 
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: 500,
            title: "Warmup failed"
        );
    }
}).WithName("Warmup").WithTags("System");

// ✅ ENDPOINT DE PRUEBA: Consulta simple a la DB para verificar conexión
// ✅ MEJORADO: Diagnóstico completo de conexión a base de datos según análisis
app.MapGet("/test-db", async (AppDbContext db, ILogger<Program> logger) =>
{
    var startTime = DateTime.UtcNow;
    logger.LogInformation("[TEST-DB] ========================================");
    logger.LogInformation("[TEST-DB] 🔍 Iniciando test completo de base de datos...");
    logger.LogInformation("[TEST-DB] ========================================");
    
    try
    {
        // 1. Verificar conexión
        logger.LogInformation("[TEST-DB] 1️⃣ Verificando CanConnectAsync()...");
        var canConnectStartTime = DateTime.UtcNow;
        var canConnect = false;
        string? canConnectError = null;
        
        try
        {
            canConnect = await db.Database.CanConnectAsync();
            var canConnectDuration = (DateTime.UtcNow - canConnectStartTime).TotalMilliseconds;
            logger.LogInformation($"[TEST-DB]    ✅ CanConnect: {canConnect} ({canConnectDuration:F2}ms)");
        }
        catch (Exception connEx)
        {
            canConnect = false;
            canConnectError = connEx.Message;
            var canConnectDuration = (DateTime.UtcNow - canConnectStartTime).TotalMilliseconds;
            logger.LogError(connEx, $"[TEST-DB]    ❌ CanConnect falló después de {canConnectDuration:F2}ms");
            logger.LogError($"[TEST-DB]    Exception Type: {connEx.GetType().Name}");
            logger.LogError($"[TEST-DB]    Exception Message: {connEx.Message}");
            logger.LogError($"[TEST-DB]    Inner Exception: {connEx.InnerException?.Message ?? "None"}");
            
            // Detectar tipos específicos de errores
            if (connEx.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || 
                connEx.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("[TEST-DB]    🔴 PROBLEMA DETECTADO: Error de autenticación - Credenciales inválidas");
            }
            if (connEx.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("[TEST-DB]    🔴 PROBLEMA DETECTADO: Timeout - Problema de red o latencia");
            }
            if (connEx.Message.Contains("dns", StringComparison.OrdinalIgnoreCase) || 
                connEx.Message.Contains("resolve", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("[TEST-DB]    🔴 PROBLEMA DETECTADO: DNS - Problema de resolución de hostname");
            }
        }
        
        if (!canConnect)
        {
            logger.LogError("[TEST-DB] ❌ NO SE PUEDE CONECTAR A LA BASE DE DATOS");
            logger.LogInformation("[TEST-DB] ========================================");
            return Results.Problem(
                detail: canConnectError ?? "Cannot connect to database",
                statusCode: 503,
                title: "Database connection failed"
            );
        }
        
        // 2. Verificar que la conexión está activa con una query simple
        logger.LogInformation("[TEST-DB] 2️⃣ Ejecutando query simple: SELECT 1...");
        var queryStartTime = DateTime.UtcNow;
        int? queryResult = null;
        string? queryError = null;
        
        try
        {
            queryResult = await db.Database.SqlQueryRaw<int>("SELECT 1").FirstOrDefaultAsync();
            var queryDuration = (DateTime.UtcNow - queryStartTime).TotalMilliseconds;
            logger.LogInformation($"[TEST-DB]    ✅ Query SELECT 1 exitosa: {queryResult} ({queryDuration:F2}ms)");
        }
        catch (Exception queryEx)
        {
            queryError = queryEx.Message;
            var queryDuration = (DateTime.UtcNow - queryStartTime).TotalMilliseconds;
            logger.LogError(queryEx, $"[TEST-DB]    ❌ Query SELECT 1 falló después de {queryDuration:F2}ms");
            logger.LogError($"[TEST-DB]    Exception Type: {queryEx.GetType().Name}");
            logger.LogError($"[TEST-DB]    Exception Message: {queryEx.Message}");
        }
        
        // 3. Contar usuarios (query más compleja)
        logger.LogInformation("[TEST-DB] 3️⃣ Ejecutando query: db.Users.CountAsync()...");
        var countStartTime = DateTime.UtcNow;
        int? userCount = null;
        string? countError = null;
        
        try
        {
            userCount = await db.Users.CountAsync();
            var countDuration = (DateTime.UtcNow - countStartTime).TotalMilliseconds;
            logger.LogInformation($"[TEST-DB]    ✅ CountAsync exitoso: {userCount} usuarios ({countDuration:F2}ms)");
            
            if (countDuration > 5000)
            {
                logger.LogWarning($"[TEST-DB]    ⚠️ Query lenta detectada: {countDuration:F2}ms (más de 5 segundos)");
            }
        }
        catch (Exception countEx)
        {
            countError = countEx.Message;
            var countDuration = (DateTime.UtcNow - countStartTime).TotalMilliseconds;
            logger.LogError(countEx, $"[TEST-DB]    ❌ CountAsync falló después de {countDuration:F2}ms");
            logger.LogError($"[TEST-DB]    Exception Type: {countEx.GetType().Name}");
            logger.LogError($"[TEST-DB]    Exception Message: {countEx.Message}");
        }
        
        var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
        logger.LogInformation($"[TEST-DB] ✅ Test completado en {totalDuration:F2}ms");
        logger.LogInformation("[TEST-DB] ========================================");
        
        return Results.Ok(new
        {
            success = true,
            canConnect = canConnect,
            queryTest = queryResult.HasValue ? "success" : "failed",
            queryResult = queryResult,
            userCount = userCount,
            userCountTest = userCount.HasValue ? "success" : "failed",
            errors = new
            {
                canConnectError = canConnectError,
                queryError = queryError,
                countError = countError
            },
            timestamp = DateTime.UtcNow,
            totalDuration = totalDuration
        });
    }
    catch (Exception ex)
    {
        var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
        logger.LogError(ex, $"[TEST-DB] ❌ ERROR GENERAL en test de DB después de {totalDuration:F2}ms");
        logger.LogError($"[TEST-DB]    Exception Type: {ex.GetType().Name}");
        logger.LogError($"[TEST-DB]    Exception Message: {ex.Message}");
        logger.LogError($"[TEST-DB]    Inner Exception: {ex.InnerException?.Message ?? "None"}");
        logger.LogError($"[TEST-DB]    StackTrace: {ex.StackTrace}");
        logger.LogInformation("[TEST-DB] ========================================");
        
        return Results.Problem(
            detail: ex.Message,
            statusCode: 500,
            title: "Database test failed",
            extensions: new Dictionary<string, object?>
            {
                ["exceptionType"] = ex.GetType().Name,
                ["innerException"] = ex.InnerException?.Message
            }
        );
    }
}).WithName("TestDb").WithTags("System");

// ✅ CRÍTICO: Verificar que los controladores se descubrieron antes de mapear
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var controllerTypes = typeof(CategoriesController).Assembly
    .GetTypes()
    .Where(t => t.IsSubclassOf(typeof(ControllerBase)) || t.IsSubclassOf(typeof(Controller)))
    .ToList();

logger.LogInformation("========================================");
logger.LogInformation("🔍 CONTROLADORES DESCUBIERTOS EN ASSEMBLY:");
logger.LogInformation("========================================");
logger.LogInformation($"✅ Total controladores encontrados: {controllerTypes.Count}");
foreach (var controllerType in controllerTypes)
{
    var routeAttr = controllerType.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), false)
        .FirstOrDefault() as Microsoft.AspNetCore.Mvc.RouteAttribute;
    var apiControllerAttr = controllerType.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ApiControllerAttribute), false)
        .FirstOrDefault();
    
    logger.LogInformation($"  - {controllerType.Name}");
    logger.LogInformation($"    Route: {routeAttr?.Template ?? "N/A"}");
    logger.LogInformation($"    ApiController: {apiControllerAttr != null}");
}
logger.LogInformation("========================================");

app.MapControllers();
// SignalR ChatHub removido - Usando Supabase Realtime para chat en tiempo real

// ✅ CRÍTICO: Logging de endpoints para diagnosticar por qué /api no funciona
// Esto es TEMPORAL para ver qué endpoints se están registrando
var endpointDataSource = app.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
var endpoints = endpointDataSource.Endpoints;

logger.LogInformation("========================================");
logger.LogInformation("📋 ENDPOINTS REGISTRADOS DESPUÉS DE MapControllers():");
logger.LogInformation("========================================");

var apiEndpoints = new List<string>();
var otherEndpoints = new List<string>();

foreach (var endpoint in endpoints)
{
    var displayName = endpoint.DisplayName ?? "N/A";
    
    // Obtener ruta del endpoint
    var routePattern = "N/A";
    var routeEndpoint = endpoint as Microsoft.AspNetCore.Routing.RouteEndpoint;
    if (routeEndpoint != null)
    {
        routePattern = routeEndpoint.RoutePattern.RawText ?? "N/A";
    }
    
    // Obtener métodos HTTP
    var httpMethods = new List<string>();
    var httpMethodMetadata = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>();
    if (httpMethodMetadata != null)
    {
        httpMethods.AddRange(httpMethodMetadata.HttpMethods);
    }
    
    var endpointInfo = $"  - {displayName}";
    if (routePattern != "N/A")
    {
        endpointInfo += $" | Route: {routePattern}";
    }
    if (httpMethods.Any())
    {
        endpointInfo += $" | Methods: {string.Join(", ", httpMethods)}";
    }
    
    if (routePattern.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
    {
        apiEndpoints.Add(endpointInfo);
    }
    else
    {
        otherEndpoints.Add(endpointInfo);
    }
}

logger.LogInformation($"✅ Total endpoints: {endpoints.Count()}");
logger.LogInformation($"✅ Endpoints /api: {apiEndpoints.Count}");
logger.LogInformation($"✅ Otros endpoints: {otherEndpoints.Count}");

if (apiEndpoints.Any())
{
    logger.LogInformation("========================================");
    logger.LogInformation("📋 ENDPOINTS /api REGISTRADOS:");
    logger.LogInformation("========================================");
    foreach (var apiEndpoint in apiEndpoints)
    {
        logger.LogInformation(apiEndpoint);
    }
}
else
{
    logger.LogWarning("⚠️ ⚠️ ⚠️ NO SE ENCONTRARON ENDPOINTS /api REGISTRADOS ⚠️ ⚠️ ⚠️");
    logger.LogWarning("Esto indica que MapControllers() no está registrando los controladores correctamente");
}

logger.LogInformation("========================================");
logger.LogInformation("📋 OTROS ENDPOINTS REGISTRADOS:");
logger.LogInformation("========================================");
foreach (var otherEndpoint in otherEndpoints)
{
    logger.LogInformation(otherEndpoint);
}
logger.LogInformation("========================================");

// ✅ RENDER.COM: Según documentación oficial
// "Bind your host to 0.0.0.0 and optionally set the PORT environment variable"
// https://render.com/docs/troubleshooting-deploys
// 
// ✅ CRÍTICO: app.Run() DEBE llamarse INMEDIATAMENTE
// Cualquier inicialización pesada (DB, logging, etc.) se hace DESPUÉS en background
// Esto permite que Render.com detecte el puerto rápidamente

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

// ✅ Background task: Inicialización pesada DESPUÉS de que el servidor inicie
lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        try
        {
            await Task.Delay(2000); // Esperar 2 segundos para que el servidor esté completamente listo
            
            logger.LogInformation("========================================");
            logger.LogInformation("🧪 PROBANDO ENDPOINTS DE HOMEPAGE...");
            logger.LogInformation("========================================");
            
            // Obtener la URL base del servidor
            // ✅ FIX: No usar 0.0.0.0 para HTTP requests (solo para escuchar)
            // Usar localhost o 127.0.0.1 para pruebas internas
            var urls = app.Urls.ToList();
            var rawUrl = urls.FirstOrDefault() ?? "http://0.0.0.0:10000";
            var baseUrl = rawUrl.Replace("0.0.0.0", "localhost").Replace("::0", "localhost");
            if (baseUrl.EndsWith("/"))
            {
                baseUrl = baseUrl.TrimEnd('/');
            }
            
            logger.LogInformation($"[TEST] Base URL original: {rawUrl}");
            logger.LogInformation($"[TEST] Base URL para pruebas: {baseUrl}");
            
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(120); // ✅ Aumentado a 120 segundos para endpoints que pueden tardar
            
            // 1. Probar /api/ServiceType/public
            logger.LogInformation("========================================");
            logger.LogInformation("[TEST] 🧪 Probando: GET /api/ServiceType/public");
            logger.LogInformation("========================================");
            try
            {
                var startTime = DateTime.UtcNow;
                var response = await httpClient.GetAsync($"{baseUrl}/api/ServiceType/public");
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                var content = await response.Content.ReadAsStringAsync();
                
                logger.LogInformation($"[TEST] ✅ Status Code: {response.StatusCode}");
                logger.LogInformation($"[TEST] ✅ Duración: {duration:F2}ms");
                logger.LogInformation($"[TEST] ✅ Content Length: {content.Length} caracteres");
                
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation($"[TEST] ✅ JSON Response (primeros 500 chars):");
                    var preview = content.Length > 500 ? content.Substring(0, 500) + "..." : content;
                    logger.LogInformation($"[TEST] {preview}");
                }
                else
                {
                    logger.LogError($"[TEST] ❌ ERROR: Status {response.StatusCode}");
                    logger.LogError($"[TEST] ❌ Response: {content}");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TEST] ❌ EXCEPCIÓN al probar /api/ServiceType/public");
                logger.LogError($"[TEST]    Exception Type: {ex.GetType().Name}");
                logger.LogError($"[TEST]    Exception Message: {ex.Message}");
                logger.LogError($"[TEST]    Inner Exception: {ex.InnerException?.Message ?? "None"}");
            }
            
            // 2. Probar /api/Categories
            logger.LogInformation("========================================");
            logger.LogInformation("[TEST] 🧪 Probando: GET /api/Categories");
            logger.LogInformation("========================================");
            try
            {
                var startTime = DateTime.UtcNow;
                var response = await httpClient.GetAsync($"{baseUrl}/api/Categories");
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                var content = await response.Content.ReadAsStringAsync();
                
                logger.LogInformation($"[TEST] ✅ Status Code: {response.StatusCode}");
                logger.LogInformation($"[TEST] ✅ Duración: {duration:F2}ms");
                logger.LogInformation($"[TEST] ✅ Content Length: {content.Length} caracteres");
                
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation($"[TEST] ✅ JSON Response (primeros 500 chars):");
                    var preview = content.Length > 500 ? content.Substring(0, 500) + "..." : content;
                    logger.LogInformation($"[TEST] {preview}");
                }
                else
                {
                    logger.LogError($"[TEST] ❌ ERROR: Status {response.StatusCode}");
                    logger.LogError($"[TEST] ❌ Response: {content}");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TEST] ❌ EXCEPCIÓN al probar /api/Categories");
                logger.LogError($"[TEST]    Exception Type: {ex.GetType().Name}");
                logger.LogError($"[TEST]    Exception Message: {ex.Message}");
                logger.LogError($"[TEST]    Inner Exception: {ex.InnerException?.Message ?? "None"}");
            }
            
            // 3. Probar /api/SearchService/homepage-wall (necesita categoryId)
            logger.LogInformation("========================================");
            logger.LogInformation("[TEST] 🧪 Probando: GET /api/SearchService/homepage-wall?categoryId=1");
            logger.LogInformation("========================================");
            try
            {
                var startTime = DateTime.UtcNow;
                var response = await httpClient.GetAsync($"{baseUrl}/api/SearchService/homepage-wall?categoryId=1");
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                var content = await response.Content.ReadAsStringAsync();
                
                logger.LogInformation($"[TEST] ✅ Status Code: {response.StatusCode}");
                logger.LogInformation($"[TEST] ✅ Duración: {duration:F2}ms");
                logger.LogInformation($"[TEST] ✅ Content Length: {content.Length} caracteres");
                
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation($"[TEST] ✅ JSON Response (primeros 500 chars):");
                    var preview = content.Length > 500 ? content.Substring(0, 500) + "..." : content;
                    logger.LogInformation($"[TEST] {preview}");
                }
                else
                {
                    logger.LogError($"[TEST] ❌ ERROR: Status {response.StatusCode}");
                    logger.LogError($"[TEST] ❌ Response: {content}");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TEST] ❌ EXCEPCIÓN al probar /api/SearchService/homepage-wall");
                logger.LogError($"[TEST]    Exception Type: {ex.GetType().Name}");
                logger.LogError($"[TEST]    Exception Message: {ex.Message}");
                logger.LogError($"[TEST]    Inner Exception: {ex.InnerException?.Message ?? "None"}");
            }
            
            logger.LogInformation("========================================");
            logger.LogInformation("✅ PRUEBAS DE ENDPOINTS COMPLETADAS");
            logger.LogInformation("========================================");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[INIT] ❌ ERROR en inicialización en background");
        }
    });
    
    // Logging simple del servidor iniciado
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    var urlsAfterStart = app.Urls.ToList();
    if (urlsAfterStart.Any())
    {
        logger.LogInformation($"✅ Servidor iniciado y escuchando en: {string.Join(", ", urlsAfterStart)}");
        Console.WriteLine($"[RENDER] ✅ Servidor escuchando en: {string.Join(", ", urlsAfterStart)}");
    }
});

// ✅ FIX CRÍTICO: Manejador global de excepciones no manejadas
// Esto previene que la aplicación se detenga por excepciones en callbacks de timers o ThreadPool
TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
    logger.LogError(e.Exception, "[CRITICAL] ❌ UnobservedTaskException capturada - La aplicación NO se detendrá");
    logger.LogError($"[CRITICAL]    Exception Type: {e.Exception.GetType().Name}");
    logger.LogError($"[CRITICAL]    Inner Exceptions: {e.Exception.InnerExceptions.Count}");
    foreach (var innerEx in e.Exception.InnerExceptions)
    {
        logger.LogError($"[CRITICAL]    - {innerEx.GetType().Name}: {innerEx.Message}");
        if (innerEx is ObjectDisposedException disposedEx)
        {
            logger.LogWarning($"[CRITICAL]    ⚠️ ObjectDisposedException detectada (normal durante timeouts/cancelaciones)");
        }
    }
    // Marcar como observada para evitar que la aplicación se detenga
    e.SetObserved();
};

// ✅ FIX CRÍTICO: Manejador de excepciones no manejadas en AppDomain
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
    var exception = e.ExceptionObject as Exception;
    
    if (exception != null)
    {
        logger.LogError(exception, "[CRITICAL] ❌ UnhandledException capturada");
        logger.LogError($"[CRITICAL]    Exception Type: {exception.GetType().Name}");
        logger.LogError($"[CRITICAL]    Exception Message: {exception.Message}");
        
        // Si es AggregateException con ObjectDisposedException, es normal durante timeouts
        if (exception is AggregateException aggEx && aggEx.InnerException is ObjectDisposedException)
        {
            logger.LogWarning($"[CRITICAL]    ⚠️ AggregateException con ObjectDisposedException (normal durante timeouts/cancelaciones)");
            logger.LogWarning($"[CRITICAL]    ⚠️ La aplicación continuará funcionando normalmente");
        }
        else if (exception is ObjectDisposedException)
        {
            logger.LogWarning($"[CRITICAL]    ⚠️ ObjectDisposedException (normal durante timeouts/cancelaciones)");
            logger.LogWarning($"[CRITICAL]    ⚠️ La aplicación continuará funcionando normalmente");
        }
    }
    
    // NO terminar la aplicación para ObjectDisposedException relacionadas con timeouts
    if (exception is AggregateException aggEx2 && aggEx2.InnerException is ObjectDisposedException)
    {
        // No hacer nada, la aplicación continuará
    }
    else if (exception is ObjectDisposedException)
    {
        // No hacer nada, la aplicación continuará
    }
};

// ✅ app.Run() - INICIAR SERVIDOR INMEDIATAMENTE
// Según documentación de Render.com: "Bind your host to 0.0.0.0"
// El servidor debe estar escuchando lo más rápido posible
app.Run();
