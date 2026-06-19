using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using FluentAssertions;

namespace NewApi.Tests.Integration;

/// <summary>
/// Tareas 5 y 6 — captura diferida en modo vendedor: redes de seguridad de EXPIRACIÓN.
///
/// Tarea 5 (watchdog 7d): ProcessExpiringPaymentIntentsAsync cancela proactivamente los PI
///   autorizados-sin-capturar con CreatedAt &gt; 6.5d para que Stripe no los expire a 7d. Con
///   captura diferida, el hire seller autorizado queda en CaptureStatus="Authorized"; el WHERE
///   del watchdog debe incluir TANTO "Pending" como "Authorized".
///
/// Tarea 6 (expiración del plazo de 48h): cuando un seller booking autorizado vence su plazo
///   sin cita, ProcessExpiredSellerBookingsAsync anula el token y encola la distribución; la
///   rama N10 de RefundService.ProcessMoneyDistributionAsync ve el PI en requires_capture y lo
///   CANCELA (0 € de fee) en vez de reembolsar — NO se crea FT "Refund". Verificación: el
///   código ya lo hace, así que esto es solo el test que lo demuestra.
///
/// Reusa el harness HTTP real (ApiFactoryFixture + FakeStripeServer). Como en tests Hangfire
/// no procesa jobs (HANGFIRE_SERVER_DISABLED=1), los jobs se resuelven del DI real y se llaman
/// directamente — mismo patrón que HttpHireFlowTests / SellerBookingConfirmCaptureTests.
/// </summary>
[Collection("Api")]
public class SellerExpiryWatchdogTests
{
    private readonly ApiFactoryFixture _api;
    public SellerExpiryWatchdogTests(ApiFactoryFixture api) => _api = api;

    // Siembra un hire seller AUTORIZADO con FT ServicePayment (vínculo PI↔hire) y CaptureStatus
    // controlable. createdAtUtc fija la antigüedad; deadlineUtc fija el plazo de 48h del vendedor.
    private async Task<(int hireId, string pi)> SeedAuthorizedSellerHireAsync(
        DateTime createdAtUtc, DateTime? deadlineUtc = null, string captureStatus = "Authorized",
        string? token = "auto")
    {
        await using var db = _api.CreateDbContext();
        var expertUser = await new UserBuilder().AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").WithDuration(1).PersistAsync(db);

        var client = await new UserBuilder().AsClient().Verified().PersistAsync(db);
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithStatusValue("pending").PersistAsync(db);

        hire.CreatedAt = createdAtUtc;
        hire.SellerBookingToken = token == "auto"
            ? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")
            : token;
        hire.SellerBookingDeadline = deadlineUtc ?? createdAtUtc.AddHours(48);
        hire.ExpertTimezone = "Europe/Madrid";
        hire.CaptureStatus = captureStatus;

        var pi = "pi_sew_" + Guid.NewGuid().ToString("N")[..12];
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
        await db.SaveChangesAsync();
        return (hire.Id, pi);
    }

    // ── T5: el watchdog de 7d CANCELA un PI autorizado-sin-capturar con CreatedAt > 6.5d.
    //    El PI por defecto en FakeStripe está en requires_capture → el watchdog lo cancela.
    [Fact(DisplayName = "T5 · watchdog 7d cancela PI 'Authorized' rancio (>6.5d)")]
    public async Task ExpiryWatchdog_cancelsStaleAuthorizedPi()
    {
        // CreatedAt 7 días atrás → supera el cutoff de 6.5d del watchdog.
        var (hireId, pi) = await SeedAuthorizedSellerHireAsync(DateTime.UtcNow.AddDays(-7));

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IPlatformMaintenanceService>();
            await maintenance.ProcessExpiringPaymentIntentsAsync();
        }

