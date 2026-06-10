using Microsoft.EntityFrameworkCore;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using NewApi.Tests.Simulators;
using NewApi.Tests.StripeMocks;

namespace NewApi.Tests.Integration;

/// <summary>
/// Tests de SEGURIDAD del manejo de webhooks de Stripe Connect (Agente 3).
///
/// Cubre las defensas estructurales que NO requieren arrancar el host HTTP:
///   - Validación de firma (base HMAC-SHA256 estilo Stripe).
///   - Idempotencia atómica vía índice único en ProcessedWebhookEvent.EventId.
///   - Race de entregas concurrentes con el mismo EventId.
///   - Re-claim de eventos "Failed".
///   - Árbol de decisión EvaluateStripeAccount (degradado de Approved cuando hay
///     future_requirements.errors[] — Round 29 FIX-DOC-FAIL).
///   - Limpieza del perfil en deauthorization.
///   - Idempotencia por EventId (no por tipo de evento).
///
/// La lógica pura de clasificación se replica en
/// <see cref="MarketplaceFlowSimulator.EvaluateStripeAccountReplica"/>, verificada
/// fielmente contra SubscriptionController.EvaluateStripeAccount (cs:251-533).
/// </summary>
public class WebhookSecurityTests : IntegrationTestBase
{
    public WebhookSecurityTests(PostgresContainerFixture fixture) : base(fixture) { }

