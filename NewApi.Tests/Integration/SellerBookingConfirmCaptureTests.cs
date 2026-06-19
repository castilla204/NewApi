using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using FluentAssertions;

namespace NewApi.Tests.Integration;

/// <summary>
/// Confirmación del experto (Tarea 3) — el confirm del vendedor YA NO captura el pago.
///
/// Tras el checkout en modo "seller" sin hueco, el pago queda AUTORIZADO (PI en
/// requires_capture, hire.CaptureStatus="Authorized"). Cuando el vendedor confirma
/// día/hora/lugar vía POST /api/seller-booking/{token}/confirm la cita nace en
/// `appointment_pending_expert_confirmation` y el pago SIGUE autorizado: la captura se
/// hará cuando el EXPERTO apruebe la cita (otra tarea). El confirm NO emite POST .../capture
/// y genera el token+plazo (36h) de confirmación del experto, anulando el SellerBookingToken.
///
/// Reusa el harness HTTP real (ApiFactoryFixture + FakeStripeServer) y siembra un hire
/// seller AUTORIZADO con su FinancialTransaction ServicePayment (vínculo PI↔hire) más un
/// experto que trabaja todos los días 09-18 Madrid, igual que SellerBookingWindowHttpTests.
/// </summary>
[Collection("Api")]
public class SellerBookingConfirmCaptureTests
{
    private readonly ApiFactoryFixture _api;
    public SellerBookingConfirmCaptureTests(ApiFactoryFixture api) => _api = api;

