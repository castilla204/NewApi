using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using NewApi.Tests.Simulators;

namespace NewApi.Tests.Integration;

/// <summary>
/// JOURNEY-A · 4 tests E2E del ciclo de vida del Appointment con propuesta/confirmación/reporte.
///
/// Verifican el camino completo de un servicio que SÍ requiere cita (a diferencia de los
/// FLOW-01..08 que ejercitan caminos cortos o de excepción):
///
///   webhook → proponer → confirmar → +3h → expert_report → aprobar → completed
///
/// Cada test verifica:
///   - Estados intermedios del Appointment Y del SearchHire en CADA paso
///   - Creación y expiración de timers (proposal, response, awaiting_report_transition,
///     expert_report, client_decision) consistente con AppointmentService.cs
///   - Ledger FT al final con AssertLedgerAsync (mismo invariante que CompleteFlowTests)
///   - Conservación: ServicePayment − Refund − Payout = retención plataforma
///
/// El helper SeedActorsAsync, LoadSplitAsync y AssertLedgerAsync son COPIA-PEGA de
/// CompleteFlowTests para mantener cada archivo de test legible de forma autónoma; si
/// la duplicación llega a estorbar el siguiente refactor extraerá una clase base.
/// </summary>
public class JourneyAppointmentTests : IntegrationTestBase
{
    public JourneyAppointmentTests(PostgresContainerFixture fixture) : base(fixture) { }

    private record SeedActors(int ClientId, int ExpertUserId, int ExpertProfileId, int ServiceId);

