using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using NewApi.Tests.Simulators;

namespace NewApi.Tests.Integration;

/// <summary>
/// JOURNEY · CADENAS LARGAS COMPLEJAS (hand-crafted) — secuencias multi-paso "nasty"
/// que combinan propose / reject / cancel / repropose / timers y verifican el estado
/// del Appointment Y del SearchHire en CADA paso intermedio, los tres contadores, y la
/// distribución de dinero final.
///
/// ─────────────────────────────────────────────────────────────────────────────────
/// AUDITORÍA PREVIA (verificada contra Services/AppointmentService.cs del backend real):
///
///   (1) RE-PROPONER desde 'appointment_proposed' → ❌ NO permitido.
///       AppointmentService.cs:839-852: 'appointment_proposed' está en
///       invalidStatesForPropose. Estados VÁLIDOS para proponer (L831-836):
///         awaiting_appointment | appointment_rejected
///         | appointment_cancelled_by_client | appointment_cancelled_by_expert.
///       ⇒ El ejemplo literal del usuario "proponer → (response timer expira, experto
///         no acepta) → re-proponer" NO es válido: si el timer 'response' vence estando
///         en 'appointment_proposed' (L3491 + case "response" L3687-3836), la cita pasa
///         a 'appointment_cancelled_by_expert_no_response', que es un estado FINAL
///         (100/0/0, hire → cancelled). NO vuelve a un estado re-proponible.
///       ⇒ CAMINO REAL EQUIVALENTE a "proponer sin aceptar y volver a proponer":
///         experto RECHAZA (1ª) → 'appointment_rejected' (L1942) → cliente re-propone
///         (estado válido). En CC-01 se usa esta vía y se documenta la divergencia.
///
///   (2) CANCELAR requiere 'appointment_confirmed' → ✅ ÚNICO estado válido.
///       AppointmentService.cs:2573-2577: validStatesForCancel = { "appointment_confirmed" }.
///       NO se puede cancelar desde 'appointment_proposed' (L2569) ni desde
///       'awaiting_appointment' (L2504-2517). ⇒ Toda cancelación va precedida de confirmar.
///
///   (3) CONTADORES (RejectionCount / ClientCancellationCount / ExpertCancellationCount)
///       NO se resetean al re-proponer. ProposeAppointmentAsync (L648-1161) nunca los
///       toca. ⇒ El 2º rechazo (RejectionCount>=1, L1918) o la 2ª cancelación del mismo
///       actor (L2642 / L2666) finalizan SIEMPRE, sin importar las re-propuestas o las
///       acciones del OTRO actor intercaladas (contadores independientes por actor).
///
/// Estados finales y splits (del seed de tests SEED_ESTADOS_COMPLETO.sql; se leen
/// dinámicamente vía LoadSplitAsync para no hardcodear):
///   appointment_cancelled_by_expert_rejection → (mapping) cancelled        100/0/0
///   appointment_cancelled_by_client_second                                   0/95/5
///   appointment_completed_without_client_approval                            0/95/5
///   completed                                                                0/95/5
/// ─────────────────────────────────────────────────────────────────────────────────
/// </summary>
public class JourneyComplexChainTests : IntegrationTestBase
{
    public JourneyComplexChainTests(PostgresContainerFixture fixture) : base(fixture) { }

    private record SeedActors(int ClientId, int ExpertUserId, int ExpertProfileId, int ServiceId);

    private async Task<SeedActors> SeedActorsAsync(AppDbContext db, string slug)
    {
        var client = await new UserBuilder($"cc-{slug}-cli@test.dev").AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder($"cc-{slug}-exp@test.dev").AsExpert().Verified().PersistAsync(db);
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

    /// <summary>
    /// Verifica el ledger final: exactamente 1 ServicePayment, importes exactos de Refund
    /// y Payout (o ausencia si % = 0), suma 100% (residuo = plataforma) y sin negativos.
    /// </summary>
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
        fts.Should().OnlyContain(ft => ft.Amount >= 0, $"{reason}: ningún importe negativo");
    }

