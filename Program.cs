using Google.Cloud.SecretManager.V1;
using Swashbuckle.AspNetCore.SwaggerGen;
using Google.Api.Gax.Grpc;
using Grpc.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Stripe;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using RabbitMQ.Client;
using newApi.RabbitMQ;
using newApi.Services;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using Hangfire;
using Hangfire.PostgreSql;
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

// Instancia el cliente de Secret Manager (funciona igual en desarrollo y producción)
// Se inicializa si las credenciales de Google Cloud están disponibles
SecretManagerServiceClient? secretClient = null;
bool secretManagerAvailable = false;

// Obtener ruta de credenciales
var credentialsPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");

// En desarrollo: usar fallback a ubicación estándar si la variable no está configurada
// En producción: solo usar variable de entorno (sin fallback)
if (string.IsNullOrEmpty(credentialsPath) && isDevelopment)
{
    // Fallback solo en desarrollo: usar ubicación estándar
    credentialsPath = "C:\\cloudcredential.json";
    
    // Configurar la variable de entorno para esta sesión en desarrollo
    // Esto asegura que Google Cloud SDK y otras librerías también la usen
    try
    {
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", credentialsPath, EnvironmentVariableTarget.Process);
    }
    catch
    {
        // Si falla, continuar sin configurar la variable (no crítico)
    }
}

builder.Logging.AddConsole();
var initLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");

initLogger.LogInformation($"=== INICIALIZANDO SECRET MANAGER ===");
initLogger.LogInformation($"Entorno: {(isDevelopment ? "Development" : "Production")}");
initLogger.LogInformation($"GOOGLE_APPLICATION_CREDENTIALS: {Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS") ?? "NO CONFIGURADO"}");
initLogger.LogInformation($"Ruta de credenciales a usar: {credentialsPath}");

