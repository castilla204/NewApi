using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        Marketplace mk, string eventId, string sessionId, string paymentIntentId, decimal amount = 110m,
        string? coordinationMode = null)
    {
        var payload = StripeEventBuilder.CheckoutSessionCompleted(
            eventId, sessionId, paymentIntentId, mk.ClientId, mk.ServiceId, amount,
            coordinationMode: coordinationMode);
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

    private async Task<string?> HireCaptureStatusAsync(int hireId)
    {
        await using var db = _api.CreateDbContext();
        return await db.SearchHires.Where(h => h.Id == hireId)
            .Select(h => h.CaptureStatus).SingleAsync();
    }

    private async Task<(int Id, string Status)> AppointmentOfAsync(int hireId)
    {
        await using var db = _api.CreateDbContext();
        var appt = await db.Appointments.Include(a => a.Status)
            .SingleAsync(a => a.SearchHireId == hireId);
        return (appt.Id, appt.Status!.StatusValue);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-SLOT · webhook con hueco (Calendly) → cita YA appointment_confirmed con
    //           intervalo UTC + ProposedDate/Time local. Camino de la Fase C.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-SLOT · webhook con hueco → cita appointment_confirmed con intervalo UTC")]
    public async Task Webhook_with_slot_creates_confirmed_appointment()
    {
        var mk = await SeedMarketplaceAsync("hfslot");
        var startUtc = new DateTime(2026, 8, 3, 7, 0, 0, DateTimeKind.Utc); // 09:00 Madrid CEST
        var endUtc = startUtc.AddHours(2);

        var payload = StripeEventBuilder.CheckoutSessionCompleted(
            "evt_hfslot_" + Guid.NewGuid().ToString("N"),
            "cs_hfslot_" + Guid.NewGuid().ToString("N"),
            "pi_hfslot_" + Guid.NewGuid().ToString("N"),
            mk.ClientId, mk.ServiceId, 110m, startsAtUtc: startUtc, endsAtUtc: endUtc);
        var request = StripeWebhookSigner.BuildSignedPost(
            WebhookUrl, payload, ApiFactoryFixture.GeneralWebhookSecret);
        var response = await _api.Client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"webhook status {(int)response.StatusCode}");

        await using var db = _api.CreateDbContext();
        var hireId = await db.SearchHires
            .Where(h => h.ClientId == mk.ClientId && h.SearchServiceId == mk.ServiceId)
            .OrderByDescending(h => h.Id).Select(h => (int?)h.Id).FirstOrDefaultAsync();
        Assert.NotNull(hireId);

        var appt = await db.Appointments.Include(a => a.Status).SingleAsync(a => a.SearchHireId == hireId!.Value);
        Assert.Equal("appointment_confirmed", appt.Status!.StatusValue);
        Assert.Equal(startUtc, appt.StartsAtUtc);
        Assert.Equal(endUtc, appt.EndsAtUtc);
        Assert.True(appt.BlocksCalendar);
        Assert.Equal(mk.ExpertUserId, appt.ExpertId);
        Assert.Equal(new DateTime(2026, 8, 3), appt.ProposedDate!.Value.Date);
        Assert.Equal(new TimeSpan(9, 0, 0), appt.ProposedTime); // 07:00 UTC = 09:00 Europe/Madrid (CEST)
    }

    /// <summary>Crea un hire con cita CON HUECO ya confirmada vía webhook y devuelve (hireId, apptId).</summary>
    private async Task<(int hireId, int apptId)> SeedSlotHireAsync(Marketplace mk, string slug, DateTime startUtc)
    {
        var payload = StripeEventBuilder.CheckoutSessionCompleted(
            $"evt_{slug}_" + Guid.NewGuid().ToString("N"), $"cs_{slug}_" + Guid.NewGuid().ToString("N"),
            $"pi_{slug}_" + Guid.NewGuid().ToString("N"), mk.ClientId, mk.ServiceId, 110m,
            startsAtUtc: startUtc, endsAtUtc: startUtc.AddHours(2));
        var resp = await _api.Client.SendAsync(StripeWebhookSigner.BuildSignedPost(
            WebhookUrl, payload, ApiFactoryFixture.GeneralWebhookSecret));
        resp.IsSuccessStatusCode.Should().BeTrue("el webhook con hueco debe crear la cita confirmada");

        await using var db = _api.CreateDbContext();
        var hireId = await db.SearchHires.Where(h => h.ClientId == mk.ClientId && h.SearchServiceId == mk.ServiceId)
            .OrderByDescending(h => h.Id).Select(h => h.Id).FirstAsync();
        var apptId = await db.Appointments.Where(a => a.SearchHireId == hireId).Select(a => a.Id).FirstAsync();
        return (hireId, apptId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-SLOT-CANC · cliente cancela cita con hueco (>24h, N=0 default) → tramo 6-24h (50/50)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-SLOT-CANC · cliente cancela cita con hueco (>24h, N=0) → tramo 50/50 + refund")]
    public async Task Slot_client_cancel_applies_tiered_policy()
    {
        var mk = await SeedMarketplaceAsync("hfcanc");
        var (hireId, apptId) = await SeedSlotHireAsync(mk, "hfcanc", new DateTime(2026, 8, 3, 7, 0, 0, DateTimeKind.Utc));

        var refundsBefore = StripeCalls("POST /v1/refunds");
        var cancel = await CancelAsync(mk, apptId, byExpert: false, "Cambio de planes");
        var body = await cancel.Content.ReadAsStringAsync();
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, $"la cancelación con hueco se permite siempre. Body: {body}");

        (await AppointmentOfAsync(hireId)).Status.Should().Be("appointment_cancelled_by_client_6to24h",
            ">24h con N=0 (default) cae al tramo medio 50/50");
        (await HireStatusAsync(hireId)).Should().Be("cancelled");
        StripeCalls("POST /v1/refunds").Should().BeGreaterThan(refundsBefore, "50% de reembolso al cliente vía Stripe");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-SLOT-CANC-EXP · experto cancela cita con hueco → expert_strike + strike +1
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-SLOT-CANC-EXP · experto cancela cita con hueco → expert_strike + strike +1")]
    public async Task Slot_expert_cancel_applies_strike()
    {
        var mk = await SeedMarketplaceAsync("hfcancx");
        var (hireId, apptId) = await SeedSlotHireAsync(mk, "hfcancx", new DateTime(2026, 8, 4, 7, 0, 0, DateTimeKind.Utc));

        var cancel = await CancelAsync(mk, apptId, byExpert: true, "No puedo asistir");
        var body = await cancel.Content.ReadAsStringAsync();
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, $"el experto puede cancelar. Body: {body}");

        (await AppointmentOfAsync(hireId)).Status.Should().Be("appointment_cancelled_by_expert_strike");
        (await HireStatusAsync(hireId)).Should().Be("cancelled");

        await using var db = _api.CreateDbContext();
        var strikes = await db.ExpertProfiles.Where(p => p.UserId == mk.ExpertUserId)
            .Select(p => p.CancellationStrikes).FirstAsync();
        strikes.Should().Be(1, "el experto recibe un strike al cancelar");
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
    // HF-03b · captura DIFERIDA: modo "seller" SIN hueco → el PI NO se captura
    //           (sigue autorizado) y el hire queda CaptureStatus="Authorized".
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-03b · seller sin hueco → PI NO capturado, hire CaptureStatus=Authorized")]
    public async Task Seller_checkout_without_slot_defers_capture()
    {
        var mk = await SeedMarketplaceAsync("hf03b");
        var pi = "pi_hf03b_" + Guid.NewGuid().ToString("N")[..10];
        var eventId = "evt_hf03b_" + Guid.NewGuid().ToString("N");

        // Modo seller SIN startsAtUtc → captura diferida.
        var (response, hireId) = await PostCheckoutWebhookAsync(
            mk, eventId, "cs_hf03b", pi, coordinationMode: "seller");

        var body = await response.Content.ReadAsStringAsync();
        string? serverError = null;
        await using (var diag = _api.CreateDbContext())
            serverError = await diag.ProcessedWebhookEvents
                .Where(e => e.EventId == eventId).Select(e => e.ErrorMessage).FirstOrDefaultAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"body: {body} | ProcessedWebhookEvent.ErrorMessage: {serverError}");
        hireId.Should().NotBeNull("el handler real debe crear el SearchHire también en modo seller");

        // El hire queda AUTORIZADO (no cobrado): se capturará al confirmar el vendedor (Tarea 3).
        (await HireCaptureStatusAsync(hireId!.Value)).Should().Be("Authorized",
            "modo seller sin hueco difiere la captura: PI en requires_capture, sin cobro");

        // La FT ServicePayment SÍ se inserta (mantiene el vínculo PI↔hire que usan los jobs).
        await using var db = _api.CreateDbContext();
        var ft = await db.FinancialTransactions.SingleOrDefaultAsync(f =>
            f.StripePaymentIntentId == pi && f.TransactionType == "ServicePayment");
        ft.Should().NotBeNull("la FT ServicePayment se inserta como hoy, incluso difiriendo la captura");
        ft!.RelatedEntityId.Should().Be(hireId.Value);

        // CLAVE: el backend NO debe haber capturado el PaymentIntent contra el stub.
        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "captura diferida: no debe haber POST .../capture para el PI del seller sin hueco");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-03c · NO regresión: caso self/normal → captura como hoy y CaptureStatus="Captured".
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-03c · self/normal → PI capturado y hire CaptureStatus=Captured")]
    public async Task Self_checkout_captures_immediately()
    {
        var mk = await SeedMarketplaceAsync("hf03c");
        var pi = "pi_hf03c_" + Guid.NewGuid().ToString("N")[..10];
        var eventId = "evt_hf03c_" + Guid.NewGuid().ToString("N");

        // Sin coordinationMode (equivale a self) y sin hueco → captura inmediata, como hoy.
        var (response, hireId) = await PostCheckoutWebhookAsync(mk, eventId, "cs_hf03c", pi);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"body: {body}");
        hireId.Should().NotBeNull();

        (await HireCaptureStatusAsync(hireId!.Value)).Should().Be("Captured",
            "el caso self/normal captura el pago en el webhook como hoy");

        _api.FakeStripe.Requests.Should().Contain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "el caso self/normal SÍ captura el PaymentIntent");
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
    [Fact(DisplayName = "HF-07 · propose+confirm por API real → appointment_confirmed", Skip = "Sistema antiguo retirado: propose/confirm/reject comentado (#if false)")]
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
    [Fact(DisplayName = "HF-08 · cancel 1ª vez (cliente) por API real → appointment_cancelled_by_client, hire pending", Skip = "Sistema antiguo retirado: propose/confirm/reject comentado (#if false)")]
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
    [Fact(DisplayName = "HF-09 · reject 1ª vez (experto) por API real → appointment_rejected", Skip = "Sistema antiguo retirado: propose/confirm/reject comentado (#if false)")]
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

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers de cadena (reducen repetición en HF-11..17)
    // ─────────────────────────────────────────────────────────────────────────
    private int StripeCalls(string entry) => _api.FakeStripe.Requests.Count(r => r == entry);

    private async Task ProposeOkAsync(Marketplace mk, int hireId, int days, string time)
    {
        var jwt = _api.MintJwtFor(mk.ClientId, mk.ClientEmail);
        var r = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, $"/api/Appointment/propose/{hireId}",
            new
            {
                proposedDate = DateTime.UtcNow.Date.AddDays(days),
                proposedTime = time,
                location = "Calle de Prueba, Madrid",
                timezone = "Europe/Madrid",
            }, jwt));
        var body = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, $"propose debe aceptarse. Body: {body}");
    }

    private async Task ConfirmOkAsync(Marketplace mk, int apptId)
    {
        var jwt = _api.MintJwtFor(mk.ExpertUserId, mk.ExpertEmail, role: "Expert");
        var r = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, "/api/Appointment/confirm", new { appointmentId = apptId, notes = "ok" }, jwt));
        var body = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, $"confirm debe aceptarse. Body: {body}");
    }

    private async Task<HttpResponseMessage> CancelAsync(Marketplace mk, int apptId, bool byExpert, string reason)
    {
        var jwt = byExpert
            ? _api.MintJwtFor(mk.ExpertUserId, mk.ExpertEmail, role: "Expert")
            : _api.MintJwtFor(mk.ClientId, mk.ClientEmail);
        return await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, "/api/Appointment/cancel", new { appointmentId = apptId, reason }, jwt));
    }

    private async Task<HttpResponseMessage> RejectAsync(Marketplace mk, int apptId, string reason)
    {
        var jwt = _api.MintJwtFor(mk.ExpertUserId, mk.ExpertEmail, role: "Expert");
        return await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, "/api/Appointment/reject", new { appointmentId = apptId, reason }, jwt));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-11 · propose → reject(1º) → repropose → reject(2º) → hire cancelled + refund 100%
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-11 · 2º rechazo del experto por API real → cancelled_by_expert_rejection + refund", Skip = "Sistema antiguo retirado: propose/confirm/reject comentado (#if false)")]
    public async Task Second_rejection_finalizes_with_full_refund()
    {
        var mk = await SeedMarketplaceAsync("hf11");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf11_" + Guid.NewGuid().ToString("N"), "cs_hf11",
            "pi_hf11_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        await ProposeOkAsync(mk, hireId!.Value, days: 4, time: "09:30:00");
        var (apptId, _) = await AppointmentOfAsync(hireId.Value);
        (await RejectAsync(mk, apptId, "No puedo ese día")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_rejected");

        // El cliente re-propone (válido desde appointment_rejected)
        await ProposeOkAsync(mk, hireId.Value, days: 6, time: "12:00:00");
        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_proposed");

        var refundsBefore = StripeCalls("POST /v1/refunds");
        var reject2 = await RejectAsync(mk, apptId, "Tampoco puedo, lo siento");
        var body = await reject2.Content.ReadAsStringAsync();
        reject2.StatusCode.Should().Be(HttpStatusCode.OK, $"el 2º reject debe aceptarse. Body: {body}");

        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_cancelled_by_expert_rejection");
        (await HireStatusAsync(hireId.Value)).Should().Be("cancelled",
            "el 2º rechazo del experto finaliza el hire (AppointmentService.cs:1918)");
        StripeCalls("POST /v1/refunds").Should().BeGreaterThan(refundsBefore,
            "el split 100/0/0 reembolsa al cliente vía Stripe (inline en la request)");

        await using var db = _api.CreateDbContext();
        var appt = await db.Appointments.SingleAsync(a => a.Id == apptId);
        appt.RejectionCount.Should().Be(2);
        appt.ExpertCancellationCount.Should().Be(1, "el 2º rechazo incrementa el contador del experto");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-12 · 2ª cancelación del EXPERTO → cancelled_by_expert_second + refund al cliente
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-12 · 2ª cancelación del experto por API real → expert_second + refund", Skip = "Sistema antiguo retirado: propose/confirm/reject comentado (#if false)")]
    public async Task Second_expert_cancellation_finalizes_with_refund()
    {
        var mk = await SeedMarketplaceAsync("hf12");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf12_" + Guid.NewGuid().ToString("N"), "cs_hf12",
            "pi_hf12_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        // 1ª ronda: propose → confirm → cancel experto (NO finaliza)
        await ProposeOkAsync(mk, hireId!.Value, days: 4, time: "10:00:00");
        var (apptId, _) = await AppointmentOfAsync(hireId.Value);
        await ConfirmOkAsync(mk, apptId);
        (await CancelAsync(mk, apptId, byExpert: true, "Imprevisto del experto"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_cancelled_by_expert");
        (await HireStatusAsync(hireId.Value)).Should().Be("pending");

        // 2ª ronda: re-propose → confirm → cancel experto otra vez (finaliza)
        await ProposeOkAsync(mk, hireId.Value, days: 8, time: "17:00:00");
        await ConfirmOkAsync(mk, apptId);

        var refundsBefore = StripeCalls("POST /v1/refunds");
        var cancel2 = await CancelAsync(mk, apptId, byExpert: true, "Segundo imprevisto");
        var body = await cancel2.Content.ReadAsStringAsync();
        cancel2.StatusCode.Should().Be(HttpStatusCode.OK, $"la 2ª cancelación debe aceptarse. Body: {body}");

        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_cancelled_by_expert_second");
        (await HireStatusAsync(hireId.Value)).Should().Be("cancelled");
        StripeCalls("POST /v1/refunds").Should().BeGreaterThan(refundsBefore,
            "la 2ª cancelación del experto reembolsa al cliente vía Stripe");

        await using var db = _api.CreateDbContext();
        var appt = await db.Appointments.SingleAsync(a => a.Id == apptId);
        appt.ExpertCancellationCount.Should().Be(2);
        appt.ClientCancellationCount.Should().Be(0, "los contadores son independientes por parte");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-13 · 2ª cancelación del CLIENTE → client_second + transfer 95% al experto
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-13 · 2ª cancelación del cliente por API real → client_second + transfer al experto", Skip = "Sistema antiguo retirado: propose/confirm/reject comentado (#if false)")]
    public async Task Second_client_cancellation_finalizes_with_expert_payout()
    {
        var mk = await SeedMarketplaceAsync("hf13");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf13_" + Guid.NewGuid().ToString("N"), "cs_hf13",
            "pi_hf13_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        await ProposeOkAsync(mk, hireId!.Value, days: 4, time: "10:00:00");
        var (apptId, _) = await AppointmentOfAsync(hireId.Value);
        await ConfirmOkAsync(mk, apptId);
        (await CancelAsync(mk, apptId, byExpert: false, "Cambio de planes"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await HireStatusAsync(hireId.Value)).Should().Be("pending");

        await ProposeOkAsync(mk, hireId.Value, days: 9, time: "13:00:00");
        await ConfirmOkAsync(mk, apptId);

        var transfersBefore = StripeCalls("POST /v1/transfers");
        var cancel2 = await CancelAsync(mk, apptId, byExpert: false, "Cancelo definitivamente");
        var body = await cancel2.Content.ReadAsStringAsync();
        cancel2.StatusCode.Should().Be(HttpStatusCode.OK, $"la 2ª cancelación debe aceptarse. Body: {body}");

        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_cancelled_by_client_second");
        (await HireStatusAsync(hireId.Value)).Should().Be("cancelled");
        StripeCalls("POST /v1/transfers").Should().BeGreaterThan(transfersBefore,
            "el split 0/95/5 paga al experto (penalización al cliente) vía Stripe");

        await using var db = _api.CreateDbContext();
        var appt = await db.Appointments.SingleAsync(a => a.Id == apptId);
        appt.ClientCancellationCount.Should().Be(2);
        appt.ExpertCancellationCount.Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-14/15 · disputa por HTTP + resolución por ADMIN (ambos sentidos)
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<(Marketplace Mk, int HireId, int DisputeId)> SetupDisputedHireAsync(string slug)
    {
        var mk = await SeedMarketplaceAsync(slug);
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, $"evt_{slug}_" + Guid.NewGuid().ToString("N"), $"cs_{slug}",
            $"pi_{slug}_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        await using (var db = _api.CreateDbContext())
        {
            var awaiting = await db.SystemStatuses.SingleAsync(s =>
                s.StatusType == "SearchHireStatus" && s.StatusValue == "awaiting_client_decision");
            var hire = await db.SearchHires.SingleAsync(h => h.Id == hireId);
            hire.StatusId = awaiting.Id;
            await db.SaveChangesAsync();
        }

        var jwt = _api.MintJwtFor(mk.ClientId, mk.ClientEmail);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(hireId!.Value.ToString()), "SearchHireId" },
            { new StringContent("El servicio no se realizó correctamente"), "Reason" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Dispute/dispute-service") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        (await _api.Client.SendAsync(request)).IsSuccessStatusCode.Should().BeTrue();

        await using var verify = _api.CreateDbContext();
        var disputeId = await verify.Disputes
            .Where(d => d.SearchHireId == hireId.Value).Select(d => d.Id).SingleAsync();
        return (mk, hireId.Value, disputeId);
    }

    private async Task<HttpResponseMessage> AdminResolveAsync(int disputeId, string action)
    {
        int adminId;
        var adminEmail = $"admin-{Guid.NewGuid():N}@test.dev";
        await using (var db = _api.CreateDbContext())
            adminId = (await new UserBuilder(adminEmail).AsClient().Verified().PersistAsync(db)).Id;

        var adminJwt = _api.MintJwtFor(adminId, adminEmail, name: "Admin Test", role: "Admin");
        return await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Put, $"/api/Dispute/{disputeId}/resolve",
            new { action, resolutionComments = "Resuelto tras revisar las evidencias" }, adminJwt));
    }

    [Fact(DisplayName = "HF-14 · disputa + resolución admin pro-CLIENTE → dispute_resolved_client + refund inline")]
    public async Task Dispute_resolved_for_client_via_http()
    {
        var (mk, hireId, disputeId) = await SetupDisputedHireAsync("hf14");
        (await HireStatusAsync(hireId)).Should().Be("disputed", "abrir la disputa marca el hire como disputed");

        var refundsBefore = StripeCalls("POST /v1/refunds");
        var retriesBefore = await CountRetriesAsync();
        var resolve = await AdminResolveAsync(disputeId, "refund_client");
        var body = await resolve.Content.ReadAsStringAsync();
        resolve.StatusCode.Should().Be(HttpStatusCode.OK, $"el admin debe poder resolver. Body: {body}");

        (await HireStatusAsync(hireId)).Should().Be("dispute_resolved_client");

        // FIX TX-5 (RefundService.cs Fase 1): antes el dinero NUNCA se movía inline porque
        // la lockTx manual chocaba con NpgsqlRetryingExecutionStrategy y TODO acababa
        // (y moría) en el retry de Hangfire (Logs prod #4649/#5565). Tras envolver el lock
        // en CreateExecutionStrategy, el split 90/8/2 se ejecuta DENTRO de la request.
        StripeCalls("POST /v1/refunds").Should().BeGreaterThan(refundsBefore,
            "el split 90/8/2 reembolsa el 90% al cliente vía Stripe INLINE (FIX TX-5)");

        await using var db = _api.CreateDbContext();
        var dispute = await db.Disputes.SingleAsync(d => d.Id == disputeId);
        dispute.Status.Should().Be("Resolved");

        (await CountScheduledMoneyRetriesAsync(db)).Should().Be(retriesBefore,
            "con el dinero movido inline NO debe encolarse RetryMoneyDistributionJobAsync");
    }

    /// <summary>Jobs Hangfire programados que apuntan a RetryMoneyDistributionJobAsync.</summary>
    private static async Task<int> CountScheduledMoneyRetriesAsync(AppDbContext db)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText =
            "SELECT count(*)::int FROM hangfire.job WHERE invocationdata::text LIKE '%RetryMoneyDistributionJobAsync%'";
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync();
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    [Fact(DisplayName = "HF-15 · disputa + resolución admin pro-EXPERTO → dispute_resolved_expert + transfer")]
    public async Task Dispute_resolved_for_expert_via_http()
    {
        var (mk, hireId, disputeId) = await SetupDisputedHireAsync("hf15");

        var transfersBefore = StripeCalls("POST /v1/transfers");
        var retriesBefore = await CountRetriesAsync();
        var resolve = await AdminResolveAsync(disputeId, "pay_expert");
        var body = await resolve.Content.ReadAsStringAsync();
        resolve.StatusCode.Should().Be(HttpStatusCode.OK, $"el admin debe poder resolver. Body: {body}");

        (await HireStatusAsync(hireId)).Should().Be("dispute_resolved_expert");

        // FIX TX-5: igual que HF-14 — el payout 0/95/5 se ejecuta inline, sin retry.
        StripeCalls("POST /v1/transfers").Should().BeGreaterThan(transfersBefore,
            "el split 0/95/5 paga al experto vía Stripe INLINE (FIX TX-5)");

        await using var db = _api.CreateDbContext();
        (await db.Disputes.SingleAsync(d => d.Id == disputeId)).Status.Should().Be("Resolved");

        (await CountScheduledMoneyRetriesAsync(db)).Should().Be(retriesBefore,
            "con el dinero movido inline NO debe encolarse RetryMoneyDistributionJobAsync");
    }

    private async Task<int> CountRetriesAsync()
    {
        await using var db = _api.CreateDbContext();
        return await CountScheduledMoneyRetriesAsync(db);
    }

    [Fact(DisplayName = "HF-15b · resolver disputa SIN rol admin → rechazado")]
    public async Task Dispute_resolution_requires_admin_role()
    {
        var (mk, hireId, disputeId) = await SetupDisputedHireAsync("hf15b");

        // El CLIENTE (no admin) intenta resolver su propia disputa a su favor
        var clientJwt = _api.MintJwtFor(mk.ClientId, mk.ClientEmail);
        var resolve = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Put, $"/api/Dispute/{disputeId}/resolve",
            new { action = "refund_client", resolutionComments = "yo me lo apruebo" }, clientJwt));

        resolve.IsSuccessStatusCode.Should().BeFalse("solo un admin puede resolver disputas");
        (await HireStatusAsync(hireId)).Should().Be("disputed", "la disputa debe seguir abierta");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-16 · contadores SEPARADOS: cancel cliente(1ª) + cancel experto(1ª) NO finalizan;
    //          la 2ª del MISMO lado (cliente) sí
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-16 · cancelaciones intercaladas cliente/experto: solo la 2ª del mismo lado finaliza", Skip = "Sistema antiguo retirado: propose/confirm/reject comentado (#if false)")]
    public async Task Interleaved_cancellations_have_separate_counters()
    {
        var mk = await SeedMarketplaceAsync("hf16");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf16_" + Guid.NewGuid().ToString("N"), "cs_hf16",
            "pi_hf16_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        // Ronda 1: cancela el CLIENTE (1ª suya)
        await ProposeOkAsync(mk, hireId!.Value, days: 3, time: "09:00:00");
        var (apptId, _) = await AppointmentOfAsync(hireId.Value);
        await ConfirmOkAsync(mk, apptId);
        (await CancelAsync(mk, apptId, byExpert: false, "1ª del cliente")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await HireStatusAsync(hireId.Value)).Should().Be("pending");

        // Ronda 2: cancela el EXPERTO (1ª suya — el hire sigue vivo)
        await ProposeOkAsync(mk, hireId.Value, days: 5, time: "11:00:00");
        await ConfirmOkAsync(mk, apptId);
        (await CancelAsync(mk, apptId, byExpert: true, "1ª del experto")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await HireStatusAsync(hireId.Value)).Should().Be("pending",
            "1+1 cancelaciones de partes DISTINTAS no finalizan (contadores separados)");

        // Ronda 3: el CLIENTE cancela su 2ª → finaliza
        await ProposeOkAsync(mk, hireId.Value, days: 7, time: "15:00:00");
        await ConfirmOkAsync(mk, apptId);
        (await CancelAsync(mk, apptId, byExpert: false, "2ª del cliente")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_cancelled_by_client_second");
        (await HireStatusAsync(hireId.Value)).Should().Be("cancelled");

        await using var db = _api.CreateDbContext();
        var appt = await db.Appointments.SingleAsync(a => a.Id == apptId);
        appt.ClientCancellationCount.Should().Be(2);
        appt.ExpertCancellationCount.Should().Be(1);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-17 · flujo de informe por HTTP: confirm → [timer→awaiting_report vía BD] →
    //          submit-report → awaiting_client_decision → complete-service → completed
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-17 · submit-report + aprobación del cliente por API real → completed", Skip = "Setup vía sistema antiguo (propose/confirm) comentado (#if false)")]
    public async Task Report_flow_via_http()
    {
        var mk = await SeedMarketplaceAsync("hf17");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf17_" + Guid.NewGuid().ToString("N"), "cs_hf17",
            "pi_hf17_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        await ProposeOkAsync(mk, hireId!.Value, days: 2, time: "10:00:00");
        var (apptId, _) = await AppointmentOfAsync(hireId.Value);
        await ConfirmOkAsync(mk, apptId);

        // confirmed → awaiting_report es transición de TIMER Hangfire (cita+3h), no
        // user-driven: la reproducimos en BD (igual que harían los 10 min del watchdog).
        await using (var db = _api.CreateDbContext())
        {
            var awaitingReport = await db.SystemStatuses.SingleAsync(s =>
                s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_awaiting_report");
            var appt = await db.Appointments.SingleAsync(a => a.Id == apptId);
            appt.StatusId = awaitingReport.Id;
            await db.SaveChangesAsync();
        }

        // El experto envía el informe por la API real
        var expertJwt = _api.MintJwtFor(mk.ExpertUserId, mk.ExpertEmail, role: "Expert");
        var report = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, $"/api/Appointment/submit-report/{apptId}",
            new { notes = "Inspección completada: estructura en buen estado." }, expertJwt));
        var reportBody = await report.Content.ReadAsStringAsync();
        report.StatusCode.Should().Be(HttpStatusCode.OK, $"submit-report debe aceptarse. Body: {reportBody}");

        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_report_sent");
        (await HireStatusAsync(hireId.Value)).Should().Be("awaiting_client_decision",
            "el informe enviado pasa el hire a decisión del cliente (24h)");

        // El cliente aprueba → completed + payout
        var transfersBefore = StripeCalls("POST /v1/transfers");
        var clientJwt = _api.MintJwtFor(mk.ClientId, mk.ClientEmail);
        var complete = await _api.Client.SendAsync(AuthedJson(
            HttpMethod.Post, "/api/SearchHire/complete-service",
            new { searchHireId = hireId.Value, clientApproved = true }, clientJwt));
        complete.StatusCode.Should().Be(HttpStatusCode.OK);

        (await HireStatusAsync(hireId.Value)).Should().Be("completed");
        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_completed");
        StripeCalls("POST /v1/transfers").Should().BeGreaterThan(transfersBefore,
            "la aprobación dispara el payout 95% al experto");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-18 · RetryMoneyDistributionJobAsync con statusValue de CITA (caso hire 16 prod):
    //          la guarda R16 no debe auto-omitir el reintento y el dinero debe moverse
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-18 · retry de dinero con AppointmentStatus (timer) → mueve el dinero (R16b)")]
    public async Task Money_retry_with_appointment_status_executes()
    {
        var mk = await SeedMarketplaceAsync("hf18");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf18_" + Guid.NewGuid().ToString("N"), "cs_hf18",
            "pi_hf18_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        // Reproducir el estado exacto del hire 16 de prod: el watchdog canceló por
        // no-propose → appointment en _no_proposal y hire finalizado a cancelled,
        // con el dinero AÚN sin mover (el inline falló y se encoló el retry).
        await using (var db = _api.CreateDbContext())
        {
            var apptStatus = await db.SystemStatuses.SingleAsync(s =>
                s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_cancelled_by_client_no_proposal");
            // El handler del webhook ya crea el Appointment (IX_Appointments_SearchHireId
            // es único): actualizar el existente o crearlo si este flujo no lo trajo.
            var appt = await db.Appointments.SingleOrDefaultAsync(a => a.SearchHireId == hireId!.Value);
            if (appt is null)
                db.Appointments.Add(new Appointment { SearchHireId = hireId!.Value, StatusId = apptStatus.Id });
            else
                appt.StatusId = apptStatus.Id;

            var cancelled = await db.SystemStatuses.SingleAsync(s =>
                s.StatusType == "SearchHireStatus" && s.StatusValue == "cancelled");
            var hire = await db.SearchHires.SingleAsync(h => h.Id == hireId);
            hire.StatusId = cancelled.Id;
            await db.SaveChangesAsync();
        }

        // Ejecutar el job EXACTAMENTE como lo invoca Hangfire: resolviendo el servicio del DI real.
        var moneyBefore = StripeCalls("POST /v1/transfers") + StripeCalls("POST /v1/refunds");
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var refundService = scope.ServiceProvider
                .GetRequiredService<newApi.Services.StripeRefundService>();
            // Antes del fix R16b esto era un no-op silencioso (hire='cancelled' !=
            // statusValue de cita) y el dinero quedaba atascado para siempre.
            await refundService.RetryMoneyDistributionJobAsync(
                hireId!.Value,
                "appointment_cancelled_by_client_no_proposal",
                "Retry tras cancelación automática por timer (test HF-18)",
                null);
        }

        var moneyAfter = StripeCalls("POST /v1/transfers") + StripeCalls("POST /v1/refunds");
        moneyAfter.Should().BeGreaterThan(moneyBefore,
            "el retry debe ejecutar la distribución del split de appointment_cancelled_by_client_no_proposal contra Stripe");

        await using var verify = _api.CreateDbContext();
        var moneyFts = await verify.FinancialTransactions.CountAsync(ft =>
            ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hireId &&
            ft.TransactionType != "ServicePayment");
        moneyFts.Should().BeGreaterThan(0, "la distribución debe registrar la FT del dinero movido");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-19 · FIX TX-6: refund falla tras el transfer → NO auto-reversal; el retry
    //          completa SOLO el refund y cada parte cobra exactamente una vez
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-19 · refund falla → transfer se conserva (sin reversal) y el retry converge (TX-6)")]
    public async Task Refund_failure_keeps_transfer_and_retry_converges()
    {
        var mk = await SeedMarketplaceAsync("hf19");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf19_" + Guid.NewGuid().ToString("N"), "cs_hf19",
            "pi_hf19_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        var reversalsBefore = _api.FakeStripe.Requests.Count(r => r.Contains("/reversals"));
        var transfersBefore = StripeCalls("POST /v1/transfers");

        // INTENTO 1: el split 90/8/2 — el transfer del 8% sale bien, el refund del 90%
        // falla persistentemente. OJO: Stripe.net reintenta los 500 por su cuenta
        // (MaxNetworkRetries) ADEMÁS del bucle de 3 intentos del RefundService —
        // inyectamos fallos de sobra para agotar todas las capas.
        _api.FakeStripe.RefundFailuresRemaining = 20;
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<newApi.Services.StripeRefundService>();
            var ok = await svc.ProcessMoneyDistributionAsync(
                hireId!.Value, "dispute_resolved_client", "test TX-6 intento 1", null, updateState: false);
            ok.Should().BeFalse("el refund falló — la distribución queda pendiente de retry");
        }

        StripeCalls("POST /v1/transfers").Should().BeGreaterThan(transfersBefore,
            "el transfer del 8% al experto se ejecutó antes del fallo del refund");
        _api.FakeStripe.Requests.Count(r => r.Contains("/reversals")).Should().Be(reversalsBefore,
            "FIX TX-6: NO debe auto-reversarse el transfer cuando falla el refund (el retry lo completa)");

        await using (var db = _api.CreateDbContext())
        {
            var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
            hire.RequiresManualReview.Should().BeTrue("el fallo debe quedar marcado para visibilidad admin");
            hire.RefundFailedAt.Should().NotBeNull("RefundFailedAt alimenta el digest diario P3-1");
        }

        // INTENTO 2 (= retry de Hangfire): el refund ya no falla → debe completar SOLO lo
        // que falta y dejar el dinero correcto: cliente 90%, experto 8% (una sola vez).
        _api.FakeStripe.RefundFailuresRemaining = 0; // limpiar fallos sobrantes
        var refundsBefore = StripeCalls("POST /v1/refunds");
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<newApi.Services.StripeRefundService>();
            var ok = await svc.ProcessMoneyDistributionAsync(
                hireId.Value, "dispute_resolved_client", "test TX-6 retry", null, updateState: false);
            ok.Should().BeTrue("con el refund ya operativo el retry debe converger");
        }

        StripeCalls("POST /v1/refunds").Should().BeGreaterThan(refundsBefore,
            "el retry ejecuta el refund del 90% pendiente");
        _api.FakeStripe.Requests.Count(r => r.Contains("/reversals")).Should().Be(reversalsBefore,
            "tampoco en el retry debe haber reversal alguna");

        await using (var verify = _api.CreateDbContext())
        {
            var fts = await verify.FinancialTransactions
                .Where(ft => ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hireId)
                .ToListAsync();
            fts.Count(ft => ft.TransactionType == "Refund").Should().Be(1, "un único refund del 90%");
            fts.Count(ft => ft.TransactionType == "Payout").Should().Be(1, "un único payout del 8% — el experto cobra exactamente una vez");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-20 · FIX TX-7: dos distribuciones CONCURRENTES del mismo hire con estados
    //          distintos → el advisory lock de la fase de dinero serializa y el
    //          dinero total que sale NUNCA supera lo que entró
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-20 · distribuciones concurrentes (completed vs dispute) → dinero conservado (TX-7)")]
    public async Task Concurrent_distributions_conserve_money()
    {
        var mk = await SeedMarketplaceAsync("hf20");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf20_" + Guid.NewGuid().ToString("N"), "cs_hf20",
            "pi_hf20_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        // Dos flujos reales que ANTES de TX-7 podían cruzarse sin lock: el payout de un
        // 'completed' (95% experto) y la resolución de disputa pro-cliente (90/8/2).
        // Cada tarea usa su propio scope/DbContext (conexiones distintas, como en prod).
        async Task<bool> RunAsync(string statusValue)
        {
            using var scope = _api.Factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<newApi.Services.StripeRefundService>();
            return await svc.ProcessMoneyDistributionAsync(
                hireId!.Value, statusValue, $"test TX-7 {statusValue}", null, updateState: false);
        }

        var tasks = new[]
        {
            Task.Run(() => RunAsync("completed")),
            Task.Run(() => RunAsync("dispute_resolved_client")),
        };
        await Task.WhenAll(tasks);

        // INVARIANTE DE CONSERVACIÓN: lo que sale (refunds + payouts − reversals) no
        // puede superar lo que entró (110€), con tolerancia de redondeo.
        await using var db = _api.CreateDbContext();
        var fts = await db.FinancialTransactions
            .Where(ft => ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hireId)
            .ToListAsync();

        var refunds = fts.Where(f => f.TransactionType == "Refund").Sum(f => Math.Abs(f.Amount));
        var payouts = fts.Where(f => f.TransactionType == "Payout").Sum(f => Math.Abs(f.Amount));
        var reversals = fts.Where(f => f.TransactionType == "TransferReversal").Sum(f => Math.Abs(f.Amount));
        var outflow = refunds + payouts - reversals;

        outflow.Should().BeLessThanOrEqualTo(110m + 0.02m,
            $"el dinero debe conservarse aunque dos flujos compitan. Refunds={refunds} Payouts={payouts} Reversals={reversals} (FTs: {string.Join(", ", fts.Select(f => f.TransactionType + "=" + f.Amount))})");

        // Y nunca puede haber DOS payouts vivos (el guard existingTransfer + el lock lo impiden)
        fts.Count(f => f.TransactionType == "Payout").Should().BeLessThanOrEqualTo(1,
            "el experto no puede cobrar dos veces por el mismo hire");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-21 · FIX TX-8: si el dinero de una 2ª cancelación (de usuario) falla, el hire
    //          finaliza igual PERO se encola el retry de Hangfire (antes quedaba atascado)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-21 · 2ª cancelación con dinero fallido → hire cancelled + retry encolado (TX-8)", Skip = "Sistema antiguo retirado: propose/confirm/reject comentado (#if false)")]
    public async Task Second_cancellation_money_failure_enqueues_retry()
    {
        var mk = await SeedMarketplaceAsync("hf21");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf21_" + Guid.NewGuid().ToString("N"), "cs_hf21",
            "pi_hf21_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        // 1ª cancelación del cliente (sin dinero) + repropose + confirm
        await ProposeOkAsync(mk, hireId!.Value, days: 4, time: "10:00:00");
        var (apptId, _) = await AppointmentOfAsync(hireId.Value);
        await ConfirmOkAsync(mk, apptId);
        (await CancelAsync(mk, apptId, byExpert: false, "1ª")).StatusCode.Should().Be(HttpStatusCode.OK);
        await ProposeOkAsync(mk, hireId.Value, days: 9, time: "13:00:00");
        await ConfirmOkAsync(mk, apptId);

        int retriesBefore;
        await using (var db0 = _api.CreateDbContext())
            retriesBefore = await CountScheduledMoneyRetriesAsync(db0);

        // 2ª cancelación del cliente (0/95/5 → transfer al experto) PERO el transfer falla:
        // antes el hire se finalizaba con el dinero atascado y SIN retry encolado.
        _api.FakeStripe.TransferFailuresRemaining = 20;
        var cancel2 = await CancelAsync(mk, apptId, byExpert: false, "2ª con fallo de Stripe");
        cancel2.StatusCode.Should().Be(HttpStatusCode.OK, "la cancelación no debe bloquear al usuario");
        _api.FakeStripe.TransferFailuresRemaining = 0;

        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_cancelled_by_client_second");
        (await HireStatusAsync(hireId.Value)).Should().Be("cancelled", "el estado se finaliza igual");

        await using var db = _api.CreateDbContext();
        (await CountScheduledMoneyRetriesAsync(db)).Should().BeGreaterThan(retriesBefore,
            "FIX TX-8: el dinero pendiente debe quedar encolado en Hangfire (antes se quedaba atascado sin retry)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-22 · FIX TX-9 (caso A): deauthorization del experto con refund fallido →
    //          retry encolado (antes: notificación falsa de "reembolsado" y dinero atascado)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-22 · deauthorization con refund fallido → retry encolado (TX-9)")]
    public async Task Deauthorization_refund_failure_enqueues_retry()
    {
        var mk = await SeedMarketplaceAsync("hf22");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf22_" + Guid.NewGuid().ToString("N"), "cs_hf22",
            "pi_hf22_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        string acctId;
        int retriesBefore;
        await using (var db0 = _api.CreateDbContext())
        {
            acctId = (await db0.ExpertProfiles.SingleAsync(ep => ep.UserId == mk.ExpertUserId)).StripeAccountId!;
            retriesBefore = await CountScheduledMoneyRetriesAsync(db0);
        }

        // El experto desconecta su cuenta (webhook Connect FIRMADO) con el refund roto.
        _api.FakeStripe.RefundFailuresRemaining = 20;
        var payload = StripeEventBuilder.AccountApplicationDeauthorized(
            "evt_hf22_deauth_" + Guid.NewGuid().ToString("N"), acctId);
        var request = StripeWebhookSigner.BuildSignedPost(
            "/api/subscription/webhook", payload, ApiFactoryFixture.WebhookSecret);
        var response = await _api.Client.SendAsync(request);
        _api.FakeStripe.RefundFailuresRemaining = 0;

        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"el webhook de deauth debe procesarse. Body: {body}");

        await using var db = _api.CreateDbContext();
        (await CountScheduledMoneyRetriesAsync(db)).Should().BeGreaterThan(retriesBefore,
            "FIX TX-9: el refund fallido tras deauthorization debe quedar encolado (antes se notificaba 'reembolsado' sin reembolso)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-23 · FIX TX-9 (caso B): borrado de cuenta del experto con refund fallido →
    //          retry encolado ANTES de la anonimización (que conserva FTs/PI por ley)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-23 · borrado de cuenta del experto con refund fallido → retry encolado (TX-9)")]
    public async Task Account_deletion_refund_failure_enqueues_retry()
    {
        var mk = await SeedMarketplaceAsync("hf23");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf23_" + Guid.NewGuid().ToString("N"), "cs_hf23",
            "pi_hf23_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        int retriesBefore;
        await using (var db0 = _api.CreateDbContext())
            retriesBefore = await CountScheduledMoneyRetriesAsync(db0);

        // El EXPERTO borra su cuenta con un hire pending → rama refund-al-cliente (1109).
        _api.FakeStripe.RefundFailuresRemaining = 20;
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var deletion = scope.ServiceProvider
                .GetRequiredService<newApi.Services.IAccountDeletionService>();
            var result = await deletion.DeleteAccountAsync(
                mk.ExpertUserId,
                new newApi.DataLayer.Models.DTOs.AccountDeletionRequestDto { Reason = "test TX-9" });
            result.Success.Should().BeTrue($"el borrado GDPR no debe bloquearse por un fallo de Stripe. Msg: {result.Message}");
        }
        _api.FakeStripe.RefundFailuresRemaining = 0;

        await using var db = _api.CreateDbContext();
        (await CountScheduledMoneyRetriesAsync(db)).Should().BeGreaterThan(retriesBefore,
            "FIX TX-9: el refund fallido en el borrado debe quedar encolado (la anonimización conserva FTs/PI, el retry es viable)");

        // La FT ServicePayment sobrevive a la anonimización (retención legal) → el retry tiene lo que necesita
        var sp = await db.FinancialTransactions.SingleOrDefaultAsync(ft =>
            ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hireId &&
            ft.TransactionType == "ServicePayment");
        sp.Should().NotBeNull();
        sp!.StripePaymentIntentId.Should().NotBeNullOrEmpty("la anonimización solo nulea UserId, no el PI");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-24 · FIX TX-10 (barrido A-v): un timer reclamado-y-muerto (claim atómico +
    //          crash duro antes del handler) es rescatado por el watchdog: re-abierto,
    //          re-procesado, transición + dinero ejecutados
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-24 · timer claim-then-crash → el watchdog A-v lo rescata (transición + refund) (TX-10)", Skip = "Setup vía sistema antiguo (propose/confirm) comentado (#if false)")]
    public async Task Claimed_then_crashed_timer_is_rescued_by_watchdog()
    {
        var mk = await SeedMarketplaceAsync("hf24");
        var (_, hireId) = await PostCheckoutWebhookAsync(
            mk, "evt_hf24_" + Guid.NewGuid().ToString("N"), "cs_hf24",
            "pi_hf24_" + Guid.NewGuid().ToString("N")[..10]);
        hireId.Should().NotBeNull();

        // Cliente propone → cita en appointment_proposed con timer 'response' real (24h).
        await ProposeOkAsync(mk, hireId!.Value, days: 4, time: "10:00:00");
        var (apptId, status) = await AppointmentOfAsync(hireId.Value);
        status.Should().Be("appointment_proposed");

        // Simular claim-then-crash: el timer 'response' fue reclamado (IsExpired=true)
        // hace 20 min, su EndTime ya venció, pero el handler NUNCA corrió (la cita sigue
        // en appointment_proposed y el hire en pending). Sin A-v, esto quedaba congelado
        // para siempre: el barrido normal ignora expirados y el catch nunca llegó a re-abrir.
        await using (var db = _api.CreateDbContext())
        {
            var timer = await db.AppointmentTimers.SingleAsync(t =>
                t.AppointmentId == apptId && t.TimerType == "response" && !t.IsExpired);
            timer.IsExpired = true;
            timer.ExpiredAt = DateTime.UtcNow.AddMinutes(-20);
            timer.EndTime = DateTime.UtcNow.AddMinutes(-30);
            await db.SaveChangesAsync();
        }

        // El watchdog REAL corre (igual que el RecurringJob cada 10 min)
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<newApi.Services.IAppointmentService>();
            await svc.ProcessOverdueTimersAsync();
        }

        // Rescate completo: el experto no respondió → cancelled_by_expert_no_response,
        // hire finalizado y refund 100/0/0 al cliente ejecutado contra el stub.
        (await AppointmentOfAsync(hireId.Value)).Status.Should().Be("appointment_cancelled_by_expert_no_response",
            "el barrido A-v debe re-abrir el timer muerto y ejecutar su transición");
        (await HireStatusAsync(hireId.Value)).Should().Be("cancelled");

        await using (var verify = _api.CreateDbContext())
        {
            var refund = await verify.FinancialTransactions.SingleOrDefaultAsync(ft =>
                ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hireId &&
                ft.TransactionType == "Refund");
            refund.Should().NotBeNull("el no-response reembolsa el 100% al cliente — el dinero no puede perderse por un crash");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HF-SNAPSHOT · el snapshot contractual de % capturado al crear el hire lee la
    //   config de 'completed' (camino feliz = 0/95/5), NO la de 'pending'. Reproduce
    //   la divergencia de prod (config 'pending' viva alterada a 100/0/0) y comprueba
    //   que el snapshot grabado sigue siendo 0/95/5 → si el código leyera 'pending'
    //   quedaría 100/0/0 e invertiría el reparto al completar (cliente 100%, experto 0).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "HF-SNAPSHOT · snapshot captura el split de 'completed' (0/95/5), no el de 'pending' divergente")]
    public async Task Snapshot_captures_completed_split_not_pending()
    {
        var mk = await SeedMarketplaceAsync("hfsnap");

        // Lee los % originales de la config global de 'pending' para restaurarlos al final
        // (la StatusConfiguration se siembra una sola vez por contenedor y se comparte).
        var (origCp, origEp, origPp) = await GetGlobalSplitAsync("pending");
        try
        {
            // Simula la divergencia de prod: la config viva de 'pending' pasa a 100/0/0.
            // Si la captura leyera 'pending', grabaría este reparto invertido.
            await SetGlobalSplitAsync("pending", 100, 0, 0);

            var (response, hireId) = await PostCheckoutWebhookAsync(
                mk, "evt_hfsnap_" + Guid.NewGuid().ToString("N"), "cs_hfsnap",
                "pi_hfsnap_" + Guid.NewGuid().ToString("N")[..10]);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"el webhook debe crear el hire. Body: {body}");
            hireId.Should().NotBeNull();

            await using var db = _api.CreateDbContext();
            var hire = await db.SearchHires.SingleAsync(h => h.Id == hireId!.Value);

            // CLAVE: el snapshot debe reflejar el reparto de 'completed' (0/95/5), que es el
            // que RefundService aplica al finalizar con éxito — NO el 'pending' divergente (100/0/0).
            hire.ClientPercentageSnapshot.Should().Be(0m,
                "el snapshot lee 'completed' (cliente 0%), no el 'pending' divergente (100%)");
            hire.ExpertPercentageSnapshot.Should().Be(95m,
                "el experto se queda el 95% al completar — invertir esto le pagaría 0");
            hire.PlatformPercentageSnapshot.Should().Be(5m, "la plataforma retiene el 5%");
        }
        finally
        {
            await SetGlobalSplitAsync("pending", origCp, origEp, origPp);
        }
    }

    /// <summary>Lee el split (%) de la StatusConfiguration GLOBAL (CategoryId NULL) de un SearchHireStatus.</summary>
    private async Task<(decimal Cp, decimal Ep, decimal Pp)> GetGlobalSplitAsync(string statusValue)
    {
        await using var db = _api.CreateDbContext();
        var cfg = await (from sc in db.StatusConfigurations
                         join ss in db.SystemStatuses on sc.StatusId equals ss.Id
                         where ss.StatusValue == statusValue && ss.StatusType == "SearchHireStatus"
                               && sc.CategoryId == null && sc.ServiceTypeCategoryId == null
                         select sc).SingleAsync();
        return (cfg.ClientPercentage, cfg.ExpertPercentage, cfg.PlatformPercentage);
    }

    /// <summary>Sobrescribe el split (%) de la StatusConfiguration GLOBAL de un SearchHireStatus.</summary>
    private async Task SetGlobalSplitAsync(string statusValue, decimal cp, decimal ep, decimal pp)
    {
        await using var db = _api.CreateDbContext();
        var cfg = await (from sc in db.StatusConfigurations
                         join ss in db.SystemStatuses on sc.StatusId equals ss.Id
                         where ss.StatusValue == statusValue && ss.StatusType == "SearchHireStatus"
                               && sc.CategoryId == null && sc.ServiceTypeCategoryId == null
                         select sc).SingleAsync();
        cfg.ClientPercentage = cp;
        cfg.ExpertPercentage = ep;
        cfg.PlatformPercentage = pp;
        cfg.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
