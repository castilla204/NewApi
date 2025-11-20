using Google.Cloud.SecretManager.V1;
using Google.Api.Gax.Grpc;
using Grpc.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Stripe;
using System.IO;
using System.Text;
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

// Instancia el cliente de Secret Manager solo si NO está en desarrollo
SecretManagerServiceClient? secretClient = null;
bool secretManagerAvailable = false;
if (!isDevelopment)
{
    // Verificar si el archivo de credenciales existe
    var credentialsPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
    builder.Logging.AddConsole();
    var initLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
    
    initLogger.LogInformation($"=== INICIALIZANDO SECRET MANAGER ===");
    initLogger.LogInformation($"GOOGLE_APPLICATION_CREDENTIALS: {credentialsPath ?? "NO CONFIGURADO"}");
    
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
                
                // Crear el cliente de Secret Manager con configuración mejorada
                initLogger.LogInformation("Creando cliente de Secret Manager...");
                
                // Configurar el cliente con opciones específicas para Kubernetes
                // El problema puede ser que gRPC necesita configuración especial para HTTP/2 en Kubernetes
                var clientBuilder = new SecretManagerServiceClientBuilder();
                
                // Configurar el endpoint explícitamente
                var endpoint = "secretmanager.googleapis.com:443";
                clientBuilder.Endpoint = endpoint;
                
                // Configurar opciones de gRPC con timeouts más largos
                // Esto es crítico para Kubernetes donde las conexiones pueden ser más lentas
                clientBuilder.GrpcAdapter = GrpcNetClientAdapter.Default.WithAdditionalOptions(options =>
                {
                    // Configurar timeouts más largos para la conexión inicial
                    options.MaxReceiveMessageSize = 4 * 1024 * 1024; // 4MB
                    options.MaxSendMessageSize = 4 * 1024 * 1024; // 4MB
                });
                
                secretClient = clientBuilder.Build();
                
                initLogger.LogInformation($"Cliente de Secret Manager creado exitosamente (endpoint: {endpoint})");
                
                // Forzar IPv4 a nivel de sistema si es posible
                // Esto ayuda a evitar problemas cuando IPv6 no está disponible
                try
                {
                    // Establecer variable de entorno para forzar IPv4 en .NET
                    Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_DISABLEIPV6", "1");
                    initLogger.LogInformation("IPv6 deshabilitado para forzar IPv4");
                }
                catch
                {
                    // Ignorar si no se puede establecer
                }
                
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
            initLogger.LogWarning("Usando solo variables de entorno como fallback.");
        }
    }
    else
    {
        initLogger.LogWarning("GOOGLE_APPLICATION_CREDENTIALS no está configurado. Usando solo variables de entorno.");
    }
    
    initLogger.LogInformation($"Secret Manager disponible: {secretManagerAvailable}");
}

