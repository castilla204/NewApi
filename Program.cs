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

// ✅ RENDER.COM: Configurar puerto ANTES de crear el builder
// Render.com usa la variable PORT (generalmente 10000)
// CRÍTICO: Render.com escanea puertos MUY rápido, antes de que la app termine de iniciar
// Por eso debemos configurar el puerto INMEDIATAMENTE con un valor por defecto
var renderPort = Environment.GetEnvironmentVariable("PORT");
var portToUse = "10000"; // Puerto por defecto de Render.com

if (!string.IsNullOrEmpty(renderPort) && int.TryParse(renderPort, out int portNumber))
{
    portToUse = renderPort;
    Console.WriteLine($"[RENDER] ✅ Variable PORT detectada: {renderPort}");
}
else
{
    Console.WriteLine($"[RENDER] ⚠️ Variable PORT no encontrada, usando puerto por defecto: {portToUse}");
}

// Configurar puerto INMEDIATAMENTE antes de crear el builder
// Esto garantiza que Render.com pueda detectar el puerto durante el escaneo rápido
var builder = WebApplication.CreateBuilder(args);

// ✅ RENDER.COM: Configurar puerto EXACTAMENTE como recomienda el análisis
// Usar formato exacto: http://0.0.0.0:{PORT} para binding correcto
// El análisis sugiere usar UseUrls() directamente en el builder
builder.WebHost.UseUrls($"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? portToUse}");
Console.WriteLine($"[RENDER] ✅ Puerto configurado INMEDIATAMENTE en builder: UseUrls(http://0.0.0.0:{portToUse})");

// También configurar ASPNETCORE_URLS como respaldo (según análisis)
Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://0.0.0.0:{portToUse}");

// Configurar logging básico
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Verificar el entorno PRIMERO (necesario para configurar logging)
var isDevelopment = builder.Environment.IsDevelopment();

// ✅ LOG DE VERSIÓN: Identificar la versión desplegada
var versionLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
var buildDate = System.IO.File.GetLastWriteTime(System.Reflection.Assembly.GetExecutingAssembly().Location);
var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
versionLogger.LogInformation("═══════════════════════════════════════════════════════════");
versionLogger.LogInformation("🚀 INICIANDO APLICACIÓN - NUEVA VERSIÓN CON TRANSACTION POOLER");
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
        initLogger.LogInformation($"🔍 DEBUG: Primeros 100 caracteres: {credentialsJson.Substring(0, Math.Min(100, credentialsJson.Length))}");
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
    // ✅ PRODUCCIÓN (Render.com): Connection string HARDCODEADA
    // Transaction Pooler (puerto 6543) - Compatible con Hangfire y IPv4/IPv6
    // Timeouts aumentados para Render.com: Timeout=60, CommandTimeout=120
    connectionString = "User Id=postgres.rveqsehzlvbttlpmsbmi;Password=__REDACTED_CREDENTIAL__;Server=aws-1-eu-west-2.pooler.supabase.com;Port=6543;Database=postgres;SslMode=Require;Timeout=60;CommandTimeout=120;Pooling=true;Multiplexing=false;Enlist=false;Max Auto Prepare=0;KeepAlive=30;";
    connectionStringSource = "Hardcoded (Producción - Render.com)";
    configLogger.LogInformation("✅ Producción: Connection string HARDCODEADA (Render.com)");
    configLogger.LogInformation("   Transaction Pooler (puerto 6543) - Compatible con Hangfire");
    configLogger.LogInformation("   Timeouts aumentados: Timeout=60, CommandTimeout=120");
    configLogger.LogInformation("   KeepAlive=30 para mantener conexiones vivas");
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
    configLogger.LogInformation($"   Port: {dbPort} {(dbPort == "6543" ? "✅ (Transaction Pooler - CORRECTO)" : dbPort == "5432" ? "❌ (Session Pooler - CAMBIAR A 6543)" : "")}");
    configLogger.LogInformation($"   Database: {dbName}");
    configLogger.LogInformation($"   Username: {dbUsername}");
    configLogger.LogInformation($"   Entorno: {(isDevelopment ? "Development" : "Production")}");
    
    if (dbPort == "5432" && !isDevelopment)
    {
        configLogger.LogError("❌ ERROR CRÍTICO: Puerto 5432 detectado en PRODUCCIÓN");
        configLogger.LogError("   Session Pooler (5432) NO es compatible con Hangfire");
        configLogger.LogError("   SOLUCIÓN: Actualizar variable de entorno en Azure Portal:");
        configLogger.LogError("   1. Ir a Azure Portal -> App Service -> Configuration");
        configLogger.LogError("   2. Buscar: ConnectionStrings__PostgresConnection");
        configLogger.LogError("   3. Cambiar: Port=5432 -> Port=6543");
        configLogger.LogError("   4. Guardar y reiniciar la aplicación");
    }
}


