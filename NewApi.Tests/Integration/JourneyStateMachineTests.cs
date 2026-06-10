using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using NewApi.Tests.Simulators;
using Xunit;
using SM = NewApi.Tests.Simulators.MarketplaceFlowSimulator.StateMachine;

namespace NewApi.Tests.Integration;

/// <summary>
/// SM · Model-based state-machine testing del lifecycle del Appointment.
///
/// Construye un MODELO PURO de las transiciones válidas (verificado contra
/// AppointmentService.cs — ver auditoría en MarketplaceFlowSimulator.StateMachine.cs),
/// genera con un walker DFS todas las secuencias VÁLIDAS que alcanzan un estado FINAL
/// hasta profundidad acotada, y las EJECUTA contra la BD del testcontainer verificando:
///
///   • SM-01: cada secuencia generada termina en el estado final que el modelo predice,
///            tanto en el Appointment como en el SearchHire.
///   • SM-02: ninguna transición INVÁLIDA (acción desde estado no permitido) se ejecuta
///            sin lanzar excepción — los guards del simulador la rechazan.
///   • SM-03: invariante de conservación de dinero — para cada terminal, la suma de las
///            FinancialTransactions (ServicePayment − Refund − Payout) = retención plataforma
///            según la StatusConfiguration, sin importes negativos.
///
/// Cobertura: el walker a profundidad 6 genera 14 secuencias terminales (ver SM-01).
/// Para mantener el test < 60s se ejecutan TODAS (son pocas y baratas: cada una es una
/// transacción corta contra Postgres).
/// </summary>
public class JourneyStateMachineTests : IntegrationTestBase
{
    public JourneyStateMachineTests(PostgresContainerFixture fixture) : base(fixture) { }

    private const int WalkerMaxDepth = 8;

    private record SeedActors(int ClientId, int ExpertUserId, int ExpertProfileId, int ServiceId);

    private async Task<SeedActors> SeedActorsAsync(AppDbContext db, string slug)
    {
        var client = await new UserBuilder($"sm-{slug}-cli@test.dev").AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder($"sm-{slug}-exp@test.dev").AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").PersistAsync(db);
        return new SeedActors(client.Id, expertUser.Id, expert.Id, svc.Id);
    }

    private async Task<string> CurrentApptStatusAsync(AppDbContext db, int hireId)
    {
        var appt = await db.Appointments.Include(a => a.Status).SingleAsync(a => a.SearchHireId == hireId);
        return appt.Status.StatusValue;
    }

