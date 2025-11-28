using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using newApi.DataLayer.Models;
using newApi.Filters;
using newApi.Services;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Database
var connectionString = builder.Configuration.GetConnectionString("PostgresConnection") 
    ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
    ?? throw new InvalidOperationException("PostgresConnection string not found");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"] 
    ?? Environment.GetEnvironmentVariable("JWT_KEY")
    ?? throw new InvalidOperationException("JWT Key not found");

var jwtIssuer = builder.Configuration["Jwt:Issuer"] 
    ?? Environment.GetEnvironmentVariable("JWT_ISSUER")
    ?? "https://rutascomerciales.retocsv.es/";

var jwtAudience = builder.Configuration["Jwt:Audience"] 
    ?? Environment.GetEnvironmentVariable("JWT_AUDIENCE")
    ?? "https://rutascomerciales.retocsv.es/";

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ValidateIssuer = !string.IsNullOrEmpty(jwtIssuer),
        ValidIssuer = jwtIssuer,
        ValidateAudience = !string.IsNullOrEmpty(jwtAudience),
        ValidAudience = jwtAudience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization();

// CORS - Permitir iframes desde el frontend
var frontendUrl = builder.Configuration["Frontend:Url"] 
    ?? Environment.GetEnvironmentVariable("FRONTEND_URL")
    ?? "https://inspecciono.com";

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(frontendUrl)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials()
              .WithExposedHeaders("*");
    });
});

// Hangfire Configuration
builder.Services.AddHangfire(config =>
{
    config.UsePostgreSqlStorage(connectionString, new PostgreSqlStorageOptions
    {
        SchemaName = "hangfire"
    });
    
    // Configurar reintentos globales
    config.UseDefaultTypeSerializer();
    config.UseRecommendedSerializerSettings();
});

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = Environment.ProcessorCount * 5;
    options.ServerTimeout = TimeSpan.FromMinutes(4);
    options.SchedulePollingInterval = TimeSpan.FromSeconds(15);
});

// Register services
builder.Services.AddScoped<ILoggingService, LoggingService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<MfaService>();
builder.Services.AddScoped<HangfireFailedJobNotificationFilter>();

// Add Hangfire filters
GlobalJobFilters.Filters.Add(new HangfireFailedJobNotificationFilter(
    builder.Services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()));

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");

// Authentication & Authorization
app.UseAuthentication();
app.UseAuthorization();

// Hangfire Dashboard - Configurado después de UseAuthorization para que el filtro pueda acceder al usuario
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAuthorizationFilter(app.Configuration) },
    DashboardTitle = "Inspecciono - Hangfire Dashboard",
    // Permitir iframes desde el frontend
    IsReadOnlyFunc = (DashboardContext context) => false,
    // Configurar para permitir iframes
    IgnoreAntiforgeryToken = true
});

app.MapControllers();

app.Run();

