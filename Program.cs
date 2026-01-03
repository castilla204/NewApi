using Google.Cloud.SecretManager.V1;
using Google.Api.Gax.Grpc;
using Grpc.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
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
using newApi.Middleware;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Configurar logging básico
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Configurar zona horaria de España
TimeZoneInfo spainTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture("es-ES");
    options.SupportedCultures = new[] { new System.Globalization.CultureInfo("es-ES") };
    options.SupportedUICultures = new[] { new System.Globalization.CultureInfo("es-ES") };
});

// Verificar el entorno PRIMERO
var isDevelopment = builder.Environment.IsDevelopment();

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
    credentialsJson = Environment.GetEnvironmentVariable("GoogleCredentialJson");
    
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

if (shouldInitialize)
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

// Función para obtener secretos
// En desarrollo: intenta secretos con sufijo -dev, luego sin sufijo
// En producción: usa secretos sin sufijo
string? GetSecretValue(string secretName, string? defaultValue = null)
{
    // Intentar usar Secret Manager si está disponible (tanto en desarrollo como producción)
    if (secretClient != null && secretManagerAvailable)
    {
        var projectId = "grup-441318";  // ✅ Project ID correcto
        var secretLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
                secretLogger.LogInformation($"📦 Usando Project ID: {projectId}");
        
        // Determinar qué nombres de secretos intentar según el entorno
        var secretNamesToTry = new List<string>();
        
        if (isDevelopment)
        {
            // En desarrollo: intentar primero con -dev, luego sin sufijo
            secretNamesToTry.Add($"{secretName}-dev");
            secretNamesToTry.Add(secretName);
            secretLogger.LogInformation($"🔧 DESARROLLO: Intentando secretos: {string.Join(" -> ", secretNamesToTry)}");
        }
        else
        {
            // En producción: usar directamente el nombre sin sufijo
            secretNamesToTry.Add(secretName);
            secretLogger.LogInformation($"🏭 PRODUCCIÓN: Usando secreto: {secretName}");
        }
        
        // Intentar cada nombre de secreto en orden
        foreach (var secretNameToTry in secretNamesToTry)
        {
            try
            {
                var secretPath = $"projects/{projectId}/secrets/{secretNameToTry}/versions/latest";
                secretLogger.LogInformation($"Intentando obtener secreto: {secretNameToTry} desde {secretPath}");
                
                // Configurar call settings con timeout y reintentos mejorados
                var callSettings = CallSettings.FromRetry(
                    RetrySettings.FromExponentialBackoff(
                        maxAttempts: 3,
                        initialBackoff: TimeSpan.FromSeconds(5),
                        maxBackoff: TimeSpan.FromSeconds(20),
                        backoffMultiplier: 2.0,
                        retryFilter: RetrySettings.FilterForStatusCodes(
                            Grpc.Core.StatusCode.Unavailable, 
                            Grpc.Core.StatusCode.DeadlineExceeded,
                            Grpc.Core.StatusCode.Internal,
                            Grpc.Core.StatusCode.ResourceExhausted
                        )
                    )
                ).WithTimeout(TimeSpan.FromSeconds(60));
                
                var startTime = DateTime.UtcNow;
                var secretVersion = secretClient.AccessSecretVersion(secretPath, callSettings: callSettings);
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                
                var secretValue = secretVersion.Payload.Data.ToStringUtf8();
                secretLogger.LogInformation($"✅ Secreto {secretNameToTry} obtenido exitosamente en {duration}ms - Valor COMPLETO: [{secretValue}] (longitud: {secretValue.Length})");
                return secretValue;
            }
            catch (Grpc.Core.RpcException rpcEx)
            {
                // Si el secreto no existe (NotFound), intentar el siguiente
                if (rpcEx.StatusCode == Grpc.Core.StatusCode.NotFound)
                {
                    secretLogger.LogWarning($"⚠️ Secreto {secretNameToTry} no encontrado, intentando siguiente...");
                    continue; // Intentar siguiente nombre
                }
                
                // Para otros errores, marcar como no disponible y retornar
                if (secretManagerAvailable)
                {
                    secretManagerAvailable = false;
                    secretLogger.LogError($"ERROR gRPC al obtener secreto {secretNameToTry}:");
                    secretLogger.LogError($"  Status Code: {rpcEx.StatusCode}");
                    secretLogger.LogError($"  Status Detail: {rpcEx.Status.Detail}");
                    secretLogger.LogWarning("Secret Manager no está disponible. Usando solo variables de entorno.");
                }
                return defaultValue;
            }
            catch (Exception ex)
            {
                // Para errores inesperados, marcar como no disponible
                if (secretManagerAvailable)
                {
                    secretManagerAvailable = false;
                    secretLogger.LogError($"ERROR inesperado al obtener secreto {secretNameToTry}: {ex.GetType().Name} - {ex.Message}");
                    secretLogger.LogWarning("Secret Manager no está disponible. Usando solo variables de entorno.");
                }
                return defaultValue;
            }
        }
        
        // Si llegamos aquí, ningún secreto fue encontrado
        secretLogger.LogWarning($"⚠️ Ningún secreto encontrado para: {secretName} (intentados: {string.Join(", ", secretNamesToTry)})");
        return defaultValue;
    }
    
    // Si Secret Manager no está disponible, usar valor por defecto
    return defaultValue;
}

