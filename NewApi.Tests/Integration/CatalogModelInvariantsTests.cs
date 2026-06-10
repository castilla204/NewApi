using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;

namespace NewApi.Tests.Integration;

/// <summary>
/// Tercera tanda (Fase 3b · cobertura de invariantes de modelo).
///
/// Cubre:
///   - StripeStatus enum (11 estados Connect documentados por Agente A)
///   - ExpertProfile invariantes: StripeAccountId nullable, vacación, deauthorization
///   - GDPR soft-delete: User.IsDeleted filtra automáticamente, SearchHire admite
///     ClientId/ExpertId = null (anonimización tras borrado)
///   - ProcessedWebhookEvent admite EventTypes distintos del catálogo Stripe
///   - AppointmentTimer admite los 4 tipos del catálogo + Hangfire job id
///
/// Estos tests aseguran que el modelo C# sigue sirviendo la arquitectura del
/// marketplace: una regresión que introduzca un null o cambie el filtro hace
/// que uno de estos tests reviente con un mensaje claro.
/// </summary>
public class CatalogModelInvariantsTests : IntegrationTestBase
{
    public CatalogModelInvariantsTests(PostgresContainerFixture fixture) : base(fixture) { }

    // ─────────────────────────────────────────────────────────────────────────
    // Stripe Connect — los 11 estados documentados son persistibles
    // ─────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllStripeStatuses => new[]
    {
        new object[] { StripeStatus.NotRequested },
        new object[] { StripeStatus.Pending },
        new object[] { StripeStatus.Approved },
        new object[] { StripeStatus.Rejected },
        new object[] { StripeStatus.Deauthorized },
        new object[] { StripeStatus.ActionRequired },
        new object[] { StripeStatus.PendingVerification },
        new object[] { StripeStatus.RequirementsDue },
        new object[] { StripeStatus.RequirementsPastDue },
        new object[] { StripeStatus.RestrictedSoon },
        new object[] { StripeStatus.Restricted },
        new object[] { StripeStatus.Disabled },
        new object[] { StripeStatus.UnderReview },
    };

    [Theory(DisplayName = "STRIPE · Los 11 (en realidad 13) estados Connect son persistibles y re-leíbles")]
    [MemberData(nameof(AllStripeStatuses))]
    public async Task StripeStatus_enum_roundtrip(StripeStatus status)
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder($"stripe-{status}@test.dev").AsExpert().PersistAsync(db);

