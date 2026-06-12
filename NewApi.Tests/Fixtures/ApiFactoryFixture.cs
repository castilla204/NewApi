using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NewApi.Tests.StripeMocks;
using Stripe;
using Testcontainers.PostgreSql;

namespace NewApi.Tests.Fixtures;

/// <summary>
/// Arranca el backend REAL (Program.cs completo: pipeline HTTP, JWT auth, middleware MFA,
/// Hangfire, DI...) vía WebApplicationFactory&lt;Program&gt; contra un Postgres testcontainer
/// PROPIO. A diferencia del MarketplaceFlowSimulator (réplica del contrato), aquí cada
/// request ejecuta el código de producción de Controllers/Services de verdad.
///
/// Aislamiento de la BD real — CRÍTICO: appsettings.Development.json contiene la
/// connection string de Render (prod). Program.cs lee PRIMERO las variables de entorno
/// (ConnectionStrings__PostgresConnection en Program.cs:702; GetSecretValue:409-425 antes
/// que el fallback a config), así que este fixture fija env vars de proceso ANTES de
/// construir la factory para garantizar que el boot apunte SOLO al testcontainer:
///   - ConnectionStrings__PostgresConnection → testcontainer
///   - JWT_KEY / MFA_ENCRYPTION_KEY          → claves de test deterministas
///   - SKIP_MAPBOX_SMOKE_TEST=1              → evita Environment.Exit(1) del smoke (1743-1812)
///
/// El MigrateAsync de arranque (background, Program.cs:2702-2835) chocará con el esquema
/// EnsureCreated (42P07) pero está envuelto en try/catch y solo loguea — igual que en prod
/// (las migraciones EF divergen del esquema real; el seed SQL es la fuente de verdad).
/// </summary>
public sealed class ApiFactoryFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("inspecciono_api_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string ConnectionString => _container.GetConnectionString();

    // 64+ bytes (Program.cs:584 exige >=32; recomendado 64). Solo para tests.
    private const string TestJwtKey =
        "TEST-ONLY-jwt-signing-key-0123456789-0123456789-0123456789-ABCDEFGH";
    private const string TestMfaKey =
        "TEST-ONLY-mfa-encryption-key-0123456789-ABCDEFGHIJKLMNOP";

    /// <summary>Secret del endpoint Connect (POST /api/subscription/webhook).</summary>
    public const string WebhookSecret = "whsec_test_connect_secret";

    /// <summary>Secret del endpoint general (POST /api/subscription/webhook-general).</summary>
    public const string GeneralWebhookSecret = "whsec_test_general_secret";

    private const string TestStripeKey = "sk_test_fake_key_for_local_stub";

    /// <summary>
    /// Stub de la API de Stripe al que apunta StripeConfiguration.ApiBase. El backend
    /// real ejecuta sus capturas/transfers/refunds contra él (patrón stripe-mock).
    /// </summary>
    public FakeStripeServer FakeStripe { get; } = new();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Mismo bootstrap de esquema que PostgresContainerFixture: EnsureCreated
        // (las migraciones EF están desincronizadas de prod) + seed SQL canónico.
        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            var seedPath = Path.Combine(AppContext.BaseDirectory, "Resources", "SEED_ESTADOS_COMPLETO.sql");
            var seedSql = await System.IO.File.ReadAllTextAsync(seedPath);
            await db.Database.ExecuteSqlRawAsync(seedSql);
        }

        // Env vars de PROCESO: ganan a appsettings.Development.json en Program.cs.
        Environment.SetEnvironmentVariable("ConnectionStrings__PostgresConnection", ConnectionString);
        Environment.SetEnvironmentVariable("JWT_KEY", TestJwtKey);
        Environment.SetEnvironmentVariable("MFA_ENCRYPTION_KEY", TestMfaKey);
        Environment.SetEnvironmentVariable("SKIP_MAPBOX_SMOKE_TEST", "1");
        // HF-LOCAL GUARD v2 enciende el worker en Development con BD local (la del
        // testcontainer lo es) — en tests NO queremos un worker procesando jobs de
        // fondo (emails, timers) en paralelo a las aserciones. Kill-switch explícito.
        Environment.SetEnvironmentVariable("HANGFIRE_SERVER_DISABLED", "1");

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                // Development: salta HTTPS redirect/HSTS, smoke Mapbox con token vacío,
                // y habilita el fallback de secrets a config (GetSecretValue:440-457).
                b.UseEnvironment("Development");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    // Cinturón y tirantes para CI (sin appsettings.Development.json):
                    // estos valores in-memory son la última fuente → ganan al dev json.
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:PostgresConnection"] = ConnectionString,
                        ["Jwt:Key"] = TestJwtKey,
                        ["Secrets:jwt-key"] = TestJwtKey,
                        ["Secrets:mfa-encryption-key"] = TestMfaKey,
                        // Stripe determinista: el background task de Program.cs:1654-1664
                        // solo SOBREESCRIBE estos valores si las env vars traen algo —
                        // aquí no las hay, así que estos in-memory mandan (y pisan al
                        // appsettings.Development.json local).
                        ["Stripe:SecretKey"] = TestStripeKey,
                        ["Stripe:WebhookSecret"] = WebhookSecret,
                        ["Stripe:GeneralWebhookSecret"] = GeneralWebhookSecret,
                    });
                });
            });

        // CreateClient fuerza el boot completo de Program.cs (lazy hasta aquí).
        Client = Factory.CreateClient();

        // Stub de Stripe: fijamos el cliente GLOBAL de Stripe.net con apiBase → fake.
        // El backend solo toca StripeConfiguration.ApiKey (Program.cs:1667) y con el
        // MISMO valor (viene de nuestra config in-memory), así que el setter no resetea
        // el cliente. Todas las llamadas `new PaymentIntentService()` del código de
        // producción usan este cliente global → van al stub.
        FakeStripe.Start();
        StripeConfiguration.ApiKey = TestStripeKey;
        StripeConfiguration.StripeClient = new StripeClient(
            apiKey: TestStripeKey,
            apiBase: FakeStripe.BaseUrl);
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString, n => n.EnableRetryOnFailure(0))
            .EnableSensitiveDataLogging()
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Mintea un JWT con los MISMOS claims que UserService.GenerateJwtToken
    /// (UserService.cs:1920-1927: NameIdentifier, Email, Name, Role, Jti) y firmado con
    /// la clave/issuer/audience EFECTIVOS del host (leídos del IConfiguration del
    /// factory), de modo que el TokenValidationParameters real lo acepte.
    /// </summary>
    public string MintJwtFor(int userId, string email, string name = "Test User", string role = "Client")
    {
        var cfg = Factory.Services.GetRequiredService<IConfiguration>();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));
        var token = new JwtSecurityToken(
            issuer: cfg["Jwt:Issuer"],
            audience: cfg["Jwt:Audience"],
            claims: new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, name),
                new Claim(ClaimTypes.Role, role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            },
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>JWT firmado con una clave DISTINTA — debe ser rechazado (401).</summary>
    public string MintForgedJwtFor(int userId, string email)
    {
        var cfg = Factory.Services.GetRequiredService<IConfiguration>();
        var wrongKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("FORGED-key-that-the-server-never-configured-0123456789-ABCDEF"));
        var token = new JwtSecurityToken(
            issuer: cfg["Jwt:Issuer"],
            audience: cfg["Jwt:Audience"],
            claims: new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, "Forged"),
                new Claim(ClaimTypes.Role, "Client"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            },
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(wrongKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
            await Factory.DisposeAsync();
        FakeStripe.Dispose();
        await _container.DisposeAsync();
    }
}

/// <summary>
/// Colección xUnit para tests HTTP contra el backend real. Separada de "Postgres"
/// (simulador) para que ambas puedan correr en paralelo con contenedores independientes.
/// </summary>
[CollectionDefinition("Api")]
public class ApiCollection : ICollectionFixture<ApiFactoryFixture> { }
