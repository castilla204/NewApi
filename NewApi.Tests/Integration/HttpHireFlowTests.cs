using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using NewApi.Tests.StripeMocks;

namespace NewApi.Tests.Integration;

/// <summary>
/// Flujo de contratación REAL por HTTP contra el backend de producción
/// (WebApplicationFactory + FakeStripeServer como ApiBase de Stripe.net):
///
///   1. Webhook checkout.session.completed FIRMADO (HMAC-SHA256) → el handler real
///      captura el PaymentIntent (contra el stub) y crea SearchHire(pending) + FT.
///   2. POST /api/SearchHire/complete-service → hire completed + transfer al experto.
///   3. Citas por API real: propose / confirm / reject / cancel (controllers reales).
///   4. POST /api/Dispute/dispute-service (multipart) → 201 + Dispute.
///
/// Todo atraviesa: firma de webhook real (EventUtility.ConstructEvent), idempotencia
/// ProcessedWebhookEvent, JWT auth, ownership checks, servicios y Postgres reales.
/// </summary>
[Collection("Api")]
public class HttpHireFlowTests
{
    private readonly ApiFactoryFixture _api;

    public HttpHireFlowTests(ApiFactoryFixture api) => _api = api;

    private const string WebhookUrl = "/api/subscription/webhook-general";

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────
    private sealed record Marketplace(int ClientId, int ExpertUserId, int ServiceId,
        string ClientEmail, string ExpertEmail);