// Función para obtener secretos
string? GetSecretValue(string secretName, string? defaultValue = null)
{
    // En desarrollo, usar valor por defecto si está disponible
    if (isDevelopment)
    {
        return defaultValue;
    }
    
    // En producción, USAR SECRET MANAGER (prioridad absoluta)
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

// Cargar secretos de Google Cloud Secret Manager
var googleClientIds = GetSecretValue("google-client-ids", null)
                      ?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                      .Select(id => id.Trim())
                      .ToArray();

if (googleClientIds != null && googleClientIds.Length > 0)
{
    var configDict = new Dictionary<string, string>();
    for (int i = 0; i < googleClientIds.Length; i++)
    {
        configDict[$"Google:ClientIds:{i}"] = googleClientIds[i];
    }
    builder.Configuration.AddInMemoryCollection(configDict);
}

builder.Configuration["Jwt:Key"] = GetSecretValue("jwt-key", null) ?? "";
builder.Configuration["Jwt:Issuer"] = GetSecretValue("jwt-issuer", null) ?? "";
builder.Configuration["Jwt:Audience"] = GetSecretValue("jwt-audience", null) ?? "";
builder.Configuration["RabbitMQ:Password"] = GetSecretValue("rabbitmq-password", null) ?? "";
builder.Configuration["OpenAI:ApiKey"] = GetSecretValue("openai-api-key", null) ?? "";
if (isDevelopment)
{
    // En desarrollo: usar variables de entorno o User Secrets
    // NUNCA hardcodear secretos en el código
    // Configurar con: dotnet user-secrets set "Stripe:SecretKey" "valor"
    // O usar variables de entorno: STRIPE_SECRET_KEY
    if (string.IsNullOrEmpty(builder.Configuration["Stripe:SecretKey"]))
    {
        builder.Configuration["Stripe:SecretKey"] = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY") ?? "";
    }
    if (string.IsNullOrEmpty(builder.Configuration["Stripe:WebhookSecret"]))
    {
        builder.Configuration["Stripe:WebhookSecret"] = Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET") ?? "";
    }
    if (string.IsNullOrEmpty(builder.Configuration["Stripe:GeneralWebhookSecret"]))
    {
        builder.Configuration["Stripe:GeneralWebhookSecret"] = Environment.GetEnvironmentVariable("STRIPE_GENERAL_WEBHOOK_SECRET") ?? "";
    }
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
    // Usar localhost:5433 para conectarse a través del túnel SSH
    var existingConnectionString = builder.Configuration.GetConnectionString("PostgresConnection");
    
    if (!string.IsNullOrEmpty(existingConnectionString))
    {
        // Usar connection string desde appsettings.Development.json o user secrets
        connectionString = existingConnectionString;
    }
    else
    {
        // Construir desde variables de entorno individuales (para túnel local)
        var dbHost = Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost";
        var dbPort = Environment.GetEnvironmentVariable("DB_PORT") ?? "5433"; // Puerto del túnel SSH
        var dbUsername = Environment.GetEnvironmentVariable("DB_USERNAME") ?? "postgres";
        var dbPassword = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "postgres";
        var dbName = Environment.GetEnvironmentVariable("DB_NAME") ?? "newapi";
        
        connectionString = $"Host={dbHost};Port={dbPort};Username={dbUsername};Password={dbPassword};Database={dbName};Timeout=30;CommandTimeout=30;ConnectionIdleLifetime=300;ConnectionPruningInterval=10;";
    }
}
else
{
    // En producción: Intentar desde Secret Manager, pero usar variables de entorno como fallback
    // Esto permite que la app funcione aunque Secret Manager no esté disponible temporalmente
    var dbHost = GetSecretValue("postgres-host") ?? Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "postgres-svc";
    var dbPort = GetSecretValue("postgres-port") ?? Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
    var dbUsername = GetSecretValue("postgres-username") ?? Environment.GetEnvironmentVariable("POSTGRES_USERNAME");
    var dbPassword = GetSecretValue("postgres-password") ?? Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
    var dbName = GetSecretValue("postgres-database") ?? Environment.GetEnvironmentVariable("POSTGRES_DATABASE") ?? "newapi";
    
    // Si no hay credenciales de DB, lanzar error claro
    if (string.IsNullOrEmpty(dbUsername) || string.IsNullOrEmpty(dbPassword))
    {
        throw new InvalidOperationException(
            "Database credentials are required in production. " +
            "Configure via Secret Manager (postgres-username, postgres-password) " +
            "or environment variables (POSTGRES_USERNAME, POSTGRES_PASSWORD). " +
            "Secret Manager status: " + (secretManagerAvailable ? "Available but failed to retrieve secrets" : "Not available"));
    }
    
    connectionString = $"Host={dbHost};Port={dbPort};Username={dbUsername};Password={dbPassword};Database={dbName};Timeout=30;CommandTimeout=30;ConnectionIdleLifetime=300;ConnectionPruningInterval=10;";
}

builder.Configuration["ConnectionStrings:PostgresConnection"] = connectionString;

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
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header (optional for Swagger testing)",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey
    });
});

// ✅ SEGURIDAD 2025: Configurar Rate Limiting nativo de .NET 8
builder.Services.AddRateLimiter(options =>
{
    // 1. Política para autenticación: 5 intentos cada 5 minutos por IP
    options.AddFixedWindowLimiter("auth", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromMinutes(5);
        opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0; // No permitir cola
    });

    // 2. Política para API general: 100 requests por minuto por IP
    options.AddFixedWindowLimiter("api", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 2; // Permitir 2 requests en cola
    });

    // 3. Política para operaciones de pago: 10 por minuto por usuario
    options.AddFixedWindowLimiter("payment", opt =>
    {
        opt.PermitLimit = 10;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0;
    });

    // 4. Política para admin: 200 requests por minuto
    options.AddFixedWindowLimiter("admin", opt =>
    {
        opt.PermitLimit = 200;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 5;
    });

    // 5. Política global por IP: 1000 requests por hora
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<Microsoft.AspNetCore.Http.HttpContext, string>(httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 1000,
                Window = TimeSpan.FromHours(1)
            }));

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

builder.Services.AddAutoMapper(typeof(AdMappingProfile).Assembly,
    typeof(PlatformMappingProfile).Assembly,
    typeof(CategoryMappingProfile).Assembly,
    typeof(UserMappingProfile).Assembly);

// Configure SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
})
.AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.PropertyNamingPolicy = null;
});

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
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not found in configuration."))),
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
        npgsqlOptions.CommandTimeout(30);
        npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
    }));

// Configure Google Cloud Storage
builder.Services.AddSingleton(StorageClient.Create());

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


// Configure Hangfire
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

builder.Services.AddHangfireServer();

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

// Register AutoMapper
builder.Services.AddAutoMapper(typeof(AdMappingProfile).Assembly, typeof(PlatformMappingProfile).Assembly, typeof(CategoryMappingProfile).Assembly);

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


// Schedule recurring job with Hangfire
app.UseHangfireDashboard("/hangfire");

// ✅ SEGURIDAD 2025: Configurar limpieza automática de refresh tokens
// Se ejecuta todos los días a las 3:00 AM
RecurringJob.AddOrUpdate<RefreshTokenCleanupService>(
    "cleanup-expired-refresh-tokens",
    service => service.CleanupExpiredTokensAsync(),
    Cron.Daily(3), // 3:00 AM todos los días
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

// Add health check endpoint
app.MapHealthChecks("/health");

app.MapControllers();
app.MapHub<ChatHub>("/chatHub");

app.Urls.Add("http://0.0.0.0:7124");

app.Run();