// Cargar secretos de Google Cloud Secret Manager
var googleClientIdsSecret = GetSecretValue("google-client-ids", null);
var initLogger2 = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");

if (!string.IsNullOrEmpty(googleClientIdsSecret))
{
    initLogger2.LogInformation($"✅ Secreto google-client-ids obtenido (longitud: {googleClientIdsSecret.Length})");
    initLogger2.LogInformation($"📋 Contenido completo del secreto: [{googleClientIdsSecret}]");
    
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
// ✅ SIMPLIFICADO: Conectar directamente a Supabase tanto en desarrollo como en producción
// Usar la misma conexión a Supabase configurada en appsettings.Development.json
string connectionString;

// Obtener connection string desde configuración (appsettings.Development.json o appsettings.json)
var existingConnectionString = builder.Configuration.GetConnectionString("PostgresConnection");

if (string.IsNullOrEmpty(existingConnectionString))
{
    // Si no hay connection string en configuración, intentar construir desde Secret Manager (solo producción)
    if (!isDevelopment)
    {
        var dbHost = GetSecretValue("postgres-host", null);
        var dbPort = GetSecretValue("postgres-port", null) ?? "5432";
        var dbUsername = GetSecretValue("postgres-username", null);
        var dbPassword = GetSecretValue("postgres-password", null);
        var dbName = GetSecretValue("postgres-database", null) ?? "postgres";
        
        if (string.IsNullOrEmpty(dbHost) || string.IsNullOrEmpty(dbUsername) || string.IsNullOrEmpty(dbPassword))
        {
            throw new InvalidOperationException(
                "Database connection string not configured. " +
                "Set 'PostgresConnection' in appsettings.json or configure via Secret Manager " +
                "(postgres-host, postgres-username, postgres-password, postgres-database).");
        }
        
        connectionString = $"Host={dbHost};Port={dbPort};Username={dbUsername};Password={dbPassword};Database={dbName};" +
                          $"SslMode=Require;Timeout=30;CommandTimeout=60;Pooling=true;";
        
        configLogger.LogInformation($"✅ Connection string construido desde Secret Manager: Host={dbHost}, Port={dbPort}, Database={dbName}, Username={dbUsername}");
    }
    else
    {
        throw new InvalidOperationException(
            "Database connection string not configured. " +
            "Set 'PostgresConnection' in appsettings.Development.json with your Supabase connection string.");
    }
}
else
{
    // Usar connection string desde configuración (appsettings.Development.json o appsettings.json)
    connectionString = existingConnectionString;
    
    // Extraer información para logging (sin mostrar contraseña)
    var hostMatch = Regex.Match(connectionString, @"Host=([^;]+)");
    var portMatch = Regex.Match(connectionString, @"Port=([^;]+)");
    var userMatch = Regex.Match(connectionString, @"Username=([^;]+)");
    var dbMatch = Regex.Match(connectionString, @"Database=([^;]+)");
    
    var dbHost = hostMatch.Success ? hostMatch.Groups[1].Value : "unknown";
    var dbPort = portMatch.Success ? portMatch.Groups[1].Value : "unknown";
    var dbUsername = userMatch.Success ? userMatch.Groups[1].Value : "unknown";
    var dbName = dbMatch.Success ? dbMatch.Groups[1].Value : "unknown";
    
    configLogger.LogInformation($"✅ Connection string desde configuración: Host={dbHost}, Port={dbPort}, Database={dbName}, Username={dbUsername}");
    configLogger.LogInformation($"   Entorno: {(isDevelopment ? "Development" : "Production")}");
}


builder.Configuration["ConnectionStrings:PostgresConnection"] = connectionString;

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
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // ✅ CORRECCIÓN: Configurar JSON para evitar referencias circulares
        // ReferenceHandler.IgnoreCycles está disponible desde .NET 6
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.WriteIndented = false;
        // ✅ MEJORA: Configurar para mejor rendimiento
        options.JsonSerializerOptions.PropertyNamingPolicy = null; // Mantener nombres de propiedades como están
    });