    private async Task<Marketplace> SeedMarketplaceAsync(string slug)
    {
        await using var db = _api.CreateDbContext();
        var client = await new UserBuilder($"{slug}-client@test.dev").AsClient().Verified().PersistAsync(db);
        var expert = await new UserBuilder($"{slug}-expert@test.dev").AsExpert().Verified().PersistAsync(db);
        var profile = await new ExpertProfileBuilder(expert.Id).Approved().PersistAsync(db);

        // El propose convierte fecha LOCAL→UTC con el timezone del experto.
        profile.Timezone = "Europe/Madrid";
        profile.Country = "ES";

        // Validación REAL del propose: "El experto no tiene horarios de disponibilidad
        // configurados" → exige ExpertAvailability activa. Disponibilidad amplia 7d/8-20h.
        db.Set<newApi.DataLayer.Models.PostGresModels.ExpertAvailability>().Add(new()
        {
            ExpertId = profile.Id,
            DaysOfWeek = """["Monday","Tuesday","Wednesday","Thursday","Friday","Saturday","Sunday"]""",
            StartTime = TimeSpan.FromHours(8),
            EndTime = TimeSpan.FromHours(20),
            Timezone = "Europe/Madrid",
            EffectiveFrom = DateTime.UtcNow.AddDays(-1),
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = await new SearchServiceBuilder(profile.Id).WithPrice(110m, "EUR").PersistAsync(db);
        return new Marketplace(client.Id, expert.Id, service.Id,
            $"{slug}-client@test.dev", $"{slug}-expert@test.dev");
    }

    /// <summary>POST del webhook firmado y devuelve el hire creado (o null).</summary>
    private async Task<(HttpResponseMessage Response, int? HireId)> PostCheckoutWebhookAsync(
        Marketplace mk, string eventId, string sessionId, string paymentIntentId, decimal amount = 110m)
    {
        var payload = StripeEventBuilder.CheckoutSessionCompleted(
            eventId, sessionId, paymentIntentId, mk.ClientId, mk.ServiceId, amount);
        var request = StripeWebhookSigner.BuildSignedPost(
            WebhookUrl, payload, ApiFactoryFixture.GeneralWebhookSecret);
        var response = await _api.Client.SendAsync(request);

        await using var db = _api.CreateDbContext();
        var hireId = await db.SearchHires
            .Where(h => h.ClientId == mk.ClientId && h.SearchServiceId == mk.ServiceId)
            .OrderByDescending(h => h.Id)
            .Select(h => (int?)h.Id)
            .FirstOrDefaultAsync();
        return (response, hireId);
    }

    private HttpRequestMessage AuthedJson(HttpMethod method, string url, object body, string jwt)
    {
        var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return req;
    }

    private async Task<string> HireStatusAsync(int hireId)
    {
        await using var db = _api.CreateDbContext();
        return await db.SearchHires.Where(h => h.Id == hireId)
            .Select(h => h.Status!.StatusValue).SingleAsync();
    }

    private async Task<(int Id, string Status)> AppointmentOfAsync(int hireId)
    {
        await using var db = _api.CreateDbContext();
        var appt = await db.Appointments.Include(a => a.Status)
            .SingleAsync(a => a.SearchHireId == hireId);
        return (appt.Id, appt.Status!.StatusValue);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-01 · webhook sin header de firma → 400
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-01 · webhook-general sin Stripe-Signature → 400")]
    public async Task Webhook_without_signature_is_rejected()
    {
        var payload = StripeEventBuilder.CheckoutSessionCompleted(
            "evt_hf01_" + Guid.NewGuid().ToString("N"), "cs_hf01", "pi_hf01", 1, 1, 110m);
        var response = await _api.Client.PostAsync(WebhookUrl,
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-02 · firma con secret EQUIVOCADO → rechazado, no crea nada
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-02 · webhook con firma de secret incorrecto → rechazado sin efectos")]
    public async Task Webhook_with_wrong_secret_is_rejected()
    {
        var mk = await SeedMarketplaceAsync("hf02");
        var eventId = "evt_hf02_" + Guid.NewGuid().ToString("N");
        var payload = StripeEventBuilder.CheckoutSessionCompleted(
            eventId, "cs_hf02", "pi_hf02", mk.ClientId, mk.ServiceId, 110m);

        var request = StripeWebhookSigner.BuildSignedPost(
            WebhookUrl, payload, "whsec_WRONG_secret_attacker");
        var response = await _api.Client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeFalse(
            "EventUtility.ConstructEvent lanza con firma inválida (SubscriptionController.cs:4159)");

        await using var db = _api.CreateDbContext();
        (await db.SearchHires.AnyAsync(h => h.ClientId == mk.ClientId)).Should().BeFalse();
        (await db.ProcessedWebhookEvents.AnyAsync(e => e.EventId == eventId)).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-03 · checkout.session.completed firmado → captura PI + SearchHire(pending) + FT
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-03 · webhook firmado válido → 200, captura PI y crea hire(pending)+FT")]
    public async Task Valid_checkout_webhook_creates_hire()
    {
        var mk = await SeedMarketplaceAsync("hf03");
        var pi = "pi_hf03_" + Guid.NewGuid().ToString("N")[..10];
        var eventId = "evt_hf03_" + Guid.NewGuid().ToString("N");
        var (response, hireId) = await PostCheckoutWebhookAsync(mk, eventId, "cs_hf03", pi);

        var body = await response.Content.ReadAsStringAsync();
        string? serverError = null;
        await using (var diag = _api.CreateDbContext())
            serverError = await diag.ProcessedWebhookEvents
                .Where(e => e.EventId == eventId).Select(e => e.ErrorMessage).FirstOrDefaultAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"body: {body} | ProcessedWebhookEvent.ErrorMessage: {serverError}");
        hireId.Should().NotBeNull("el handler real debe crear el SearchHire");

        (await HireStatusAsync(hireId!.Value)).Should().Be("pending");

        await using var db = _api.CreateDbContext();
        var ft = await db.FinancialTransactions.SingleOrDefaultAsync(f =>
            f.StripePaymentIntentId == pi && f.TransactionType == "ServicePayment");
        ft.Should().NotBeNull("el handler registra la FT ServicePayment con el PaymentIntentId");
        ft!.RelatedEntityId.Should().Be(hireId.Value);

        // El backend REAL capturó el PaymentIntent contra el stub de Stripe.
        _api.FakeStripe.Requests.Should().Contain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "EnsurePaymentCapturedAsync llama a PaymentIntentService.CaptureAsync (SubscriptionController.cs:6314)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-04 · replay del MISMO eventId → 200 'already processed' y sigue habiendo 1 hire
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-04 · replay de webhook (mismo eventId) → idempotente, no duplica hire")]
    public async Task Webhook_replay_is_idempotent()
    {
        var mk = await SeedMarketplaceAsync("hf04");
        var eventId = "evt_hf04_" + Guid.NewGuid().ToString("N");
        var payload = StripeEventBuilder.CheckoutSessionCompleted(
            eventId, "cs_hf04", "pi_hf04_" + Guid.NewGuid().ToString("N")[..10],
            mk.ClientId, mk.ServiceId, 110m);

        var first = await _api.Client.SendAsync(
            StripeWebhookSigner.BuildSignedPost(WebhookUrl, payload, ApiFactoryFixture.GeneralWebhookSecret));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await _api.Client.SendAsync(
            StripeWebhookSigner.BuildSignedPost(WebhookUrl, payload, ApiFactoryFixture.GeneralWebhookSecret));
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadAsStringAsync()).Should().Contain("already processed");

        await using var db = _api.CreateDbContext();
        (await db.SearchHires.CountAsync(h => h.ClientId == mk.ClientId))
            .Should().Be(1, "la idempotencia por ProcessedWebhookEvent.EventId impide duplicar el hire");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-05 · flujo dinero: webhook → complete-service → hire completed + transfer
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-05 · complete-service (JWT cliente) → 200, hire completed y transfer al experto")]
    public async Task Complete_service_via_http_completes_hire()
    {
        var mk = await SeedMarketplaceAsync("hf05");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf05_" + Guid.NewGuid().ToString("N"), "cs_hf05",
            "pi_hf05_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        var jwt = _api.MintJwtFor(mk.ClientId, mk.ClientEmail);
        var response = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, "/api/SearchHire/complete-service",
            new { searchHireId = hireId!.Value, clientApproved = true }, jwt));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await HireStatusAsync(hireId.Value)).Should().Be("completed");

        // El RefundService real ejecutó el payout 95% contra el stub.
        _api.FakeStripe.Requests.Should().Contain(r => r == "POST /v1/transfers",
            "ProcessMoneyDistributionAsync('completed') transfiere el 95% al experto (RefundService.cs:1433)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-06 · complete-service de un hire AJENO → rechazado y sin cambios
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-06 · complete-service con JWT de otro usuario → rechazado, hire intacto")]
    public async Task Complete_service_of_foreign_hire_is_rejected()
    {
        var mk = await SeedMarketplaceAsync("hf06");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf06_" + Guid.NewGuid().ToString("N"), "cs_hf06",
            "pi_hf06_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        int strangerId;
        await using (var db = _api.CreateDbContext())
            strangerId = (await new UserBuilder("hf06-stranger@test.dev").AsClient().Verified().PersistAsync(db)).Id;

        var jwt = _api.MintJwtFor(strangerId, "hf06-stranger@test.dev");
        var response = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, "/api/SearchHire/complete-service",
            new { searchHireId = hireId!.Value, clientApproved = true }, jwt));

        response.IsSuccessStatusCode.Should().BeFalse("solo el ClientId del hire puede completarlo");
        (await HireStatusAsync(hireId.Value)).Should().Be("pending", "el hire no debe cambiar");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-07 · propose (cliente) + confirm (experto) por HTTP
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-07 · propose+confirm por API real → appointment_confirmed")]
    public async Task Propose_and_confirm_via_http()
    {
        var mk = await SeedMarketplaceAsync("hf07");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf07_" + Guid.NewGuid().ToString("N"), "cs_hf07",
            "pi_hf07_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        // Cliente propone
        var clientJwt = _api.MintJwtFor(mk.ClientId, mk.ClientEmail);
        var propose = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, $"/api/Appointment/propose/{hireId}",
            new
            {
                proposedDate = DateTime.UtcNow.Date.AddDays(7),
                proposedTime = "10:00:00",
                location = "Calle de Prueba 1, Madrid",
                latitude = 40.4168m,
                longitude = -3.7038m,
                timezone = "Europe/Madrid",
            }, clientJwt));
        var proposeBody = await propose.Content.ReadAsStringAsync();
        propose.StatusCode.Should().Be(HttpStatusCode.OK, $"propose debe aceptarse. Body: {proposeBody}");

        var (apptId, statusAfterPropose) = await AppointmentOfAsync(hireId!.Value);
        statusAfterPropose.Should().Be("appointment_proposed");

        // Experto confirma
        var expertJwt = _api.MintJwtFor(mk.ExpertUserId, mk.ExpertEmail, role: "Expert");
        var confirm = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, "/api/Appointment/confirm",
            new { appointmentId = apptId, notes = "Confirmado para el día 7" }, expertJwt));
        var confirmBody = await confirm.Content.ReadAsStringAsync();
        confirm.StatusCode.Should().Be(HttpStatusCode.OK, $"confirm debe aceptarse. Body: {confirmBody}");

        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_confirmed");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-08 · 1ª cancelación del cliente por HTTP → cancelled_by_client, hire sigue pending
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-08 · cancel 1ª vez (cliente) por API real → appointment_cancelled_by_client, hire pending")]
    public async Task First_cancellation_by_client_via_http()
    {
        var mk = await SeedMarketplaceAsync("hf08");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf08_" + Guid.NewGuid().ToString("N"), "cs_hf08",
            "pi_hf08_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        var clientJwt = _api.MintJwtFor(mk.ClientId, mk.ClientEmail);
        var propose = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, $"/api/Appointment/propose/{hireId}",
            new
            {
                proposedDate = DateTime.UtcNow.Date.AddDays(5),
                proposedTime = "11:00:00",
                location = "Calle de Prueba 2, Madrid",
                timezone = "Europe/Madrid",
            }, clientJwt));
        propose.StatusCode.Should().Be(HttpStatusCode.OK);

