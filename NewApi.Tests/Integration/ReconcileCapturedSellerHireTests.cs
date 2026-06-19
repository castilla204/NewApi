using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using FluentAssertions;

namespace NewApi.Tests.Integration;

/// <summary>
/// HUECO 1b — red de seguridad (defensa en profundidad sobre ProcessExpiredSellerBookingsAsync).
///
/// Escenario: en la captura diferida modo vendedor (SellerBookingController.Confirm),
/// EnsureCapturedAsync captura con éxito (dinero COBRADO, PI → succeeded) pero el
/// SaveChanges/Commit POSTERIOR falla → rollback: la cita NO se persiste y el hire queda
/// con CaptureStatus="Authorized" sin Appointment. Los datos del hueco se pierden → no se
/// puede recrear la cita. La resolución correcta es REEMBOLSAR 100% al comprador.
///
/// CRÍTICO — no romper el auto-rescate: mientras el SellerBookingDeadline (48h) no venza, el
/// vendedor puede reintentar el confirm (token sigue válido) y el sistema se auto-cura
/// (PI ya succeeded → EnsureCaptured no-op → crea la cita → commit). Por eso este job SOLO
/// actúa DESPUÉS de que venza la ventana de 48h (SellerBookingDeadline &lt; now).
///
/// Reusa el harness HTTP real (ApiFactoryFixture + FakeStripeServer); patrón idéntico a
/// SellerExpiryWatchdogTests. En tests Hangfire no procesa (HANGFIRE_SERVER_DISABLED=1) → el
/// job encola la distribución y el test la DRENA llamando a ProcessMoneyDistributionAsync
/// con los mismos argumentos que encola el job.
/// </summary>
[Collection("Api")]
public class ReconcileCapturedSellerHireTests
{
    private readonly ApiFactoryFixture _api;
    public ReconcileCapturedSellerHireTests(ApiFactoryFixture api) => _api = api;

    // Siembra un hire seller (modo vendedor) con FT ServicePayment (vínculo PI↔hire), SIN
    // Appointment. deadlineUtc fija la ventana de 48h del vendedor; captureStatus el reflejo
    // local. Devuelve (hireId, pi) para fijar el estado del PI en el stub.
    private async Task<(int hireId, string pi)> SeedSellerHireAsync(
        DateTime? deadlineUtc, string captureStatus = "Authorized",
        string statusValue = "pending", string? token = "auto")
    {
        await using var db = _api.CreateDbContext();
        var expertUser = await new UserBuilder().AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").WithDuration(1).PersistAsync(db);

        var client = await new UserBuilder().AsClient().Verified().PersistAsync(db);
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithStatusValue(statusValue).PersistAsync(db);

        hire.CreatedAt = DateTime.UtcNow.AddDays(-3);
        hire.SellerBookingToken = token == "auto"
            ? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")
            : token;
        hire.SellerBookingDeadline = deadlineUtc; // null => no es modo seller
        hire.ExpertTimezone = "Europe/Madrid";
        hire.CaptureStatus = captureStatus;

        var pi = "pi_rec_" + Guid.NewGuid().ToString("N")[..12];
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

    private async Task DrainQueuedDistributionAsync(int hireId)
    {
        // En tests Hangfire no procesa: drenamos la distribución encolada por el job con los
        // MISMOS argumentos. N10 ve el PI succeeded → no cancela → refund 100% al comprador.
        using var scope = _api.Factory.Services.CreateScope();
        var refundService = scope.ServiceProvider.GetRequiredService<StripeRefundService>();
        await refundService.ProcessMoneyDistributionAsync(
            hireId,
            "appointment_cancelled_by_client_gt24h",
            "Reconciliación HUECO 1b: pago capturado sin cita persistida; reembolso 100% al comprador.",
            null,
            true);
    }

    // ── Test 1: HUECO 1b real → el job reembolsa al comprador y finaliza el hire a 'cancelled'.
    [Fact(DisplayName = "Reconcile · reembolsa hire seller capturado sin cita (HUECO 1b)")]
    public async Task Reconcile_refundsCapturedSellerHireWithoutAppointment()
    {
        // Deadline vencido (post-48h) → ya no hay auto-rescate posible. PI succeeded = dinero cobrado.
        var (hireId, pi) = await SeedSellerHireAsync(DateTime.UtcNow.AddHours(-1));
        _api.FakeStripe.SetPaymentIntentStatus(pi, "succeeded");

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IPlatformMaintenanceService>();
            await maintenance.ReconcileCapturedSellerHiresWithoutAppointmentAsync();
        }

        // El token se anuló (mutex contra un confirm tardío) y la distribución se encoló.
        await using (var db = _api.CreateDbContext())
        {
            var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
            hire.SellerBookingToken.Should().BeNull("la reconciliación anula el token de un solo uso (mutex)");
        }

        await DrainQueuedDistributionAsync(hireId);

        // PI succeeded → N10 NO cancela: se reembolsa al comprador (POST /v1/refunds).
        _api.FakeStripe.Requests.Should().Contain(r => r == "POST /v1/refunds",
            "un PI capturado (succeeded) sin cita se reembolsa 100% al comprador");
        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/cancel",
            "un PI ya succeeded no se puede cancelar — se reembolsa");

