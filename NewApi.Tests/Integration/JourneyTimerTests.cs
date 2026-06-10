using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using NewApi.Tests.Simulators;

namespace NewApi.Tests.Integration;

/// <summary>
/// JOURNEY · TIMERS · 4 tests E2E que ejercitan transiciones disparadas por timers
/// + watchdog (Hangfire RecurringJob "appointment-timers-watchdog" cron "*/10 * * * *"
/// UTC — Program.cs:1843-1847).
///
/// Cobertura:
///   - J-T1 proposal expira          → 0/100/0, hire=cancelled
///   - J-T2 response expira          → 100/0/0, hire=cancelled
///   - J-T3 watchdog multi-timer    → 3 timers de distinto tipo procesados en una pasada
///   - J-T4 watchdog idempotente    → 2ª pasada NO duplica nada (UNIQUE indexes intactos)
///
/// Cada test verifica:
///   - estado final de Appointment + SearchHire
///   - ledger FinancialTransactions (AssertLedgerAsync, mismo helper que FLOW-*)
///   - timer.IsExpired=true + ExpiredAt poblado
///   - timer.HangfireJobId=null tras expiración (el wrapper real lo limpia)
/// </summary>
public class JourneyTimerTests : IntegrationTestBase
{
    public JourneyTimerTests(PostgresContainerFixture fixture) : base(fixture) { }

    private record SeedActors(int ClientId, int ExpertUserId, int ExpertProfileId, int ServiceId);

