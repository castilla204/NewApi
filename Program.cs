using Google.Cloud.SecretManager.V1;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Stripe;
using System.Text;
using RabbitMQ.Client;
using newApi.RabbitMQ;
using newApi.Services;
using DataLayer;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using newApi.DataLayer.Models;
using newApi.DataLayer;

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
builder.Configuration["Stripe:SecretKey"] = "sk_test_51RDr9QR7PVKiStYueTMBRCFZGySidpx1f07iqqJYt9K4KqvAmgZHi6V1GDVpRyQxU3U08OcgGZDcHuqg7MpZaf0l004FRtdQym";  //el modo de prueba stripe
//builder.Configuration["Stripe:SecretKey"] = GetSecretValue("stripe-secret-key"); 
//builder.Configuration["Stripe:WebhookSecret"] = GetSecretValue("stripe-webhook-secret");
builder.Configuration["Stripe:WebhookSecret"] = "whsec_N7DIsIAyUHN1oMRK6hGZOIUDD3CiP86N"; // webhook secret para pruebas
builder.Configuration["Twilio:AccountSid"] = GetSecretValue("twilio-account-sid");
builder.Configuration["Twilio:AuthToken"] = GetSecretValue("twilio-auth-token");
builder.Configuration["Twilio:VerificationServiceSid"] = GetSecretValue("twilio-verification-service-sid");
builder.Configuration["GoogleCloud:BucketName"] = "atrapobucket";

// Configurar la cadena de conexión según el entorno
if (builder.Environment.IsDevelopment())
{
    builder.Configuration["ConnectionStrings:PostgresConnection"] = "Host=localhost;Port=5432;Username=postgres;Password=coche109;Database=grup";
}
else
{
    builder.Configuration["ConnectionStrings:PostgresConnection"] = "Host=localhost;Port=5432;Username=postgres;Password=coche109;Database=grup";
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
});

// Configure PostgreSQL
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgresConnection")));

// Configure Google Cloud Storage
builder.Services.AddSingleton(StorageClient.Create());

// Configure RabbitMQ
builder.Services.AddSingleton<IConnectionFactory>(sp =>
{
    var config = builder.Configuration;
    var isDevelopment = builder.Environment.IsDevelopment();
    return new ConnectionFactory
    {
        HostName = isDevelopment ? "localhost" : config["RABBITMQ_HOSTNAME"] ?? "rabbitmq-svc",
        Port = int.Parse(config["RABBITMQ_PORT"] ?? "5672"),
        UserName = config["RABBITMQ_USERNAME"] ?? "admin",
        Password = config["RABBITMQ_PASSWORD"] ?? "Pedrohabo1//"
    };
});

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", builder =>
    {
        builder
               .AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
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

// Register Service Implementations
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<SearchServiceService>();
builder.Services.AddScoped<SearchHireService>();

builder.Services.AddHttpClient();

// Register AutoMapper
builder.Services.AddAutoMapper(typeof(AdMappingProfile).Assembly, typeof(PlatformMappingProfile).Assembly, typeof(CategoryMappingProfile).Assembly);

var app = builder.Build();

// Configure Stripe
StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"];

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

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Urls.Add("http://0.0.0.0:7124");

app.Run();