    private async Task<SeedActors> SeedActorsAsync(AppDbContext db, string slug)
    {
        var client = await new UserBuilder($"j-{slug}-cli@test.dev").AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder($"j-{slug}-exp@test.dev").AsExpert().Verified().PersistAsync(db);
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
        decimal expectedClientPct, decimal expectedExpertPct, string reason)
    {
        var fts = await db.FinancialTransactions
            .Where(ft => ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hireId)
            .ToListAsync();

        var servicePayment = fts.SingleOrDefault(ft => ft.TransactionType == "ServicePayment");
        servicePayment.Should().NotBeNull($"{reason}: debe existir exactamente 1 ServicePayment");
        servicePayment!.AmountCents.Should().Be((long)(originalAmount * 100));

        var expectedRefund = Math.Round(originalAmount * expectedClientPct / 100m, 2);
        var expectedPayout = Math.Round(originalAmount * expectedExpertPct / 100m, 2);

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

        var netToPlatform = servicePayment.Amount - (refund?.Amount ?? 0m) - (payout?.Amount ?? 0m);
        var expectedPlatform = Math.Round(originalAmount * (100m - expectedClientPct - expectedExpertPct) / 100m, 2);
        netToPlatform.Should().Be(expectedPlatform,
            $"{reason}: residuo en plataforma debe ser exactamente {expectedPlatform}€");

        fts.Should().OnlyContain(ft => ft.Amount >= 0, "ninguna FT puede tener Amount negativo");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers de assertion específicos del lifecycle de cita
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<string> CurrentApptStatusAsync(AppDbContext db, int hireId)
    {
        var appt = await db.Appointments
            .Include(a => a.Status)
            .SingleAsync(a => a.SearchHireId == hireId);
        return appt.Status.StatusValue;
    }

    private async Task<string> CurrentHireStatusAsync(AppDbContext db, int hireId)
    {
        var hire = await db.SearchHires
            .Include(h => h.Status)
            .SingleAsync(h => h.Id == hireId);
        return hire.Status.StatusValue;
    }

    private async Task<List<AppointmentTimer>> TimersAsync(AppDbContext db, int hireId)
    {
        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hireId);
        return await db.AppointmentTimers
            .Where(t => t.AppointmentId == appt.Id)
            .ToListAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J-A1 · Recorrido feliz completo (10+ pasos)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "J-A1 · Lifecycle completo: webhook→propone→confirma→+3h→reporte→aprueba · 0/95/5")]
    public async Task Full_appointment_lifecycle_happy_path()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "a1");

        // Step 1 · Webhook checkout.session.completed
        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_ja1_" + Guid.NewGuid().ToString("N"));

        (await CurrentHireStatusAsync(db, hire.Id)).Should().Be("pending");

        // Step 2 · Crear Appointment vacío (lo que haría ProposeAppointmentAsync si no existe)
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);
        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("awaiting_appointment");

        // Step 3 · Cliente propone fecha futura
        var proposedDate = DateTime.UtcNow.AddDays(3);
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(
            db, hire.Id, actors.ClientId, proposedDate);

        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed");
        var afterPropose = await TimersAsync(db, hire.Id);
        afterPropose.Should().Contain(t => t.TimerType == "response" && !t.IsExpired,
            "ProposeAppointmentAsync crea timer 'response' 24h");

        // Step 4 · Experto confirma
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(
            db, hire.Id, actors.ExpertUserId);

        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_confirmed");
        var afterConfirm = await TimersAsync(db, hire.Id);
        afterConfirm.Should().Contain(t => t.TimerType == "response" && t.IsExpired,
            "ConfirmAppointmentAsync expira timer 'response'");
        afterConfirm.Should().Contain(t => t.TimerType == "awaiting_report_transition" && !t.IsExpired,
            "ConfirmAppointmentAsync crea timer 'awaiting_report_transition' a cita+3h");

        // El hire sigue en pending durante todo el flujo hasta el report
        (await CurrentHireStatusAsync(db, hire.Id)).Should().Be("pending");

        // Step 5 · Simula el job awaiting_report_transition (+3h post cita)
        await MarketplaceFlowSimulator.AdvanceToAwaitingReportAsync(db, hire.Id);

        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_awaiting_report");
        var afterAdvance = await TimersAsync(db, hire.Id);
        afterAdvance.Should().Contain(t => t.TimerType == "awaiting_report_transition" && t.IsExpired,
            "AdvanceToAwaitingReport expira el timer de transición");
        afterAdvance.Should().Contain(t => t.TimerType == "expert_report" && !t.IsExpired,
            "AdvanceToAwaitingReport crea timer 'expert_report' 24h");

        // Step 6 · Experto envía el reporte
        await MarketplaceFlowSimulator.ExpertSubmitsReportAsync(
            db, hire.Id, actors.ExpertUserId);

        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_report_sent");
        (await CurrentHireStatusAsync(db, hire.Id)).Should().Be("awaiting_client_decision");
        var afterSubmit = await TimersAsync(db, hire.Id);
        afterSubmit.Should().Contain(t => t.TimerType == "expert_report" && t.IsExpired,
            "SubmitReport expira TODOS los timers activos");
        afterSubmit.Should().Contain(t => t.TimerType == "client_decision" && !t.IsExpired,
            "SubmitReport crea timer 'client_decision' 24h");

        // Step 7 · Cliente aprueba desde awaiting_client_decision
        await MarketplaceFlowSimulator.ClientApprovesReportFromAwaitingDecisionAsync(
            db, hire.Id, actors.ClientId);

        // Estado final
        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_completed");
        (await CurrentHireStatusAsync(db, hire.Id)).Should().Be("completed");

        var allTimers = await TimersAsync(db, hire.Id);
        allTimers.Where(t => !t.IsExpired).Should().BeEmpty(
            "todos los timers deben quedar expirados al completarse el servicio");

        var (c, e, _) = await LoadSplitAsync(db, "completed");
        (c, e).Should().Be((0m, 95m));
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "J-A1 happy path");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J-A2 · Ciclo con rechazo + re-proposición
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "J-A2 · Rechazo → re-propuesta → confirma → ... → completed (RejectionCount=1)")]
    public async Task Rejection_reproposal_then_complete()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "a2");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_ja2_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // 1ª propuesta
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(
            db, hire.Id, actors.ClientId, DateTime.UtcNow.AddDays(3));
        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed");

        // Experto rechaza (1er rechazo) → vuelve a appointment_rejected, NO finaliza, sin money
        var rejectStatus = await MarketplaceFlowSimulator.ExpertRejectsAppointmentAsync(
            db, hire.Id, actors.ExpertUserId);
        rejectStatus.Should().Be("appointment_rejected");

        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_rejected");
        (await CurrentHireStatusAsync(db, hire.Id)).Should().Be("pending",
            "1er rechazo NO debe finalizar el hire — permite re-propuesta");

        var apptAfterReject = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);
        apptAfterReject.RejectionCount.Should().Be(1);

        var afterReject = await TimersAsync(db, hire.Id);
        afterReject.Should().Contain(t => t.TimerType == "proposal" && !t.IsExpired,
            "tras 1er rechazo se crea timer 'proposal' nuevo para que el cliente proponga otra fecha");

        // Sin Refund/Payout — todavía solo el ServicePayment
        var ftsAfterReject = await db.FinancialTransactions
            .Where(ft => ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hire.Id)
            .ToListAsync();
        ftsAfterReject.Should().ContainSingle().Which.TransactionType.Should().Be("ServicePayment");

        // Cliente re-propone (desde appointment_rejected, estado válido)
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(
            db, hire.Id, actors.ClientId, DateTime.UtcNow.AddDays(5));
        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed");

        // Experto confirma esta vez
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(
            db, hire.Id, actors.ExpertUserId);
        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_confirmed");

        await MarketplaceFlowSimulator.AdvanceToAwaitingReportAsync(db, hire.Id);
        await MarketplaceFlowSimulator.ExpertSubmitsReportAsync(db, hire.Id, actors.ExpertUserId);
        await MarketplaceFlowSimulator.ClientApprovesReportFromAwaitingDecisionAsync(
            db, hire.Id, actors.ClientId);

        // Estado final
        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_completed");
        (await CurrentHireStatusAsync(db, hire.Id)).Should().Be("completed");

        // RejectionCount preservado = 1 (no se reseta al re-proponer)
        var finalAppt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);
        finalAppt.RejectionCount.Should().Be(1, "RejectionCount no se resetea al re-proponer");

        var (c, e, _) = await LoadSplitAsync(db, "completed");
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "J-A2 reject→re-propose→complete");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J-A3 · Cliente reemplaza propuesta antes de confirmación
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "J-A3 · Cliente cambia propuesta (A → B) antes de confirmar · completed sin rechazos")]
    public async Task Client_replaces_proposal_before_confirmation()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "a3");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_ja3_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // Propuesta A
        var dateA = DateTime.UtcNow.AddDays(3);
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(
            db, hire.Id, actors.ClientId, dateA);
        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed");
        var apptAfterA = await db.Appointments.AsNoTracking().SingleAsync(a => a.SearchHireId == hire.Id);
        apptAfterA.ProposedDate!.Value.Date.Should().Be(dateA.Date);
        apptAfterA.ProposedTime.Should().BeCloseTo(dateA.TimeOfDay, TimeSpan.FromMilliseconds(1));

        // ❗ FAITHFUL: en el código REAL no se puede re-proponer desde appointment_proposed
        // (línea 839-844 marca ese estado como inválido). En la app, esto requiere primero
        // un rechazo o cancelación. Para modelar "cambio de idea sin rechazo", el flujo real
        // sería cliente cancela 1ª vez → propone de nuevo. Lo replicamos así:
        await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, actors.ClientId);
        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_cancelled_by_client",
            "1ª cancelación cliente NO finaliza, permite re-propuesta");
        (await CurrentHireStatusAsync(db, hire.Id)).Should().Be("pending");

        // Propuesta B (desde appointment_cancelled_by_client, estado válido)
        var dateB = DateTime.UtcNow.AddDays(7);
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(
            db, hire.Id, actors.ClientId, dateB);
        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed");

        var apptAfterB = await db.Appointments.AsNoTracking().SingleAsync(a => a.SearchHireId == hire.Id);
        apptAfterB.ProposedDate!.Value.Date.Should().Be(dateB.Date,
            "la propuesta B debe reemplazar a la A");
        apptAfterB.ProposedTime.Should().BeCloseTo(dateB.TimeOfDay, TimeSpan.FromMilliseconds(1));
        apptAfterB.ClientCancellationCount.Should().Be(1, "queda registro de la cancelación");

        // Experto confirma B y completa el flujo
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, actors.ExpertUserId);
        await MarketplaceFlowSimulator.AdvanceToAwaitingReportAsync(db, hire.Id);
        await MarketplaceFlowSimulator.ExpertSubmitsReportAsync(db, hire.Id, actors.ExpertUserId);
        await MarketplaceFlowSimulator.ClientApprovesReportFromAwaitingDecisionAsync(
            db, hire.Id, actors.ClientId);

        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_completed");
        (await CurrentHireStatusAsync(db, hire.Id)).Should().Be("completed");

        var finalAppt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);
        finalAppt.RejectionCount.Should().Be(0, "sin rechazos del experto en este flujo");

        var (c, e, _) = await LoadSplitAsync(db, "completed");
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "J-A3 replace proposal");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J-A4 · Confirmación tardía tras 1 rechazo de misma fecha
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "J-A4 · Rechazo → re-propone misma fecha → confirma · completed (RejectionCount=1)")]
    public async Task Late_confirm_after_one_rejection_same_date()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "a4");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_ja4_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        var samedate = DateTime.UtcNow.AddDays(4);
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(
            db, hire.Id, actors.ClientId, samedate);

        // Experto rechaza (1er rechazo, NO money)
        await MarketplaceFlowSimulator.ExpertRejectsAppointmentAsync(db, hire.Id, actors.ExpertUserId);
        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_rejected");
        (await CurrentHireStatusAsync(db, hire.Id)).Should().Be("pending");

        // Cliente re-propone la MISMA fecha
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(
            db, hire.Id, actors.ClientId, samedate);
        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed");

        var apptMid = await db.Appointments.AsNoTracking().SingleAsync(a => a.SearchHireId == hire.Id);
        apptMid.ProposedDate!.Value.Date.Should().Be(samedate.Date,
            "la re-propuesta de la misma fecha debe quedar registrada");
        apptMid.RejectionCount.Should().Be(1);

        // Esta vez el experto confirma tarde pero todavía a tiempo
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, actors.ExpertUserId);
        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_confirmed");

        // Verificar timers: hay timer 'response' expirado (del rechazo) + 'response' expirado (de la confirm)
        // + 'proposal' expirado (creado tras rechazo y matado por re-propuesta) + 'awaiting_report_transition' activo
        var midTimers = await TimersAsync(db, hire.Id);
        midTimers.Should().Contain(t => t.TimerType == "awaiting_report_transition" && !t.IsExpired);
        midTimers.Where(t => !t.IsExpired)
            .Should().HaveCount(1, "tras confirmar solo el timer awaiting_report_transition queda activo");

        // Resto del flujo
        await MarketplaceFlowSimulator.AdvanceToAwaitingReportAsync(db, hire.Id);
        await MarketplaceFlowSimulator.ExpertSubmitsReportAsync(db, hire.Id, actors.ExpertUserId);
        await MarketplaceFlowSimulator.ClientApprovesReportFromAwaitingDecisionAsync(
            db, hire.Id, actors.ClientId);

        (await CurrentApptStatusAsync(db, hire.Id)).Should().Be("appointment_completed");
        (await CurrentHireStatusAsync(db, hire.Id)).Should().Be("completed");

        var finalAppt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);
        finalAppt.RejectionCount.Should().Be(1, "RejectionCount preservado tras confirmar tarde");

        var finalTimers = await TimersAsync(db, hire.Id);
        finalTimers.Where(t => !t.IsExpired).Should().BeEmpty(
            "todos los timers deben quedar expirados al completarse");

        var (c, e, _) = await LoadSplitAsync(db, "completed");
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "J-A4 late confirm");
    }
}
