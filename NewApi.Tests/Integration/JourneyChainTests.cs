using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using NewApi.Tests.Simulators;

namespace NewApi.Tests.Integration;

/// <summary>
/// JOURNEY CHAINS · cadenas combinatorias REALISTAS que cierran los huecos que
/// los tests FLOW-* / J-A* / J-D* no cubrían:
///
///   J-C1 · 2ª rechazo del experto → appointment_cancelled_by_expert_rejection
///          (sin config propia → fallback vía mapping a cancelled → 100/0/0).
///   J-C2 · Cadena MIXTA realista: cliente cancela 1ª → re-propone → confirma →
///          experto cancela 1ª → re-propone → confirma → cliente cancela 2ª →
///          finaliza 0/95/5. Verifica contadores SEPARADOS (la cancelación del
///          experto NO cuenta para la finalización del cliente).
///   J-C3 · Independencia de contadores: cliente 1ª + experto 1ª (intercalando
///          re-propuesta) → NINGUNO finaliza, el hire sigue pending, sin dinero.
///   J-C4 · Doble cancelación PURA del cliente con re-propuesta entre medias
///          (secuencia realista, a diferencia de FLOW-03 que cancela 2x seguidas).
///
/// A diferencia de FLOW-03/04 (que llaman Cancel dos veces SIN re-proponer — algo
/// imposible en la vida real porque no puedes cancelar una cita ya cancelada),
/// estos tests recorren el ciclo realista propose→confirm→cancel entre cada paso.
/// </summary>
public class JourneyChainTests : IntegrationTestBase
{
    public JourneyChainTests(PostgresContainerFixture fixture) : base(fixture) { }

    private record SeedActors(int ClientId, int ExpertUserId, int ExpertProfileId, int ServiceId);

    private async Task<SeedActors> SeedActorsAsync(AppDbContext db, string slug)
    {
        var client = await new UserBuilder($"jc-{slug}-cli@test.dev").AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder($"jc-{slug}-exp@test.dev").AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").PersistAsync(db);
        return new SeedActors(client.Id, expertUser.Id, expert.Id, svc.Id);
    }

