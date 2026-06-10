using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using NewApi.Tests.Simulators;

namespace NewApi.Tests.Integration;

/// <summary>
/// FLOWS · 8 tests end-to-end del marketplace usando MarketplaceFlowSimulator.
///
/// Cada test recorre el flujo completo desde el webhook checkout.session.completed
/// hasta el estado final, y verifica:
///   - estado final del SearchHire (StatusValue correcto)
///   - estado final del Appointment (si aplica)
///   - ledger FinancialTransactions: ServicePayment única + Refund + Payout
///     con los importes EXACTOS calculados desde StatusConfiguration del seed
///   - invariante de conservación: ServicePayment − Refund − Payout = retencion plataforma
///   - sin negativos
///
/// Los splits se leen de la BD en cada test (no hardcoded) — si alguien edita
/// el seed, estos tests reflejan la realidad nueva automáticamente.
/// </summary>
public class CompleteFlowTests : IntegrationTestBase
{
    public CompleteFlowTests(PostgresContainerFixture fixture) : base(fixture) { }

    private record SeedActors(int ClientId, int ExpertUserId, int ExpertProfileId, int ServiceId);

    private async Task<SeedActors> SeedActorsAsync(AppDbContext db, string slug)
    {
        var client = await new UserBuilder($"flow-{slug}-cli@test.dev").AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder($"flow-{slug}-exp@test.dev").AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").PersistAsync(db);
        return new SeedActors(client.Id, expertUser.Id, expert.Id, svc.Id);
    }

    private async Task<(decimal client, decimal expert, decimal platform)> LoadSplitAsync(
        AppDbContext db, string statusValue)
    {
        var status = await db.SystemStatuses
            .Where(s => s.StatusValue == statusValue)
            .OrderBy(s => s.StatusType == "AppointmentStatus" ? 0 : 1)
            .FirstAsync();
        var cfg = await db.StatusConfigurations.SingleAsync(c =>
            c.StatusId == status.Id && c.CategoryId == null && c.ServiceTypeCategoryId == null);
        return (cfg.ClientPercentage, cfg.ExpertPercentage, cfg.PlatformPercentage);
    }

