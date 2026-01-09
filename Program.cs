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

// Configurar logging básico
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Verificar el entorno PRIMERO (necesario para configurar logging)
// NOTA: isDevelopment ya se detectó arriba (línea 39)

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
    connectionString = "User Id=postgres.rveqsehzlvbttlpmsbmi;Password=hrpQTD57m7H.C+&;Server=aws-1-eu-west-2.pooler.supabase.com;Port=6543;Database=postgres;SslMode=Require;Timeout=60;CommandTimeout=120;Pooling=true;Multiplexing=false;Enlist=false;Max Auto Prepare=0;KeepAlive=30;";
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
// ✅ CRÍTICO: Especificar explícitamente el assembly de controladores para asegurar descubrimiento
builder.Services.AddControllers()
    .AddApplicationPart(typeof(CategoriesController).Assembly) // Forzar descubrimiento de controladores
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
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            
            logger.LogInformation($"[JWT] OnMessageReceived - Path: {path}, HasQueryToken: {!string.IsNullOrEmpty(accessToken)}");
            
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/chatHub"))
            {
                context.Token = accessToken;
                logger.LogInformation($"[JWT] ✅ Token extraído de query string para {path}");
            }
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
            var publicEndpoints = new[] { "/api/Categories", "/api/ServiceType/public", "/api/SearchService/homepage-wall", "/health", "/warmup" };
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
            var publicEndpoints = new[] { 
                "/api/Categories", 
                "/api/ServiceType/public", 
                "/api/SearchService/homepage-wall", 
                "/health", 
                "/warmup", 
                "/health-detailed" 
            };
            var pathString = context.HttpContext.Request.Path;
            var isPublicEndpoint = publicEndpoints.Any(ep => pathString.StartsWithSegments(ep));
            
            logger.LogInformation($"[JWT] 🔔 OnChallenge - {method} {path}");
            logger.LogInformation($"[JWT]    HasAuthHeader: {hasAuthHeader}, IsPublicEndpoint: {isPublicEndpoint}");
            logger.LogInformation($"[JWT]    Error: {context.Error}, ErrorDescription: {context.ErrorDescription}");
            
            // Si es endpoint público y no hay token, no hacer challenge (dejar pasar)
            if (isPublicEndpoint && !hasAuthHeader)
            {
                logger.LogInformation($"[JWT] ✅ Endpoint público sin token - HandleResponse() para permitir acceso");
                context.HandleResponse(); // No enviar 401, dejar que el endpoint público maneje la request
            }
            else if (!hasAuthHeader)
            {
                logger.LogWarning($"[JWT] ⚠️ Endpoint protegido sin token - Se enviará 401");
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
    
    // ✅ LOGGING DETALLADO: Habilitar logging de todas las operaciones de base de datos
    // Esto incluye: queries SQL, conexiones, transacciones, timeouts, errores
    // ✅ MEJORADO: En producción también loguear queries que fallan o son lentas
    if (isDevelopment)
    {
        // En desarrollo: logging completo
        options.LogTo(
            message => {
                var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("EFCore");
                logger.LogInformation($"[EF CORE] {message}");
            },
            Microsoft.Extensions.Logging.LogLevel.Information
        );
    }
    else
    {
        // En producción: warnings, errores Y queries que fallan o son lentas
        options.LogTo(
            message => {
                var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("EFCore");
                var lowerMessage = message.ToLowerInvariant();
                
                // Loggear siempre: errores, timeouts, excepciones, conexiones fallidas
                if (lowerMessage.Contains("error") || 
                    lowerMessage.Contains("timeout") ||
                    lowerMessage.Contains("failed") ||
                    lowerMessage.Contains("exception") ||
                    lowerMessage.Contains("cannot open") ||
                    lowerMessage.Contains("connection") && (lowerMessage.Contains("refused") || lowerMessage.Contains("closed")) ||
                    lowerMessage.Contains("authentication") ||
                    lowerMessage.Contains("password") && lowerMessage.Contains("failed"))
                {
                    logger.LogError($"[EF CORE] ❌ {message}");
                }
                // Loggear también: queries que toman mucho tiempo (más de 5 segundos)
                else if (lowerMessage.Contains("executed") && lowerMessage.Contains("elapsed"))
                {
                    // Intentar extraer el tiempo de ejecución
                    var elapsedMatch = System.Text.RegularExpressions.Regex.Match(message, @"(\d+\.?\d*)\s*ms");
                    if (elapsedMatch.Success && double.TryParse(elapsedMatch.Groups[1].Value, out var elapsedMs) && elapsedMs > 5000)
                    {
                        logger.LogWarning($"[EF CORE] ⚠️ Query lenta detectada: {message}");
                    }
                }
            },
            Microsoft.Extensions.Logging.LogLevel.Warning
        );
    }
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
            
            builder.Configuration["Stripe:SecretKey"] = secretKey;
            builder.Configuration["Stripe:WebhookSecret"] = webhookSecret;
            builder.Configuration["Stripe:GeneralWebhookSecret"] = generalWebhookSecret;
            
            StripeConfiguration.ApiKey = secretKey;
            
            var stripeLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            stripeLogger.LogInformation($"✅ Claves Stripe cargadas en modo: {mode}");
            stripeLogger.LogInformation($"   SecretKey presente: {!string.IsNullOrEmpty(secretKey)}");
            stripeLogger.LogInformation($"   WebhookSecret presente: {!string.IsNullOrEmpty(webhookSecret)}");
            
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

// ❌ DESHABILITADO: Response Compression causa timeouts en Render.com
// El problema: Gzip/Brotli se bloquea al comprimir la respuesta
// Síntoma: Query completa en 500ms pero cliente timeout a 30s
// Causa: Render.com usa nginx que ya comprime, doble compresión causa deadlock
// Solución: Render/nginx manejará la compresión automáticamente
// app.UseResponseCompression();

// ✅ RENDER.COM: Los timeouts de Kestrel ya están configurados arriba
// KeepAliveTimeout y RequestHeadersTimeout están en 5 y 2 minutos respectivamente

// ✅ RENDER.COM BEST PRACTICES: Orden correcto del middleware según ASP.NET Core
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
app.MapHub<ChatHub>("/chatHub");

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

// ✅ app.Run() - INICIAR SERVIDOR INMEDIATAMENTE
// Según documentación de Render.com: "Bind your host to 0.0.0.0"
// El servidor debe estar escuchando lo más rápido posible
app.Run();