    private async Task<(decimal client, decimal expert, decimal platform)> LoadSplitAsync(
        AppDbContext db, string statusValue, string statusType)
    {
        var status = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == statusType && s.StatusValue == statusValue);
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
            refund.Should().NotBeNull($"{reason}: Refund con {expectedClientPct}%");
            refund!.Amount.Should().Be(expectedRefund);
        }
        else refund.Should().BeNull($"{reason}: sin Refund (client%=0)");

        var payout = fts.SingleOrDefault(ft => ft.TransactionType == "Payout");
        if (expectedPayout > 0)
        {
            payout.Should().NotBeNull($"{reason}: Payout con {expectedExpertPct}%");
            payout!.Amount.Should().Be(expectedPayout);
        }
        else payout.Should().BeNull($"{reason}: sin Payout (expert%=0)");

        var net = servicePayment.Amount - (refund?.Amount ?? 0m) - (payout?.Amount ?? 0m);
        var expectedPlatform = Math.Round(originalAmount * (100m - expectedClientPct - expectedExpertPct) / 100m, 2);
        net.Should().Be(expectedPlatform, $"{reason}: residuo plataforma = {expectedPlatform}€");
        fts.Should().OnlyContain(ft => ft.Amount >= 0);
    }

    private async Task<string> HireStatusAsync(AppDbContext db, int hireId)
        => (await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hireId)).Status.StatusValue;

    private async Task<string> ApptStatusAsync(AppDbContext db, int hireId)
        => (await db.Appointments.Include(a => a.Status).SingleAsync(a => a.SearchHireId == hireId)).Status.StatusValue;

    // ─────────────────────────────────────────────────────────────────────────
    // J-C1 · 2ª rechazo del experto → cancelled (100/0/0)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "J-C1 · Experto rechaza 2 veces (con re-propuesta) → appointment_cancelled_by_expert_rejection · 100/0/0 vía fallback mapping")]
    public async Task Expert_second_rejection_finalizes_with_full_refund()
    {
        await using var db = NewDbContext();
        var a = await SeedActorsAsync(db, "c1");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a.ClientId, a.ExpertUserId, a.ServiceId, 100m, "pi_jc1_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // Cliente propone → experto rechaza (1ª) → estado appointment_rejected
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(3));
        var r1 = await MarketplaceFlowSimulator.ExpertRejectsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        r1.Should().Be("appointment_rejected", "1ª rechazo NO finaliza — permite re-proponer");
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_rejected");

        // Cliente re-propone → experto rechaza (2ª) → finaliza
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(4));
        var r2 = await MarketplaceFlowSimulator.ExpertRejectsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        r2.Should().Be("appointment_cancelled_by_expert_rejection", "2ª rechazo finaliza");

        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_cancelled_by_expert_rejection");

        // Dinero: el estado de rechazo NO tiene config propia → fallback vía mapping a
        // SearchHireStatus.cancelled = 100/0/0 (reembolso completo al cliente).
        var (c, e, p) = await LoadSplitAsync(db, "cancelled", "SearchHireStatus");
        (c, e, p).Should().Be((100m, 0m, 0m));
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "J-C1 2ª rechazo experto");

        var appt = await db.Appointments.SingleAsync(a2 => a2.SearchHireId == hire.Id);
        appt.RejectionCount.Should().Be(2);
        appt.ExpertCancellationCount.Should().Be(1, "el 2ª rechazo incrementa ExpertCancellationCount (AppointmentService.cs:1976)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J-C2 · Cadena MIXTA: cliente 1ª → experto 1ª → cliente 2ª (finaliza 0/95/5)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "J-C2 · Cadena mixta cliente1ª+experto1ª+cliente2ª · contadores SEPARADOS · finaliza 0/95/5")]
    public async Task Mixed_cancellation_chain_separate_counters_finalize_on_client_second()
    {
        await using var db = NewDbContext();
        var a = await SeedActorsAsync(db, "c2");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a.ClientId, a.ExpertUserId, a.ServiceId, 100m, "pi_jc2_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // Paso 1: cliente propone → confirma → cliente cancela (1ª) — NO finaliza
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(3));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        var s1 = await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId);
        s1.Should().Be("appointment_cancelled_by_client");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending", "cliente 1ª NO finaliza");

        // Paso 2: re-propone → confirma → experto cancela (1ª) — NO finaliza
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(4));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        var s2 = await MarketplaceFlowSimulator.CancelByExpertAsync(db, hire.Id, a.ExpertUserId);
        s2.Should().Be("appointment_cancelled_by_expert");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending",
            "experto 1ª NO finaliza — y CRÍTICO: no cuenta para la finalización del cliente (contadores separados)");

        // Paso 3: re-propone → confirma → cliente cancela (2ª) — AHORA SÍ finaliza
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(5));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        var s3 = await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId);
        s3.Should().Be("appointment_cancelled_by_client_second",
            "la 2ª cancelación del CLIENTE (ClientCancellationCount>=1) finaliza, independiente de las del experto");

        var (c, e, p) = await LoadSplitAsync(db, "appointment_cancelled_by_client_second", "AppointmentStatus");
        (c, e, p).Should().Be((0m, 95m, 5m));
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "J-C2 mixta cliente 2ª");

        var appt = await db.Appointments.SingleAsync(x => x.SearchHireId == hire.Id);
        appt.ClientCancellationCount.Should().Be(2);
        appt.ExpertCancellationCount.Should().Be(1, "el experto solo canceló 1 vez; su contador NO llegó a 2");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J-C3 · Independencia de contadores: cliente 1ª + experto 1ª → ninguno finaliza
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "J-C3 · Cliente 1ª + Experto 1ª (sin segundas) → hire sigue pending · sin money distribution")]
    public async Task First_cancellations_of_both_actors_do_not_finalize()
    {
        await using var db = NewDbContext();
        var a = await SeedActorsAsync(db, "c3");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a.ClientId, a.ExpertUserId, a.ServiceId, 100m, "pi_jc3_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(3));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId);

        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(4));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        await MarketplaceFlowSimulator.CancelByExpertAsync(db, hire.Id, a.ExpertUserId);

        (await HireStatusAsync(db, hire.Id)).Should().Be("pending",
            "1ª de cada actor NO finaliza — 1+1 de actores distintos NO suma a 2");

        // Cero dinero distribuido: solo el ServicePayment original
        var fts = await db.FinancialTransactions
            .Where(ft => ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hire.Id)
            .ToListAsync();
        fts.Should().ContainSingle().Which.TransactionType.Should().Be("ServicePayment");

        var appt = await db.Appointments.SingleAsync(x => x.SearchHireId == hire.Id);
        appt.ClientCancellationCount.Should().Be(1);
        appt.ExpertCancellationCount.Should().Be(1);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J-C4 · Doble cancelación PURA del cliente con re-propuesta entre medias
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "J-C4 · Cliente cancela 1ª → re-propone → confirma → cancela 2ª (secuencia realista) · 0/95/5")]
    public async Task Client_double_cancel_with_repropose_between_finalizes()
    {
        await using var db = NewDbContext();
        var a = await SeedActorsAsync(db, "c4");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a.ClientId, a.ExpertUserId, a.ServiceId, 100m, "pi_jc4_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(3));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        var s1 = await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId);
        s1.Should().Be("appointment_cancelled_by_client");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending");

        // Re-propone la MISMA fecha (realista: el cliente reagenda) → confirma → cancela 2ª
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(3));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        var s2 = await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId);
        s2.Should().Be("appointment_cancelled_by_client_second");

        var (c, e, p) = await LoadSplitAsync(db, "appointment_cancelled_by_client_second", "AppointmentStatus");
        (c, e, p).Should().Be((0m, 95m, 5m));
        await AssertLedgerAsync(db, hire.Id, 100m, c, e, "J-C4 doble cancel cliente realista");

        var appt = await db.Appointments.SingleAsync(x => x.SearchHireId == hire.Id);
        appt.ClientCancellationCount.Should().Be(2);
        appt.ExpertCancellationCount.Should().Be(0, "el experto nunca canceló");
    }
}