        var profile = new ExpertProfile
        {
            UserId = user.Id,
            ProfilePictureUrl = "https://example.test/avatar.png",
            ProfilePictureObjectName = "avatar.png",
            Description = "X",
            Latitude = "40", Longitude = "-3",
            StripeStatus = status,
            StripeAccountId = status == StripeStatus.NotRequested ? null : $"acct_test_{status}".Substring(0, Math.Min(24, $"acct_test_{status}".Length)),
            OnboardingCompleted = status == StripeStatus.Approved,
            CreatedAt = DateTime.UtcNow,
        };
        db.ExpertProfiles.Add(profile);
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.ExpertProfiles.SingleAsync(p => p.Id == profile.Id);
        fromDb.StripeStatus.Should().Be(status);
    }

    [Fact(DisplayName = "STRIPE · ExpertProfile.StripeAccountId nullable cuando NotRequested")]
    public async Task NotRequested_expert_has_null_stripe_account()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("notreq@test.dev").AsExpert().PersistAsync(db);
        var profile = new ExpertProfile
        {
            UserId = user.Id,
            ProfilePictureUrl = "https://x.png", ProfilePictureObjectName = "x.png",
            Description = "X", Latitude = "40", Longitude = "-3",
            StripeStatus = StripeStatus.NotRequested,
            StripeAccountId = null,
            CreatedAt = DateTime.UtcNow,
        };
        db.ExpertProfiles.Add(profile);
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.ExpertProfiles.SingleAsync(p => p.Id == profile.Id);
        fromDb.StripeAccountId.Should().BeNull(
            "expertos sin onboarding iniciado no tienen cuenta Stripe");
        fromDb.OnboardingCompleted.Should().BeFalse();
    }

    [Fact(DisplayName = "STRIPE · Deauthorization conserva el historial: StripeAccountId puede pasar a null")]
    public async Task Deauthorized_expert_loses_stripe_account()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("deauth@test.dev").AsExpert().PersistAsync(db);
        var profile = await new ExpertProfileBuilder(user.Id).Approved().PersistAsync(db);

        profile.StripeAccountId.Should().NotBeNull();

        // Simulamos el evento `account.application.deauthorized`
        profile.StripeStatus = StripeStatus.Deauthorized;
        profile.StripeAccountId = null;
        profile.OnboardingCompleted = false;
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.ExpertProfiles.SingleAsync(p => p.Id == profile.Id);
        fromDb.StripeStatus.Should().Be(StripeStatus.Deauthorized);
        fromDb.StripeAccountId.Should().BeNull("el webhook desautoriza la cuenta y se borra la referencia");
        fromDb.OnboardingCompleted.Should().BeFalse();
    }

    [Fact(DisplayName = "STRIPE · IsOnVacation flag persiste y se inicializa false")]
    public async Task IsOnVacation_default_false_and_toggleable()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("vac@test.dev").AsExpert().PersistAsync(db);
        var profile = await new ExpertProfileBuilder(user.Id).Approved().PersistAsync(db);

        profile.IsOnVacation.Should().BeFalse("default = false según ExpertProfile.cs");

        profile.IsOnVacation = true;
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.ExpertProfiles.SingleAsync(p => p.Id == profile.Id);
        fromDb.IsOnVacation.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GDPR · Soft-delete + nullable FKs
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "GDPR · User.IsDeleted=true es filtrado automáticamente por el query filter")]
    public async Task Soft_deleted_user_is_invisible_to_default_queries()
    {
        await using var db = NewDbContext();
        var alive = await new UserBuilder("alive@test.dev").AsClient().PersistAsync(db);
        var deleted = await new UserBuilder("rip@test.dev").AsClient().SoftDeleted().PersistAsync(db);

        var defaultQuery = await db.Users.Where(u => u.Email.EndsWith("@test.dev")).ToListAsync();
        defaultQuery.Should().HaveCount(1, "el query filter excluye automáticamente IsDeleted=true");
        defaultQuery.Single().Id.Should().Be(alive.Id);

        // IgnoreQueryFilters permite acceder al borrado (para soporte/auditoría)
        var withDeleted = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Email.EndsWith("@test.dev")).ToListAsync();
        withDeleted.Should().HaveCount(2);
        withDeleted.Should().Contain(u => u.Id == deleted.Id);
    }

    [Fact(DisplayName = "GDPR · SearchHire admite ClientId/ExpertId = null (anonimización post-borrado)")]
    public async Task SearchHire_nullable_client_expert_for_anonymization()
    {
        await using var db = NewDbContext();
        var client = await new UserBuilder("anon-client@test.dev").AsClient().PersistAsync(db);
        var expertUser = await new UserBuilder("anon-expert@test.dev").AsExpert().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).PersistAsync(db);
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithAmount(100m).PersistAsync(db);

        // Anonimización: el FK se vacía pero el hire sobrevive con su histórico contable
        hire.ClientId = null;
        hire.ExpertId = null;
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.SearchHires.SingleAsync(h => h.Id == hire.Id);
        fromDb.ClientId.Should().BeNull("post-borrado de cuenta, el FK se rompe pero el hire persiste");
        fromDb.ExpertId.Should().BeNull();
        fromDb.SearchServiceId.Should().Be(svc.Id, "el FK al servicio permanece (FK no nullable)");
        fromDb.Amount.Should().Be(100m, "el histórico contable sobrevive a la anonimización");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ProcessedWebhookEvent · EventTypes del catálogo Stripe
    // ─────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> WebhookEventTypes => new[]
    {
        new object[] { "checkout.session.completed" },
        new object[] { "payment_intent.succeeded" },
        new object[] { "payment_intent.payment_failed" },
        new object[] { "charge.refunded" },
        new object[] { "charge.dispute.created" },
        new object[] { "charge.dispute.closed" },
        new object[] { "transfer.created" },
        new object[] { "transfer.failed" },
        new object[] { "account.updated" },
        new object[] { "account.application.deauthorized" },
    };

    [Theory(DisplayName = "WEBHOOK · Cada EventType del catálogo Stripe se persiste con su propio EventId")]
    [MemberData(nameof(WebhookEventTypes))]
    public async Task Different_event_types_are_persistable(string eventType)
    {
        await using var db = NewDbContext();
        var eventId = $"evt_{eventType.Replace('.', '_')}_{Guid.NewGuid():N}";

        db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
        {
            EventId = eventId,
            EventType = eventType,
            Status = "Success",
            ProcessedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.ProcessedWebhookEvents.SingleAsync(p => p.EventId == eventId);
        fromDb.EventType.Should().Be(eventType);
        fromDb.Status.Should().Be("Success");
    }

    [Fact(DisplayName = "WEBHOOK · ErrorMessage permite registrar fallos para reintento")]
    public async Task Failed_webhooks_record_error_message()
    {
        await using var db = NewDbContext();
        var eventId = "evt_fail_" + Guid.NewGuid().ToString("N");

        db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
        {
            EventId = eventId,
            EventType = "charge.dispute.created",
            Status = "Failed",
            ErrorMessage = "No SearchHire found for charge ch_xxx",
            ProcessedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.ProcessedWebhookEvents.SingleAsync(p => p.EventId == eventId);
        fromDb.Status.Should().Be("Failed");
        fromDb.ErrorMessage.Should().Contain("No SearchHire found");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AppointmentTimer · 4 tipos del catálogo
    // ─────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> TimerTypes => new[]
    {
        new object[] { "proposal",                     "Cliente propone fecha en 24h" },
        new object[] { "response",                     "Experto confirma propuesta en 24h" },
        new object[] { "expert_report",                "Experto sube reporte 24h post-cita" },
        new object[] { "client_decision",              "Cliente aprueba/disputa reporte en 24h" },
        new object[] { "awaiting_report_transition",   "Transición 3h post-cita a awaiting_report" },
    };

    [Theory(DisplayName = "TIMER · Cada uno de los 5 tipos del catálogo se persiste con su HangfireJobId")]
    [MemberData(nameof(TimerTypes))]
    public async Task AppointmentTimer_persists_each_type(string timerType, string description)
    {
        await using var db = NewDbContext();

        var client = await new UserBuilder($"timer-cli-{timerType}@test.dev").AsClient().PersistAsync(db);
        var expertUser = await new UserBuilder($"timer-exp-{timerType}@test.dev").AsExpert().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).PersistAsync(db);
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .PersistAsync(db);
        var appt = await new AppointmentBuilder(hire.Id).PersistAsync(db);

        var hangfireJobId = $"hangfire_{Guid.NewGuid():N}";
        var startTime = DateTime.UtcNow;
        var endTime = startTime.AddHours(24);

        db.AppointmentTimers.Add(new AppointmentTimer
        {
            AppointmentId = appt.Id,
            TimerType = timerType,
            StartTime = startTime,
            EndTime = endTime,
            IsExpired = false,
            HangfireJobId = hangfireJobId,
            CreatedAt = DateTime.UtcNow,
            Notes = description,
        });
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.AppointmentTimers.SingleAsync(t => t.HangfireJobId == hangfireJobId);
        fromDb.TimerType.Should().Be(timerType);
        fromDb.AppointmentId.Should().Be(appt.Id);
        fromDb.IsExpired.Should().BeFalse();
        (fromDb.EndTime - fromDb.StartTime).Should().BeCloseTo(
            TimeSpan.FromHours(timerType == "awaiting_report_transition" ? 24 : 24),
            TimeSpan.FromSeconds(1));
    }

    [Fact(DisplayName = "TIMER · Marcar IsExpired=true + ExpiredAt cuando vence (escenario watchdog)")]
    public async Task AppointmentTimer_expiration_is_recorded()
    {
        await using var db = NewDbContext();

        var client = await new UserBuilder("exp-timer-cli@test.dev").AsClient().PersistAsync(db);
        var expertUser = await new UserBuilder("exp-timer-exp@test.dev").AsExpert().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).PersistAsync(db);
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .PersistAsync(db);
        var appt = await new AppointmentBuilder(hire.Id).PersistAsync(db);

        var timer = new AppointmentTimer
        {
            AppointmentId = appt.Id,
            TimerType = "response",
            StartTime = DateTime.UtcNow.AddHours(-25),
            EndTime = DateTime.UtcNow.AddHours(-1), // vencido hace 1h
            IsExpired = false,
            CreatedAt = DateTime.UtcNow.AddHours(-25),
        };
        db.AppointmentTimers.Add(timer);
        await db.SaveChangesAsync();

        // El watchdog cada 10 min hace este UPDATE para marcar el timer como expirado
        timer.IsExpired = true;
        timer.ExpiredAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.AppointmentTimers.SingleAsync(t => t.Id == timer.Id);
        fromDb.IsExpired.Should().BeTrue();
        fromDb.ExpiredAt.Should().NotBeNull();
        fromDb.ExpiredAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
    }
}