        await using (var db = _api.CreateDbContext())
        {
            var refundFts = await db.FinancialTransactions.AsNoTracking()
                .Where(t => t.RelatedEntityType == "SearchHire" && t.RelatedEntityId == hireId
                            && t.TransactionType == "Refund")
                .ToListAsync();
            refundFts.Should().HaveCount(1, "se crea exactamente una FT Refund al comprador");

            var statusValue = await db.SearchHires.AsNoTracking()
                .Where(h => h.Id == hireId)
                .Join(db.SystemStatuses.AsNoTracking(), h => h.StatusId, s => s.Id, (h, s) => s.StatusValue)
                .SingleAsync();
            statusValue.Should().Be("cancelled", "el reembolso 100% al comprador finaliza el hire a 'cancelled'");
        }
    }

    // ── Test 2: idempotencia — ejecutar el job dos veces NO genera doble reembolso.
    [Fact(DisplayName = "Reconcile · idempotente, sin doble reembolso")]
    public async Task Reconcile_isIdempotent_noDoubleRefund()
    {
        var (hireId, pi) = await SeedSellerHireAsync(DateTime.UtcNow.AddHours(-1));
        _api.FakeStripe.SetPaymentIntentStatus(pi, "succeeded");

        // Primera pasada completa: job + drenar la distribución → FT Refund creada.
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IPlatformMaintenanceService>();
            await maintenance.ReconcileCapturedSellerHiresWithoutAppointmentAsync();
        }
        await DrainQueuedDistributionAsync(hireId);

        // Segunda ejecución del job: el guard "ya existe FT Refund" debe descartar el candidato →
        // no se vuelve a encolar distribución. (Y aunque se drenase, existingRefund la frenaría.)
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IPlatformMaintenanceService>();
            await maintenance.ReconcileCapturedSellerHiresWithoutAppointmentAsync();
        }
        await DrainQueuedDistributionAsync(hireId);

        await using var db = _api.CreateDbContext();
        var refundFts = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.RelatedEntityType == "SearchHire" && t.RelatedEntityId == hireId
                        && t.TransactionType == "Refund")
            .ToListAsync();
        refundFts.Should().HaveCount(1, "ejecutar el job dos veces NO genera un segundo Refund (idempotente)");
    }

    // ── Test 3: NO toca un hire autorizado-pero-NO-capturado (PI requires_capture ≠ HUECO 1b).
    [Fact(DisplayName = "Reconcile · NO toca PI no capturado (requires_capture)")]
    public async Task Reconcile_skipsAuthorizedNotYetCaptured()
    {
        var (hireId, pi) = await SeedSellerHireAsync(DateTime.UtcNow.AddHours(-1));
        // PI por defecto del stub: requires_capture → ese caso lo maneja ProcessExpiredSellerBookings.
        _api.FakeStripe.SetPaymentIntentStatus(pi, "requires_capture");

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IPlatformMaintenanceService>();
            await maintenance.ReconcileCapturedSellerHiresWithoutAppointmentAsync();
        }

        // El job NO debe actuar sobre ESTE PI: no lo cancela (el Requests es PI-scoped en /cancel).
        // El refund/cancel global no es fiable para aserciones (cola compartida en la colección Api):
        // verificamos el no-efecto por hire vía BD (token intacto + sin FT Refund de este hire).
        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/cancel",
            "el job de reconciliación solo actúa sobre PI succeeded; requires_capture lo maneja otro job");

        await using var db = _api.CreateDbContext();
        var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
        hire.SellerBookingToken.Should().NotBeNull("un PI no capturado no es HUECO 1b → el token NO se anula");
        var refundFts = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.RelatedEntityType == "SearchHire" && t.RelatedEntityId == hireId
                        && t.TransactionType == "Refund")
            .ToListAsync();
        refundFts.Should().BeEmpty("sin captura no hay nada que reembolsar");
    }

    // ── Test 4: respeta la ventana — deadline en el FUTURO → auto-rescate aún posible → no actúa.
    [Fact(DisplayName = "Reconcile · respeta la ventana de 48h (no actúa antes del deadline)")]
    public async Task Reconcile_skipsBeforeDeadline()
    {
        var (hireId, pi) = await SeedSellerHireAsync(DateTime.UtcNow.AddHours(24)); // deadline futuro
        _api.FakeStripe.SetPaymentIntentStatus(pi, "succeeded"); // capturado, pero aún auto-rescatable

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IPlatformMaintenanceService>();
            await maintenance.ReconcileCapturedSellerHiresWithoutAppointmentAsync();
        }

        // No-efecto verificado por hire vía BD (la cola FakeStripe.Requests es compartida en la
        // colección y no permite aislar el refund de ESTE hire): antes del deadline el token sigue
        // vivo (auto-rescate posible) y no se crea FT Refund para este hire.
        await using var db = _api.CreateDbContext();
        var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
        hire.SellerBookingToken.Should().NotBeNull("antes del deadline el token sigue vivo (auto-rescate)");
        var refundFts = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.RelatedEntityType == "SearchHire" && t.RelatedEntityId == hireId
                        && t.TransactionType == "Refund")
            .ToListAsync();
        refundFts.Should().BeEmpty("antes del deadline no se reembolsa");
    }
}
