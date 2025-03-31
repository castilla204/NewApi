using Google.Cloud.SecretManager.V1;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Stripe;
using System.Text;
using RabbitMQ.Client;
using newApi.RabbitMQ;
using newApi.Services;
using DataLayer.Models;
using DataLayer;
using newApi.DataLayer;
using Google.Apis.Auth.OAuth2;

var builder = WebApplication.CreateBuilder(args);




// Instancia el cliente de Secret Manager
//coge automaticamente la variable del sistema con nombre GOOGLE_APPLICATION_CREDENTIALS que debe apuntar al json descargado donde se ha descargado en nuestro pc de nuestra cuenta de servicio con los roles de ver permisos
var secretClient = SecretManagerServiceClient.Create();

// Función para obtener secretos
string GetSecretValue(string secretName)
{
    // Accede al secreto desde Google Cloud Secret Manager
    var projectId = "grup-441318";
    var secretVersion = secretClient.AccessSecretVersion($"projects/{projectId}/secrets/{secretName}/versions/latest");
    return secretVersion.Payload.Data.ToStringUtf8();
}



// Cargar secretos de Google Cloud Secret Manager
//-como lo que se recibe son dos valores y hay que guardarlo en un array se hace esto
var googleClientIds = GetSecretValue("google-client-ids")
                      ?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                      .Select(id => id.Trim())
                      .ToArray();

if (googleClientIds != null && googleClientIds.Length > 0)
{
    var configDict = new Dictionary<string, string>();

    // Estructuramos el array como lo haría appsettings.json
    for (int i = 0; i < googleClientIds.Length; i++)
    {
        configDict[$"Google:ClientIds:{i}"] = googleClientIds[i];
    }

    // Agregamos la configuración en memoria para que se comporte como un JSON real
    builder.Configuration.AddInMemoryCollection(configDict);
}

//-
builder.Configuration["Jwt:Key"] = GetSecretValue("jwt-key");
builder.Configuration["Jwt:Issuer"] = GetSecretValue("jwt-issuer");
builder.Configuration["Jwt:Audience"] = GetSecretValue("jwt-audience");
builder.Configuration["RabbitMQ:Password"] = GetSecretValue("rabbitmq-password");
builder.Configuration["ConnectionStrings:PostgresConnection"] = $"Host=localhost;Port=5432;Username=postgres;Password={GetSecretValue("postgres-password")};Database=grup";
builder.Configuration["OpenAI:ApiKey"] = GetSecretValue("openai-api-key");
builder.Configuration["Stripe:SecretKey"] = GetSecretValue("stripe-secret-key");
builder.Configuration["Stripe:WebhookSecret"] = GetSecretValue("stripe-webhook-secret");
builder.Configuration["Twilio:AccountSid"] = GetSecretValue("twilio-account-sid");
builder.Configuration["Twilio:AuthToken"] = GetSecretValue("twilio-auth-token");
builder.Configuration["Twilio:VerificationServiceSid"] = GetSecretValue("twilio-verification-service-sid");

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

// Configure RabbitMQ
builder.Services.AddSingleton<IConnectionFactory>(sp =>
    new ConnectionFactory
    {
        HostName = builder.Configuration["RabbitMQ:HostName"] ?? "localhost",
        UserName = builder.Configuration["RabbitMQ:UserName"] ?? "guest",
        Password = builder.Configuration["RabbitMQ:Password"] ?? "guest"
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
builder.Services.AddHttpClient();

// Register AutoMapper
builder.Services.AddAutoMapper(typeof(AdMappingProfile).Assembly, typeof(PlatformMappingProfile).Assembly, typeof(CategoryMappingProfile).Assembly);  // Registrar el perfil de AutoMapper

var app = builder.Build();

// Configure Stripe
StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"];

// Habilita Swagger solo en el entorno de desarrollo
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

app.Urls.Add("http://0.0.0.0:7124");  /// Para acceso en red local

app.Run();
