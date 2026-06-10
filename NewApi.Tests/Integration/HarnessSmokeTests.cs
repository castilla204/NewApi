using Microsoft.EntityFrameworkCore;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using NewApi.Tests.StripeMocks;

namespace NewApi.Tests.Integration;

/// <summary>
/// Tests piloto que validan que el harness de integración arranca y funciona:
///   - PILOT-01: el testcontainer Postgres está vivo y AppDbContext conecta.
///   - PILOT-02: el seed cargó las 11 SearchHireStatuses y 20 AppointmentStatuses.
///   - PILOT-03: los builders persisten un User + ExpertProfile + Service + Hire encadenados.
///   - PILOT-04: el truncate entre tests limpia datos mutables pero preserva el seed.
///   - PILOT-05: el StripeWebhookSigner produce un header con shape válido.
///
/// Si TODOS pasan, los siguientes tests reales (HIRE-002, APPT-006, APPT-013,
/// DISP-007, EDGE-001) pueden empezar a construirse sobre la misma base.
/// </summary>
public class HarnessSmokeTests : IntegrationTestBase
{
    public HarnessSmokeTests(PostgresContainerFixture fixture) : base(fixture) { }

    [Fact(DisplayName = "PILOT-01 · AppDbContext conecta al testcontainer Postgres")]
    public async Task Postgres_container_is_reachable()
    {
        await using var db = NewDbContext();
        var connected = await db.Database.CanConnectAsync();
        connected.Should().BeTrue("el testcontainer debería estar arrancado por la fixture");
    }

    [Fact(DisplayName = "PILOT-02 · Seed cargó SystemStatuses esperados (11 SearchHire + 20 Appointment)")]
    public async Task Seed_loaded_system_statuses()
    {
        await using var db = NewDbContext();

        var hireStatuses = await db.SystemStatuses
            .Where(s => s.StatusType == "SearchHireStatus")
            .ToListAsync();
        var apptStatuses = await db.SystemStatuses
            .Where(s => s.StatusType == "AppointmentStatus")
            .ToListAsync();

        hireStatuses.Should().HaveCountGreaterThanOrEqualTo(11,
            "el agente A documentó 11 SearchHireStatus en el seed");
        apptStatuses.Should().HaveCountGreaterThanOrEqualTo(20,
            "el agente A documentó 20 AppointmentStatus en el seed");

        // Verificaciones puntuales contra la verdad del catálogo
        hireStatuses.Should().Contain(s => s.StatusValue == "pending" && !s.IsFinalizationStatus);
        hireStatuses.Should().Contain(s => s.StatusValue == "completed" && s.IsFinalizationStatus);
        hireStatuses.Should().Contain(s => s.StatusValue == "disputed" && s.IsFinalizationStatus);

        // 1ª cancelación NO finaliza, 2ª SÍ — invariante crítico del marketplace
        apptStatuses.Should().Contain(s =>
            s.StatusValue == "appointment_cancelled_by_client" && !s.IsFinalizationStatus);
        apptStatuses.Should().Contain(s =>
            s.StatusValue == "appointment_cancelled_by_client_second" && s.IsFinalizationStatus);
    }

    [Fact(DisplayName = "PILOT-03 · Builders encadenan User → Expert → Service → Hire")]
    public async Task Builders_chain_user_expert_service_hire()
    {
        await using var db = NewDbContext();

        var clientUser = await new UserBuilder("e2e-client@test.dev").AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder("e2e-expert@test.dev").AsExpert().Verified().PersistAsync(db);
        var expertProfile = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expertProfile.Id).WithPrice(150m, "EUR").PersistAsync(db);

        var hire = await new SearchHireBuilder()
            .ForClient(clientUser.Id)
            .ForExpert(expertUser.Id)
            .ForService(svc.Id)
            .WithAmount(150m, "EUR")
            .WithStatusValue("pending")
            .PersistAsync(db);

        // Releer desde BD para verificar persistencia real (no caché del builder)
        await using var verify = Fixture.CreateDbContext();
        var fromDb = await verify.SearchHires
            .Include(h => h.Status)
            .FirstAsync(h => h.Id == hire.Id);

        fromDb.ClientId.Should().Be(clientUser.Id);
        fromDb.ExpertId.Should().Be(expertUser.Id);
        fromDb.SearchServiceId.Should().Be(svc.Id);
        fromDb.Amount.Should().Be(150m);
        fromDb.Currency.Should().Be("EUR");
        fromDb.Status.StatusValue.Should().Be("pending");
        fromDb.Status.IsFinalizationStatus.Should().BeFalse();
    }

    [Fact(DisplayName = "PILOT-04 · InitializeAsync (TRUNCATE) limpia mutables pero preserva el seed")]
    public async Task Truncate_between_tests_preserves_seed()
    {
        // Insertar datos en este test
        await using (var db = NewDbContext())
        {
            await new UserBuilder("transient@test.dev").PersistAsync(db);
        }

        // Re-correr InitializeAsync (lo que xUnit haría entre tests)
        await InitializeAsync();

        await using var verify = NewDbContext();
        var users = await verify.Users.CountAsync();
        var statuses = await verify.SystemStatuses.CountAsync();

        users.Should().Be(0, "el TRUNCATE debió borrar los usuarios creados");
        statuses.Should().BeGreaterThan(30, "el seed (>30 estados) debe sobrevivir al TRUNCATE");
    }

    [Theory(DisplayName = "PILOT-05 · StripeWebhookSigner produce header con shape t=<num>,v1=<hex>")]
    [InlineData("whsec_test_connect")]
    [InlineData("whsec_super_secret_key_123")]
    public void Webhook_signer_header_shape(string secret)
    {
        var payload = """{"id":"evt_test_1","type":"checkout.session.completed"}""";
        var header = StripeWebhookSigner.SignHeader(payload, secret);

        header.Should().MatchRegex(@"^t=\d+,v1=[0-9a-f]{64}$",
            "el header Stripe-Signature debe tener un timestamp unix y un HMAC-SHA256 hex de 64 chars");
    }

    [Fact(DisplayName = "PILOT-06 · StripeEventBuilder produce JSON parseable con campos esperados")]
    public void Event_builder_emits_valid_json()
    {
        var json = StripeEventBuilder.CheckoutSessionCompleted(
            eventId: "evt_pilot_06",
            sessionId: "cs_test_pilot",
            paymentIntentId: "pi_test_pilot",
            clientUserId: 42,
            serviceId: 7,
            amount: 99.50m);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetString().Should().Be("evt_pilot_06");
        doc.RootElement.GetProperty("type").GetString().Should().Be("checkout.session.completed");

        var metadata = doc.RootElement
            .GetProperty("data").GetProperty("object").GetProperty("metadata");
        metadata.GetProperty("userId").GetString().Should().Be("42");
        metadata.GetProperty("serviceId").GetString().Should().Be("7");
    }
}
