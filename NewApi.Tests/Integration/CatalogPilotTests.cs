using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;

namespace NewApi.Tests.Integration;

/// <summary>
/// Tests piloto que implementan los 5 flujos críticos del FLOWS_CATALOG
/// directamente contra la BD del testcontainer. NO arrancan el host completo
/// (Program.cs tiene 3052 líneas, Google Secret Manager, Stripe init…) — en su
/// lugar verifican la verdad estructural y de comportamiento que cualquier
/// implementación válida del servicio TIENE que respetar:
///
///   HIRE-002 → idempotencia ProcessedWebhookEvent
///   APPT-006 → state machine: 2ª cancelación cliente penaliza 0/95/5
///   APPT-013 → state machine: timer response → 100/0/0
///   DISP-007 → state machine: dispute_resolved_client → 90/8/2
///   EDGE-001 → pg_advisory_xact_lock serializa transacciones concurrentes
///
/// Cuando llegue Fase 4 y arranquemos el host real, estos tests siguen siendo
/// válidos como red de seguridad de la fuente de verdad (seed).
/// </summary>
public class CatalogPilotTests : IntegrationTestBase
{
    public CatalogPilotTests(PostgresContainerFixture fixture) : base(fixture) { }

    // ─────────────────────────────────────────────────────────────────────────
    // HIRE-002 · Idempotencia ProcessedWebhookEvent
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "HIRE-002 · ProcessedWebhookEvent rechaza el mismo EventId 2× con UniqueViolation")]
    public async Task ProcessedWebhookEvent_unique_eventId_blocks_duplicates()
    {
        await using var db = NewDbContext();

        var eventId = "evt_test_hire002_" + Guid.NewGuid().ToString("N");

        // 1ª inserción: webhook procesado por primera vez
        db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
        {
            EventId = eventId,
            EventType = "checkout.session.completed",
            Status = "Success",
            ProcessedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // 2ª inserción con el MISMO EventId: debe fallar por unique constraint
        // (lo que el código real hace en TryBeginProcessingEventAsync para garantizar
        // que cada webhook se procesa exactamente una vez).
        await using var db2 = NewDbContext();
        db2.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
        {
            EventId = eventId, // ← duplicado
            EventType = "checkout.session.completed",
            Status = "Success",
            ProcessedAt = DateTime.UtcNow,
        });

        var act = async () => await db2.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>(
            "el unique index sobre EventId garantiza idempotencia atómica");

        // Verificación: solo hay 1 fila para ese EventId
        await using var verify = NewDbContext();
        var count = await verify.ProcessedWebhookEvents.CountAsync(e => e.EventId == eventId);
        count.Should().Be(1, "tras el rechazo del 2º insert solo debe existir el original");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // APPT-006 · 2ª cancelación cliente → 0/95/5 + estado final
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "APPT-006 · Seed: appointment_cancelled_by_client_second finaliza y reparte 0/95/5")]
    public async Task Second_client_cancellation_status_finalizes_and_splits_0_95_5()
    {
        await using var db = NewDbContext();

        // (1) El estado de 2ª cancelación es FINAL (a diferencia de la 1ª, que no)
        var first = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" &&
            s.StatusValue == "appointment_cancelled_by_client");
        first.IsFinalizationStatus.Should().BeFalse(
            "1ª cancelación NO finaliza el hire — el cliente puede reagendar");

        var second = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" &&
            s.StatusValue == "appointment_cancelled_by_client_second");
        second.IsFinalizationStatus.Should().BeTrue(
            "2ª cancelación SÍ finaliza el hire (penalización MUD-AI)");

        // (2) Split de dinero exacto 0/95/5 (cliente penalizado, experto cobra)
        var config = await db.StatusConfigurations
            .Where(c => c.StatusId == second.Id && c.CategoryId == null && c.ServiceTypeCategoryId == null)
            .SingleAsync();
        config.ClientPercentage.Should().Be(0);
        config.ExpertPercentage.Should().Be(95);
        config.PlatformPercentage.Should().Be(5);
        config.IsValid.Should().BeTrue("client+expert+platform = 100");

        // (3) StatusMapping a SearchHire.cancelled DEBE estar ACTIVO para 2ª cancelación
        var cancelledHireStatus = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "cancelled");

        var mappingSecond = await db.StatusMappings.SingleOrDefaultAsync(m =>
            m.SourceStatusId == second.Id && m.TargetStatusId == cancelledHireStatus.Id);
        mappingSecond.Should().NotBeNull("debe haber mapping para que el hire pase a cancelled");
        mappingSecond!.IsActive.Should().BeTrue();

        // (4) StatusMapping para 1ª cancelación → cancelled DEBE estar INACTIVO
        var mappingFirst = await db.StatusMappings.SingleOrDefaultAsync(m =>
            m.SourceStatusId == first.Id && m.TargetStatusId == cancelledHireStatus.Id);
        if (mappingFirst is not null)
        {
            mappingFirst.IsActive.Should().BeFalse(
                "1ª cancelación NO debe propagar a SearchHire.cancelled (permite reagendar)");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // APPT-013 · Timer response (experto no responde 24h) → 100/0/0
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "APPT-013 · SearchHireStatus.cancelled_by_expert_no_response → 100/0/0 (reembolso completo)")]
    public async Task Expert_no_response_timer_refunds_client_fully()
    {
        await using var db = NewDbContext();

        // Realidad verificada vía MCP Postgres: el split 100/0/0 está cableado a
        // nivel SearchHireStatus, no AppointmentStatus. El flujo es:
        //   1. Timer "response" expira
        //   2. Appointment.StatusId pasa a un estado "no_response"
        //   3. El servicio actualiza SearchHire.StatusId = cancelled_by_expert_no_response
        //   4. ProcessMoneyDistributionAsync lee la config de ESE status final → 100/0/0
        var hireStatus = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" &&
            s.StatusValue == "cancelled_by_expert_no_response");

        hireStatus.IsFinalizationStatus.Should().BeTrue(
            "es un estado terminal — el hire ya no transita");

        var config = await db.StatusConfigurations
            .Where(c => c.StatusId == hireStatus.Id &&
                        c.CategoryId == null &&
                        c.ServiceTypeCategoryId == null)
            .SingleAsync();
        config.ClientPercentage.Should().Be(100, "experto incumple → cliente recupera todo");
        config.ExpertPercentage.Should().Be(0);
        config.PlatformPercentage.Should().Be(0);
        config.IsActive.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DISP-007 · Admin resuelve dispute pro-cliente → 90/8/2
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "DISP-007 · Seed: dispute_resolved_client → 90/8/2 (cliente mayoría, experto 8% por intento)")]
    public async Task Dispute_resolved_client_splits_90_8_2()
    {
        await using var db = NewDbContext();

        var status = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "dispute_resolved_client");
        status.IsFinalizationStatus.Should().BeTrue();

        var config = await db.StatusConfigurations
            .Where(c => c.StatusId == status.Id && c.CategoryId == null && c.ServiceTypeCategoryId == null)
            .SingleAsync();

        // Verdad documentada en el catálogo del Agente D: NO es un refund total,
        // el experto recibe 8% "por haber intentado". Si alguien lo cambia a
        // 100/0/0, este test rompe y obliga a revisar el FLOWS_CATALOG.
        config.ClientPercentage.Should().Be(90);
        config.ExpertPercentage.Should().Be(8);
        config.PlatformPercentage.Should().Be(2);
        config.IsValid.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EDGE-001 · pg_advisory_xact_lock serializa transacciones concurrentes
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "EDGE-001 · pg_advisory_xact_lock(hireId) bloquea la 2ª tx hasta el COMMIT de la 1ª")]
    public async Task Advisory_xact_lock_serializes_concurrent_transactions()
    {
        // Setup: necesitamos un hire al que apuntar el lock (PG locks usan int8/int4)
        await using (var seed = NewDbContext())
        {
            var client = await new UserBuilder("edge1-client@test.dev").AsClient().PersistAsync(seed);
            var expertUser = await new UserBuilder("edge1-expert@test.dev").AsExpert().PersistAsync(seed);
            var expertProfile = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(seed);
            var svc = await new SearchServiceBuilder(expertProfile.Id).PersistAsync(seed);
            var hire = await new SearchHireBuilder()
                .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
                .WithAmount(100m).PersistAsync(seed);

            // Compartimos el id por closure (PG advisory locks usan bigint)
            _hireIdForLockTest = hire.Id;
        }

        var lockAcquired = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var secondAcquiredBeforeRelease = false;

        // Tx A: adquiere lock, señaliza, espera a soltarlo
        var taskA = Task.Run(async () =>
        {
            await using var dbA = NewDbContext();
            await using var txA = await dbA.Database.BeginTransactionAsync();
#pragma warning disable EF1002 // _hireIdForLockTest es int controlado por el test, no input externo
            await dbA.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({_hireIdForLockTest});");
#pragma warning restore EF1002
            lockAcquired.SetResult();

            // Mantener el lock 1s para dar tiempo a B a intentarlo y bloquearse
            await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await txA.CommitAsync();
        });

        // Tx B: espera a que A tenga el lock, luego intenta adquirir → debe BLOQUEAR
        var taskB = Task.Run(async () =>
        {
            await lockAcquired.Task;

            await using var dbB = NewDbContext();
            await using var txB = await dbB.Database.BeginTransactionAsync();

            var lockTask = Task.Run(async () =>
            {
#pragma warning disable EF1002
                await dbB.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({_hireIdForLockTest});");
#pragma warning restore EF1002
            });

            // Damos 500ms a B para intentarlo. Si B termina antes de soltar A,
            // el lock no estaba serializando → fallo.
            var winner = await Task.WhenAny(lockTask, Task.Delay(500));
            secondAcquiredBeforeRelease = (winner == lockTask);

            // Soltamos A y esperamos a que B acabe su adquisición
            releaseFirst.SetResult();
            await lockTask;
            await txB.CommitAsync();
        });

        await Task.WhenAll(taskA, taskB);

        secondAcquiredBeforeRelease.Should().BeFalse(
            "pg_advisory_xact_lock debe BLOQUEAR la 2ª transacción mientras la 1ª retiene el lock — " +
            "este invariante es lo que impide doble distribución de dinero (MUD-AP FIX)");
    }

    // Variable compartida entre tareas Task.Run del test EDGE-001
    private int _hireIdForLockTest;
}