    private async Task AssertLedgerAsync(
        AppDbContext db, int hireId, decimal originalAmount,
        decimal expectedClientPct, decimal expectedExpertPct,
        string reason)
    {
        var fts = await db.FinancialTransactions
            .Where(ft => ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hireId)
            .ToListAsync();

        var servicePayment = fts.SingleOrDefault(ft => ft.TransactionType == "ServicePayment");
        servicePayment.Should().NotBeNull($"{reason}: debe existir exactamente 1 ServicePayment");
        servicePayment!.AmountCents.Should().Be((long)(originalAmount * 100));

        var expectedRefund = Math.Round(originalAmount * expectedClientPct / 100m, 2);
        var expectedPayout = Math.Round(originalAmount * expectedExpertPct / 100m, 2);

        // Refund
        var refund = fts.SingleOrDefault(ft => ft.TransactionType == "Refund");
        if (expectedRefund > 0)
        {
            refund.Should().NotBeNull($"{reason}: debe existir Refund con {expectedClientPct}%");
            refund!.Amount.Should().Be(expectedRefund);
            refund.StripeRefundId.Should().NotBeNullOrEmpty("traceability");
        }
        else
        {
            refund.Should().BeNull($"{reason}: no debe haber Refund (ClientPercentage=0)");
        }

        // Payout
        var payout = fts.SingleOrDefault(ft => ft.TransactionType == "Payout");
        if (expectedPayout > 0)
        {
            payout.Should().NotBeNull($"{reason}: debe existir Payout con {expectedExpertPct}%");
            payout!.Amount.Should().Be(expectedPayout);
            payout.StripeTransferId.Should().NotBeNullOrEmpty();
        }
        else
        {
            payout.Should().BeNull($"{reason}: no debe haber Payout (ExpertPercentage=0)");
        }

        // Invariante de conservación: ServicePayment − Refund − Payout = retencion plataforma
        var netToPlatform = servicePayment.Amount - (refund?.Amount ?? 0m) - (payout?.Amount ?? 0m);
        var expectedPlatform = Math.Round(originalAmount * (100m - expectedClientPct - expectedExpertPct) / 100m, 2);
        netToPlatform.Should().Be(expectedPlatform,
            $"{reason}: residuo en plataforma debe ser exactamente {expectedPlatform}€");

        // Sin negativos
        fts.Should().OnlyContain(ft => ft.Amount >= 0, "ninguna FT puede tener Amount negativo");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FLOW-01 · Happy path · webhook → CompleteService(approved=true)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "FLOW-01 · Happy path: webhook + CompleteService(approved) → completed (0/95/5)")]
    public async Task Happy_path_webhook_then_approve_completes()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "01");

        // STEP 1: webhook
        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_flow01_" + Guid.NewGuid().ToString("N"));

        // STEP 2: cliente aprueba
        await MarketplaceFlowSimulator.ClientApprovesServiceAsync(db, hire.Id, actors.ClientId);

        // Verificación
        var final = await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hire.Id);
        final.Status.StatusValue.Should().Be("completed");
        final.ClientApproved.Should().Be(true);

        var (c, e, p) = await LoadSplitAsync(db, "completed");
        (c, e, p).Should().Be((0m, 95m, 5m));
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "FLOW-01 happy path");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FLOW-02 · Cliente cancela 1ª vez → hire sigue pending, sin dinero
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "FLOW-02 · Cliente cancela 1ª vez · hire sigue pending · NO money distribution")]
    public async Task Client_first_cancel_does_not_finalize()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "02");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_flow02_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        var statusAfter = await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, actors.ClientId);
        statusAfter.Should().Be("appointment_cancelled_by_client");

        var final = await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hire.Id);
        final.Status.StatusValue.Should().Be("pending",
            "1ª cancelación NO finaliza el hire — permite reagendar");

        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);
        appt.ClientCancellationCount.Should().Be(1);

        // Sólo el ServicePayment original; cero Refund/Payout
        var fts = await db.FinancialTransactions
            .Where(ft => ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hire.Id)
            .ToListAsync();
        fts.Should().ContainSingle().Which.TransactionType.Should().Be("ServicePayment");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FLOW-03 · Cliente cancela 1ª + 2ª → cancelled (0/95/5 penalización)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "FLOW-03 · Cliente cancela 2 veces · penalización 0/95/5")]
    public async Task Client_second_cancel_penalizes_client()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "03");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_flow03_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, actors.ClientId);
        var statusAfter = await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, actors.ClientId);
        statusAfter.Should().Be("appointment_cancelled_by_client_second");

        var (c, e, p) = await LoadSplitAsync(db, "appointment_cancelled_by_client_second");
        (c, e, p).Should().Be((0m, 95m, 5m));
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "FLOW-03 2ª cancel cliente");

        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);
        appt.ClientCancellationCount.Should().Be(2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FLOW-04 · Experto cancela 1ª + 2ª → cancelled (100/0/0 reembolso completo)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "FLOW-04 · Experto cancela 2 veces · reembolso completo 100/0/0")]
    public async Task Expert_second_cancel_refunds_client_fully()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "04");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_flow04_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        await MarketplaceFlowSimulator.CancelByExpertAsync(db, hire.Id, actors.ExpertUserId);
        var statusAfter = await MarketplaceFlowSimulator.CancelByExpertAsync(db, hire.Id, actors.ExpertUserId);
        statusAfter.Should().Be("appointment_cancelled_by_expert_second");

        var (c, e, p) = await LoadSplitAsync(db, "appointment_cancelled_by_expert_second");
        (c, e, p).Should().Be((100m, 0m, 0m));
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "FLOW-04 2ª cancel experto");

        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);
        appt.ExpertCancellationCount.Should().Be(2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FLOW-05 · Reporte enviado → timer client_decision expira → auto-aprobado 0/95/5
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "FLOW-05 · Reporte → timer client_decision expira · auto-aprobado 0/95/5")]
    public async Task Client_decision_timer_auto_approves()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "05");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_flow05_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);
        await MarketplaceFlowSimulator.ProposeAndConfirmAppointmentAsync(db, hire.Id);
        await MarketplaceFlowSimulator.SubmitExpertReportAsync(db, hire.Id, actors.ExpertUserId);

        // El hire debe estar en awaiting_client_decision tras submit del reporte
        var afterReport = await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hire.Id);
        afterReport.Status.StatusValue.Should().Be("awaiting_client_decision");

        await MarketplaceFlowSimulator.ExpireClientDecisionTimerAsync(db, hire.Id);

        var (c, e, p) = await LoadSplitAsync(db, "appointment_completed_without_client_approval");
        (c, e, p).Should().Be((0m, 95m, 5m));
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "FLOW-05 auto-approve");

        var appt = await db.Appointments.Include(a => a.Status).SingleAsync(a => a.SearchHireId == hire.Id);
        appt.Status.StatusValue.Should().Be("appointment_completed_without_client_approval");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FLOW-06 · Experto no entrega reporte → timer expert_report expira (95/0/5)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "FLOW-06 · Timer expert_report expira sin reporte · 95/0/5 (cliente recupera mayoría)")]
    public async Task Expert_no_report_timer_refunds_client_majority()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "06");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_flow06_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);
        await MarketplaceFlowSimulator.ProposeAndConfirmAppointmentAsync(db, hire.Id);

        await MarketplaceFlowSimulator.ExpireExpertReportTimerAsync(db, hire.Id);

        var (c, e, p) = await LoadSplitAsync(db, "appointment_cancelled_by_no_report");
        (c, e, p).Should().Be((95m, 0m, 5m));
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "FLOW-06 no report");

        var appt = await db.Appointments.Include(a => a.Status).SingleAsync(a => a.SearchHireId == hire.Id);
        appt.Status.StatusValue.Should().Be("appointment_cancelled_by_no_report");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FLOW-07 · Disputa abierta → admin resuelve pro-cliente (90/8/2)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "FLOW-07 · Disputa · admin resuelve pro-cliente · 90/8/2")]
    public async Task Dispute_resolved_pro_client_splits_90_8_2()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "07");
        var admin = await new UserBuilder("admin-07@test.dev").AsAdmin().Verified().PersistAsync(db);

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_flow07_" + Guid.NewGuid().ToString("N"));

        var dispute = await MarketplaceFlowSimulator.OpenDisputeAsync(db, hire.Id, actors.ClientId,
            reason: "Experto no entregó el reporte prometido");

        // El hire entra en disputed
        var afterOpen = await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hire.Id);
        afterOpen.Status.StatusValue.Should().Be("disputed");
        dispute.Status.Should().Be("Pending");

        await MarketplaceFlowSimulator.ResolveDisputeProClientAsync(db, dispute.Id, admin.Id);

        var final = await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hire.Id);
        final.Status.StatusValue.Should().Be("dispute_resolved_client");

        var refreshed = await db.Disputes.SingleAsync(d => d.Id == dispute.Id);
        refreshed.Status.Should().Be("Resolved");

        var (c, e, p) = await LoadSplitAsync(db, "dispute_resolved_client");
        (c, e, p).Should().Be((90m, 8m, 2m));
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "FLOW-07 dispute pro-client");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FLOW-08 · Disputa abierta → admin resuelve pro-experto (0/95/5)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "FLOW-08 · Disputa · admin resuelve pro-experto · 0/95/5")]
    public async Task Dispute_resolved_pro_expert_splits_0_95_5()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "08");
        var admin = await new UserBuilder("admin-08@test.dev").AsAdmin().Verified().PersistAsync(db);

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_flow08_" + Guid.NewGuid().ToString("N"));

        var dispute = await MarketplaceFlowSimulator.OpenDisputeAsync(db, hire.Id, actors.ClientId,
            reason: "Cliente no aceptó trabajo legítimo");

        await MarketplaceFlowSimulator.ResolveDisputeProExpertAsync(db, dispute.Id, admin.Id);

        var final = await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hire.Id);
        final.Status.StatusValue.Should().Be("dispute_resolved_expert");

        var (c, e, p) = await LoadSplitAsync(db, "dispute_resolved_expert");
        (c, e, p).Should().Be((0m, 95m, 5m));
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "FLOW-08 dispute pro-expert");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // INVARIANTE GLOBAL: idempotencia del webhook checkout.session.completed
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "FLOW-INV · Webhook duplicado lanza InvalidOperationException (idempotencia)")]
    public async Task Duplicate_webhook_is_rejected_idempotent()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "inv");
        var pi = "pi_dup_" + Guid.NewGuid().ToString("N");

        await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: pi);

        var act = async () => await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: pi);
        await act.Should().ThrowAsync<InvalidOperationException>(
            "el handler real chequea ServicePayment con StripePaymentIntentId — segundo webhook devuelve 200 'already processed'");
    }
}