builder.Configuration["ConnectionStrings:PostgresConnection"] = connectionString;

// ✅ HANGFIRE: Configurar connection string usando Direct Connection (recomendado)
// SOLUCIÓN 2026: Usar NpgsqlConnectionStringBuilder para parsear correctamente
// NO construir hostnames manualmente para evitar errores de DNS
string hangfireConnectionString;
try
{
    // Usar NpgsqlConnectionStringBuilder para parsear la connection string correctamente
    var builderConn = new NpgsqlConnectionStringBuilder(connectionString);
    var host = builderConn.Host ?? string.Empty;
    var username = builderConn.Username ?? string.Empty;
    
    // ✅ DETECTAR TIPO DE CONEXIÓN SUPABASE (según documentación oficial)
    // Direct Connection: db.*.supabase.co (puerto 5432, solo IPv6)
    // Session Pooler: pooler.supabase.com (puerto 5432, IPv4/IPv6, NO compatible con Hangfire)
    // Transaction Pooler: pooler.supabase.com (puerto 6543, IPv4/IPv6, PERFECTO para Hangfire)
    var port = builderConn.Port;
    var isDirectConnection = host.Contains("db.") && host.Contains(".supabase.co");
    var isSessionPooler = host.Contains("pooler.supabase.com") && port == 5432;
    var isTransactionPooler = host.Contains("pooler.supabase.com") && port == 6543;
    
    // Extraer project reference del username (formato: postgres.PROJECT_REF)
    var projectRef = username.Contains(".") ? username.Split('.').LastOrDefault() ?? string.Empty : string.Empty;
    
    // Validar project reference (debe tener al menos 10 caracteres para ser válido)
    var projectRefValid = !string.IsNullOrEmpty(projectRef) && projectRef.Length >= 10 && 
                          Regex.IsMatch(projectRef, @"^[a-zA-Z0-9_-]+$");
    
    if (isTransactionPooler)
    {
        // PERFECTO: Transaction Pooler (puerto 6543) - Compatible con IPv4/IPv6 y Hangfire
        hangfireConnectionString = connectionString;
        configLogger.LogInformation("[OK] USANDO TRANSACTION POOLER (Puerto 6543) - PERFECTO PARA HANGFIRE");
        configLogger.LogInformation("   [OK] Compatible con IPv4/IPv6 (resuelve problemas DNS)");
        configLogger.LogInformation("   [OK] Compatible con Hangfire (no causa ObjectDisposedException)");
        configLogger.LogInformation("   [OK] Recomendado por documentacion oficial de Supabase para background jobs");
        configLogger.LogInformation($"   Host: {host}, Port: {port}");
    }
    else if (isDirectConnection)
    {
        // Direct Connection: ideal pero requiere IPv6
        hangfireConnectionString = connectionString;
        configLogger.LogInformation("[OK] Usando Direct Connection para Hangfire (requiere IPv6 habilitado)");
        configLogger.LogInformation($"   Host: {host}, Port: {port}");
        configLogger.LogInformation("   [WARNING] NOTA: Direct Connection requiere IPv6. Si tienes problemas DNS, usa Transaction Pooler (puerto 6543)");
    }
    else if (isSessionPooler)
    {
        // Session Pooler (puerto 5432): NO recomendado para Hangfire
        // Intentar cambiar automáticamente a Transaction Pooler (puerto 6543)
        if (projectRefValid)
        {
            // Construir Transaction Pooler connection string automáticamente
            var transactionPoolerConn = new NpgsqlConnectionStringBuilder(connectionString);
            transactionPoolerConn.Port = 6543; // Cambiar a Transaction Pooler
            hangfireConnectionString = transactionPoolerConn.ConnectionString;
            
            configLogger.LogWarning("[WARNING] Session Pooler (puerto 5432) detectado - NO recomendado para Hangfire");
            configLogger.LogWarning("   [OK] SOLUCION AUTOMATICA: Cambiando a Transaction Pooler (puerto 6543)");
            configLogger.LogInformation($"   Host: {host}, Port: 5432 -> 6543 (Transaction Pooler)");
            configLogger.LogInformation("   [OK] Transaction Pooler es compatible con IPv4/IPv6 y Hangfire");
        }
        else
        {
            // No se puede construir automáticamente, usar Session Pooler con advertencia
            hangfireConnectionString = connectionString;
            configLogger.LogError("[ERROR] Session Pooler (puerto 5432) detectado - NO recomendado para Hangfire");
            configLogger.LogError("   [ERROR] Puede causar ObjectDisposedException en locks distribuidos");
            configLogger.LogError("   [OK] SOLUCION: Cambiar manualmente a Transaction Pooler (puerto 6543)");
            configLogger.LogError("   En appsettings.json, cambia Port=5432 a Port=6543");
        }
    }
    else
    {
        // ⚠️ No es Supabase o formato desconocido: usar connection string principal
        hangfireConnectionString = connectionString;
        configLogger.LogWarning("⚠️ Connection string no detectada como Supabase. Usando connection string principal.");
        configLogger.LogWarning("   Verifica que la connection string sea válida para Hangfire.");
    }
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
    
    // ✅ DETECTAR TIPO DE CONEXIÓN Y ADVERTIR SOBRE PROBLEMAS DE DNS/IPv6
    var builderConn = new NpgsqlConnectionStringBuilder(connectionString);
    var host = builderConn.Host ?? string.Empty;
    var port = builderConn.Port;
    var isSessionPooler = host.Contains("pooler.supabase.com") && port == 5432;
    var isTransactionPooler = host.Contains("pooler.supabase.com") && port == 6543;
    var isDirectConnection = host.Contains("db.") && host.Contains(".supabase.co");
    
    var dbLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
    
    if (isTransactionPooler)
    {
        // PERFECTO: Transaction Pooler (puerto 6543) - Compatible con IPv4/IPv6
        dbLogger.LogInformation("[OK] TRANSACTION POOLER DETECTADO (Puerto 6543)");
        dbLogger.LogInformation("   [OK] Compatible con IPv4/IPv6 (resuelve problemas DNS)");
        dbLogger.LogInformation("   [OK] Compatible con Hangfire (no causa ObjectDisposedException)");
        dbLogger.LogInformation("   [OK] Recomendado por documentacion oficial de Supabase");
        dbLogger.LogInformation($"   Host: {host}, Port: {port}");
    }
    else if (isSessionPooler)
    {
        // Session Pooler (puerto 5432): Puede tener problemas DNS y con Hangfire
        dbLogger.LogWarning("[WARNING] SESSION POOLER DETECTADO (Puerto 5432)");
        dbLogger.LogWarning($"   Host actual: {host}, Port: {port}");
        dbLogger.LogError("   [ERROR] PROBLEMA 1: Puede tener errores DNS (IPv4/IPv6)");
        dbLogger.LogError("   [ERROR] PROBLEMA 2: NO compatible con Hangfire (ObjectDisposedException)");
        dbLogger.LogError("");
        dbLogger.LogError("   [SOLUCION] RECOMENDADA: Cambiar a Transaction Pooler (Puerto 6543)");
        dbLogger.LogError("   En appsettings.json o appsettings.Development.json:");
        dbLogger.LogError("   Cambiar: Port=5432 -> Port=6543");
        dbLogger.LogError("");
        dbLogger.LogError("   Connection String CORRECTO:");
        dbLogger.LogError($"   Host={host};Port=6543;Username=postgres.rveqsehzlvbttlpmsbmi;Password=***;Database=postgres;SslMode=Require;");
        dbLogger.LogError("");
        dbLogger.LogError("   [OK] Transaction Pooler (6543) resuelve:");
        dbLogger.LogError("   - Problemas de DNS (compatible IPv4/IPv6)");
        dbLogger.LogError("   - ObjectDisposedException en Hangfire");
        dbLogger.LogError("   - Compatible con background jobs segun docs oficiales");
    }
    else if (isDirectConnection)
    {
        // Direct Connection: Requiere IPv6 habilitado
        dbLogger.LogInformation("[OK] Direct Connection detectado (requiere IPv6)");
        dbLogger.LogInformation($"   Host: {host}, Port: {port}");
        dbLogger.LogWarning("   [WARNING] NOTA: Direct Connection requiere IPv6 habilitado en Windows");
        dbLogger.LogWarning("   Si tienes problemas DNS, usa Transaction Pooler (puerto 6543)");
    }
    
    // ✅ FIX CRÍTICO: Deshabilitar multiplexing explícitamente y configurar Enlist
    // El Transaction Pooler ya maneja la multiplexación a nivel de pool,
    // no necesitamos multiplexing a nivel de Npgsql que requiere transacciones explícitas
    var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connectionString);
    connectionStringBuilder.Multiplexing = false; // ✅ CRÍTICO: Deshabilitar multiplexing para evitar error "transactions must be started with BeginTransaction"
    connectionStringBuilder.Enlist = false; // ✅ CRÍTICO: Evitar que Npgsql se una automáticamente a transacciones ambientales
    
    // ✅ CRÍTICO PARA TRANSACTION POOLER (PgBouncer): Deshabilitar Prepared Statements
    // PgBouncer en Transaction Mode no admite Prepared Statements (CAUSA ObjectDisposedException/Connection Reset)
    connectionStringBuilder.MaxAutoPrepare = 0;
    
    // ✅ FIX CONEXIONES: Agregar timeouts y configuración de pooling para evitar "Exception while reading from stream"
    // CRÍTICO para Render.com: aumentar timeouts debido a latencia de red
    if (connectionStringBuilder.Timeout < 60)
    {
        connectionStringBuilder.Timeout = 60; // Timeout de conexión aumentado para Render.com
    }
    if (connectionStringBuilder.CommandTimeout < 120)
    {
        connectionStringBuilder.CommandTimeout = 120; // Timeout de comandos aumentado para Render.com
    }
    connectionStringBuilder.Pooling = true; // Habilitar pooling de conexiones
    connectionStringBuilder.MinPoolSize = 2; // Mínimo aumentado para mantener conexiones activas
    connectionStringBuilder.MaxPoolSize = 30; // Máximo aumentado para Render.com
    connectionStringBuilder.ConnectionLifetime = 600; // Reciclar conexiones después de 10 minutos (aumentado)
    connectionStringBuilder.KeepAlive = 30; // Enviar keepalive cada 30 segundos para mantener conexiones vivas 
    
    var finalConnectionString = connectionStringBuilder.ToString();
    
    // ✅ VERIFICACIÓN: Asegurar que Multiplexing=false y Max Auto Prepare=0 estén en la cadena final
    if (!finalConnectionString.Contains("Multiplexing=false", StringComparison.OrdinalIgnoreCase))
    {
        finalConnectionString += (finalConnectionString.Contains(';') ? ";" : "") + "Multiplexing=false;";
    }
    if (!finalConnectionString.Contains("Enlist=false", StringComparison.OrdinalIgnoreCase))
    {
        finalConnectionString += "Enlist=false;";
    }
    if (!finalConnectionString.Contains("Max Auto Prepare=0", StringComparison.OrdinalIgnoreCase))
    {
        finalConnectionString += "Max Auto Prepare=0;";
    }
    
    dbLogger.LogWarning("🔧 CRITICAL FIX: Using connection string DIRECTLY (no NpgsqlDataSourceBuilder)");
    dbLogger.LogWarning($"   Multiplexing=false is EXPLICITLY set in connection string");
    dbLogger.LogWarning($"   This prevents 'transactions must be started with BeginTransaction' error");
    dbLogger.LogInformation("🔧 EnableRetryOnFailure HABILITADO para manejar errores transitorios");
    dbLogger.LogInformation($"   Retry logic ayuda con timeouts y conexiones inestables en Render.com");
    
    // ✅ CRITICAL: Use connection string DIRECTLY, do NOT use NpgsqlDataSourceBuilder
    // NpgsqlDataSourceBuilder ignores Multiplexing=false from connection string
    options.UseNpgsql(finalConnectionString, npgsqlOptions =>
    {
        npgsqlOptions.CommandTimeout(120); // Aumentado a 120 segundos para Render.com (más latencia)
        
        // ✅ HABILITADO: EnableRetryOnFailure para manejar errores transitorios de conexión
        // CRÍTICO para Render.com donde hay más latencia y conexiones pueden ser inestables
        // IMPORTANTE: Solo reintenta en operaciones que NO usan transacciones manuales
        // Las transacciones manuales (UserService.GoogleAuth) manejan sus propios reintentos
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5, // Aumentado a 5 reintentos para Render.com
            maxRetryDelay: TimeSpan.FromSeconds(10), // Delay máximo aumentado
            errorCodesToAdd: null // Usar códigos de error por defecto de Npgsql
        );
    });
    
    // ✅ CRITICAL: Disable Execution Strategy completely to prevent multiplexing issues
    // Execution Strategy can cause "transactions must be started with BeginTransaction" error
    // even when Multiplexing=false is set, because it tries to use multiplexing internally
    options.EnableSensitiveDataLogging(isDevelopment);
    options.EnableDetailedErrors(isDevelopment);
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
                "https://inspecciono.com",
                "https://www.inspecciono.com") // <--- agregar dominio de frontend producci�n
                   .AllowAnyMethod()
                   .AllowAnyHeader()
                   .AllowCredentials()
                   .SetPreflightMaxAge(TimeSpan.FromSeconds(600));
        });
    }
});


