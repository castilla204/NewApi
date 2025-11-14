using Google.Cloud.SecretManager.V1;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Stripe;
using System.Text;
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

// Instancia el cliente de Secret Manager
var secretClient = SecretManagerServiceClient.Create();

// Funci�n para obtener secretos
string GetSecretValue(string secretName)
{
    var projectId = "grup-441318";
    var secretVersion = secretClient.AccessSecretVersion($"projects/{projectId}/secrets/{secretName}/versions/latest");
    return secretVersion.Payload.Data.ToStringUtf8();
}

// Cargar secretos de Google Cloud Secret Manager
var googleClientIds = GetSecretValue("google-client-ids")
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

builder.Configuration["Jwt:Key"] = GetSecretValue("jwt-key");
builder.Configuration["Jwt:Issuer"] = GetSecretValue("jwt-issuer");
builder.Configuration["Jwt:Audience"] = GetSecretValue("jwt-audience");
builder.Configuration["RabbitMQ:Password"] = GetSecretValue("rabbitmq-password");
builder.Configuration["OpenAI:ApiKey"] = GetSecretValue("openai-api-key");

// Configurar Stripe según el entorno (desarrollo vs producción)
var isDevelopment = builder.Environment.IsDevelopment();
if (isDevelopment)
{
    // En desarrollo: valores hardcodeados (no van al Secret Manager)
    builder.Configuration["Stripe:SecretKey"] = "sk_test_51RDr9QR7PVKiStYueTMBRCFZGySidpx1f07iqqJYt9K4KqvAmgZHi6V1GDVpRyQxU3U08OcgGZDcHuqg7MpZaf0l004FRtdQym";
    builder.Configuration["Stripe:WebhookSecret"] = "whsec_YMCdzRPhKbpE0xke1L1YP3TPQnkO2UHt";
    builder.Configuration["Stripe:GeneralWebhookSecret"] = "whsec_6rVBKKefl9hkihHD82LIA828KF5usVxJ";
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
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header (optional for Swagger testing)",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey
    });
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
    options.RequireHttpsMetadata = false;
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
            "https://inspecciono.com") // <--- agregar dominio de frontend producci�n
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