if (!string.IsNullOrEmpty(credentialsPath))
{
    var fileExists = System.IO.File.Exists(credentialsPath);
    initLogger.LogInformation($"Archivo de credenciales existe: {fileExists}");
    
    if (fileExists)
        {
            try
            {
                // Leer información básica del archivo de credenciales
                var credContent = System.IO.File.ReadAllText(credentialsPath);
                if (credContent.Contains("project_id"))
                {
                    initLogger.LogInformation("Archivo de credenciales parece válido (contiene project_id)");
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
        else
        {
            initLogger.LogWarning($"El archivo de credenciales no existe en la ruta: {credentialsPath}");
            initLogger.LogWarning("Secret Manager no estará disponible. Usando solo variables de entorno como fallback.");
        }
}
else
{
    initLogger.LogWarning("No se pudo determinar la ruta de credenciales. Secret Manager no estará disponible.");
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
// Funciona igual en desarrollo y producción: intenta Secret Manager si está disponible
string? GetSecretValue(string secretName, string? defaultValue = null)
{
    // Intentar usar Secret Manager si está disponible (tanto en desarrollo como producción)
    if (secretClient != null && secretManagerAvailable)
    {
        try
        {
            var projectId = "grup-441318";
            var secretPath = $"projects/{projectId}/secrets/{secretName}/versions/latest";
            
            builder.Logging.AddConsole();
            var secretLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
            secretLogger.LogInformation($"Intentando obtener secreto: {secretName} desde {secretPath}");
            
            // Configurar call settings con timeout y reintentos mejorados para Kubernetes
            // Aumentar timeout significativamente para manejar problemas de conectividad
            // gRPC puede necesitar más tiempo para establecer la conexión HTTP/2
            var callSettings = CallSettings.FromRetry(
                RetrySettings.FromExponentialBackoff(
                    maxAttempts: 3, // Reducir reintentos pero aumentar timeout inicial
                    initialBackoff: TimeSpan.FromSeconds(5), // Esperar más antes del primer reintento
                    maxBackoff: TimeSpan.FromSeconds(20),
                    backoffMultiplier: 2.0,
                    retryFilter: RetrySettings.FilterForStatusCodes(
                        Grpc.Core.StatusCode.Unavailable, 
                        Grpc.Core.StatusCode.DeadlineExceeded,
                        Grpc.Core.StatusCode.Internal,
                        Grpc.Core.StatusCode.ResourceExhausted
                    )
                )
            ).WithTimeout(TimeSpan.FromSeconds(60)); // Timeout MUY largo (60s) para Kubernetes - gRPC puede ser lento
            
            secretLogger.LogInformation($"Llamando a Secret Manager con timeout de 60 segundos...");
            var startTime = DateTime.UtcNow;
            
            var secretVersion = secretClient.AccessSecretVersion(secretPath, callSettings: callSettings);
            
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            secretLogger.LogInformation($"Secreto {secretName} obtenido exitosamente en {duration}ms");
            
            return secretVersion.Payload.Data.ToStringUtf8();
        }
        catch (Grpc.Core.RpcException rpcEx)
        {
            // Si falla una vez, marcar como no disponible para evitar más intentos
            if (secretManagerAvailable)
            {
                secretManagerAvailable = false;
                builder.Logging.AddConsole();
                var tempLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
                tempLogger.LogError($"ERROR gRPC al obtener secreto {secretName}:");
                tempLogger.LogError($"  Status Code: {rpcEx.StatusCode}");
                tempLogger.LogError($"  Status Detail: {rpcEx.Status.Detail}");
                tempLogger.LogError($"  Debug Exception: {rpcEx.Status.DebugException?.Message ?? "N/A"}");
                if (rpcEx.Status.DebugException is System.Net.Sockets.SocketException socketEx)
                {
                    tempLogger.LogError($"  Socket Error Code: {socketEx.SocketErrorCode}");
                    tempLogger.LogError($"  Native Error Code: {socketEx.NativeErrorCode}");
                }
                tempLogger.LogWarning("Secret Manager no está disponible. Usando solo variables de entorno para todos los secretos.");
            }
            return defaultValue; // Retornar valor por defecto en caso de error
        }
        catch (Exception ex)
        {
            // Si falla una vez, marcar como no disponible para evitar más intentos
            if (secretManagerAvailable)
            {
                secretManagerAvailable = false;
                builder.Logging.AddConsole();
                var tempLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
                tempLogger.LogError($"ERROR inesperado al obtener secreto {secretName}: {ex.GetType().Name} - {ex.Message}");
                tempLogger.LogError($"Stack trace: {ex.StackTrace}");
                tempLogger.LogWarning("Secret Manager no está disponible. Usando solo variables de entorno para todos los secretos.");
            }
            return defaultValue; // Retornar valor por defecto en caso de error
        }
    }
    
    // Si Secret Manager no está disponible, usar valor por defecto (que puede ser null)
    return defaultValue;
}

// Cargar Google Client IDs: Prioridad 1) Variable de entorno, 2) Secret Manager
string[]? googleClientIds = null;

// Intentar leer de variable de entorno primero (formato JSON array o separado por comas)
var googleClientIdsFromEnv = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_IDS");
if (!string.IsNullOrEmpty(googleClientIdsFromEnv))
{
    try
    {
        // Intentar parsear como JSON array primero
        if (googleClientIdsFromEnv.TrimStart().StartsWith("["))
        {
            googleClientIds = System.Text.Json.JsonSerializer.Deserialize<string[]>(googleClientIdsFromEnv);
        }
        else
        {
            // Si no es JSON, tratar como separado por comas
            googleClientIds = googleClientIdsFromEnv
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim().Trim('"', '[', ']'))
                .ToArray();
        }
    }
    catch
    {
        // Si falla el parseo JSON, intentar como separado por comas
        googleClientIds = googleClientIdsFromEnv
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => id.Trim().Trim('"', '[', ']'))
            .ToArray();
    }
}

// Si no hay en variable de entorno, intentar desde Secret Manager
if (googleClientIds == null || googleClientIds.Length == 0)
{
    var googleClientIdsFromSecret = GetSecretValue("google-client-ids", null);
    if (!string.IsNullOrEmpty(googleClientIdsFromSecret))
    {
        try
        {
            // Intentar parsear como JSON array primero
            if (googleClientIdsFromSecret.TrimStart().StartsWith("["))
            {
                googleClientIds = System.Text.Json.JsonSerializer.Deserialize<string[]>(googleClientIdsFromSecret);
            }
            else
            {
                // Si no es JSON, tratar como separado por comas
                googleClientIds = googleClientIdsFromSecret
                    ?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(id => id.Trim().Trim('"', '[', ']'))
                    .ToArray();
            }
        }
        catch
        {
            // Si falla el parseo JSON, intentar como separado por comas
            googleClientIds = googleClientIdsFromSecret
                ?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim().Trim('"', '[', ']'))
                .ToArray();
        }
    }
}

if (googleClientIds != null && googleClientIds.Length > 0)
{
    var configDict = new Dictionary<string, string?>();
    for (int i = 0; i < googleClientIds.Length; i++)
    {
        configDict[$"Google:ClientIds:{i}"] = googleClientIds[i];
    }
    builder.Configuration.AddInMemoryCollection(configDict);
    
    var googleConfigLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
    googleConfigLogger.LogInformation($"Google Client IDs configurados: {googleClientIds.Length} ID(s) encontrado(s)");
}

// JWT - Leer de variables de entorno primero, luego de Secret Manager como fallback
// Misma lógica para desarrollo y producción
var configLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");

// Leer JWT Key: Prioridad 1) Variables de entorno, 2) Secret Manager (Google Cloud), 3) Configuration
var jwtKeyFromEnv = Environment.GetEnvironmentVariable("JWT_KEY");
var jwtKeyFromSecret = GetSecretValue("jwt-key", null);
var jwtKeyFromConfig = builder.Configuration["Jwt:Key"];

builder.Configuration["Jwt:Key"] = jwtKeyFromEnv ?? jwtKeyFromSecret ?? jwtKeyFromConfig ?? "";

// Leer Issuer y Audience con la misma prioridad
builder.Configuration["Jwt:Issuer"] = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? GetSecretValue("jwt-issuer", null) ?? builder.Configuration["Jwt:Issuer"] ?? "newApi";

builder.Configuration["Jwt:Audience"] = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? GetSecretValue("jwt-audience", null) ?? builder.Configuration["Jwt:Audience"] ?? "newApi";

// Obtener jwtKey para validación y logging
var jwtKey = builder.Configuration["Jwt:Key"];
var jwtKeySource = !string.IsNullOrEmpty(jwtKeyFromEnv) ? "Environment Variable" 
    : (!string.IsNullOrEmpty(jwtKeyFromSecret) ? "Google Cloud Secret Manager" 
    : (!string.IsNullOrEmpty(jwtKeyFromConfig) ? "Configuration/User Secrets" : "NOT FOUND"));
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
        "Please set 'JWT_KEY' environment variable, 'jwt-key' in Google Cloud Secret Manager, or User Secrets. " +
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
builder.Configuration["RabbitMQ:Password"] = GetSecretValue("rabbitmq-password", null) ?? "";
builder.Configuration["OpenAI:ApiKey"] = GetSecretValue("openai-api-key", null) ?? "";
if (isDevelopment)
{
    // En desarrollo: usar variables de entorno, User Secrets, o valor hardcodeado como fallback
    // Configurar con: dotnet user-secrets set "Stripe:SecretKey" "valor"
    // O usar variables de entorno: STRIPE_SECRET_KEY
    if (string.IsNullOrEmpty(builder.Configuration["Stripe:SecretKey"]))
    {
        builder.Configuration["Stripe:SecretKey"] = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY") 
            ?? "__REDACTED_STRIPE_SECRET__"; // ✅ DESARROLLO: Clave hardcodeada como fallback
    }
    if (string.IsNullOrEmpty(builder.Configuration["Stripe:WebhookSecret"]))
    {
        builder.Configuration["Stripe:WebhookSecret"] = Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET") 
            ?? "__REDACTED_STRIPE_WEBHOOK__"; // ✅ DESARROLLO: Webhook secret para Connect events (account.updated, etc.)
    }
    // ⚠️ IMPORTANTE: El WebhookSecret es único para cada endpoint de webhook en Stripe
    // Para obtenerlo: Stripe Dashboard → Developers → Webhooks → Tu endpoint → Signing secret (whsec_...)
    // Configurar con: dotnet user-secrets set "Stripe:WebhookSecret" "whsec_..."
    // O variable de entorno: STRIPE_WEBHOOK_SECRET=whsec_...
    
    if (string.IsNullOrEmpty(builder.Configuration["Stripe:GeneralWebhookSecret"]))
    {
        builder.Configuration["Stripe:GeneralWebhookSecret"] = Environment.GetEnvironmentVariable("STRIPE_GENERAL_WEBHOOK_SECRET") 
            ?? "__REDACTED_STRIPE_WEBHOOK__"; // ✅ DESARROLLO: Webhook secret para eventos generales (payment_intent.succeeded, checkout.session.completed, etc.)
    }
    // ⚠️ IMPORTANTE: El GeneralWebhookSecret es para eventos generales (no Connect)
    // Para obtenerlo: Stripe Dashboard → Developers → Webhooks → Tu endpoint → Signing secret (whsec_...)
    // Configurar con: dotnet user-secrets set "Stripe:GeneralWebhookSecret" "whsec_..."
    // O variable de entorno: STRIPE_GENERAL_WEBHOOK_SECRET=whsec_...
}
else
{
    // En producción: valores desde Google Cloud Secret Manager
    builder.Configuration["Stripe:SecretKey"] = GetSecretValue("stripe-secret-key", null) ?? "";
    builder.Configuration["Stripe:WebhookSecret"] = GetSecretValue("stripe-webhook-secret", null) ?? "";
    builder.Configuration["Stripe:GeneralWebhookSecret"] = GetSecretValue("stripe-general-webhook-secret", null) ?? "";
}

builder.Configuration["Twilio:AccountSid"] = GetSecretValue("twilio-account-sid", null) ?? "";
builder.Configuration["Twilio:AuthToken"] = GetSecretValue("twilio-auth-token", null) ?? "";
builder.Configuration["Twilio:VerificationServiceSid"] = GetSecretValue("twilio-verification-service-sid", null) ?? "";
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
// Configurar la cadena de conexión desde Secret Manager
string connectionString;

if (isDevelopment)
{
    // En desarrollo: usar configuración local del túnel (variables de entorno o user secrets)
    // NO usar Google Cloud Secret Manager en desarrollo
    // Probar múltiples puertos automáticamente hasta encontrar uno disponible
    
    // Lista de puertos a probar en orden
    // ✅ CORRECCIÓN: Priorizar 5433 (puerto por defecto del túnel SSH) sobre 5432 en desarrollo
    // Esto acelera la detección cuando se usa el túnel SSH
    var dbPortsToTry = new[] { 
        5433,  // ✅ PRIORIDAD: Puerto por defecto del script db-access.sh (probar primero)
        5432,  // Puerto estándar de PostgreSQL
        5434,  // Puerto alternativo común para túneles
        5435,  // Puerto alternativo común para túneles
        15433, // Puerto alternativo (formato antiguo)
        25432, // Puerto alternativo (formato antiguo)
        35432, // Puerto alternativo (formato antiguo)
        45432, // Puerto alternativo (formato antiguo)
        55432, // Puerto alternativo (formato antiguo)
        65432  // Puerto alternativo (formato antiguo)
    };
    
    var existingConnectionString = builder.Configuration.GetConnectionString("PostgresConnection");
    string? baseConnectionString = null;
    string dbHost;
    string dbUsername;
    string dbPassword;
    string dbName;
    
    if (!string.IsNullOrEmpty(existingConnectionString) && !existingConnectionString.Equals("", StringComparison.OrdinalIgnoreCase))
    {
        // Usar connection string desde appsettings.Development.json o user secrets
        baseConnectionString = existingConnectionString;
        
        // Extraer valores del connection string existente
        var hostMatch = Regex.Match(existingConnectionString, @"Host=([^;]+)");
        var userMatch = Regex.Match(existingConnectionString, @"Username=([^;]+)");
        var passMatch = Regex.Match(existingConnectionString, @"Password=([^;]+)");
        var dbMatch = Regex.Match(existingConnectionString, @"Database=([^;]+)");
        
        dbHost = hostMatch.Success ? hostMatch.Groups[1].Value : "localhost";
        dbUsername = userMatch.Success ? userMatch.Groups[1].Value : "admin";
        dbPassword = passMatch.Success ? passMatch.Groups[1].Value : "";
        dbName = dbMatch.Success ? dbMatch.Groups[1].Value : "atrapo";
        
        // Forzar valores en desarrollo: admin y atrapo
        dbUsername = "admin";
        dbName = "atrapo";
    }
    else
    {
        // Construir desde variables de entorno individuales (para túnel local)
        // Valores por defecto para desarrollo: admin y atrapo
        dbHost = Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost";
        dbUsername = Environment.GetEnvironmentVariable("DB_USERNAME") ?? "admin";
        dbPassword = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "";
        dbName = Environment.GetEnvironmentVariable("DB_NAME") ?? "atrapo";
    }
    
    // Validar que tenemos password
    if (string.IsNullOrEmpty(dbPassword))
    {
        configLogger.LogWarning("DB_PASSWORD not set in environment variables. Using empty password (may fail).");
    }
    
    // Función para probar conexión a un puerto específico
    // ✅ CORRECCIÓN: Timeout reducido a 1 segundo para detectar puertos más rápido
    bool TestConnection(int port)
    {
        try
        {
            var testConnectionString = $"Host={dbHost};Port={port};Username={dbUsername};Password={dbPassword};Database={dbName};Timeout=1;CommandTimeout=1;";
            using var testConn = new Npgsql.NpgsqlConnection(testConnectionString);
            testConn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    // Probar cada puerto en orden hasta encontrar uno que funcione
    int? workingPort = null;
    configLogger.LogInformation("=== Probando puertos de PostgreSQL en desarrollo ===");
    configLogger.LogInformation($"Puertos a probar (en orden): {string.Join(", ", dbPortsToTry)}");
    configLogger.LogInformation($"Host: {dbHost}");
    configLogger.LogInformation($"Username: {dbUsername}");
    configLogger.LogInformation($"Database: {dbName}");
    // Mostrar contraseña (enmascarada pero visible para debugging)
    var passwordDisplay = string.IsNullOrEmpty(dbPassword) ? "(vacía)" : $"{dbPassword.Substring(0, Math.Min(3, dbPassword.Length))}*** (longitud: {dbPassword.Length})";
    configLogger.LogInformation($"Password: {passwordDisplay}");
    configLogger.LogInformation("");
    
    foreach (var port in dbPortsToTry)
    {
        configLogger.LogInformation($"[{Array.IndexOf(dbPortsToTry, port) + 1}/{dbPortsToTry.Length}] Probando puerto {port}...");
        if (TestConnection(port))
        {
            workingPort = port;
            configLogger.LogInformation($"✅ Puerto {port} disponible y funcionando - USANDO ESTE PUERTO");
            break;
        }
        else
        {
            configLogger.LogWarning($"❌ Puerto {port} no disponible o no responde - probando siguiente...");
        }
    }
    
    // Si no se encontró ningún puerto disponible
    if (!workingPort.HasValue)
    {
        var passwordInfo = string.IsNullOrEmpty(dbPassword) ? "(vacía - puede ser el problema)" : $"configurada (longitud: {dbPassword.Length})";
        throw new InvalidOperationException(
            $"No se pudo conectar a PostgreSQL en ningún puerto. Puertos probados: {string.Join(", ", dbPortsToTry)}\n" +
            $"Host: {dbHost}\n" +
            $"Username: {dbUsername}\n" +
            $"Database: {dbName}\n" +
            $"Password: {passwordInfo}\n" +
            $"Verifica que:\n" +
            $"  1. El túnel SSH esté activo (ejecuta ./db-access.sh)\n" +
            $"  2. PostgreSQL esté corriendo en el servidor remoto\n" +
            $"  3. Las credenciales sean correctas (DB_PASSWORD configurado)\n" +
            $"  4. El usuario '{dbUsername}' tenga acceso a la base de datos '{dbName}'");
    }
    
    // Construir connection string final con el puerto que funciona
    if (baseConnectionString != null)
    {
        // Reemplazar el puerto en el connection string existente
        var portPattern = @"Port=\d+";
        if (Regex.IsMatch(baseConnectionString, portPattern))
        {
            connectionString = Regex.Replace(baseConnectionString, portPattern, $"Port={workingPort.Value}");
        }
        else
        {
            connectionString = baseConnectionString.TrimEnd(';') + $";Port={workingPort.Value};";
        }
        
        // Asegurar que el nombre de la base de datos sea "atrapo" y username "admin"
        var dbNamePattern = @"Database=[^;]+";
        if (Regex.IsMatch(connectionString, dbNamePattern))
        {
            connectionString = Regex.Replace(connectionString, dbNamePattern, "Database=atrapo");
        }
        else
        {
            connectionString = connectionString.TrimEnd(';') + ";Database=atrapo;";
        }
        
        // Asegurar que el username sea "admin"
        var usernamePattern = @"Username=[^;]+";
        if (Regex.IsMatch(connectionString, usernamePattern))
        {
            connectionString = Regex.Replace(connectionString, usernamePattern, "Username=admin");
        }
        else
        {
            connectionString = connectionString.TrimEnd(';') + ";Username=admin;";
        }
    }
    else
    {
        // Connection string optimizado para desarrollo con túnel SSH
        // ✅ CORRECCIÓN: Agregar parámetros para mejor manejo de conexiones y detección de desconexiones
        connectionString = $"Host={dbHost};Port={workingPort.Value};Username={dbUsername};Password={dbPassword};Database={dbName};" +
                          $"Timeout=30;CommandTimeout=60;" +
                          $"Connection Idle Lifetime=300;Connection Pruning Interval=10;" +
                          $"Keepalive=30;Tcp Keepalive=true;" +
                          $"Pooling=true;Minimum Pool Size=1;Maximum Pool Size=20;" +
                          $"No Reset On Close=true;"; // ✅ MEJORA: No resetear conexión al cerrar para mejor manejo de errores
    }
    
    configLogger.LogInformation($"✅ Connection string configurado: Host={dbHost}, Port={workingPort.Value}, Database={dbName}, Username={dbUsername}");
}
else
{
    // En producción: Leer de variables de entorno PRIMERO, luego Secret Manager como fallback
    // Esto permite que la app funcione aunque Secret Manager no esté disponible temporalmente
    var dbHost = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? GetSecretValue("postgres-host", null) ?? "postgres-svc";
    var dbPort = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? GetSecretValue("postgres-port", null) ?? "5432";
    var dbUsername = Environment.GetEnvironmentVariable("POSTGRES_USERNAME") ?? GetSecretValue("postgres-username", null);
    var dbPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? GetSecretValue("postgres-password", null);
    var dbName = Environment.GetEnvironmentVariable("POSTGRES_DATABASE") ?? GetSecretValue("postgres-database", null) ?? "newapi";
    
    // Si no hay credenciales de DB, lanzar error claro
    if (string.IsNullOrEmpty(dbUsername) || string.IsNullOrEmpty(dbPassword))
    {
        throw new InvalidOperationException(
            "Database credentials are required in production. " +
            "Configure via environment variables (POSTGRES_USERNAME, POSTGRES_PASSWORD) " +
            "or Secret Manager (postgres-username, postgres-password). " +
            "Secret Manager status: " + (secretManagerAvailable ? "Available but failed to retrieve secrets" : "Not available"));
    }
    
    connectionString = $"Host={dbHost};Port={dbPort};Username={dbUsername};Password={dbPassword};Database={dbName};Timeout=30;CommandTimeout=30;ConnectionIdleLifetime=300;ConnectionPruningInterval=10;";
    configLogger.LogInformation($"Built connection string for production: Host={dbHost}, Port={dbPort}, Database={dbName}, Username={dbUsername}");
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
builder.Services.AddControllers();

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
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header (optional for Swagger testing)",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey
    });
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

builder.Services.AddAutoMapper(cfg => {
    cfg.AddMaps(typeof(AdMappingProfile).Assembly);
    cfg.AddMaps(typeof(PlatformMappingProfile).Assembly);
    cfg.AddMaps(typeof(CategoryMappingProfile).Assembly);
    cfg.AddMaps(typeof(UserMappingProfile).Assembly);
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
    var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") 
        ?? GetSecretValue("redis-connection-string", null);
    
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
        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "",
        ValidAudience = builder.Configuration["Jwt:Audience"] ?? Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "",
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_KEY") ?? throw new InvalidOperationException("JWT Key not found in configuration or environment variables."))),
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
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgresConnection"), npgsqlOptions =>
    {
        npgsqlOptions.CommandTimeout(60); // Aumentado a 60 segundos para conexiones lentas
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5, // Aumentado de 3 a 5 reintentos
            maxRetryDelay: TimeSpan.FromSeconds(10), // Aumentado delay máximo
            errorCodesToAdd: null);
        
        // ✅ CORRECCIÓN: Los parámetros de conexión (Keepalive, Pooling, etc.) ya están en el connection string
        // No es necesario configurarlos aquí, se aplican automáticamente desde el connection string
    }));

// Configure Google Cloud Storage
builder.Services.AddSingleton<StorageClient>(sp =>
{
    var credentialsPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
    if (!string.IsNullOrEmpty(credentialsPath) && System.IO.File.Exists(credentialsPath))
    {
        var credential = GoogleCredential.FromFile(credentialsPath);
        return StorageClient.Create(credential);
    }
    // Si no hay credenciales, intentar usar credenciales predeterminadas o retornar null
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

// Configure RabbitMQ
builder.Services.AddSingleton<RabbitMQ.Client.IConnectionFactory>(sp =>
{
    var config = builder.Configuration;
    var isDevelopment = builder.Environment.IsDevelopment();
    
    string password;
    if (isDevelopment)
    {
        // En desarrollo: usar valor de desarrollo o desde configuración
        password = config["RABBITMQ_PASSWORD"] ?? "guest";
    }
    else
    {
        // En producción: OBLIGATORIO desde configuración (sin fallback hardcodeado)
        password = config["RABBITMQ_PASSWORD"] ?? throw new InvalidOperationException("RABBITMQ_PASSWORD is required in production");
    }
    
    return new ConnectionFactory
    {
        HostName = isDevelopment ? "localhost" : config["RABBITMQ_HOSTNAME"] ?? "rabbitmq-svc",
        Port = int.Parse(config["RABBITMQ_PORT"] ?? "5672"),
        UserName = config["RABBITMQ_USERNAME"] ?? "admin",
        Password = password
    };
});

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


// ✅ HANGFIRE HABILITADO
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

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 2;
    options.ServerTimeout = TimeSpan.FromMinutes(5);
    options.HeartbeatInterval = TimeSpan.FromSeconds(30);
    options.ServerCheckInterval = TimeSpan.FromMinutes(1);
    options.SchedulePollingInterval = TimeSpan.FromSeconds(30);
});

// Register Services
builder.Services.AddScoped<IRabbitMQService, RabbitMQService>();
builder.Services.AddScoped<IWebMixerService, WebMixerService>();
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
builder.Services.AddScoped<ILoggingService, LoggingService>();
builder.Services.AddScoped<IInvoiceService, newApi.Services.InvoiceService>();
builder.Services.AddScoped<IStripeValidationService, StripeValidationService>();
builder.Services.AddScoped<RefreshTokenCleanupService>(); // ✅ SEGURIDAD 2025: Limpieza de refresh tokens
builder.Services.AddScoped<MfaService>(); // ✅ SEGURIDAD 2025: Autenticación Multifactor (MFA/2FA)

// Background services - AppointmentTimerBackgroundService migrated to Hangfire

builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<SearchServiceService>();
builder.Services.AddScoped<SearchHireService>();

builder.Services.AddHttpClient();

// Add Health Checks
builder.Services.AddHealthChecks();

// AutoMapper ya está registrado arriba (línea 848), no es necesario duplicarlo

var app = builder.Build();

// Configure Stripe
StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"];

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


// ✅ Hangfire Dashboard habilitado
app.UseHangfireDashboard("/hangfire");

// ✅ SEGURIDAD 2025: Limpieza automática de refresh tokens
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
    .UseFilter(new HangfireFailedJobNotificationFilter(app.Services.GetRequiredService<IServiceScopeFactory>())); // ✅ Filtro para alertar a soporte cuando jobs fallan definitivamente

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

app.UseCors("AllowSpecificOrigin"); // Aplicar CORS antes de otros middleware

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
app.UseRequireMfa();

// Add health check endpoint
app.MapHealthChecks("/health");

app.MapControllers();
app.MapHub<ChatHub>("/chatHub");

app.Urls.Add("http://0.0.0.0:7124");

app.Run();