// Configure request size limits for file uploads
builder.Services.Configure<IISServerOptions>(options =>
{
    options.MaxRequestBodySize = 50 * 1024 * 1024; // 50MB
});

builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = 50 * 1024 * 1024; // 50MB
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
// Note: Swagger security definition removed due to Microsoft.OpenApi.Models namespace issues
// Swagger will still work for testing endpoints

// ✅ OPTIMIZACIÓN: Habilitar compresión HTTP (reduce tamaño 60-80%)
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/json", "application/json; charset=utf-8" });
});

builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});

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

// ✅ MEJORAS 2025: Configure SignalR con mejores prácticas
// - Timeouts optimizados para conexiones estables
// - KeepAlive mejorado para detectar desconexiones rápidamente
// - Protocolos optimizados para mejor rendimiento
// - Soporte para reconexión automática mejorada
var signalRBuilder = builder.Services.AddSignalR(options =>
{
    // ✅ MEJORA 2025: Habilitar errores detallados solo en desarrollo
    options.EnableDetailedErrors = isDevelopment;
    
    // ✅ MEJORA 2025: KeepAlive optimizado - enviar ping cada 15 segundos
    // Esto ayuda a detectar conexiones muertas más rápido
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    
    // ✅ MEJORA 2025: Timeout de cliente aumentado a 60 segundos
    // Permite más tiempo para reconexión automática antes de marcar como desconectado
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    
    // ✅ MEJORA 2025: MaximumReceiveMessageSize aumentado para archivos grandes
    // Permite mensajes más grandes (útil para metadata de archivos)
    options.MaximumReceiveMessageSize = 32 * 1024; // 32KB
    
    // ✅ MEJORA 2025: MaximumParallelInvocationsPerClient
    // Limita invocaciones paralelas por cliente para evitar sobrecarga
    options.MaximumParallelInvocationsPerClient = 5;
    
    // ✅ MEJORA 2025: StreamBufferCapacity para streaming (si se usa en el futuro)
    options.StreamBufferCapacity = 10;
})
.AddJsonProtocol(options =>
{
    // ✅ MEJORA 2025: Mantener naming policy null para compatibilidad con frontend
    options.PayloadSerializerOptions.PropertyNamingPolicy = null;
    
    // ✅ MEJORA 2025: Configurar opciones de serialización para mejor rendimiento
    options.PayloadSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    options.PayloadSerializerOptions.WriteIndented = false; // No indentar en producción
});