    // ─────────────────────────────────────────────────────────────────────────
    // WHS-01 · Firma válida vs inválida (base de la validación de firma)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "WHS-01 · Firma válida produce header t=<num>,v1=<hex64>; secret distinto → hash distinto")]
    public void Valid_signature_shape_and_wrong_secret_differs()
    {
        var payload = StripeEventBuilder.AccountUpdatedApproved("evt_whs01", "acct_whs01");

        const string realSecret = "whsec_connect_real";
        const string attackerSecret = "whsec_attacker_guess";

        var validHeader = StripeWebhookSigner.SignHeader(payload, realSecret, DateTimeOffset.UtcNow);
        var forgedHeader = StripeWebhookSigner.SignHeader(payload, attackerSecret, DateTimeOffset.UtcNow);

        // Shape: t=<unix>,v1=<hmac-sha256 hex 64 chars>
        validHeader.Should().MatchRegex(@"^t=\d+,v1=[0-9a-f]{64}$");

        // El v1 firmado con el secret correcto NO debe coincidir con el del atacante:
        // esa diferencia es exactamente lo que ConstructEvent detecta y rechaza con 400.
        var validV1 = validHeader.Split("v1=")[1];
        var forgedV1 = forgedHeader.Split("v1=")[1];
        validV1.Should().NotBe(forgedV1,
            "un payload firmado con un secret distinto produce un HMAC distinto → firma inválida");

        // El mismo payload + mismo secret + mismo timestamp es determinista (verificación reproducible).
        var when = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        StripeWebhookSigner.SignHeader(payload, realSecret, when)
            .Should().Be(StripeWebhookSigner.SignHeader(payload, realSecret, when));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WHS-02 · UNIQUE EventId bloquea duplicados (idempotencia a nivel BD)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "WHS-02 · ProcessedWebhookEvent UNIQUE(EventId) → 2º insert del mismo evt_id lanza DbUpdateException")]
    public async Task Duplicate_event_id_violates_unique_index()
    {
        const string eventId = "evt_dup_whs02";

        await using (var db = NewDbContext())
        {
            db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
            {
                EventId = eventId,
                EventType = "account.updated",
                Status = "Success",
                ProcessedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDbContext())
        {
            db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
            {
                EventId = eventId, // mismo evt_id → choca con el índice único
                EventType = "account.updated",
                Status = "Success",
                ProcessedAt = DateTime.UtcNow,
            });

            var act = async () => await db.SaveChangesAsync();
            (await act.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<Npgsql.PostgresException>()
                .Which.SqlState.Should().Be("23505", "violación de índice único de PostgreSQL");
        }

        await using (var verify = NewDbContext())
        {
            (await verify.ProcessedWebhookEvents.CountAsync(e => e.EventId == eventId))
                .Should().Be(1, "solo la primera entrega debe persistir");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WHS-03 · Race de N entregas concurrentes → 1 sola fila persiste
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "WHS-03 · N entregas concurrentes con mismo EventId → solo 1 fila (índice único es el cerrojo)")]
    public async Task Concurrent_inserts_same_event_id_only_one_wins()
    {
        const string eventId = "evt_race_whs03";
        const int concurrency = 12;

        // Cada "entrega" usa su propio DbContext (como cada request del webhook).
        var deliveries = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            await using var db = NewDbContext();
            db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
            {
                EventId = eventId,
                EventType = "account.updated",
                Status = "Processing",
                ProcessedAt = DateTime.UtcNow,
            });
            try
            {
                await db.SaveChangesAsync();
                return true; // ganó el claim
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505")
            {
                return false; // otra entrega ganó la carrera
            }
        });

        var results = await Task.WhenAll(deliveries);

        results.Count(won => won).Should().Be(1,
            "el índice único garantiza que solo una de las N entregas concurrentes reclama el evento");

        await using var verify = NewDbContext();
        (await verify.ProcessedWebhookEvents.CountAsync(e => e.EventId == eventId))
            .Should().Be(1, "nunca debe persistir más de una fila para el mismo EventId");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WHS-04 · Evento "Failed" puede re-reclamarse y avanzar a "Success"
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "WHS-04 · Evento Failed se re-reclama (re-claim) y transiciona a Success sin violar el unique")]
    public async Task Failed_event_can_be_reclaimed_and_succeed()
    {
        const string eventId = "evt_failed_whs04";

        // 1er intento: el handler falló (p.ej. StripeException transitoria → 500 → Stripe reintenta).
        await using (var db = NewDbContext())
        {
            db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
            {
                EventId = eventId,
                EventType = "account.updated",
                Status = "Failed",
                ErrorMessage = "transient db lock timeout",
                ProcessedAt = DateTime.UtcNow.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        // Reintento de Stripe: TryBeginProcessingEventAsync encuentra la fila Failed y la re-reclama
        // (UPDATE a Processing → al terminar, Success). NO inserta una fila nueva.
        await using (var db = NewDbContext())
        {
            var existing = await db.ProcessedWebhookEvents.SingleAsync(e => e.EventId == eventId);
            existing.Status.Should().Be("Failed", "estado de partida tras el primer fallo");

            // re-claim
            existing.Status = "Processing";
            existing.ErrorMessage = null;
            existing.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            // procesamiento exitoso en el reintento
            existing.Status = "Success";
            await db.SaveChangesAsync();
        }

        await using (var verify = NewDbContext())
        {
            var rows = await verify.ProcessedWebhookEvents.Where(e => e.EventId == eventId).ToListAsync();
            rows.Should().HaveCount(1, "el re-claim actualiza la fila existente, no crea duplicados");
            rows[0].Status.Should().Be("Success");
            rows[0].ErrorMessage.Should().BeNull();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WHS-05 · future_requirements.errors[] poblado NO debe dar Approved (FIX-DOC-FAIL)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "WHS-05 · charges+payouts enabled PERO future_requirements.errors[] poblado → NO Approved (degrada a ActionRequired)")]
    public void FutureRequirementsErrors_degrades_approved()
    {
        // Replica el caso del fixture AccountUpdatedFutureRequirementsDocFailed:
        // cuenta "active" (charges/payouts enabled) pero con error de documento en el
        // bucket future_requirements.errors[] + deadline futuro.
        var account = new MarketplaceFlowSimulator.StripeAccountSnapshot
        {
            ChargesEnabled = true,
            PayoutsEnabled = true,
            TransfersActive = true,
            DetailsSubmitted = true,
            TosAccepted = true,
            DisabledReason = null,
            FutureCurrentlyDue = new() { "individual.verification.document" },
            FutureRequirementsErrors = new() { "verification_document_failed" },
            CurrentDeadline = null, // el deadline vive en future_requirements en este caso
        };

        var (status, completed) = MarketplaceFlowSimulator.EvaluateStripeAccountReplica(account);

        status.Should().NotBe(StripeStatus.Approved,
            "VULN evitada: una cuenta con future_requirements.errors[] poblado NO puede quedar Approved");
        completed.Should().BeFalse("hay acción pendiente del experto → onboarding no completo");
        // future_currently_due lleva la cuenta a RestrictedSoon; el bucle de errors la mantiene degradada.
        status.Should().BeOneOf(StripeStatus.RestrictedSoon, StripeStatus.ActionRequired);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WHS-06 · Caso feliz: charges+payouts enabled sin errores → Approved
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "WHS-06 · charges+payouts enabled, sin requisitos ni errores → Approved + onboarding completo")]
    public void CleanAccount_is_approved()
    {
        var account = new MarketplaceFlowSimulator.StripeAccountSnapshot
        {
            ChargesEnabled = true,
            PayoutsEnabled = true,
            TransfersActive = true,
            DetailsSubmitted = true,
            TosAccepted = true,
            DisabledReason = null,
            // todas las listas vacías por defecto + sin deadline
        };

        var (status, completed) = MarketplaceFlowSimulator.EvaluateStripeAccountReplica(account);

        status.Should().Be(StripeStatus.Approved);
        completed.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WHS-07 · Deauthorization limpia StripeAccountId y bloquea
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "WHS-07 · account.application.deauthorized limpia StripeAccountId/Pending y marca Deauthorized")]
    public async Task Deauthorization_clears_account_and_blocks()
    {
        await using var db = NewDbContext();

        var expertUser = await new UserBuilder("whs07-expert@test.dev").AsExpert().Verified().PersistAsync(db);
        var profile = await new ExpertProfileBuilder(expertUser.Id)
            .Approved()
            .WithStripeAccountId("acct_whs07")
            .PersistAsync(db);

        profile.StripeAccountId.Should().Be("acct_whs07");
        profile.StripeStatus.Should().Be(StripeStatus.Approved);

        // Aplicar la limpieza que hace el handler real de deauthorization.
        MarketplaceFlowSimulator.ApplyDeauthorizationReplica(profile);
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var reloaded = await verify.ExpertProfiles.SingleAsync(p => p.Id == profile.Id);
        reloaded.StripeStatus.Should().Be(StripeStatus.Deauthorized);
        reloaded.OnboardingCompleted.Should().BeFalse();
        reloaded.StripeAccountId.Should().BeNull("Stripe recomienda desvincular por completo la cuenta");
        reloaded.PendingStripeAccountId.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WHS-08 · Idempotencia por EventId, no por tipo de evento
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "WHS-08 · Distintos EventTypes con EventIds distintos coexisten; la idempotencia es por EventId")]
    public async Task Idempotency_is_per_event_id_not_per_type()
    {
        var events = new (string Id, string Type)[]
        {
            ("evt_whs08_acct",   "account.updated"),
            ("evt_whs08_refund", "charge.refunded"),
            ("evt_whs08_tfail",  "transfer.failed"),
            ("evt_whs08_deauth", "account.application.deauthorized"),
        };

        await using (var db = NewDbContext())
        {
            foreach (var (id, type) in events)
            {
                db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
                {
                    EventId = id,
                    EventType = type,
                    Status = "Success",
                    ProcessedAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        // Las 4 filas (distinto EventId) deben coexistir aunque dos compartan, p.ej.,
        // que sean del mismo account: la clave de idempotencia es el EventId.
        await using (var verify = NewDbContext())
        {
            (await verify.ProcessedWebhookEvents.CountAsync()).Should().Be(4);
            (await verify.ProcessedWebhookEvents.Select(e => e.EventType).Distinct().CountAsync())
                .Should().Be(4, "los 4 tipos distintos persisten");
        }

        // Reentrega de UNO de ellos (mismo EventId) → choca con el unique, no duplica.
        await using (var db = NewDbContext())
        {
            db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
            {
                EventId = "evt_whs08_refund", // mismo EventId que ya existe
                EventType = "charge.refunded",
                Status = "Success",
                ProcessedAt = DateTime.UtcNow,
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using (var verify = NewDbContext())
        {
            (await verify.ProcessedWebhookEvents.CountAsync()).Should().Be(4,
                "la reentrega del mismo EventId no añade filas");
        }
    }
}
