using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;

namespace NewApi.Tests.Integration;

/// <summary>
/// Primeros tests contra el BACKEND REAL por HTTP (WebApplicationFactory&lt;Program&gt;).
///
/// A diferencia del resto de la suite (MarketplaceFlowSimulator = réplica del contrato),
/// aquí cada request atraviesa el pipeline de producción de verdad: Kestrel/TestServer,
/// middleware (RequestPipelineLogging, RequireMfa), autenticación JWT con los
/// TokenValidationParameters reales (Program.cs:1142-1156), model binding, el controller
/// y el service reales, y la BD del testcontainer.
///
/// Esto cubre la capa que el simulador no puede: si alguien rompe un [Authorize], el
/// pipeline JWT, el middleware MFA o el wiring de DI, estos tests fallan.
/// </summary>
[Collection("Api")]
public class HttpApiSmokeTests
{
    private readonly ApiFactoryFixture _api;

    public HttpApiSmokeTests(ApiFactoryFixture api) => _api = api;

    // ─────────────────────────────────────────────────────────────────────────
    // API-01 · el backend real arranca y /health responde 200
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "API-01 · GET /health → 200 (Program.cs completo arranca)")]
    public async Task Health_endpoint_responds_ok()
    {
        var response = await _api.Client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // API-02 · endpoint público con BD real ([AllowAnonymous] + query EF)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "API-02 · GET /api/Categories (público) → 200 con query real a Postgres")]
    public async Task Public_categories_endpoint_hits_real_db()
    {
        var response = await _api.Client.GetAsync("/api/Categories");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "CategoriesController.GetCategories es [AllowAnonymous] y consulta la BD del testcontainer");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // API-03 · [Authorize] sin token → 401 (pipeline JWT real)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "API-03 · GET /api/User/expert-profile sin token → 401")]
    public async Task Protected_endpoint_without_token_returns_401()
    {
        var response = await _api.Client.GetAsync("/api/User/expert-profile");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "UserController.GetExpertProfile lleva [Authorize] (UserController.cs:850)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // API-04 · JWT firmado con clave INCORRECTA → 401 (la firma se valida de verdad)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "API-04 · JWT con firma falsa → 401 (ValidateIssuerSigningKey real)")]
    public async Task Forged_token_is_rejected()
    {
        var forged = _api.MintForgedJwtFor(99999, "forged@test.dev");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/User/expert-profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forged);
        var response = await _api.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "el token va firmado con una clave que el servidor no conoce");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // API-05 · JWT válido + usuario CON ExpertProfile → 200 (controller+service reales)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "API-05 · JWT válido y user con profile → 200 con datos reales")]
    public async Task Valid_token_with_expert_profile_returns_200()
    {
        int userId;
        await using (var db = _api.CreateDbContext())
        {
            var user = await new UserBuilder("api05@test.dev").AsExpert().Verified().PersistAsync(db);
            await new ExpertProfileBuilder(user.Id).Approved().PersistAsync(db);
            userId = user.Id;
        }

        var jwt = _api.MintJwtFor(userId, "api05@test.dev", role: "Expert");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/User/expert-profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var response = await _api.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "el pipeline completo (JWT → MFA middleware → UserController → UserService → Postgres) debe resolver el profile");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // API-06 · JWT válido + usuario SIN ExpertProfile → 404 (lógica real del service)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "API-06 · JWT válido pero user sin profile → 404")]
    public async Task Valid_token_without_expert_profile_returns_404()
    {
        int userId;
        await using (var db = _api.CreateDbContext())
        {
            var user = await new UserBuilder("api06@test.dev").AsClient().Verified().PersistAsync(db);
            userId = user.Id;
        }

        var jwt = _api.MintJwtFor(userId, "api06@test.dev");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/User/expert-profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var response = await _api.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "GetExpertProfile devuelve NotFound cuando el user no tiene ExpertProfile (UserController.cs:863-866)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // API-07 · HF-LOCAL GUARD: el host en Development NO arranca BackgroundJobServer
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "API-07 · en Development NO se registra ningún Hangfire server (HF-LOCAL GUARD)")]
    public async Task Development_host_does_not_register_hangfire_server()
    {
        // El host de tests arranca con UseEnvironment("Development") y SIN
        // HANGFIRE_SERVER_ENABLED=1 → el guard debe impedir el BackgroundJobServer.
        // Sin el guard, una API local conectada (por error de config) a la BD de prod
        // se une a la cola compartida y procesa jobs reales con binario desactualizado
        // (caso real: SearchHire 15, 2026-06-11 00:50 UTC).
        await using var db = _api.CreateDbContext();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT count(*)::int FROM hangfire.server";
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync();
        var servers = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        servers.Should().Be(0,
            "en Development el BackgroundJobServer debe quedar deshabilitado salvo opt-in HANGFIRE_SERVER_ENABLED=1");
    }
}