// ✅ ESCALABILIDAD: Configurar Redis como backplane para SignalR
// Esto permite que los mensajes se compartan entre múltiples instancias del servidor
// Solo en producción (en desarrollo no es necesario)
if (!isDevelopment)
{
    var redisConnectionString = GetSecretValue("redis-connection-string", null);
    
    if (!string.IsNullOrEmpty(redisConnectionString))
    {
        var signalRLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
        signalRLogger.LogInformation("Configurando Redis como backplane para SignalR...");
        signalRLogger.LogInformation($"Redis Connection String: {redisConnectionString.Substring(0, Math.Min(20, redisConnectionString.Length))}***");
        
        signalRBuilder.AddStackExchangeRedis(redisConnectionString, redisOptions =>
        {
            redisOptions.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("SignalR");
            redisOptions.Configuration.DefaultDatabase = 0;
        });
        
        signalRLogger.LogInformation("✅ Redis backplane configurado para SignalR");
    }
    else
    {
        var signalRLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
        signalRLogger.LogWarning("⚠️ Redis no configurado para SignalR. Los mensajes NO se compartirán entre instancias.");
        signalRLogger.LogWarning("   Para escalabilidad, configura REDIS_CONNECTION_STRING o 'redis-connection-string' en Secret Manager.");
    }
}

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
        ClockSkew = TimeSpan.Zero
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/chatHub"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});

// Configure PostgreSQL
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("PostgresConnection");
    // Configurar Npgsql para Session Pooler de Supabase
    var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
    // Deshabilitar prepared statements para Session Pooler
    dataSourceBuilder.EnableParameterLogging();
    var dataSource = dataSourceBuilder.Build();
    
    options.UseNpgsql(dataSource, npgsqlOptions =>
    {
        npgsqlOptions.CommandTimeout(60); // Aumentado a 60 segundos para conexiones lentas
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5, // Aumentado de 3 a 5 reintentos
            maxRetryDelay: TimeSpan.FromSeconds(10), // Aumentado delay máximo
            errorCodesToAdd: null);
    });
});

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

builder.Services.AddSingleton<ISignedUrlService, GoogleSignedUrlService>();


// Configure CORS

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSpecificOrigin", builder =>
    {
        builder.WithOrigins(
            "http://localhost:3000",
            "http://localhost:5173",
            "https://inspecciono.com",
            "https://www.inspecciono.com") // <--- agregar dominio de frontend producci�n
               .AllowAnyMethod()
               .AllowAnyHeader()
               .AllowCredentials()
               .SetPreflightMaxAge(TimeSpan.FromSeconds(600));
    });
});


// ✅ HANGFIRE HABILITADO: Dashboard habilitado para visualización
// ⚠️ NOTA: El servidor de Hangfire está deshabilitado para evitar problemas de recursos en K3s
// Solo se habilita el Dashboard para visualización y monitoreo
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(builder.Configuration.GetConnectionString("PostgresConnection"), new PostgreSqlStorageOptions
    {
        QueuePollInterval = TimeSpan.FromSeconds(15),
        InvisibilityTimeout = TimeSpan.FromMinutes(30),
        DistributedLockTimeout = TimeSpan.FromMinutes(10),
        PrepareSchemaIfNecessary = true
    })
    .UseDefaultTypeResolver()
    .UseDefaultTypeSerializer());

// ✅ HABILITADO: Servidor de Hangfire para procesar jobs automáticamente
// Los jobs de timers de appointments requieren que el servidor esté activo
builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 2; // Número de workers que procesan jobs simultáneamente
    options.ServerTimeout = TimeSpan.FromMinutes(5);
    options.HeartbeatInterval = TimeSpan.FromSeconds(30);
    options.ServerCheckInterval = TimeSpan.FromMinutes(1);
    options.SchedulePollingInterval = TimeSpan.FromSeconds(30); // Verifica jobs programados cada 30 segundos
});

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
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ILoggingService, LoggingService>();
builder.Services.AddScoped<IInvoiceService, newApi.Services.InvoiceService>();
builder.Services.AddScoped<IStripeValidationService, StripeValidationService>();
builder.Services.AddScoped<RefreshTokenCleanupService>(); // ✅ SEGURIDAD 2025: Limpieza de refresh tokens
builder.Services.AddScoped<MfaService>(); // ✅ SEGURIDAD 2025: Autenticación Multifactor (MFA/2FA)
builder.Services.AddScoped<ITimezoneService, TimezoneService>(); // ✅ Servicio para detección de timezone y country desde coordenadas

// Background services - AppointmentTimerBackgroundService migrated to Hangfire

builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<SearchServiceService>();
builder.Services.AddScoped<SearchHireService>();