    private async Task<SeedActors> SeedActorsAsync(AppDbContext db, string slug)
    {
        var client = await new UserBuilder($"jt-{slug}-cli@test.dev").AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder($"jt-{slug}-exp@test.dev").AsExpert().Verified().PersistAsync(db);
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

    private async Task AssertTimerExpiredAsync(
        AppDbContext db, int timerId, string reason)
    {
        var t = await db.AppointmentTimers.AsNoTracking().SingleAsync(x => x.Id == timerId);
        t.IsExpired.Should().BeTrue($"{reason}: el timer debe quedar marcado IsExpired=true");
        t.ExpiredAt.Should().NotBeNull($"{reason}: ExpiredAt debe poblarse al expirar");
        t.HangfireJobId.Should().BeNull(
            $"{reason}: el wrapper real limpia HangfireJobId tras consumir el job");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J-T1 · Timer proposal vence sin propuesta del cliente
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "J-T1 · Timer proposal expira sin propuesta → 0/100/0, hire=cancelled")]
    public async Task Proposal_timer_expires_no_proposal_cancels_to_expert_100()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "t1");

        // STEP 1: webhook checkout.session.completed
        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_jt1_" + Guid.NewGuid().ToString("N"));
        var appt = await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // STEP 2: schedule timer proposal (24h)
        var timer = await MarketplaceFlowSimulator.ScheduleAppointmentTimerAsync(
            db, appt.Id, "proposal", TimeSpan.FromHours(24));
        timer.HangfireJobId.Should().NotBeNullOrEmpty("schedule debe asignar jobId");

        // STEP 3: fast-forward (simula que pasaron 24h sin actividad)
        await MarketplaceFlowSimulator.FastForwardTimerAsync(db, timer.Id);

        // STEP 4: watchdog corre
        var processed = await MarketplaceFlowSimulator.RunWatchdogAsync(db);
        processed.Should().Be(1, "el watchdog debe haber recogido exactamente 1 timer");

        // VERIFY · Appointment + SearchHire
        await using var dbV = NewDbContext();
        var finalAppt = await dbV.Appointments.Include(a => a.Status).SingleAsync(a => a.Id == appt.Id);
        finalAppt.Status.StatusValue.Should().Be("appointment_cancelled_by_client_no_proposal");

        var finalHire = await dbV.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hire.Id);
        finalHire.Status.StatusValue.Should().Be("cancelled",
            "el mapping de appointment_cancelled_by_client_no_proposal lleva a SearchHire=cancelled");

        // VERIFY · split 0/100/0 (catálogo: SEED_ESTADOS_COMPLETO.sql:96)
        var (c, e, p) = await LoadSplitAsync(dbV, "appointment_cancelled_by_client_no_proposal");
        (c, e, p).Should().Be((0m, 100m, 0m));
        await AssertLedgerAsync(dbV, hire.Id, 100m, c, e, "J-T1 proposal expired");

        // VERIFY · timer
        await AssertTimerExpiredAsync(dbV, timer.Id, "J-T1");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J-T2 · Timer response vence tras propuesta del cliente
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "J-T2 · Timer response expira sin respuesta del experto → 100/0/0, hire=cancelled")]
    public async Task Response_timer_expires_no_expert_response_refunds_client_full()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "t2");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_jt2_" + Guid.NewGuid().ToString("N"));
        var appt = await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // Simular que el cliente propuso fecha → appointment_proposed
        var proposed = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_proposed");
        appt.StatusId = proposed.Id;
        appt.ProposedDate = DateTime.UtcNow.AddDays(3);
        await db.SaveChangesAsync();

        var timer = await MarketplaceFlowSimulator.ScheduleAppointmentTimerAsync(
            db, appt.Id, "response", TimeSpan.FromHours(24));

        await MarketplaceFlowSimulator.FastForwardTimerAsync(db, timer.Id);
        var processed = await MarketplaceFlowSimulator.RunWatchdogAsync(db);
        processed.Should().Be(1);

        await using var dbV = NewDbContext();
        var finalAppt = await dbV.Appointments.Include(a => a.Status).SingleAsync(a => a.Id == appt.Id);
        finalAppt.Status.StatusValue.Should().Be("appointment_cancelled_by_expert_no_response");

        var finalHire = await dbV.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hire.Id);
        finalHire.Status.StatusValue.Should().Be("cancelled");

        var (c, e, p) = await LoadSplitAsync(dbV, "appointment_cancelled_by_expert_no_response");
        (c, e, p).Should().Be((100m, 0m, 0m),
            "catálogo SEED_ESTADOS_COMPLETO.sql:97 — culpa del experto → cliente 100%");
        await AssertLedgerAsync(dbV, hire.Id, 100m, c, e, "J-T2 response expired");

        await AssertTimerExpiredAsync(dbV, timer.Id, "J-T2");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J-T3 · Watchdog procesa MÚLTIPLES timers en una pasada
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "J-T3 · Watchdog procesa 3 timers de distinto tipo en una pasada")]
    public async Task Watchdog_processes_multiple_timers_in_one_run()
    {
        await using var db = NewDbContext();

        // ── HIRE 1: timer proposal (cliente NO propuso) ──
        var a1 = await SeedActorsAsync(db, "t3a");
        var h1 = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a1.ClientId, a1.ExpertUserId, a1.ServiceId,
            amount: 100m, paymentIntentId: "pi_jt3a_" + Guid.NewGuid().ToString("N"));
        var ap1 = await MarketplaceFlowSimulator.AttachAppointmentAsync(db, h1.Id);
        var t1 = await MarketplaceFlowSimulator.ScheduleAppointmentTimerAsync(
            db, ap1.Id, "proposal", TimeSpan.FromHours(24));

        // ── HIRE 2: timer response (experto NO respondió) ──
        var a2 = await SeedActorsAsync(db, "t3b");
        var h2 = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a2.ClientId, a2.ExpertUserId, a2.ServiceId,
            amount: 100m, paymentIntentId: "pi_jt3b_" + Guid.NewGuid().ToString("N"));
        var ap2 = await MarketplaceFlowSimulator.AttachAppointmentAsync(db, h2.Id);
        var proposedSt = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_proposed");
        ap2.StatusId = proposedSt.Id;
        await db.SaveChangesAsync();
        var t2 = await MarketplaceFlowSimulator.ScheduleAppointmentTimerAsync(
            db, ap2.Id, "response", TimeSpan.FromHours(24));

        // ── HIRE 3: timer expert_report (experto NO entregó reporte) ──
        var a3 = await SeedActorsAsync(db, "t3c");
        var h3 = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a3.ClientId, a3.ExpertUserId, a3.ServiceId,
            amount: 100m, paymentIntentId: "pi_jt3c_" + Guid.NewGuid().ToString("N"));
        var ap3 = await MarketplaceFlowSimulator.AttachAppointmentAsync(db, h3.Id);
        var awaitingReport = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_awaiting_report");
        ap3.StatusId = awaitingReport.Id;
        await db.SaveChangesAsync();
        var t3 = await MarketplaceFlowSimulator.ScheduleAppointmentTimerAsync(
            db, ap3.Id, "expert_report", TimeSpan.FromHours(24));

        // ── Fast-forward los TRES ──
        await MarketplaceFlowSimulator.FastForwardTimerAsync(db, t1.Id);
        await MarketplaceFlowSimulator.FastForwardTimerAsync(db, t2.Id);
        await MarketplaceFlowSimulator.FastForwardTimerAsync(db, t3.Id);

        // ── Watchdog única pasada ──
        var processed = await MarketplaceFlowSimulator.RunWatchdogAsync(db);
        processed.Should().Be(3, "el watchdog procesa los 3 timers vencidos en una sola pasada");

        // ── Verify hire 1: cancelled, 0/100/0 ──
        await using var dbV = NewDbContext();

        var finalH1 = await dbV.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == h1.Id);
        finalH1.Status.StatusValue.Should().Be("cancelled");
        var finalAp1 = await dbV.Appointments.Include(a => a.Status).SingleAsync(a => a.Id == ap1.Id);
        finalAp1.Status.StatusValue.Should().Be("appointment_cancelled_by_client_no_proposal");
        var (c1, e1, _) = await LoadSplitAsync(dbV, "appointment_cancelled_by_client_no_proposal");
        await AssertLedgerAsync(dbV, h1.Id, 100m, c1, e1, "J-T3 hire1 proposal");
        await AssertTimerExpiredAsync(dbV, t1.Id, "J-T3 timer1");

        // ── Verify hire 2: cancelled, 100/0/0 ──
        var finalH2 = await dbV.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == h2.Id);
        finalH2.Status.StatusValue.Should().Be("cancelled");
        var finalAp2 = await dbV.Appointments.Include(a => a.Status).SingleAsync(a => a.Id == ap2.Id);
        finalAp2.Status.StatusValue.Should().Be("appointment_cancelled_by_expert_no_response");
        var (c2, e2, _) = await LoadSplitAsync(dbV, "appointment_cancelled_by_expert_no_response");
        await AssertLedgerAsync(dbV, h2.Id, 100m, c2, e2, "J-T3 hire2 response");
        await AssertTimerExpiredAsync(dbV, t2.Id, "J-T3 timer2");

        // ── Verify hire 3: cancelled, 95/0/5 ──
        var finalH3 = await dbV.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == h3.Id);
        finalH3.Status.StatusValue.Should().Be("cancelled");
        var finalAp3 = await dbV.Appointments.Include(a => a.Status).SingleAsync(a => a.Id == ap3.Id);
        finalAp3.Status.StatusValue.Should().Be("appointment_cancelled_by_no_report");
        var (c3, e3, p3) = await LoadSplitAsync(dbV, "appointment_cancelled_by_no_report");
        (c3, e3, p3).Should().Be((95m, 0m, 5m));
        await AssertLedgerAsync(dbV, h3.Id, 100m, c3, e3, "J-T3 hire3 expert_report");
        await AssertTimerExpiredAsync(dbV, t3.Id, "J-T3 timer3");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J-T4 · Watchdog es idempotente — 2 pasadas consecutivas no duplican
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "J-T4 · Watchdog idempotente: 2 pasadas NO duplican FT ni rompen UNIQUE indexes")]
    public async Task Watchdog_is_idempotent_no_duplicate_on_second_run()
    {
        await using var db = NewDbContext();
        var actors = await SeedActorsAsync(db, "t4");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: "pi_jt4_" + Guid.NewGuid().ToString("N"));
        var appt = await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        var timer = await MarketplaceFlowSimulator.ScheduleAppointmentTimerAsync(
            db, appt.Id, "proposal", TimeSpan.FromHours(24));
        await MarketplaceFlowSimulator.FastForwardTimerAsync(db, timer.Id);

        // ── Pasada 1: procesa 1 timer ──
        var p1 = await MarketplaceFlowSimulator.RunWatchdogAsync(db);
        p1.Should().Be(1, "primera pasada debe procesar el timer vencido");

        // Snapshot del estado tras la 1ª pasada
        int ftCountAfterFirst;
        int hireStatusIdAfterFirst;
        int apptStatusIdAfterFirst;
        DateTime? expiredAtAfterFirst;
        await using (var dbSnap = NewDbContext())
        {
            ftCountAfterFirst = await dbSnap.FinancialTransactions.CountAsync(ft =>
                ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hire.Id);
            hireStatusIdAfterFirst = await dbSnap.SearchHires
                .Where(h => h.Id == hire.Id).Select(h => h.StatusId).SingleAsync();
            apptStatusIdAfterFirst = await dbSnap.Appointments
                .Where(a => a.Id == appt.Id).Select(a => a.StatusId).SingleAsync();
            expiredAtAfterFirst = await dbSnap.AppointmentTimers
                .Where(t => t.Id == timer.Id).Select(t => t.ExpiredAt).SingleAsync();
        }

        ftCountAfterFirst.Should().Be(2,
            "split 0/100/0: ServicePayment (1) + Payout (1) = 2 FT. " +
            "No hay Refund porque ClientPercentage=0.");

        // ── Pasada 2: no debe haber timers vencidos NO expirados ──
        var p2 = await MarketplaceFlowSimulator.RunWatchdogAsync(db);
        p2.Should().Be(0,
            "2ª pasada: el timer ya fue claim-eado (IsExpired=true) en la 1ª, " +
            "el WHERE !IsExpired AND EndTime<=UtcNow no lo recoge → procesa 0");

        // ── Verify no se duplicó nada ──
        await using var dbV = NewDbContext();

        var ftCountAfterSecond = await dbV.FinancialTransactions.CountAsync(ft =>
            ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hire.Id);
        ftCountAfterSecond.Should().Be(ftCountAfterFirst,
            "la 2ª pasada NO crea FT adicionales → idempotencia confirmada");

        var hireStatusAfterSecond = await dbV.SearchHires
            .Where(h => h.Id == hire.Id).Select(h => h.StatusId).SingleAsync();
        hireStatusAfterSecond.Should().Be(hireStatusIdAfterFirst,
            "el estado del hire no cambia entre pasadas idempotentes");

        var apptStatusAfterSecond = await dbV.Appointments
            .Where(a => a.Id == appt.Id).Select(a => a.StatusId).SingleAsync();
        apptStatusAfterSecond.Should().Be(apptStatusIdAfterFirst);

        // El timer sigue marcado expired, ExpiredAt NO cambió, HangfireJobId sigue limpio
        var finalTimer = await dbV.AppointmentTimers.AsNoTracking().SingleAsync(t => t.Id == timer.Id);
        finalTimer.IsExpired.Should().BeTrue();
        finalTimer.ExpiredAt.Should().Be(expiredAtAfterFirst,
            "ExpiredAt fijado en la 1ª pasada, no se re-escribe en la 2ª");
        finalTimer.HangfireJobId.Should().BeNull();

        // Verificación final del ledger (mismas reglas que J-T1)
        await AssertLedgerAsync(dbV, hire.Id, 100m, 0m, 100m, "J-T4 ledger tras 2 pasadas idempotentes");

        // El UNIQUE index IX_AppointmentTimers_Appt_Type_Active (parcial WHERE IsExpired=false)
        // garantiza que aunque alguien intentara recrear el timer activo del mismo tipo para
        // el mismo appointment, fallaría con 23505. Confirmamos que NO hay timers activos:
        var activeTimers = await dbV.AppointmentTimers
            .CountAsync(t => t.AppointmentId == appt.Id && t.TimerType == "proposal" && !t.IsExpired);
        activeTimers.Should().Be(0,
            "índice único parcial protege contra timers activos duplicados — el procesado dejó 0 activos");
    }
}