    private async Task<string> CurrentHireStatusAsync(AppDbContext db, int hireId)
    {
        var hire = await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hireId);
        return hire.Status.StatusValue;
    }

    /// <summary>Ejecuta una secuencia completa de acciones del modelo contra la BD.</summary>
    private async Task<int> ExecuteSequenceAsync(
        AppDbContext db, SeedActors actors, string slug, IReadOnlyList<SM.Action> actions)
    {
        var hire = await MarketplaceFlowSimulator.SimulateCheckoutCompletedAsync(
            db, actors.ClientId, actors.ExpertUserId, actors.ServiceId,
            amount: 100m, paymentIntentId: $"pi_sm_{slug}_" + Guid.NewGuid().ToString("N"));
        await MarketplaceFlowSimulator.AttachAppointmentAsync(db, hire.Id);

        // Cada propuesta usa una fecha futura distinta y creciente para no colisionar
        // y respetar el "≥24h de antelación" del flujo real.
        var baseDate = DateTime.UtcNow.AddDays(10);
        var proposeIndex = 0;

        foreach (var action in actions)
        {
            var proposedDate = baseDate.AddDays(proposeIndex * 3);
            if (action == SM.Action.Propose) proposeIndex++;

            await MarketplaceFlowSimulator.DispatchStateMachineActionAsync(
                db, hire.Id, actors.ClientId, actors.ExpertUserId, action, proposedDate);
        }

        return hire.Id;
    }

    private static string Slug(int i) => i.ToString("D2");

    // ─────────────────────────────────────────────────────────────────────────
    // SM-01 · El walker genera N secuencias válidas y TODAS terminan en el estado
    //         final consistente con el modelo (Appointment + SearchHire).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "SM-01 · Walker model-based: toda secuencia válida llega a un terminal consistente")]
    public async Task Walker_all_valid_sequences_reach_modeled_terminal()
    {
        var sequences = SM.EnumerateTerminalSequences(WalkerMaxDepth);

        // El modelo debe producir un conjunto no trivial de terminales (sanity del walker).
        sequences.Should().NotBeEmpty("el walker debe generar secuencias que alcancen terminales");
        sequences.Should().HaveCountGreaterThanOrEqualTo(6,
            "a profundidad 8 hay múltiples caminos (happy path, reject×2, cancel×2 por actor, etc.)");

        // Todos los 4 terminales del modelo deben estar cubiertos por alguna secuencia.
        var coveredTerminals = sequences.Select(s => s.Final.ApptStatus).Distinct().ToHashSet();
        coveredTerminals.Should().BeEquivalentTo(SM.TerminalStates,
            "el walker debe cubrir los 4 estados finales alcanzables del modelo");

        await using var db = NewDbContext();

        for (int i = 0; i < sequences.Count; i++)
        {
            var seq = sequences[i];
            var actors = await SeedActorsAsync(db, $"01-{Slug(i)}");
            var hireId = await ExecuteSequenceAsync(db, actors, $"01{Slug(i)}", seq.Actions);

            var actualAppt = await CurrentApptStatusAsync(db, hireId);
            var actualHire = await CurrentHireStatusAsync(db, hireId);

            var path = string.Join(" → ", seq.Actions);
            actualAppt.Should().Be(seq.Final.ApptStatus,
                $"secuencia #{i} [{path}] debe dejar el Appointment en el terminal predicho por el modelo");
            // El SearchHire se asierta contra el comportamiento REAL del backend
            // (ExpectedHireStatus): en las cancelaciones terminales el hire mapea a
            // 'cancelled' (StatusMapping activo). El simulador ahora aplica esa transición
            // (FinalizeHireViaMappingAsync), por lo que la divergencia previa quedó cerrada.
            actualHire.Should().Be(SM.ExpectedHireStatus(seq.Final.ApptStatus),
                $"secuencia #{i} [{path}] debe dejar el SearchHire en el estado final REAL");

            // No deben quedar timers activos en un terminal.
            var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hireId);
            var activeTimers = await db.AppointmentTimers
                .Where(t => t.AppointmentId == appt.Id && !t.IsExpired)
                .ToListAsync();
            activeTimers.Should().BeEmpty(
                $"secuencia #{i} [{path}] no debe dejar timers activos en un estado final");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SM-02 · Ninguna transición INVÁLIDA se ejecuta sin lanzar excepción.
    //         Verifica los guards del simulador (propose/confirm/reject/submit) que
    //         el código real implementa. (Cancel se excluye: el simulador no valida
    //         estado de origen — divergencia reportada en la auditoría.)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "SM-02 · Los guards rechazan toda transición inválida con excepción")]
    public async Task Invalid_transitions_are_rejected_by_guards()
    {
        var invalidCases = SM.EnumerateGuardedInvalidTransitions();
        invalidCases.Should().NotBeEmpty("debe haber transiciones inválidas que probar");

        await using var db = NewDbContext();

        for (int i = 0; i < invalidCases.Count; i++)
        {
            var (from, action) = invalidCases[i];
            var actors = await SeedActorsAsync(db, $"02-{Slug(i)}");

            // Llevamos el appointment al estado de origen ejecutando un prefijo VÁLIDO.
            var hireId = await DriveToStateAsync(db, actors, $"02{Slug(i)}", from.ApptStatus);

            var act = async () => await MarketplaceFlowSimulator.DispatchStateMachineActionAsync(
                db, hireId, actors.ClientId, actors.ExpertUserId, action,
                DateTime.UtcNow.AddDays(30));

            await act.Should().ThrowAsync<InvalidOperationException>(
                $"acción {action} desde '{from.ApptStatus}' debe ser rechazada por el guard del simulador");

            // El estado NO debe haber cambiado tras el rechazo.
            (await CurrentApptStatusAsync(db, hireId)).Should().Be(from.ApptStatus,
                $"un rechazo no debe mutar el estado del appointment ({action} desde {from.ApptStatus})");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SM-03 · Invariante de conservación de dinero para cada terminal alcanzable.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "SM-03 · Conservación: ServicePayment = Refund + Payout + retención plataforma, sin negativos")]
    public async Task Money_conservation_holds_for_every_terminal()
    {
        // Una secuencia representativa por terminal (la más corta encontrada por el walker).
        var byTerminal = SM.EnumerateTerminalSequences(WalkerMaxDepth)
            .GroupBy(s => s.Final.ApptStatus)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Actions.Count).First());

        byTerminal.Keys.Should().BeEquivalentTo(SM.TerminalStates,
            "debe haber al menos una secuencia por cada terminal");

        await using var db = NewDbContext();
        const decimal original = 100m;

        int idx = 0;
        foreach (var (terminal, seq) in byTerminal)
        {
            var actors = await SeedActorsAsync(db, $"03-{Slug(idx)}");
            var hireId = await ExecuteSequenceAsync(db, actors, $"03{Slug(idx)}", seq.Actions);
            idx++;

            (await CurrentApptStatusAsync(db, hireId)).Should().Be(terminal);

            var (cp, ep, pp) = await LoadSplitAsync(db, SM.MoneyStatusValue(terminal));
            (cp + ep + pp).Should().Be(100m, $"{terminal}: los porcentajes de la config deben sumar 100");

            var fts = await db.FinancialTransactions
                .Where(ft => ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hireId)
                .ToListAsync();

            var servicePayment = fts.Single(ft => ft.TransactionType == "ServicePayment");
            servicePayment.Amount.Should().Be(original);

            var expectedRefund = Math.Round(original * cp / 100m, 2);
            var expectedPayout = Math.Round(original * ep / 100m, 2);

            var refund = fts.SingleOrDefault(ft => ft.TransactionType == "Refund");
            var payout = fts.SingleOrDefault(ft => ft.TransactionType == "Payout");

            (refund?.Amount ?? 0m).Should().Be(expectedRefund, $"{terminal}: Refund = {cp}%");
            (payout?.Amount ?? 0m).Should().Be(expectedPayout, $"{terminal}: Payout = {ep}%");

            // CONSERVACIÓN: el dinero que sale (refund+payout) + residuo plataforma = ingreso.
            var platformResidue = servicePayment.Amount - (refund?.Amount ?? 0m) - (payout?.Amount ?? 0m);
            var expectedPlatform = Math.Round(original * pp / 100m, 2);
            platformResidue.Should().Be(expectedPlatform,
                $"{terminal}: residuo plataforma = {pp}% (conservación del importe original)");

            fts.Should().OnlyContain(ft => ft.Amount >= 0, $"{terminal}: ninguna FT puede ser negativa");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Resuelve la StatusConfiguration efectiva de un statusValue replicando el fallback del
    /// core ProcessMoneyDistributionAsync: config directa y, si el AppointmentStatus no la
    /// tiene (p.ej. appointment_cancelled_by_expert_rejection), vía el StatusMapping ACTIVO.
    /// </summary>
    private async Task<(decimal client, decimal expert, decimal platform)> LoadSplitAsync(
        AppDbContext db, string statusValue)
    {
        var status = await db.SystemStatuses
            .Where(s => s.StatusValue == statusValue)
            .OrderBy(s => s.StatusType == "AppointmentStatus" ? 0 : 1)
            .FirstAsync();

        var cfg = await db.StatusConfigurations.SingleOrDefaultAsync(c =>
            c.StatusId == status.Id && c.CategoryId == null && c.ServiceTypeCategoryId == null);

        if (cfg is null && status.StatusType == "AppointmentStatus")
        {
            cfg = await (
                from m in db.StatusMappings
                join sc in db.StatusConfigurations on m.TargetStatusId equals sc.StatusId
                where m.SourceStatusId == status.Id && m.IsActive
                      && sc.CategoryId == null && sc.ServiceTypeCategoryId == null
                select sc).FirstOrDefaultAsync();
        }

        cfg.Should().NotBeNull($"debe existir config (directa o vía mapping activo) para '{statusValue}'");
        return (cfg!.ClientPercentage, cfg.ExpertPercentage, cfg.PlatformPercentage);
    }

    /// <summary>
    /// Conduce un appointment recién creado hasta <paramref name="targetState"/> usando un
    /// prefijo de acciones VÁLIDAS (camino canónico del modelo). Devuelve el hireId.
    /// </summary>
    private async Task<int> DriveToStateAsync(
        AppDbContext db, SeedActors actors, string slug, string targetState)
    {
        var prefix = CanonicalPathTo(targetState);
        return await ExecuteSequenceAsync(db, actors, slug, prefix);
    }

    /// <summary>Camino canónico (válido) más corto para llegar a un estado NO terminal.</summary>
    private static IReadOnlyList<SM.Action> CanonicalPathTo(string state) => state switch
    {
        SM.AwaitingAppointment => Array.Empty<SM.Action>(),
        SM.Proposed => new[] { SM.Action.Propose },
        SM.Confirmed => new[] { SM.Action.Propose, SM.Action.Confirm },
        SM.Rejected => new[] { SM.Action.Propose, SM.Action.Reject },
        SM.AwaitingReport => new[] { SM.Action.Propose, SM.Action.Confirm, SM.Action.AdvanceToAwaitingReport },
        SM.ReportSent => new[]
        {
            SM.Action.Propose, SM.Action.Confirm, SM.Action.AdvanceToAwaitingReport, SM.Action.SubmitReport,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Sin camino canónico definido"),
    };
}