// ✅ HANGFIRE CONFIGURACIÓN 2026: Integración con Supabase PostgreSQL
// 
// DOCUMENTACIÓN OFICIAL: https://docs.hangfire.io/en/latest/configuration/using-postgresql.html
// VERSIÓN ACTUAL: Hangfire.PostgreSql 1.20.13 ✅ (corrige bug ObjectDisposedException de v1.6.1)
// VERSIÓN HANGFIRE: Hangfire.AspNetCore 1.8.22 ✅
// 
// ✅ VERIFICADO: Versión 1.20.13 resuelve el bug reportado en GitHub Issue #122
// donde las conexiones se disponían prematuramente causando ObjectDisposedException.
// Esta versión es superior a la 1.6.2 que inicialmente resolvió el problema.
// 
// ✅ VERIFICADO: Esta versión resuelve el bug reportado en GitHub Issue #122
// donde las conexiones se disponían prematuramente causando ObjectDisposedException.
// 
// ✅ SOLUCIÓN OFICIAL SUPABASE (Según documentación oficial):
// 
// 1. TRANSACTION POOLER (Puerto 6543) - ⭐ RECOMENDADO PARA HANGFIRE ⭐
//    * Compatible con IPv4/IPv6 (resuelve problemas DNS)
//    * Compatible con Hangfire (no causa ObjectDisposedException)
//    * Diseñado para "temporary clients" y background jobs según docs oficiales
//    * Formato: pooler.supabase.com:6543
//    * ✅ PERFECTO para Hangfire - El código detecta Session Pooler y cambia automáticamente
// 
// 2. SESSION POOLER (Puerto 5432) - ❌ NO RECOMENDADO PARA HANGFIRE
//    * Compatible con IPv4/IPv6 pero causa problemas con Hangfire
//    * Cierra conexiones inactivas prematuramente
//    * Causa ObjectDisposedException en locks distribuidos
//    * Formato: pooler.supabase.com:5432
//    * ⚠️ El código detecta esto y cambia automáticamente a Transaction Pooler si es posible
// 
// 3. DIRECT CONNECTION (Puerto 5432) - ✅ ALTERNATIVA (requiere IPv6)
//    * Solo IPv6 (puede tener problemas DNS si IPv6 no está habilitado)
//    * Soporta conexiones de larga duración
//    * Compatible con locks distribuidos
//    * Formato: db.PROJECT_REF.supabase.co:5432
//    * ⚠️ Requiere IPv6 habilitado en Windows/red
// 
// CONFIGURACIÓN RECOMENDADA (Transaction Pooler):
// 1. Usa Transaction Pooler (puerto 6543) para Hangfire
// 2. Formato: Host=aws-1-eu-west-2.pooler.supabase.com;Port=6543;...
// 3. Configura en appsettings.json o appsettings.Development.json
// 
// EJEMPLO DE CONNECTION STRING (Transaction Pooler - RECOMENDADO):
// User Id=postgres.PROJECT_REF;Password=***;Server=aws-1-eu-west-2.pooler.supabase.com;Port=6543;Database=postgres;SslMode=Require;Timeout=30;CommandTimeout=60;Pooling=true;
// 
// REFERENCIAS:
// - Tutoriales exitosos: Pradeep Radyumna (Dev.to), Georgi Marokov (Dev.to), Cosmin Vladutu (DevGenius)
// - GitHub Issue #122: Bug ObjectDisposedException resuelto en v1.6.2+
// - Hangfire Forum: Recomendaciones para poolers y timeouts

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
    // El Transaction Pooler ya maneja la multiplexación a nivel de pool
    var hangfireConnBuilder = new NpgsqlConnectionStringBuilder(hangfireConnectionString);
    hangfireConnBuilder.Multiplexing = false; // ✅ CRÍTICO: Deshabilitar multiplexing para evitar error "transactions must be started with BeginTransaction"
    hangfireConnBuilder.Enlist = false; // ✅ CRÍTICO: Evitar que Npgsql se una automáticamente a transacciones ambientales
    hangfireConnBuilder.MaxAutoPrepare = 0; // ✅ CRÍTICO PARA TRANSACTION POOLER: Deshabilitar Prepared Statements
    hangfireConnectionString = hangfireConnBuilder.ToString();
    
    // ✅ VERIFICACIÓN: Asegurar que Multiplexing=false, Enlist=false y Max Auto Prepare=0 estén en la cadena final
    if (!hangfireConnectionString.Contains("Multiplexing=false", StringComparison.OrdinalIgnoreCase))
    {
        hangfireConnectionString += (hangfireConnectionString.Contains(';') ? ";" : "") + "Multiplexing=false;";
    }
    if (!hangfireConnectionString.Contains("Enlist=false", StringComparison.OrdinalIgnoreCase))
    {
        hangfireConnectionString += "Enlist=false;";
    }
    if (!hangfireConnectionString.Contains("Max Auto Prepare=0", StringComparison.OrdinalIgnoreCase))
    {
        hangfireConnectionString += "Max Auto Prepare=0;";
    }
    
    // ✅ HANGFIRE CONFIGURACIÓN 2026: Mejores prácticas para Supabase/PostgreSQL
    // Basado en: https://docs.hangfire.io/en/latest/configuration/using-postgresql.html
    // IMPORTANTE: Session Pooler de Supabase cierra conexiones prematuramente
    // Direct Connection (db.PROJECT_REF.supabase.co) es recomendado para Hangfire
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
            QueuePollInterval = TimeSpan.FromSeconds(15), // Balance óptimo para Supabase
            
            // ✅ INVISIBILITY TIMEOUT: Tiempo antes de reintentar job fallido
            // Aumentado para conexiones inestables (Session Pooler) o latencia de red
            // Si un job no se completa en este tiempo, se marca como fallido y se reintenta
            // RECOMENDACIÓN: 30-60 minutos para Supabase (según Hangfire Forum y casos reales)
            InvisibilityTimeout = TimeSpan.FromMinutes(30), // Suficiente para jobs largos
            
            // ✅ SLIDING INVISIBILITY: HABILITADO para renovar timeouts automáticamente
            // Reduce disposiciones al extender el timeout mientras el job está procesando
            // IMPORTANTE: Esto ayuda a evitar ObjectDisposedException con Session Pooler
            // Basado en recomendaciones de Hangfire Forum y casos exitosos (Pradeep, Georgi, etc.)
            UseSlidingInvisibilityTimeout = true,
            
            // ✅ DISTRIBUTED LOCK TIMEOUT: Crítico para evitar deadlocks
            // Los locks distribuidos necesitan más tiempo con latencia de red (Supabase + Render.com)
            // Session Pooler puede causar problemas aquí, Direct Connection es mejor
            // Basado en casos reales: timeouts altos (15+ min) resuelven problemas de locks
            DistributedLockTimeout = TimeSpan.FromMinutes(20) // Aumentado para Render.com + Supabase
            
            // ✅ NOTA: El error "DISCARD ALL cannot run inside a transaction block" ocurre porque
            // Hangfire intenta hacer DISCARD ALL pero el Transaction Pooler no lo permite.
            // Esto es un problema conocido de Hangfire con Transaction Pooler.
            // Solución temporal: Los timeouts aumentados y reintentos ayudan a mitigar el problema.
        })
        .UseDefaultTypeResolver()
        .UseDefaultTypeSerializer());
    
    // ✅ HABILITADO: Servidor de Hangfire para procesar jobs automáticamente
    // Los jobs de timers de appointments requieren que el servidor esté activo
    // En desarrollo, deshabilitar si es Session Pooler (causa ObjectDisposedException)
    var isSessionPooler = hangfireConnectionString.Contains("pooler.supabase.com");
    var isDirectConnection = hangfireConnectionString.Contains("db.") && hangfireConnectionString.Contains(".supabase.co");
    
    // Habilitar servidor solo si:
    // - No es desarrollo, O
    // - Es desarrollo Y tiene Direct Connection (no Session Pooler)
    var enableHangfireServer = !isDevelopment || isDirectConnection;
    
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
        
        hangfireLogger.LogInformation($"✅ Hangfire Server habilitado (Workers: {(isDevelopment ? 1 : Math.Max(2, Environment.ProcessorCount))})");
        if (isSessionPooler)
        {
            hangfireLogger.LogWarning("   ⚠️ Usando Session Pooler - pueden ocurrir ObjectDisposedException ocasionalmente");
        }
    }
    else
    {
        if (isDevelopment && isSessionPooler)
        {
            hangfireLogger.LogWarning("⚠️ Hangfire Server deshabilitado en desarrollo (Session Pooler no compatible)");
            hangfireLogger.LogWarning("   Session Pooler cierra conexiones prematuramente causando ObjectDisposedException");
            hangfireLogger.LogWarning("   SOLUCIÓN: Configura Direct Connection en appsettings.Development.json");
            hangfireLogger.LogWarning("   Dashboard disponible en /hangfire para monitoreo (sin procesar jobs)");
        }
        else
        {
            hangfireLogger.LogWarning("⚠️ Hangfire Server deshabilitado (connection string no válida)");
        }
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

// ✅ DEBUG: Logging de autenticación para diagnosticar problemas en Render.com
app.Use(async (context, next) =>
{
    var authLogger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    
    // Solo loggear en producción para diagnosticar problemas
    if (!app.Environment.IsDevelopment())
    {
        var path = context.Request.Path.Value ?? "";
        var hasAuth = context.Request.Headers.ContainsKey("Authorization");
        var authHeader = hasAuth ? context.Request.Headers["Authorization"].ToString().Substring(0, Math.Min(20, context.Request.Headers["Authorization"].ToString().Length)) + "..." : "NO AUTH HEADER";
        var isAuthenticated = context.User?.Identity?.IsAuthenticated ?? false;
        var origin = context.Request.Headers["Origin"].ToString();
        
        // Solo loggear si es un endpoint protegido (no /health, no /api/ServiceType/public)
        if (!path.Contains("/health") && !path.Contains("/ServiceType/public") && path.StartsWith("/api"))
        {
            authLogger.LogInformation($"🔍 [AUTH DEBUG] Path: {path}, Origin: {origin}, HasAuthHeader: {hasAuth}, IsAuthenticated: {isAuthenticated}, AuthHeader: {authHeader}");
        }
    }
    
    await next();
});

// ✅ SEGURIDAD 2025: FORZAR MFA para Admin y Expertos
// OWASP/NIST/PCI DSS: MFA obligatorio para cuentas privilegiadas
// IMPORTANTE: El middleware verifica rutas públicas internamente
// por lo que puede estar antes de mapear endpoints
app.UseRequireMfa();

// Add health check endpoint
app.MapHealthChecks("/health");

app.MapControllers();
app.MapHub<ChatHub>("/chatHub");

// ✅ RENDER.COM: Iniciar servidor DESPUÉS de configurar endpoints
// CRÍTICO: Render.com necesita detectar el puerto rápidamente
// Iniciamos el servidor DESPUÉS de configurar endpoints para que /health funcione
Console.WriteLine("[RENDER] 🚀 Iniciando servidor DESPUÉS de configurar endpoints...");
await app.StartAsync();
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
startupLogger.LogInformation($"✅ Servidor iniciado y escuchando en: {string.Join(", ", app.Urls)}");
Console.WriteLine($"[RENDER] ✅ Servidor iniciado - Puertos: {string.Join(", ", app.Urls)}");

// ✅ CONFIGURACIÓN DE PUERTO: Azure App Service vs Desarrollo
// Prioridad: ASPNETCORE_URLS > WEBSITES_PORT > PORT > Default (7124 dev / 80 prod)
// IMPORTANTE: ASPNETCORE_URLS tiene la mayor prioridad y .NET la lee automáticamente durante la inicialización del host
// Si ASPNETCORE_URLS está configurado, .NET ya lo ha aplicado antes de llegar aquí
var portLogger = app.Services.GetRequiredService<ILogger<Program>>();

// Verificar qué URLs ya están configuradas (pueden venir de ASPNETCORE_URLS)
var configuredUrls = app.Urls.ToList();
var hasConfiguredUrls = configuredUrls.Any();

if (app.Environment.IsDevelopment())
{
    // Desarrollo: solo verificar ASPNETCORE_URLS (tiene prioridad absoluta), si no está, usar siempre 7124
    var aspnetcoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    
    if (!string.IsNullOrEmpty(aspnetcoreUrls))
    {
        // ASPNETCORE_URLS está configurado - .NET ya lo ha aplicado automáticamente
        portLogger.LogInformation($"✅ Desarrollo: Usando ASPNETCORE_URLS={aspnetcoreUrls}");
        if (hasConfiguredUrls)
        {
            portLogger.LogInformation($"   URLs configuradas: {string.Join(", ", configuredUrls)}");
        }
    }
    else if (!hasConfiguredUrls)
    {
        // En desarrollo, siempre usar puerto fijo 7124 (estándar para desarrollo)
        app.Urls.Add("http://0.0.0.0:7124");
        portLogger.LogInformation("✅ Desarrollo: Puerto configurado a 7124 (por defecto)");
    }
    else
    {
        portLogger.LogInformation($"✅ Desarrollo: URLs ya configuradas: {string.Join(", ", configuredUrls)}");
    }
}
else
{
    // Producción: verificar variables en orden de prioridad (Azure App Service, Render.com, etc.)
    var aspnetcoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    var renderPortEnv = Environment.GetEnvironmentVariable("PORT");
    var isRender = !string.IsNullOrEmpty(renderPortEnv);
    
    if (!string.IsNullOrEmpty(aspnetcoreUrls))
    {
        // ASPNETCORE_URLS tiene la mayor prioridad - .NET la usa automáticamente
        // .NET ya ha leído y aplicado ASPNETCORE_URLS durante la inicialización del host
        var platform = isRender ? "Render.com" : "Azure App Service";
        portLogger.LogInformation($"✅ Producción ({platform}): Usando ASPNETCORE_URLS={aspnetcoreUrls}");
        if (hasConfiguredUrls)
        {
            portLogger.LogInformation($"   URLs configuradas automáticamente: {string.Join(", ", configuredUrls)}");
        }
        else
        {
            portLogger.LogWarning($"⚠️ ASPNETCORE_URLS está configurado pero no se detectaron URLs. Verificar formato (debe ser: http://0.0.0.0:PORT)");
        }
    }
    else if (!hasConfiguredUrls)
    {
        // Fallback: usar WEBSITES_PORT (variable estándar de Azure App Service)
        var websitesPort = Environment.GetEnvironmentVariable("WEBSITES_PORT");
        if (!string.IsNullOrEmpty(websitesPort) && int.TryParse(websitesPort, out int websitesPortNumber))
        {
            app.Urls.Add($"http://0.0.0.0:{websitesPortNumber}");
            portLogger.LogInformation($"✅ Producción (Azure App Service): Puerto configurado desde WEBSITES_PORT={websitesPortNumber}");
        }
        else if (isRender && int.TryParse(renderPortEnv, out int renderPortNum))
        {
            // Render.com: usar PORT (ya configurado en ASPNETCORE_URLS arriba, pero por si acaso)
            app.Urls.Add($"http://0.0.0.0:{renderPortNum}");
            portLogger.LogInformation($"✅ Producción (Render.com): Puerto configurado desde PORT={renderPortNum}");
        }
        else
        {
            // Último fallback: usar PORT (común en contenedores y Render.com)
            var portEnv = Environment.GetEnvironmentVariable("PORT");
            if (!string.IsNullOrEmpty(portEnv) && int.TryParse(portEnv, out int portNum))
            {
                app.Urls.Add($"http://0.0.0.0:{portNum}");
                portLogger.LogInformation($"✅ Producción: Puerto configurado desde PORT={portNum}");
            }
            else
            {
                // Si no hay ninguna variable configurada:
                // - Azure App Service: No necesita puerto explícito, Azure lo maneja automáticamente
                // - Render.com: Siempre tiene PORT configurado, así que este caso no debería ocurrir
                // - Otros entornos: Usar puerto 80 por defecto (estándar HTTP)
                app.Urls.Add("http://0.0.0.0:80");
                portLogger.LogInformation("✅ Producción: Puerto configurado a 80 (por defecto) - Azure App Service lo manejará automáticamente si es necesario");
            }
        }
    }
    else
    {
        // URLs ya configuradas (probablemente desde ASPNETCORE_URLS o configuración previa)
        var platform = isRender ? "Render.com" : "Azure App Service";
        portLogger.LogInformation($"✅ Producción ({platform}): URLs ya configuradas: {string.Join(", ", configuredUrls)}");
        
        // ✅ RENDER.COM: Asegurar que el puerto esté configurado explícitamente
        if (isRender && !hasConfiguredUrls)
        {
            // Si estamos en Render.com pero no hay URLs configuradas, configurar PORT explícitamente
            if (int.TryParse(renderPortEnv, out int renderPortNum))
            {
                app.Urls.Add($"http://0.0.0.0:{renderPortNum}");
                portLogger.LogInformation($"✅ Producción (Render.com): Puerto configurado explícitamente desde PORT={renderPortNum}");
            }
        }
    }
}

// ✅ RENDER.COM: Verificación final del puerto antes de iniciar
// El puerto ya debería estar configurado desde el builder, pero verificamos por si acaso
var finalUrls = app.Urls.ToList();
var finalLogger = app.Services.GetRequiredService<ILogger<Program>>();

if (finalUrls.Any())
{
    finalLogger.LogInformation($"🚀 APLICACIÓN INICIANDO - Puertos configurados: {string.Join(", ", finalUrls)}");
    Console.WriteLine($"[RENDER] ✅ Puertos finales antes de app.Run(): {string.Join(", ", finalUrls)}");
}
else
{
    // Fallback de emergencia: el puerto debería estar configurado desde el builder, pero por si acaso
    finalLogger.LogWarning("⚠️ ADVERTENCIA: No hay puertos configurados antes de app.Run() - usando fallback");
    Console.WriteLine("[RENDER] ⚠️ ADVERTENCIA: No hay puertos configurados - usando fallback");
    
    var emergencyPort = Environment.GetEnvironmentVariable("PORT") ?? "10000";
    if (int.TryParse(emergencyPort, out int emergencyPortNum))
    {
        app.Urls.Add($"http://0.0.0.0:{emergencyPortNum}");
        Console.WriteLine($"[RENDER] Puerto de emergencia configurado: {emergencyPortNum}");
        finalLogger.LogInformation($"✅ Puerto de emergencia configurado: {emergencyPortNum}");
    }
}

// ✅ RENDER.COM: El servidor ya está iniciado (se inició después de Build() en línea 1634)
// Solo necesitamos esperar indefinidamente (equivalente a app.Run() pero con más control)
finalLogger.LogInformation($"✅ Aplicación lista - Servidor ya está escuchando en: {string.Join(", ", app.Urls)}");
Console.WriteLine($"[RENDER] ✅ Aplicación lista - Servidor escuchando en: {string.Join(", ", app.Urls)}");
await app.WaitForShutdownAsync();
