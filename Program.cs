using Google.Cloud.SecretManager.V1;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Stripe;
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
using newApi.Middleware;

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

// Instancia el cliente de Secret Manager
var secretClient = SecretManagerServiceClient.Create();

// Funci�n para obtener secretos
string GetSecretValue(string secretName)
{
    // Primero intentar leer de variables de entorno (para override en Kubernetes)
    var envVarName = secretName.Replace("-", "_").ToUpper();
    var envValue = Environment.GetEnvironmentVariable(envVarName);
    if (!string.IsNullOrEmpty(envValue))
    {
        return envValue;
    }
    
    // Si no existe en variables de entorno, usar Secret Manager
    var projectId = "grup-441318";
    var secretVersion = secretClient.AccessSecretVersion($"projects/{projectId}/secrets/{secretName}/versions/latest");
    return secretVersion.Payload.Data.ToStringUtf8();
}

// ✅ FIX: Cargar Google Client IDs como array JSON compatible con GetSection().Get<string[]>()
var googleClientIdsFromEnv = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_IDS");
string[]? googleClientIds = null;

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
    var googleClientIdsFromSecret = GetSecretValue("google-client-ids");
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
    // ✅ FIX: Configurar como array JSON compatible con GetSection().Get<string[]>()
    // En lugar de solo claves indexadas, también configuramos como JSON string
    var configDict = new Dictionary<string, string?>();
    
    // Opción A: Configurar como array JSON (más compatible con GetSection().Get<string[]>())
    var clientIdsJson = System.Text.Json.JsonSerializer.Serialize(googleClientIds);
    configDict["Google:ClientIds"] = clientIdsJson;
    
    // Opción B: También configurar como claves indexadas para compatibilidad
    for (int i = 0; i < googleClientIds.Length; i++)
    {
        configDict[$"Google:ClientIds:{i}"] = googleClientIds[i];
    }
    
    builder.Configuration.AddInMemoryCollection(configDict);
    
    var googleConfigLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
    googleConfigLogger.LogInformation($"✅ Google Client IDs configurados: {googleClientIds.Length} ID(s) encontrado(s)");
    googleConfigLogger.LogInformation($"✅ Client IDs: {string.Join(", ", googleClientIds)}");
    googleConfigLogger.LogInformation($"✅ Client IDs JSON: {clientIdsJson}");
}
else
{
    var googleConfigLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
    googleConfigLogger.LogWarning("⚠️ No se encontraron Google Client IDs en variables de entorno ni en Secret Manager");
}

builder.Configuration["Jwt:Key"] = GetSecretValue("jwt-key");
builder.Configuration["Jwt:Issuer"] = GetSecretValue("jwt-issuer");
builder.Configuration["Jwt:Audience"] = GetSecretValue("jwt-audience");
builder.Configuration["RabbitMQ:Password"] = GetSecretValue("rabbitmq-password");
builder.Configuration["OpenAI:ApiKey"] = GetSecretValue("openai-api-key");

// Configurar Stripe según el entorno (desarrollo vs producción)
var isDevelopment = builder.Environment.IsDevelopment();
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
    builder.Configuration["Stripe:SecretKey"] = GetSecretValue("stripe-secret-key");
    builder.Configuration["Stripe:WebhookSecret"] = GetSecretValue("stripe-webhook-secret");
    builder.Configuration["Stripe:GeneralWebhookSecret"] = GetSecretValue("stripe-general-webhook-secret");
}

builder.Configuration["Twilio:AccountSid"] = GetSecretValue("twilio-account-sid");
builder.Configuration["Twilio:AuthToken"] = GetSecretValue("twilio-auth-token");
builder.Configuration["Twilio:VerificationServiceSid"] = GetSecretValue("twilio-verification-service-sid");
builder.Configuration["GoogleCloud:BucketName"] = "atrapobucket";

