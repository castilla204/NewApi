using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using FluentAssertions;

namespace NewApi.Tests.Integration;

/// <summary>
/// Tarea 3 — captura DIFERIDA del modo seller al confirmar la cita.
///
/// Tras el checkout en modo "seller" sin hueco, el pago queda AUTORIZADO (PI en
/// requires_capture, hire.CaptureStatus="Authorized"). Cuando el vendedor confirma
/// día/hora/lugar vía POST /api/seller-booking/{token}/confirm hay que CAPTURAR el pago.
///
/// DECISIÓN: si la captura FALLA se RECHAZA la confirmación (rollback de la reserva, no
/// se crea cita, hire.CaptureStatus="Failed", 402) — nunca una cita sin dinero cobrado.
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

    // ── Test 1: captura OK → 200, PI succeeded, CaptureStatus=Captured, 1 Appointment, token=null.
    [Fact(DisplayName = "SBC-01 · confirm con captura OK → 200, PI capturado, cita creada")]
    public async Task Confirm_capture_ok_creates_appointment_and_captures()
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

        // El backend capturó el PI contra el stub.
        _api.FakeStripe.Requests.Should().Contain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "al confirmar el vendedor se captura el pago autorizado");

        await using var db = _api.CreateDbContext();
        var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
        hire.CaptureStatus.Should().Be("Captured");
        hire.SellerBookingToken.Should().BeNull("token de un solo uso");

        var appts = await db.Appointments.AsNoTracking().Where(a => a.SearchHireId == hireId).ToListAsync();
        appts.Should().HaveCount(1);
    }

    // ── Test 2: captura FALLA → 402, NO se crea cita, CaptureStatus=Failed, token NO se consume.
    [Fact(DisplayName = "SBC-02 · confirm con captura que FALLA → 402, sin cita, CaptureStatus=Failed")]
    public async Task Confirm_capture_failure_rejects_and_creates_no_appointment()
    {
        var createdAt = DateTime.UtcNow;
        var (token, serviceId, hireId, pi) = await SeedAuthorizedSellerHireAsync(createdAt);
        var slot = await FirstSlotAsync(token, createdAt);

        // Forzar que la captura de ESTE PI falle.
        _api.FakeStripe.CaptureFailurePaymentIntents[pi] = 1;
        try
        {
            var res = await _api.Client.PostAsJsonAsync($"/api/seller-booking/{token}/confirm", new
            {
                startsAtUtc = slot.StartUtc, endsAtUtc = slot.EndUtc, location = "Calle Mayor 1",
            });
            res.StatusCode.Should().Be((HttpStatusCode)402, await res.Content.ReadAsStringAsync());
        }
        finally
        {
            _api.FakeStripe.CaptureFailurePaymentIntents.TryRemove(pi, out _);
        }

        await using var db = _api.CreateDbContext();
        var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
        hire.CaptureStatus.Should().Be("Failed", "captura fallida marca el hire como Failed");

        var appts = await db.Appointments.AsNoTracking().Where(a => a.SearchHireId == hireId).ToListAsync();
        appts.Should().BeEmpty("no debe quedar cita si la captura falló (rollback)");
    }

    // ── Test 3: hire AUTORIZADO pero SIN FT ServicePayment (sin PaymentIntent localizable) →
    //    500, NO se crea cita y NO se captura nada (rollback de la reserva).
    [Fact(DisplayName = "SBC-03 · confirm sin PaymentIntent → 500, sin cita, sin captura")]
    public async Task Confirm_without_payment_intent_rejects_and_creates_no_appointment()
    {
        var createdAt = DateTime.UtcNow;
        var (token, serviceId, hireId, pi) = await SeedAuthorizedSellerHireAsync(createdAt, withServicePayment: false);
        var slot = await FirstSlotAsync(token, createdAt);

        var res = await _api.Client.PostAsJsonAsync($"/api/seller-booking/{token}/confirm", new
        {
            startsAtUtc = slot.StartUtc, endsAtUtc = slot.EndUtc, location = "Calle Mayor 1",
        });
        res.StatusCode.Should().Be((HttpStatusCode)500, await res.Content.ReadAsStringAsync());

        // No se intentó capturar el PI de este hire (Requests es una cola compartida por la
        // colección, así que filtramos por el PI único sembrado, que NUNCA se persistió como FT).
        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "sin FT ServicePayment no hay PaymentIntent que capturar");

        await using var db = _api.CreateDbContext();
        var appts = await db.Appointments.AsNoTracking().Where(a => a.SearchHireId == hireId).ToListAsync();
        appts.Should().BeEmpty("sin PaymentIntent la reserva se revierte y no queda cita");
    }

    private sealed record SlotDto(DateTime StartUtc, DateTime EndUtc, string StartLocal, string Timezone);
}