builder.Services.AddHttpClient();

// Add Health Checks
builder.Services.AddHealthChecks();

var app = builder.Build();

// ✅ Cargar claves Stripe según el modo configurado en SystemSetting
try
{
    using (var scope = app.Services.CreateScope())
    {
        var stripeConfigService = scope.ServiceProvider.GetRequiredService<IStripeConfigService>();
        var mode = await stripeConfigService.GetStripeModeAsync();
        var (secretKey, webhookSecret, generalWebhookSecret) = await stripeConfigService.GetStripeKeysForModeAsync(
            mode, 
            GetSecretValue);
        
        builder.Configuration["Stripe:SecretKey"] = secretKey;
        builder.Configuration["Stripe:WebhookSecret"] = webhookSecret;
        builder.Configuration["Stripe:GeneralWebhookSecret"] = generalWebhookSecret;
        
        StripeConfiguration.ApiKey = secretKey;
        
        var stripeLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        stripeLogger.LogInformation($"✅ Claves Stripe cargadas en modo: {mode}");
        stripeLogger.LogInformation($"   SecretKey presente: {!string.IsNullOrEmpty(secretKey)}");
        stripeLogger.LogInformation($"   WebhookSecret presente: {!string.IsNullOrEmpty(webhookSecret)}");
    }
}
catch (Exception ex)
{
    var stripeLogger = app.Services.GetRequiredService<ILogger<Program>>();
    stripeLogger.LogError(ex, "Error cargando claves Stripe según modo, usando configuración por defecto");
}

// Configure Stripe - se configurará dinámicamente según el modo en SystemSetting
// La configuración inicial se hace después de inicializar la base de datos (ver más abajo)

// 🚨 LOG CRÍTICO: Configuración de Stripe
var logger = app.Services.GetRequiredService<ILogger<Program>>();

try
{
    if (string.IsNullOrEmpty(builder.Configuration["Stripe:SecretKey"]))
    {
        logger.LogError("Stripe SecretKey not found in configuration");
        
        // Usar scope para ILoggingService
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
                    SecretKeyPresent = !string.IsNullOrEmpty(builder.Configuration["Stripe:SecretKey"]),
                    PublishableKeyPresent = !string.IsNullOrEmpty(builder.Configuration["Stripe:PublishableKey"])
                }
            );
        }
    }
    else
    {
        logger.LogInformation("Stripe configuration successful");
        
        // ✅ Log informativo del sistema - NO crear notificaciones para logs de configuración
        // Usar scope para ILoggingService
        using (var scope = app.Services.CreateScope())
        {
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
                    SecretKeyPresent = !string.IsNullOrEmpty(builder.Configuration["Stripe:SecretKey"]),
                    PublishableKeyPresent = !string.IsNullOrEmpty(builder.Configuration["Stripe:PublishableKey"]),
                    Success = true
                },
                notifyUser: false // ✅ NO notificar a usuarios - es solo un log de sistema
            );
        }
    }
}
catch (Exception ex)
{
    logger.LogError(ex, "Error configuring Stripe");
}


// ✅ HANGFIRE DASHBOARD: Habilitado con autenticación JWT
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new newApi.Filters.HangfireAuthorizationFilter(app.Configuration) },
    // Configurar para permitir iframes
    IsReadOnlyFunc = (DashboardContext context) => false
});

// ✅ SEGURIDAD 2025: Limpieza automática de refresh tokens - Comentado porque Hangfire está deshabilitado
// TODO: Implementar con un servicio background alternativo o habilitar Hangfire con Redis
/*
RecurringJob.AddOrUpdate<RefreshTokenCleanupService>(
    "cleanup-expired-refresh-tokens",
    service => service.CleanupExpiredTokensAsync(),
    Cron.Daily(3),
    new RecurringJobOptions
    {
        TimeZone = TimeZoneInfo.Utc
    }
);
GlobalConfiguration.Configuration
    .UseActivator(new Hangfire.AspNetCore.AspNetCoreJobActivator(app.Services.GetRequiredService<IServiceScopeFactory>()))
    .UseFilter(new HangfireFailedJobNotificationFilter(app.Services.GetRequiredService<IServiceScopeFactory>()));
*/ // ✅ Filtro para alertar a soporte cuando jobs fallan definitivamente

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