        // El PI (requires_capture en el stub) fue cancelado por el watchdog.
        _api.FakeStripe.Requests.Should().Contain(r => r == $"POST /v1/payment_intents/{pi}/cancel",
            "un PI 'Authorized' rancio debe cancelarse antes de la expiración de Stripe a 7d");
        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "el watchdog cancela, nunca captura a estas alturas");

        await using var db = _api.CreateDbContext();
        var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
        hire.CaptureStatus.Should().Be("Failed",
            "tras cancelar el PI rancio el watchdog marca el hire Failed para que admin investigue");
    }

    // ── T5-R5: umbral bajado a -4.5d (Tarea 7). Un hire Authorized con CreatedAt a -5d AHORA
    //    entra en el barrido (antes, con cutoff -6.5d, NO entraba). Demuestra el cambio de umbral.
    [Fact(DisplayName = "T5-R5 · watchdog 7d toma un PI 'Authorized' de -5d (umbral bajado a -4.5d)")]
    public async Task ExpiryWatchdog_includesAuthorizedAtFiveDays_afterThresholdLowered()
    {
        // CreatedAt 5 días atrás → entre -4.5d (nuevo cutoff) y -6.5d (antiguo): solo entra con el nuevo.
        var (hireId, pi) = await SeedAuthorizedSellerHireAsync(DateTime.UtcNow.AddDays(-5));

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IPlatformMaintenanceService>();
            await maintenance.ProcessExpiringPaymentIntentsAsync();
        }

        // Con el umbral nuevo (-4.5d) el hire de -5d entra → su PI (requires_capture) se cancela.
        _api.FakeStripe.Requests.Should().Contain(r => r == $"POST /v1/payment_intents/{pi}/cancel",
            "con el umbral bajado a -4.5d, un PI 'Authorized' de -5d ya entra en el barrido (antes -6.5d no)");

        await using var db = _api.CreateDbContext();
        var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
        hire.CaptureStatus.Should().Be("Failed");
    }

    // ── No-regresión: un hire Authorized MUY reciente (-3d, dentro del nuevo umbral -4.5d) NO se toca.
    [Fact(DisplayName = "T5-R5-reg · watchdog NO toca un PI 'Authorized' de -3d (dentro del umbral)")]
    public async Task ExpiryWatchdog_ignoresAuthorizedWithinThreshold()
    {
        var (_, pi) = await SeedAuthorizedSellerHireAsync(DateTime.UtcNow.AddDays(-3));

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IPlatformMaintenanceService>();
            await maintenance.ProcessExpiringPaymentIntentsAsync();
        }

        _api.FakeStripe.Requests.Should().NotContain(r => r.StartsWith($"POST /v1/payment_intents/{pi}/"),
            "un hire de -3d está dentro del umbral -4.5d: no se toca todavía");
    }

    // ── No-regresión: el watchdog NO toca hires ya capturados (CaptureStatus="Captured").
    [Fact(DisplayName = "T5-reg · watchdog NO toca hires 'Captured'")]
    public async Task ExpiryWatchdog_ignoresCapturedHires()
    {
        var (_, pi) = await SeedAuthorizedSellerHireAsync(
            DateTime.UtcNow.AddDays(-7), captureStatus: "Captured");

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IPlatformMaintenanceService>();
            await maintenance.ProcessExpiringPaymentIntentsAsync();
        }

        // El WHERE filtra Pending|Authorized → un hire Captured no entra en el lote, su PI no se toca.
        _api.FakeStripe.Requests.Should().NotContain(r => r.StartsWith($"POST /v1/payment_intents/{pi}/"),
            "un hire 'Captured' está fuera del WHERE del watchdog: no se cancela ni captura");
    }

    // ── T6: vencido el plazo de 48h del vendedor sin cita → ProcessExpiredSellerBookingsAsync
    //    anula el token y la distribución encolada (N10) CANCELA el PI (0 €), SIN FT "Refund",
    //    y el hire queda finalizado a 'cancelled'.
    [Fact(DisplayName = "T6 · expiración seller cancela autorización sin Refund ni fee")]
    public async Task SellerExpiry_cancelsAuthorization_noRefundNoFee()
    {
        // CreatedAt antiguo + deadline ya vencido + token presente + sin Appointment.
        var createdAt = DateTime.UtcNow.AddDays(-3);
        var (hireId, pi) = await SeedAuthorizedSellerHireAsync(
            createdAt, deadlineUtc: DateTime.UtcNow.AddHours(-1));

        // Paso 1: el job de expiración reclama el hire (anula el token = mutex) y encola la
        // distribución. En tests Hangfire no procesa, así que el Enqueue solo registra el job.
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IPlatformMaintenanceService>();
            await maintenance.ProcessExpiredSellerBookingsAsync();
        }

        await using (var db = _api.CreateDbContext())
        {
            var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
            hire.SellerBookingToken.Should().BeNull("la expiración anula el token de un solo uso (mutex)");
        }

        // Paso 2: drenar la distribución encolada (mismos argumentos que encola el job).
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var refundService = scope.ServiceProvider.GetRequiredService<newApi.Services.StripeRefundService>();
            var ok = await refundService.ProcessMoneyDistributionAsync(
                hireId,
                "appointment_cancelled_by_client_gt24h",
                "Vendedor no reservó la cita en el plazo: reembolso 100% al comprador.",
                null,
                true);
            ok.Should().BeTrue("la distribución de una cancelación pre-captura completa (N10 cancela el PI)");
        }

        // N10: el PI estaba en requires_capture → CANCELADO (0 €), nunca refund ni capture.
        _api.FakeStripe.Requests.Should().Contain(r => r == $"POST /v1/payment_intents/{pi}/cancel",
            "un PI no capturado se cancela (0 € de fee), nunca se reembolsa");
        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "expirar el plazo del vendedor NUNCA cobra al comprador");

        await using (var db = _api.CreateDbContext())
        {
            // Sin fila FinancialTransaction "Refund": no hubo cobro que devolver.
            var refundFts = await db.FinancialTransactions.AsNoTracking()
                .Where(t => t.RelatedEntityType == "SearchHire" && t.RelatedEntityId == hireId
                            && t.TransactionType == "Refund")
                .ToListAsync();
            refundFts.Should().BeEmpty("cancelar un PI no capturado no genera FT Refund (0 € movidos)");

            // No se creó cita.
            var appts = await db.Appointments.AsNoTracking().Where(a => a.SearchHireId == hireId).ToListAsync();
            appts.Should().BeEmpty("expirar el plazo no crea cita");

            // Hire finalizado a 'cancelled'.
            var statusValue = await db.SearchHires.AsNoTracking()
                .Where(h => h.Id == hireId)
                .Join(db.SystemStatuses.AsNoTracking(), h => h.StatusId, s => s.Id, (h, s) => s.StatusValue)
                .SingleAsync();
            statusValue.Should().Be("cancelled", "la devolución 100% al comprador finaliza el hire a 'cancelled'");
        }
    }
}
