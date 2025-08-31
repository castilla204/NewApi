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

var builder = WebApplication.CreateBuilder(args);

// Instancia el cliente de Secret Manager
var secretClient = SecretManagerServiceClient.Create();

// Función para obtener secretos
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
builder.Configuration["Stripe:SecretKey"] = "__REDACTED_STRIPE_SECRET__";
builder.Configuration["Stripe:WebhookSecret"] = "__REDACTED_STRIPE_WEBHOOK__";
builder.Configuration["Twilio:AccountSid"] = GetSecretValue("twilio-account-sid");
builder.Configuration["Twilio:AuthToken"] = GetSecretValue("twilio-auth-token");
builder.Configuration["Twilio:VerificationServiceSid"] = GetSecretValue("twilio-verification-service-sid");
builder.Configuration["GoogleCloud:BucketName"] = "atrapobucket";

// Configurar la cadena de conexión según el entorno
if (builder.Environment.IsDevelopment())
{
    builder.Configuration["ConnectionStrings:PostgresConnection"] = "Host=185.166.39.4;Port=30000;Username=admin;Password=__REDACTED_CREDENTIAL__;Database=atrapo";
}
else
{
    builder.Configuration["ConnectionStrings:PostgresConnection"] = "Host=185.166.39.4;Port=30000;Username=admin;Password=__REDACTED_CREDENTIAL__;Database=atrapo";
}

// Add services to the container
builder.Services.AddControllers();
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
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgresConnection")));

// Configure Google Cloud Storage
builder.Services.AddSingleton(StorageClient.Create());

// Configure RabbitMQ
builder.Services.AddSingleton<RabbitMQ.Client.IConnectionFactory>(sp =>
{
    var config = builder.Configuration;
    var isDevelopment = builder.Environment.IsDevelopment();
    return new ConnectionFactory
    {
        HostName = isDevelopment ? "localhost" : config["RABBITMQ_HOSTNAME"] ?? "rabbitmq-svc",
        Port = int.Parse(config["RABBITMQ_PORT"] ?? "5672"),
        UserName = config["RABBITMQ_USERNAME"] ?? "admin",
        Password = config["RABBITMQ_PASSWORD"] ?? "__REDACTED_CREDENTIAL__"
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
            "https://atrapo.io") // <--- agregar dominio de frontend producción
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
    .UsePostgreSqlStorage(builder.Configuration.GetConnectionString("PostgresConnection")));

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
builder.Services.AddScoped<ICheckingClientDecisionService, CheckingClientDecisionService>();

builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<SearchServiceService>();
builder.Services.AddScoped<SearchHireService>();

builder.Services.AddHttpClient();

// Register AutoMapper
builder.Services.AddAutoMapper(typeof(AdMappingProfile).Assembly, typeof(PlatformMappingProfile).Assembly, typeof(CategoryMappingProfile).Assembly);

var app = builder.Build();

// Configure Stripe
StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"];

// Schedule recurring job with Hangfire
app.UseHangfireDashboard("/hangfire");
GlobalConfiguration.Configuration
    .UseActivator(new Hangfire.AspNetCore.AspNetCoreJobActivator(app.Services.GetRequiredService<IServiceScopeFactory>()));

RecurringJob.AddOrUpdate<ISubscriptionService>(
    "process-expired-services",
    service => service.ProcessExpiredServicesAsync(),
    Cron.Hourly);

RecurringJob.AddOrUpdate<ISubscriptionService>(
    "process-awaiting-client-decision",
    service => service.ProcessAwaitingClientDecisionAsync(),
    "*/5 * * * *"); // Cada 5 minutos


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
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ChatHub>("/chatHub");

app.Urls.Add("http://0.0.0.0:7124");

app.Run();