    private async Task<string> HireStatusAsync(AppDbContext db, int hireId)
        => (await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hireId)).Status.StatusValue;

    private async Task<string> ApptStatusAsync(AppDbContext db, int hireId)
        => (await db.Appointments.Include(a => a.Status).SingleAsync(a => a.SearchHireId == hireId)).Status.StatusValue;

    private async Task<Appointment> ApptAsync(AppDbContext db, int hireId)
        => await db.Appointments.AsNoTracking().SingleAsync(a => a.SearchHireId == hireId);

    private async Task AssertCountersAsync(
        AppDbContext db, int hireId, int rejection, int clientCancel, int expertCancel, string at)
    {
        var appt = await ApptAsync(db, hireId);
        appt.RejectionCount.Should().Be(rejection, $"{at}: RejectionCount");
        appt.ClientCancellationCount.Should().Be(clientCancel, $"{at}: ClientCancellationCount");
        appt.ExpertCancellationCount.Should().Be(expertCancel, $"{at}: ExpertCancellationCount");
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // CC-01 · El ejemplo del usuario (adaptado al camino REAL).
    //
    // Usuario pidió: proponer → (response timer expira, experto no acepta) → re-proponer
    //               → confirmar → cancelar(cliente 1ª) → re-proponer → rechazar(experto 1ª).
    //
    // DIVERGENCIA (audit #1): "response timer expira estando proposed" NO deja un estado
    // re-proponible — finaliza en appointment_cancelled_by_expert_no_response (terminal).
    // El camino REAL equivalente a "el experto no acepta y el cliente vuelve a proponer" es
    // que el experto RECHACE (1ª) → appointment_rejected → cliente re-propone (válido).
    // El resto de la cadena es literal y válido.
    // ═════════════════════════════════════════════════════════════════════════════
    [Fact(DisplayName = "CC-01 · propose→reject(1ª)→repropose→confirm→cancelCliente(1ª)→repropose→reject(experto1ª) · estados+contadores por paso · NO finaliza")]
    public async Task CC01_user_example_real_equivalent_chain()
    {
        await using var db = NewDbContext();
        var a = await SeedActorsAsync(db, "01");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a.ClientId, a.ExpertUserId, a.ServiceId, 100m, "pi_cc01_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // Paso 1: cliente propone → appointment_proposed, hire pending.
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(3));
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed", "P1");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending", "P1");
        await AssertCountersAsync(db, hire.Id, 0, 0, 0, "P1 tras proponer");

        // Paso 2: experto NO acepta — en lugar de dejar expirar el response timer (que
        // FINALIZA, audit #1), rechaza (1ª) → appointment_rejected (estado re-proponible).
        var r1 = await MarketplaceFlowSimulator.ExpertRejectsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        r1.Should().Be("appointment_rejected", "P2: 1er rechazo NO finaliza, permite re-proponer");
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_rejected", "P2");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending", "P2: rechazo 1º no toca el hire");
        await AssertCountersAsync(db, hire.Id, 1, 0, 0, "P2 tras rechazo 1º");

        // Paso 3: cliente re-propone (válido desde appointment_rejected, audit #1).
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(4));
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed", "P3");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending", "P3");
        await AssertCountersAsync(db, hire.Id, 1, 0, 0, "P3: re-proponer NO resetea contadores");

        // Paso 4: experto confirma → appointment_confirmed (requisito para poder cancelar, audit #2).
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_confirmed", "P4");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending", "P4");
        await AssertCountersAsync(db, hire.Id, 1, 0, 0, "P4 tras confirmar");

        // Paso 5: cliente cancela (1ª) → appointment_cancelled_by_client, NO finaliza.
        var c1 = await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId);
        c1.Should().Be("appointment_cancelled_by_client", "P5: 1ª cancelación NO finaliza");
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_cancelled_by_client", "P5");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending", "P5: hire sigue pending");
        await AssertCountersAsync(db, hire.Id, 1, 1, 0, "P5 tras cancelar cliente 1ª");

        // Paso 6: cliente re-propone (válido desde appointment_cancelled_by_client, audit #1).
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(5));
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed", "P6");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending", "P6");
        await AssertCountersAsync(db, hire.Id, 1, 1, 0, "P6: contadores intactos tras re-proponer");

        // Paso 7: experto rechaza. RejectionCount ya es 1 ⇒ este es el "2º rechazo" lógico
        // (isSecondRejection = RejectionCount>=1) → FINALIZA en expert_rejection (audit #3:
        // el contador NUNCA se reseteó pese a la cancelación del cliente intercalada).
        var r2 = await MarketplaceFlowSimulator.ExpertRejectsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        r2.Should().Be("appointment_cancelled_by_expert_rejection",
            "P7: RejectionCount>=1 ⇒ finaliza, aunque hubo una cancelación de cliente intercalada (contadores no se resetean)");
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_cancelled_by_expert_rejection", "P7");
        await AssertCountersAsync(db, hire.Id, 2, 1, 1,
            "P7: RejectionCount=2, ClientCancel=1 (intacto), ExpertCancel=1 (el 2º rechazo lo incrementa)");

        // Dinero final: expert_rejection no tiene config propia → fallback mapping → cancelled 100/0/0.
        var (cli, exp, _) = await LoadSplitAsync(db, "cancelled", "SearchHireStatus");
        (cli, exp).Should().Be((100m, 0m), "CC-01 termina en cancelled 100/0/0");
        await AssertLedgerAsync(db, hire.Id, 100m, cli, exp, "CC-01 final");
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // CC-02 · Maximiza rechazos cruzando con una cancelación de cliente.
    //   propose → reject(1ª) → repropose → confirm → cancelCliente(1ª) → repropose → reject(2ª).
    // Verifica que el rechazo (actor experto) y la cancelación (actor cliente) NO se mezclan:
    // el 2º rechazo finaliza por RejectionCount>=1, no por la cancelación del cliente.
    // ═════════════════════════════════════════════════════════════════════════════
    [Fact(DisplayName = "CC-02 · reject(1ª)→repropose→confirm→cancelCliente(1ª)→repropose→reject(2ª) · finaliza expert_rejection 100/0/0 · contadores cruzados no interfieren")]
    public async Task CC02_maximize_rejections_with_crossed_cancellation()
    {
        await using var db = NewDbContext();
        var a = await SeedActorsAsync(db, "02");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a.ClientId, a.ExpertUserId, a.ServiceId, 100m, "pi_cc02_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // propose → reject(1ª) → appointment_rejected
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(3));
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed", "P1");
        var r1 = await MarketplaceFlowSimulator.ExpertRejectsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        r1.Should().Be("appointment_rejected", "P2");
        await AssertCountersAsync(db, hire.Id, 1, 0, 0, "P2");

        // repropose → confirm → cancelCliente(1ª) (cruza un actor distinto en medio)
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(4));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_confirmed", "P4");
        var c1 = await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId);
        c1.Should().Be("appointment_cancelled_by_client", "P5: cliente cancela 1ª, NO finaliza");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending", "P5");
        await AssertCountersAsync(db, hire.Id, 1, 1, 0, "P5: rechazo=1 (experto) + cancel=1 (cliente), distintos buckets");

        // repropose → reject(2ª) → FINALIZA (RejectionCount>=1, no por la cancelación del cliente)
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(5));
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed", "P6");
        var r2 = await MarketplaceFlowSimulator.ExpertRejectsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        r2.Should().Be("appointment_cancelled_by_expert_rejection",
            "P7: el 2º RECHAZO finaliza; la cancelación del CLIENTE no contribuyó a este contador");
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_cancelled_by_expert_rejection", "P7");
        await AssertCountersAsync(db, hire.Id, 2, 1, 1,
            "P7: RejectionCount=2, ClientCancel=1 sin tocar, ExpertCancel=1 (2º rechazo)");

        var (cli, exp, _) = await LoadSplitAsync(db, "cancelled", "SearchHireStatus");
        (cli, exp).Should().Be((100m, 0m));
        await AssertLedgerAsync(db, hire.Id, 100m, cli, exp, "CC-02 final expert_rejection");
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // CC-03 · El cliente cambia de idea 3 veces antes de que la cita se complete.
    //
    // MECANISMO de "cambiar propuesta" (documentado, audit #1+#2): NO existe un re-propose
    // directo desde appointment_proposed. La forma realista de cambiar de fecha es:
    //   confirmar → cancelar(cliente) → re-proponer (nueva fecha).
    // Pero cancelar 2 veces finaliza (CC-04). Para "3 cambios" SIN finalizar, alternamos
    // el actor que devuelve la cita a un estado re-proponible: cancela cliente(1ª),
    // luego rechazo del experto(1ª) — así ningún contador llega a 2 — y la 3ª propuesta se
    // confirma y completa con reporte.
    //
    // Secuencia (8 pasos): propose A → confirm → cancelCliente(1ª) → re-propose B →
    //   reject experto(1ª) → re-propose C → confirm → completar (report→approve).
    // ═════════════════════════════════════════════════════════════════════════════
    [Fact(DisplayName = "CC-03 · 3 propuestas (cancel cliente1ª + reject experto1ª intercalados) → 3ª confirma+reporte+aprueba · completed 0/95/5")]
    public async Task CC03_three_proposals_change_of_mind_then_complete()
    {
        await using var db = NewDbContext();
        var a = await SeedActorsAsync(db, "03");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a.ClientId, a.ExpertUserId, a.ServiceId, 100m, "pi_cc03_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // Propuesta A → confirm → cancelCliente(1ª): el cliente reagenda (cambio #1).
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(3));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        var c1 = await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId);
        c1.Should().Be("appointment_cancelled_by_client", "cambio #1");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending");
        await AssertCountersAsync(db, hire.Id, 0, 1, 0, "tras cambio #1");

        // Propuesta B → el experto rechaza (1ª): segunda vuelta, distinto actor (cambio #2).
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(4));
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed");
        var r1 = await MarketplaceFlowSimulator.ExpertRejectsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        r1.Should().Be("appointment_rejected", "cambio #2: rechazo 1º NO finaliza");
        await AssertCountersAsync(db, hire.Id, 1, 1, 0, "tras cambio #2 — ningún contador llegó a 2");

        // Propuesta C → confirm → completar vía reporte (cambio #3, definitivo).
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(5));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_confirmed");
        await AssertCountersAsync(db, hire.Id, 1, 1, 0, "tras confirmar la 3ª — contadores intactos");

        // +3h → awaiting_report → experto entrega reporte → hire awaiting_client_decision.
        await MarketplaceFlowSimulator.AdvanceToAwaitingReportAsync(db, hire.Id);
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_awaiting_report", "tras +3h");
        await MarketplaceFlowSimulator.ExpertSubmitsReportAsync(db, hire.Id, a.ExpertUserId);
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_report_sent");
        (await HireStatusAsync(db, hire.Id)).Should().Be("awaiting_client_decision");

        // Cliente aprueba → completed.
        await MarketplaceFlowSimulator.ClientApprovesServiceAsync(db, hire.Id, a.ClientId);
        (await HireStatusAsync(db, hire.Id)).Should().Be("completed", "el cliente aprueba el reporte");
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_completed");
        await AssertCountersAsync(db, hire.Id, 1, 1, 0,
            "los contadores de las idas y venidas NO afectan al desenlace exitoso");

        var (cli, exp, _) = await LoadSplitAsync(db, "completed", "SearchHireStatus");
        (cli, exp).Should().Be((0m, 95m), "completed paga 95% al experto");
        await AssertLedgerAsync(db, hire.Id, 100m, cli, exp, "CC-03 completed");
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // CC-04 · Termina en TIMER tras varias idas y venidas.
    //   propose → confirm → cancelCliente(1ª) → re-propose → confirm → +3h(report) →
    //   experto entrega reporte → expira timer client_decision → auto-completado 0/95/5.
    // Verifica que los contadores intermedios (1 cancelación de cliente) NO alteran el
    // resultado del timer (depende solo de que el hire esté en awaiting_client_decision).
    // ═════════════════════════════════════════════════════════════════════════════
    [Fact(DisplayName = "CC-04 · cancelCliente(1ª)→repropose→confirm→report→expira client_decision · auto-completado 0/95/5 · contadores no afectan al timer")]
    public async Task CC04_chain_ending_in_client_decision_timer()
    {
        await using var db = NewDbContext();
        var a = await SeedActorsAsync(db, "04");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a.ClientId, a.ExpertUserId, a.ServiceId, 100m, "pi_cc04_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // propose → confirm → cancelCliente(1ª)
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(3));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        var c1 = await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId);
        c1.Should().Be("appointment_cancelled_by_client");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending", "P3: 1ª cancelación no finaliza");
        await AssertCountersAsync(db, hire.Id, 0, 1, 0, "P3");

        // re-propose → confirm
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(4));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_confirmed", "P5");
        await AssertCountersAsync(db, hire.Id, 0, 1, 0, "P5: contador intacto");

        // +3h → awaiting_report → experto entrega reporte → awaiting_client_decision
        await MarketplaceFlowSimulator.AdvanceToAwaitingReportAsync(db, hire.Id);
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_awaiting_report", "P6");
        await MarketplaceFlowSimulator.ExpertSubmitsReportAsync(db, hire.Id, a.ExpertUserId);
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_report_sent", "P7");
        (await HireStatusAsync(db, hire.Id)).Should().Be("awaiting_client_decision", "P7");

        // El cliente NO decide en 24h → timer client_decision expira → auto-completado.
        await MarketplaceFlowSimulator.ExpireClientDecisionTimerWithDisputeGuardAsync(db, hire.Id);
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_completed_without_client_approval", "P8");
        (await HireStatusAsync(db, hire.Id)).Should().Be("completed", "P8: el timer auto-completa el hire");
        await AssertCountersAsync(db, hire.Id, 0, 1, 0,
            "P8: la cancelación intermedia del cliente NO altera el resultado del timer");

        var (cli, exp, _) = await LoadSplitAsync(db, "appointment_completed_without_client_approval", "AppointmentStatus");
        (cli, exp).Should().Be((0m, 95m), "auto-completado paga 95% al experto");
        await AssertLedgerAsync(db, hire.Id, 100m, cli, exp, "CC-04 auto-completado por timer");
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // CC-05 · Cadenas largas (8-10 pasos) que aterrizan en CADA terminal alcanzable.
    //   (a) → appointment_cancelled_by_client_second  (2ª cancelación cliente)   0/95/5
    //   (b) → appointment_cancelled_by_expert_second  (2ª cancelación experto)   100/0/0
    //   (c) → appointment_cancelled_by_expert_rejection (2º rechazo)             100/0/0
    // Cada caso recorre idas y venidas realistas antes de finalizar.
    // ═════════════════════════════════════════════════════════════════════════════
    [Fact(DisplayName = "CC-05a · cadena larga → appointment_cancelled_by_client_second (0/95/5)")]
    public async Task CC05a_long_chain_terminal_client_second()
    {
        await using var db = NewDbContext();
        var a = await SeedActorsAsync(db, "05a");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a.ClientId, a.ExpertUserId, a.ServiceId, 100m, "pi_cc05a_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // reject(1ª) → repropose → cancelExperto(1ª) → repropose → confirm → cancelCliente(1ª)
        // → repropose → confirm → cancelCliente(2ª) [FINALIZA]
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(3));
        (await MarketplaceFlowSimulator.ExpertRejectsAppointmentAsync(db, hire.Id, a.ExpertUserId))
            .Should().Be("appointment_rejected");
        await AssertCountersAsync(db, hire.Id, 1, 0, 0, "a:reject1");

        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(4));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        (await MarketplaceFlowSimulator.CancelByExpertAsync(db, hire.Id, a.ExpertUserId))
            .Should().Be("appointment_cancelled_by_expert");
        await AssertCountersAsync(db, hire.Id, 1, 0, 1, "a:cancelExp1");

        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(5));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        (await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId))
            .Should().Be("appointment_cancelled_by_client");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending", "a:cancelCli1 no finaliza");
        await AssertCountersAsync(db, hire.Id, 1, 1, 1, "a:cancelCli1");

        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(6));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        (await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId))
            .Should().Be("appointment_cancelled_by_client_second", "a: 2ª del CLIENTE finaliza");

        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_cancelled_by_client_second");
        await AssertCountersAsync(db, hire.Id, 1, 2, 1,
            "a: ClientCancel=2 (finaliza), ExpertCancel=1 y Rejection=1 NO contribuyeron");

        var (cli, exp, _) = await LoadSplitAsync(db, "appointment_cancelled_by_client_second", "AppointmentStatus");
        (cli, exp).Should().Be((0m, 95m));
        await AssertLedgerAsync(db, hire.Id, 100m, cli, exp, "CC-05a client_second");
    }

    [Fact(DisplayName = "CC-05b · cadena larga → appointment_cancelled_by_expert_second (100/0/0)")]
    public async Task CC05b_long_chain_terminal_expert_second()
    {
        await using var db = NewDbContext();
        var a = await SeedActorsAsync(db, "05b");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a.ClientId, a.ExpertUserId, a.ServiceId, 100m, "pi_cc05b_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // cancelCliente(1ª) → repropose → cancelExperto(1ª) → repropose → cancelExperto(2ª) [FINALIZA]
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(3));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        (await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId))
            .Should().Be("appointment_cancelled_by_client");
        await AssertCountersAsync(db, hire.Id, 0, 1, 0, "b:cancelCli1");

        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(4));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        (await MarketplaceFlowSimulator.CancelByExpertAsync(db, hire.Id, a.ExpertUserId))
            .Should().Be("appointment_cancelled_by_expert");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending", "b:cancelExp1 no finaliza");
        await AssertCountersAsync(db, hire.Id, 0, 1, 1, "b:cancelExp1");

        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(5));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        (await MarketplaceFlowSimulator.CancelByExpertAsync(db, hire.Id, a.ExpertUserId))
            .Should().Be("appointment_cancelled_by_expert_second", "b: 2ª del EXPERTO finaliza");

        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_cancelled_by_expert_second");
        await AssertCountersAsync(db, hire.Id, 0, 1, 2,
            "b: ExpertCancel=2 (finaliza), ClientCancel=1 NO contribuyó");

        var (cli, exp, _) = await LoadSplitAsync(db, "appointment_cancelled_by_expert_second", "AppointmentStatus");
        (cli, exp).Should().Be((100m, 0m), "2ª cancelación del experto reembolsa 100% al cliente");
        await AssertLedgerAsync(db, hire.Id, 100m, cli, exp, "CC-05b expert_second");
    }

    [Fact(DisplayName = "CC-05c · cadena larga → appointment_cancelled_by_expert_rejection (100/0/0)")]
    public async Task CC05c_long_chain_terminal_expert_rejection()
    {
        await using var db = NewDbContext();
        var a = await SeedActorsAsync(db, "05c");

        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, a.ClientId, a.ExpertUserId, a.ServiceId, 100m, "pi_cc05c_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // reject(1ª) → repropose → confirm → cancelCliente(1ª) → repropose → confirm →
        // cancelExperto(1ª) → repropose → reject(2ª) [FINALIZA expert_rejection]
        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(3));
        (await MarketplaceFlowSimulator.ExpertRejectsAppointmentAsync(db, hire.Id, a.ExpertUserId))
            .Should().Be("appointment_rejected");
        await AssertCountersAsync(db, hire.Id, 1, 0, 0, "c:reject1");

        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(4));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        (await MarketplaceFlowSimulator.CancelByClientAsync(db, hire.Id, a.ClientId))
            .Should().Be("appointment_cancelled_by_client");
        await AssertCountersAsync(db, hire.Id, 1, 1, 0, "c:cancelCli1");

        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(5));
        await MarketplaceFlowSimulator.ExpertConfirmsAppointmentAsync(db, hire.Id, a.ExpertUserId);
        (await MarketplaceFlowSimulator.CancelByExpertAsync(db, hire.Id, a.ExpertUserId))
            .Should().Be("appointment_cancelled_by_expert");
        (await HireStatusAsync(db, hire.Id)).Should().Be("pending", "c:cancelExp1 no finaliza");
        await AssertCountersAsync(db, hire.Id, 1, 1, 1, "c:cancelExp1");

        await MarketplaceFlowSimulator.ClientProposesAppointmentAsync(db, hire.Id, a.ClientId, DateTime.UtcNow.AddDays(6));
        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_proposed", "c:repropose final");
        (await MarketplaceFlowSimulator.ExpertRejectsAppointmentAsync(db, hire.Id, a.ExpertUserId))
            .Should().Be("appointment_cancelled_by_expert_rejection",
                "c: RejectionCount>=1 finaliza pese a cancelaciones de ambos actores intercaladas");

        (await ApptStatusAsync(db, hire.Id)).Should().Be("appointment_cancelled_by_expert_rejection");
        await AssertCountersAsync(db, hire.Id, 2, 1, 2,
            "c: Rejection=2 (finaliza), ClientCancel=1, ExpertCancel=2 (1 cancel + el 2º rechazo que también lo incrementa)");

        var (cli, exp, _) = await LoadSplitAsync(db, "cancelled", "SearchHireStatus");
        (cli, exp).Should().Be((100m, 0m));
        await AssertLedgerAsync(db, hire.Id, 100m, cli, exp, "CC-05c expert_rejection");
    }
}