        var expertJwt = _api.MintJwtFor(mk.ExpertUserId, mk.ExpertEmail, role: "Expert");
        var (apptId, _) = await AppointmentOfAsync(hireId!.Value);
        var confirm = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, "/api/Appointment/confirm",
            new { appointmentId = apptId, notes = "ok" }, expertJwt));
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        // 1ª cancelación del cliente: SIN dinero, hire NO finaliza
        var cancel = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, "/api/Appointment/cancel",
            new { appointmentId = apptId, reason = "Imprevisto, necesito reagendar" }, clientJwt));
        var cancelBody = await cancel.Content.ReadAsStringAsync();
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, $"cancel debe aceptarse. Body: {cancelBody}");

        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_cancelled_by_client");
        (await HireStatusAsync(hireId.Value)).Should().Be("pending",
            "la 1ª cancelación permite reagendar — el hire no finaliza");

        await using var db = _api.CreateDbContext();
        var appt = await db.Appointments.SingleAsync(a => a.Id == apptId);
        appt.ClientCancellationCount.Should().Be(1);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-09 · 1er rechazo del experto por HTTP → appointment_rejected, sin dinero
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-09 · reject 1ª vez (experto) por API real → appointment_rejected")]
    public async Task First_rejection_by_expert_via_http()
    {
        var mk = await SeedMarketplaceAsync("hf09");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf09_" + Guid.NewGuid().ToString("N"), "cs_hf09",
            "pi_hf09_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        var clientJwt = _api.MintJwtFor(mk.ClientId, mk.ClientEmail);
        var propose = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, $"/api/Appointment/propose/{hireId}",
            new
            {
                proposedDate = DateTime.UtcNow.Date.AddDays(3),
                proposedTime = "16:30:00",
                location = "Calle de Prueba 3, Madrid",
                timezone = "Europe/Madrid",
            }, clientJwt));
        propose.StatusCode.Should().Be(HttpStatusCode.OK);

        var (apptId, _) = await AppointmentOfAsync(hireId!.Value);
        var expertJwt = _api.MintJwtFor(mk.ExpertUserId, mk.ExpertEmail, role: "Expert");
        var reject = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, "/api/Appointment/reject",
            new { appointmentId = apptId, reason = "Ese día no puedo" }, expertJwt));
        var rejectBody = await reject.Content.ReadAsStringAsync();
        reject.StatusCode.Should().Be(HttpStatusCode.OK, $"reject debe aceptarse. Body: {rejectBody}");

        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_rejected");
        (await HireStatusAsync(hireId.Value)).Should().Be("pending");

        await using var db = _api.CreateDbContext();
        var appt = await db.Appointments.SingleAsync(a => a.Id == apptId);
        appt.RejectionCount.Should().Be(1);
        appt.ExpertCancellationCount.Should().Be(0, "el 1er rechazo no cuenta como cancelación");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-10 · abrir disputa por HTTP (multipart) → 201 + fila Dispute
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-10 · dispute-service (multipart, JWT cliente) → 201 y Dispute en BD")]
    public async Task Open_dispute_via_http()
    {
        var mk = await SeedMarketplaceAsync("hf10");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf10_" + Guid.NewGuid().ToString("N"), "cs_hf10",
            "pi_hf10_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        // La disputa exige hire en awaiting_client_decision (o completed <14d)
        await using (var db = _api.CreateDbContext())
        {
            var awaiting = await db.SystemStatuses.SingleAsync(s =>
                s.StatusType == "SearchHireStatus" && s.StatusValue == "awaiting_client_decision");
            var hire = await db.SearchHires.SingleAsync(h => h.Id == hireId);
            hire.StatusId = awaiting.Id;
            hire.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var jwt = _api.MintJwtFor(mk.ClientId, mk.ClientEmail);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(hireId!.Value.ToString()), "SearchHireId" },
            { new StringContent("El informe no corresponde con la inspección realizada"), "Reason" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Dispute/dispute-service")
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var response = await _api.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        ((int)response.StatusCode).Should().BeInRange(200, 201,
            $"abrir disputa debe aceptarse. Body: {body}");

        await using var verify = _api.CreateDbContext();
        var dispute = await verify.Disputes.SingleOrDefaultAsync(d =>
            d.SearchHireId == hireId.Value && d.ReporterId == mk.ClientId);
        dispute.Should().NotBeNull("la disputa debe persistirse con el reporter correcto");
    }
}