    // Siembra un hire seller AUTORIZADO: token + experto disponible 7d/09-18 + FT ServicePayment
    // con un PaymentIntentId único + CaptureStatus="Authorized".
    // withServicePayment=false omite la FT ServicePayment para ejercitar la rama "sin PaymentIntent".
    private async Task<(string token, int serviceId, int hireId, string pi)> SeedAuthorizedSellerHireAsync(
        DateTime createdAtUtc, bool withServicePayment = true)
    {
        await using var db = _api.CreateDbContext();
        var expertUser = await new UserBuilder().AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").WithDuration(1).PersistAsync(db);
        for (var dow = 0; dow < 7; dow++)
            db.ExpertAvailabilityRules.Add(new ExpertAvailabilityRule
            {
                ExpertId = expert.Id, DayOfWeek = dow,
                StartLocal = new TimeSpan(9, 0, 0), EndLocal = new TimeSpan(18, 0, 0),
                Timezone = "Europe/Madrid", IsActive = true,
                EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });

        var client = await new UserBuilder().AsClient().Verified().PersistAsync(db);
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithStatusValue("pending").PersistAsync(db);

        hire.CreatedAt = createdAtUtc;
        hire.SellerBookingToken = token;
        hire.SellerBookingDeadline = createdAtUtc.AddHours(48);
        hire.ExpertTimezone = "Europe/Madrid";
        hire.CaptureStatus = "Authorized"; // checkout seller difirió la captura

        // FT ServicePayment: vínculo PI↔hire que usa el confirm para localizar el PaymentIntent.
        var pi = "pi_sbc_" + Guid.NewGuid().ToString("N")[..12];
        if (withServicePayment)
        {
            db.FinancialTransactions.Add(new FinancialTransaction
            {
                UserId = client.Id,
                Amount = 100m,
                AmountCents = 10000,
                Currency = "EUR",
                TransactionType = "ServicePayment",
                RelatedEntityType = "SearchHire",
                RelatedEntityId = hire.Id,
                StripePaymentIntentId = pi,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return (token, svc.Id, hire.Id, pi);
    }

    private async Task<SlotDto> FirstSlotAsync(string token, DateTime createdAt)
    {
        // Día +4 (suelo+1) cae dentro de la ventana [+3,+14]; el experto trabaja todos los días.
        var probeDate = SellerBookingWindow.StartUtc(createdAt).AddDays(1).ToString("yyyy-MM-dd");
        var slotsRes = await _api.Client.GetAsync($"/api/seller-booking/{token}/slots?date={probeDate}");
        slotsRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var slots = await slotsRes.Content.ReadFromJsonAsync<List<SlotDto>>();
        slots.Should().NotBeNullOrEmpty("el experto trabaja ese día dentro de la ventana");
        return slots![0];
    }

    // ── Test 1: confirm OK → 200, cita pending_expert_confirmation, pago SIGUE Authorized,
    //    PI NO capturado (sin POST .../capture), token+plazo (36h) generados, SellerBookingToken=null.
    [Fact(DisplayName = "SBC-01 · confirm → 200, cita pending_expert_confirmation, sin captura, token experto")]
    public async Task Confirm_creates_pending_appointment_without_capture()
    {
        var createdAt = DateTime.UtcNow;
        var (token, serviceId, hireId, pi) = await SeedAuthorizedSellerHireAsync(createdAt);
        var slot = await FirstSlotAsync(token, createdAt);

        var res = await _api.Client.PostAsJsonAsync($"/api/seller-booking/{token}/confirm", new
        {
            startsAtUtc = slot.StartUtc, endsAtUtc = slot.EndUtc, location = "Calle Mayor 1",
            latitude = "40.0", longitude = "-3.7",
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        // CLAVE: el confirm del vendedor YA NO captura — el pago se cobra al aprobar el experto.
        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "el confirm del vendedor no captura: la captura es al aprobar el experto");

        await using var db = _api.CreateDbContext();
        var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
        hire.CaptureStatus.Should().Be("Authorized", "el pago sigue autorizado tras confirmar el vendedor");
        hire.SellerBookingToken.Should().BeNull("token de un solo uso");
        hire.ExpertConfirmationToken.Should().NotBeNullOrEmpty("el confirm genera el token de confirmación del experto");
        hire.ExpertConfirmationDeadline.Should().NotBeNull();
        // Ventana seller = min(now+36h, inicio de la cita). El hueco (+4 días) está lejos → manda now+36h.
        hire.ExpertConfirmationDeadline!.Value.Should().BeOnOrBefore(slot.StartUtc, "el plazo nunca supera el inicio de la cita");
        hire.ExpertConfirmationDeadline!.Value.Should().BeCloseTo(DateTime.UtcNow.AddHours(36), TimeSpan.FromMinutes(2),
            "plazo ≈ now+36h cuando la cita es lejana");

        var appt = await db.Appointments.AsNoTracking().Include(a => a.Status)
            .SingleAsync(a => a.SearchHireId == hireId);
        appt.Status!.StatusValue.Should().Be("appointment_pending_expert_confirmation");
        appt.BlocksCalendar.Should().BeTrue();
    }

    // ── Test 2: como el confirm ya NO captura, un PI que fallaría al capturar es IRRELEVANTE:
    //    el confirm completa igual (200, sin POST .../capture). Captura = futura aprobación del experto.
    [Fact(DisplayName = "SBC-02 · confirm no intenta capturar (PI marcado como fallo de captura es irrelevante) → 200")]
    public async Task Confirm_does_not_capture_even_if_capture_would_fail()
    {
        var createdAt = DateTime.UtcNow;
        var (token, serviceId, hireId, pi) = await SeedAuthorizedSellerHireAsync(createdAt);
        var slot = await FirstSlotAsync(token, createdAt);

        // Aunque marquemos este PI para fallar al capturar, el confirm no debe NI intentar capturar.
        _api.FakeStripe.CaptureFailurePaymentIntents[pi] = 1;
        try
        {
            var res = await _api.Client.PostAsJsonAsync($"/api/seller-booking/{token}/confirm", new
            {
                startsAtUtc = slot.StartUtc, endsAtUtc = slot.EndUtc, location = "Calle Mayor 1",
            });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        }
        finally
        {
            _api.FakeStripe.CaptureFailurePaymentIntents.TryRemove(pi, out _);
        }

        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "el confirm nunca captura, así que un fallo de captura forzado no le afecta");

        await using var db = _api.CreateDbContext();
        var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
        hire.CaptureStatus.Should().Be("Authorized", "sigue autorizado: no hay captura en el confirm");

        var appts = await db.Appointments.AsNoTracking().Where(a => a.SearchHireId == hireId).ToListAsync();
        appts.Should().HaveCount(1, "la cita se crea pendiente de confirmación del experto");
    }

    // ── Test 3: hire AUTORIZADO SIN FT ServicePayment: como el confirm ya NO localiza ni captura el
    //    PI, la ausencia de FT es irrelevante → 200, cita pending creada, sin POST .../capture.
    [Fact(DisplayName = "SBC-03 · confirm sin FT ServicePayment → 200 (ya no se localiza/captura el PI)")]
    public async Task Confirm_without_payment_intent_still_creates_pending_appointment()
    {
        var createdAt = DateTime.UtcNow;
        var (token, serviceId, hireId, pi) = await SeedAuthorizedSellerHireAsync(createdAt, withServicePayment: false);
        var slot = await FirstSlotAsync(token, createdAt);

        var res = await _api.Client.PostAsJsonAsync($"/api/seller-booking/{token}/confirm", new
        {
            startsAtUtc = slot.StartUtc, endsAtUtc = slot.EndUtc, location = "Calle Mayor 1",
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        // No se intentó capturar el PI de este hire (el confirm ya no captura en absoluto).
        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "el confirm no captura, así que no necesita localizar el PaymentIntent");

        await using var db = _api.CreateDbContext();
        var appts = await db.Appointments.AsNoTracking().Where(a => a.SearchHireId == hireId).ToListAsync();
        appts.Should().HaveCount(1, "el confirm crea la cita pendiente sin depender de la FT ServicePayment");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tarea 4 — POST /api/seller-booking/{token}/decline ("no puedo coordinar").
    // El vendedor declina: cancelar la AUTORIZACIÓN del pago SIN coste (PI en
    // requires_capture → cancel = 0 €) y devolver el 100% al comprador finalizando
    // el hire a 'cancelled'. La devolución la hace la distribución de dinero encolada
    // (rama N10 del RefundService); como en tests Hangfire NO procesa jobs
    // (HANGFIRE_SERVER_DISABLED=1), simulamos el job resolviendo el StripeRefundService
    // del DI real y llamando ProcessMoneyDistributionAsync directamente — el MISMO
    // patrón que HttpHireFlowTests usa para los jobs de distribución.
    // ─────────────────────────────────────────────────────────────────────────

    // ── Decline-01: hire AUTORIZADO → 200; tras la distribución encolada: PI canceled
    //    (0 €), SIN FT "Refund" (no hubo cobro que devolver), hire finalizado a
    //    'cancelled', SellerBookingToken=null.
    [Fact(DisplayName = "SBD-01 · decline de hire autorizado → 200, PI cancelado (0€), sin Refund, hire cancelled")]
    public async Task Decline_authorized_hire_cancels_authorization_no_fee_refunds_buyer_100()
    {
        var createdAt = DateTime.UtcNow;
        var (token, _, hireId, pi) = await SeedAuthorizedSellerHireAsync(createdAt);

        var res = await _api.Client.PostAsync($"/api/seller-booking/{token}/decline", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        // Mutex: el token se consume inmediatamente en el endpoint (no espera al job).
        await using (var db = _api.CreateDbContext())
        {
            var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
            hire.SellerBookingToken.Should().BeNull("decline anula el token de un solo uso");
        }

        // Simular el job de distribución encolado (Hangfire no corre en tests): mismo patrón
        // y mismos argumentos que SellerBookingController.Decline encola.
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var refundService = scope.ServiceProvider
                .GetRequiredService<newApi.Services.StripeRefundService>();
            var ok = await refundService.ProcessMoneyDistributionAsync(
                hireId,
                "appointment_cancelled_by_client_gt24h",
                "El vendedor indicó que no puede coordinar la cita: devolución sin coste al comprador.",
                null,
                true);
            ok.Should().BeTrue("la distribución de una cancelación sin cobro debe completar (N10 cancela el PI)");
        }

        // N10: el PI estaba en requires_capture → se CANCELA (0 €), no se reembolsa.
        _api.FakeStripe.Requests.Should().Contain(r => r == $"POST /v1/payment_intents/{pi}/cancel",
            "un PI no capturado se cancela (0 €), nunca se intenta capturar");
        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "declinar NUNCA cobra al comprador");

        await using (var db = _api.CreateDbContext())
        {
            // Sin fila FinancialTransaction "Refund": no hubo cobro que devolver (cancelación pre-captura).
            var refundFts = await db.FinancialTransactions.AsNoTracking()
                .Where(t => t.RelatedEntityType == "SearchHire" && t.RelatedEntityId == hireId
                            && t.TransactionType == "Refund")
                .ToListAsync();
            refundFts.Should().BeEmpty("cancelar un PI no capturado no genera FT Refund (no se cobró nada)");

            // No se creó ninguna cita.
            var appts = await db.Appointments.AsNoTracking().Where(a => a.SearchHireId == hireId).ToListAsync();
            appts.Should().BeEmpty("declinar no crea cita");

            // Hire finalizado a 'cancelled' (el estado gt24h es finalización y mapea a cancelled).
            var statusValue = await db.SearchHires.AsNoTracking()
                .Where(h => h.Id == hireId)
                .Join(db.SystemStatuses.AsNoTracking(), h => h.StatusId, s => s.Id, (h, s) => s.StatusValue)
                .SingleAsync();
            statusValue.Should().Be("cancelled", "la devolución 100% al comprador finaliza el hire a 'cancelled'");
        }
    }

    // ── Decline-02: ya hay cita reservada (Appointment != null) → 409.
    [Fact(DisplayName = "SBD-02 · decline cuando ya hay cita → 409 Conflict")]
    public async Task Decline_when_already_booked_returns_conflict()
    {
        var createdAt = DateTime.UtcNow;
        var (token, _, hireId, _) = await SeedAuthorizedSellerHireAsync(createdAt);

        // Sembrar una cita confirmada para este hire (decline debe rechazar con 409).
        await using (var db = _api.CreateDbContext())
        {
            var confirmed = await db.SystemStatuses.SingleAsync(
                s => s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_confirmed");
            var hire = await db.SearchHires.SingleAsync(h => h.Id == hireId);
            db.Appointments.Add(new Appointment
            {
                SearchHireId = hireId,
                StatusId = confirmed.Id,
                ExpertId = hire.ExpertId,
                StartsAtUtc = createdAt.AddDays(4),
                EndsAtUtc = createdAt.AddDays(4).AddHours(1),
                BlocksCalendar = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var res = await _api.Client.PostAsync($"/api/seller-booking/{token}/decline", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());

        // El token NO se consume si ya había cita (no llega al mutex).
        await using (var db = _api.CreateDbContext())
        {
            var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
            hire.SellerBookingToken.Should().Be(token, "un 409 por cita existente no debe anular el token");
        }
    }

    // ── Decline-03: idempotencia/robustez — segundo decline con el token ya anulado → NotFound.
    [Fact(DisplayName = "SBD-03 · segundo decline con token ya anulado → 404")]
    public async Task Decline_twice_second_call_returns_not_found()
    {
        var createdAt = DateTime.UtcNow;
        var (token, _, _, _) = await SeedAuthorizedSellerHireAsync(createdAt);

        var first = await _api.Client.PostAsync($"/api/seller-booking/{token}/decline", content: null);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        // El token ya está anulado: la segunda llamada no encuentra el hire por token.
        var second = await _api.Client.PostAsync($"/api/seller-booking/{token}/decline", content: null);
        second.StatusCode.Should().Be(HttpStatusCode.NotFound, await second.Content.ReadAsStringAsync());
    }

    private sealed record SlotDto(DateTime StartUtc, DateTime EndUtc, string StartLocal, string Timezone);
}