// ✅ OPTIMIZACIÓN: Habilitar compresión de respuestas (debe ir antes de otros middlewares)
app.UseResponseCompression();

// ✅ OPTIMIZACIÓN: Habilitar compresión de respuestas (debe ir antes de otros middlewares)
app.UseResponseCompression();

// ✅ CORS DEBE SER EL PRIMERO: Aplicar CORS ANTES de cualquier otro middleware
// Esto asegura que los headers CORS se envíen incluso si hay errores
app.UseCors("AllowSpecificOrigin");

// ✅ HANGFIRE IFRAME SUPPORT: Configurar headers para permitir iframes en Hangfire Dashboard
app.Use(async (context, next) =>
{
    // Si es una ruta de Hangfire, configurar headers para permitir iframes
    if (context.Request.Path.StartsWithSegments("/hangfire", StringComparison.OrdinalIgnoreCase))
    {
        // Permitir que se cargue en iframes desde el mismo origen o desde orígenes permitidos
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        
        // Configurar Content-Security-Policy para permitir iframes
        // frame-ancestors permite especificar qué orígenes pueden embeber esta página
        var allowedOrigins = new[] { "https://inspecciono.com", "https://www.inspecciono.com", "http://localhost:3000", "http://localhost:5173" };
        var frameAncestors = string.Join(" ", allowedOrigins.Select(o => o));
        context.Response.Headers["Content-Security-Policy"] = $"frame-ancestors {frameAncestors} 'self';";
        
        // Asegurar que CORS permita las credenciales (CORS middleware lo manejará, pero esto es un fallback)
        var origin = context.Request.Headers["Origin"].FirstOrDefault();
        if (!string.IsNullOrEmpty(origin) && allowedOrigins.Any(o => origin.StartsWith(o, StringComparison.OrdinalIgnoreCase)))
        {
            if (!context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"))
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            }
            context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
        }
    }
    
    await next();
});

// ✅ SEGURIDAD 2025: Aplicar Rate Limiting
app.UseRateLimiter();

// Development mode middleware - bypass authentication for testing
// DISABLED: Using real JWT authentication instead
/*
app.Use(async (context, next) =>
{
    // Log all headers for debugging
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("🔍 Request to {Path} with headers: {Headers}", 
        context.Request.Path, 
        string.Join(", ", context.Request.Headers.Select(h => $"{h.Key}={h.Value}")));
    
    // Check for development headers
    if (context.Request.Headers.ContainsKey("X-Development-Mode") && 
context.Request.Headers.ContainsKey("X-Bypass-Auth"))
    {
        logger.LogInformation("🔧 Development mode detected! Bypassing authentication for {Path}", context.Request.Path);
        
        // Create a fake authenticated user for development
        var claims = new List<System.Security.Claims.Claim>
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "38"), // ID del usuario
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "dev-user"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, "dev@example.com"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin"),
            new System.Security.Claims.Claim("dev-token", context.Request.Headers["X-Dev-Token"].FirstOrDefault() ?? "dev-token-123")
        };
        
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "Development");
        context.User = new System.Security.Claims.ClaimsPrincipal(identity);
        
        logger.LogInformation("✅ Development user created: {User}", context.User.Identity?.Name);
    }
    else
    {
        logger.LogInformation("❌ Development headers not found for {Path}", context.Request.Path);
    }
    
    await next();
});
*/

app.UseAuthentication();
app.UseAuthorization();

// ✅ SEGURIDAD 2025: FORZAR MFA para Admin y Expertos
// OWASP/NIST/PCI DSS: MFA obligatorio para cuentas privilegiadas
// IMPORTANTE: El middleware verifica rutas públicas internamente
// por lo que puede estar antes de mapear endpoints
app.UseRequireMfa();

// Add health check endpoint
app.MapHealthChecks("/health");

app.MapControllers();
app.MapHub<ChatHub>("/chatHub");

app.Urls.Add("http://0.0.0.0:7124");

app.Run();