// Configuración de Email (opcional - si no está configurado, no se enviarán emails)
// Puede usar SMTP de hosting propio, Gmail, SendGrid, etc.
try
{
    builder.Configuration["Email:SmtpHost"] = GetSecretValue("email-smtp-host") ?? "";
    // ⚠️ RECOMENDACIÓN: Usar puerto 587 (STARTTLS) en lugar de 465 (SSL) para mejor compatibilidad
    builder.Configuration["Email:SmtpPort"] = GetSecretValue("email-smtp-port") ?? "587";
    builder.Configuration["Email:SmtpUsername"] = GetSecretValue("email-smtp-username") ?? "";
    builder.Configuration["Email:SmtpPassword"] = GetSecretValue("email-smtp-password") ?? "";
    builder.Configuration["Email:FromEmail"] = GetSecretValue("email-from-email") ?? "info@inspecciono.com";
    builder.Configuration["Email:FromName"] = GetSecretValue("email-from-name") ?? "Inspecciono";
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
    // En desarrollo: usar valores de desarrollo o desde Secret Manager
    var dbHost = GetSecretValue("postgres-host") ?? "localhost";
    var dbPort = GetSecretValue("postgres-port") ?? "5432";
    var dbUsername = GetSecretValue("postgres-username") ?? "postgres";
    var dbPassword = GetSecretValue("postgres-password") ?? "postgres";
    var dbName = GetSecretValue("postgres-database") ?? "newapi";
    
    connectionString = $"Host={dbHost};Port={dbPort};Username={dbUsername};Password={dbPassword};Database={dbName};Timeout=30;CommandTimeout=30;ConnectionIdleLifetime=300;ConnectionPruningInterval=10;";
}
else
{
    // En producción: OBLIGATORIO desde Secret Manager (sin fallbacks de producción)
    var dbHost = GetSecretValue("postgres-host") ?? throw new InvalidOperationException("postgres-host secret is required in production");
    var dbPort = GetSecretValue("postgres-port") ?? throw new InvalidOperationException("postgres-port secret is required in production");
    var dbUsername = GetSecretValue("postgres-username") ?? throw new InvalidOperationException("postgres-username secret is required in production");
    var dbPassword = GetSecretValue("postgres-password") ?? throw new InvalidOperationException("postgres-password secret is required in production");
    var dbName = GetSecretValue("postgres-database") ?? throw new InvalidOperationException("postgres-database secret is required in production");
    
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
    // TODO: Fix Microsoft.OpenApi.Models namespace issue
    // Temporarily commented out until we resolve the package reference
    // c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    // {
    //     Description = "JWT Authorization header (optional for Swagger testing)",
    //     Name = "Authorization",
    //     In = Microsoft.OpenApi.Models.ParameterLocation.Header,
    //     Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey
    // });
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

// AutoMapper will be registered later with all assemblies

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

// Register Google Signed URL Service
builder.Services.AddScoped<newApi.Services.ISignedUrlService, newApi.Services.GoogleSignedUrlService>();

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

// Configure HttpClient with IPv4 preference and increased timeout
// This helps with DNS resolution issues in Kubernetes pods
// Note: Google.Apis.Auth uses its own HttpClient, but this configuration
// helps with general HTTP requests and may be picked up by some libraries
builder.Services.AddHttpClient("default")
    .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
    });

// Configure .NET to prefer IPv4 for DNS resolution
// This is a global setting that affects all HttpClient instances
AppContext.SetSwitch("System.Net.Sockets.Socket.ForceIPv4", true);

// Add Health Checks
builder.Services.AddHealthChecks();

// Register AutoMapper with all mapping profiles
// AutoMapper will scan the assemblies for profiles
builder.Services.AddAutoMapper(cfg => {
    cfg.AddMaps(typeof(AdMappingProfile).Assembly);
    cfg.AddMaps(typeof(PlatformMappingProfile).Assembly);
    cfg.AddMaps(typeof(CategoryMappingProfile).Assembly);
    cfg.AddMaps(typeof(UserMappingProfile).Assembly);
});

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

// ✅ SEGURIDAD 2025: Verificar MFA cuando está habilitado
app.UseRequireMfa();

// Add health check endpoint
app.MapHealthChecks("/health");

app.MapControllers();
app.MapHub<ChatHub>("/chatHub");

app.Urls.Add("http://0.0.0.0:7124");

app.Run();

