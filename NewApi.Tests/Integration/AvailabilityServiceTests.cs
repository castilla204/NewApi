using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using FluentAssertions;

namespace NewApi.Tests.Integration;

public class AvailabilityServiceTests : IntegrationTestBase
{
    public AvailabilityServiceTests(PostgresContainerFixture fixture) : base(fixture) { }

    // 2026-07-06 es lunes (DayOfWeek=1); julio => Europe/Madrid CEST (UTC+2).
    private static readonly DateTime Monday = new(2026, 7, 6);

    private async Task<int> SeedExpertWithRuleAsync(int durationHours, TimeSpan start, TimeSpan end)
    {
        await using var db = NewDbContext();
        var expertUser = await new UserBuilder().AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").WithDuration(durationHours).PersistAsync(db);
        db.ExpertAvailabilityRules.Add(new ExpertAvailabilityRule
        {
            ExpertId = expert.Id,
            DayOfWeek = 1, // lunes
            StartLocal = start,
            EndLocal = end,
            Timezone = "Europe/Madrid",
            IsActive = true,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), // fijo: no depender del reloj
        });
        await db.SaveChangesAsync();
        return svc.Id;
    }

    [Fact]
    public async Task ReturnsFreeSlotsExcludingBooked()
    {
        // Lunes 09-13 Madrid, duración 2h => slots 09-11 y 11-13 (= 07-09 y 09-11 UTC).
        var serviceId = await SeedExpertWithRuleAsync(2, new TimeSpan(9, 0, 0), new TimeSpan(13, 0, 0));

        // Reservar el primer hueco (07:00-09:00 UTC) con una cita que bloquea.
        await using (var db = NewDbContext())
        {
            var svc = await db.SearchServices.Include(s => s.ExpertProfile).SingleAsync(s => s.Id == serviceId);
            var expertUserId = svc.ExpertProfile.UserId;
            var client = await new UserBuilder().AsClient().Verified().PersistAsync(db);
            var hire = await new SearchHireBuilder()
                .ForClient(client.Id).ForExpert(expertUserId).ForService(serviceId)
                .WithStatusValue("pending").PersistAsync(db);
            await new AppointmentBuilder(hire.Id).WithStatusValue("appointment_confirmed")
                .WithSlot(expertUserId,
                    new DateTime(2026, 7, 6, 7, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 7, 6, 9, 0, 0, DateTimeKind.Utc), true)
                .PersistAsync(db);
        }

        await using var sutDb = NewDbContext();
        var sut = new AvailabilityService(sutDb);
        var slots = await sut.GetAvailableSlotsAsync(serviceId, Monday);

        // Huecos de 1h fijos (desacoplados de la duración del servicio): 09,10,11,12 Madrid.
        // La reserva 09-11 Madrid (07-09 UTC) bloquea los huecos de 09:00 y 10:00 → quedan 11:00 y 12:00.
        slots.Should().HaveCount(2, "los dos primeros huecos de 1h (09:00 y 10:00) están reservados");
        slots[0].StartUtc.Should().Be(new DateTime(2026, 7, 6, 9, 0, 0, DateTimeKind.Utc)); // 11:00 Madrid
        slots[0].Timezone.Should().Be("Europe/Madrid");
    }

    [Fact]
    public async Task EmptyWhenExpertHasNoRulesForThatDay()
    {
        // Regla solo para martes (2); pedimos lunes => sin huecos.
        var serviceId = await SeedExpertWithRuleAsync(2, new TimeSpan(9, 0, 0), new TimeSpan(13, 0, 0));
        await using (var db = NewDbContext())
        {
            var rule = await db.ExpertAvailabilityRules.SingleAsync();
            rule.DayOfWeek = 2; // martes
            await db.SaveChangesAsync();
        }

        await using var sutDb = NewDbContext();
        var sut = new AvailabilityService(sutDb);
        var slots = await sut.GetAvailableSlotsAsync(serviceId, Monday);

        slots.Should().BeEmpty();
    }

    [Fact]
    public async Task IgnoresRuleNotYetEffective()
    {
        var serviceId = await SeedExpertWithRuleAsync(2, new TimeSpan(9, 0, 0), new TimeSpan(13, 0, 0));
        await using (var db = NewDbContext())
        {
            var rule = await db.ExpertAvailabilityRules.SingleAsync();
            rule.EffectiveFrom = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc); // vigente en agosto
            await db.SaveChangesAsync();
        }

        await using var sutDb = NewDbContext();
        var sut = new AvailabilityService(sutDb);
        var slots = await sut.GetAvailableSlotsAsync(serviceId, Monday); // 2026-07-06 < EffectiveFrom

        slots.Should().BeEmpty();
    }

    [Fact]
    public async Task FallsBackToLegacyAvailabilityWhenNoRules()
    {
        // 🛟 Auto-reparación: experto SIN reglas por-día pero con disponibilidad legacy
        // (la que rellena el formulario de perfil). El motor debe sintetizar huecos desde
        // legacy para que tenga huecos reservables sin reconfigurar nada.
        int serviceId;
        await using (var db = NewDbContext())
        {
            var expertUser = await new UserBuilder().AsExpert().Verified().PersistAsync(db);
            var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
            var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").WithDuration(2).PersistAsync(db);
            db.ExpertAvailabilities.Add(new ExpertAvailability
            {
                ExpertId = expert.Id,
                DaysOfWeek = "[\"Monday\"]",
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(13, 0, 0),
                Timezone = "Europe/Madrid",
                IsActive = true,
                EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EffectiveTo = null,
            });
            await db.SaveChangesAsync();
            serviceId = svc.Id;
        }

        await using var sutDb = NewDbContext();
        var sut = new AvailabilityService(sutDb);
        var slots = await sut.GetAvailableSlotsAsync(serviceId, Monday);

        // Huecos de 1h fijos en lunes 09-13 Madrid: 09,10,11,12 => 4.
        slots.Should().HaveCount(4, "sin reglas por-día debe caer al horario legacy (lunes 09-13)");
        slots[0].Timezone.Should().Be("Europe/Madrid");
    }

    [Fact]
    public async Task RulesTakePrecedenceOverLegacy()
    {
        // Con reglas por-día Y legacy, mandan las reglas; el legacy se ignora.
        var serviceId = await SeedExpertWithRuleAsync(2, new TimeSpan(9, 0, 0), new TimeSpan(11, 0, 0)); // lunes 09-11 => 09,10
        await using (var db = NewDbContext())
        {
            var svc = await db.SearchServices.SingleAsync(s => s.Id == serviceId);
            db.ExpertAvailabilities.Add(new ExpertAvailability
            {
                ExpertId = svc.ExpertProfileId!.Value,
                DaysOfWeek = "[\"Monday\"]",
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(18, 0, 0), // mucho más amplio: NO debe usarse
                Timezone = "Europe/Madrid",
                IsActive = true,
                EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
        }

        await using var sutDb = NewDbContext();
        var sut = new AvailabilityService(sutDb);
        var slots = await sut.GetAvailableSlotsAsync(serviceId, Monday);

        slots.Should().HaveCount(2, "las reglas por-día mandan sobre el horario legacy");
    }

    [Fact]
    public async Task SummaryReportsFreeSlotsPerDay()
    {
        // Lunes 09-13 Madrid; huecos de 1h fijos → 4 huecos el lunes (09,10,11,12); 0 el resto sin regla.
        var serviceId = await SeedExpertWithRuleAsync(2, new TimeSpan(9, 0, 0), new TimeSpan(13, 0, 0));

        await using var sutDb = NewDbContext();
        var sut = new AvailabilityService(sutDb);
        var summary = await sut.GetAvailabilitySummaryAsync(serviceId, Monday, 7);

        summary.Should().HaveCount(7);
        summary.Single(d => d.Date == "2026-07-06").FreeSlots.Should().Be(4, "lunes 09-13 con huecos de 1h: 09,10,11,12");
        summary.Single(d => d.Date == "2026-07-07").FreeSlots.Should().Be(0, "martes: sin regla");
    }

    [Fact]
    public async Task ClosedExceptionRemovesAllSlotsForThatDate()
    {
        // Lunes 09-13 Madrid (regla semanal). Excepción: lunes 2026-07-06 cerrado.
        var serviceId = await SeedExpertWithRuleAsync(1, new TimeSpan(9, 0, 0), new TimeSpan(13, 0, 0));
        await using (var db = NewDbContext())
        {
            var expertId = await db.SearchServices.Where(s => s.Id == serviceId)
                .Select(s => s.ExpertProfileId!.Value).SingleAsync();
            db.ExpertAvailabilityExceptions.Add(new ExpertAvailabilityException
            {
                ExpertId = expertId,
                Date = new DateOnly(2026, 7, 6),
                IsWorking = false,
                Timezone = "Europe/Madrid",
            });
            await db.SaveChangesAsync();
        }

        await using var sutDb = NewDbContext();
        var sut = new AvailabilityService(sutDb);
        var slots = await sut.GetAvailableSlotsAsync(serviceId, Monday);

        slots.Should().BeEmpty("una excepción cerrada anula el horario semanal de ese día");
    }

    [Fact]
    public async Task SpecialHoursExceptionOverridesWeeklyHours()
    {
        // Regla lunes 09-13 (4 huecos de 1h). Excepción: ese lunes solo 16-18 (2 huecos).
        var serviceId = await SeedExpertWithRuleAsync(1, new TimeSpan(9, 0, 0), new TimeSpan(13, 0, 0));
        await using (var db = NewDbContext())
        {
            var expertId = await db.SearchServices.Where(s => s.Id == serviceId)
                .Select(s => s.ExpertProfileId!.Value).SingleAsync();
            db.ExpertAvailabilityExceptions.Add(new ExpertAvailabilityException
            {
                ExpertId = expertId,
                Date = new DateOnly(2026, 7, 6),
                IsWorking = true,
                StartLocal = new TimeSpan(16, 0, 0),
                EndLocal = new TimeSpan(18, 0, 0),
                Timezone = "Europe/Madrid",
            });
            await db.SaveChangesAsync();
        }

        await using var sutDb = NewDbContext();
        var sut = new AvailabilityService(sutDb);
        var slots = await sut.GetAvailableSlotsAsync(serviceId, Monday);

        slots.Should().HaveCount(2, "solo cuentan las horas especiales 16-18 Madrid");
        slots[0].StartUtc.Should().Be(new DateTime(2026, 7, 6, 14, 0, 0, DateTimeKind.Utc)); // 16:00 Madrid = 14:00 UTC
    }

    [Fact]
    public async Task OpenExceptionOnNormallyClosedWeekdayGeneratesSlots()
    {
        // Regla solo MARTES; lunes 2026-07-06 estaría cerrado. Excepción: abrir ese lunes 10-12.
        var serviceId = await SeedExpertWithRuleAsync(1, new TimeSpan(9, 0, 0), new TimeSpan(13, 0, 0));
        await using (var db = NewDbContext())
        {
            var rule = await db.ExpertAvailabilityRules.SingleAsync();
            rule.DayOfWeek = 2; // martes
            var expertId = rule.ExpertId;
            db.ExpertAvailabilityExceptions.Add(new ExpertAvailabilityException
            {
                ExpertId = expertId,
                Date = new DateOnly(2026, 7, 6),
                IsWorking = true,
                StartLocal = new TimeSpan(10, 0, 0),
                EndLocal = new TimeSpan(12, 0, 0),
                Timezone = "Europe/Madrid",
            });
            await db.SaveChangesAsync();
        }

        await using var sutDb = NewDbContext();
        var sut = new AvailabilityService(sutDb);
        var slots = await sut.GetAvailableSlotsAsync(serviceId, Monday);

        slots.Should().HaveCount(2, "la excepción abre un día que el horario semanal tenía cerrado");
    }

    [Fact]
    public async Task SummaryReflectsClosedException()
    {
        // Regla lunes 09-13. Summary de 7 días desde 2026-07-06: el lunes debe quedar a 0 si está cerrado.
        var serviceId = await SeedExpertWithRuleAsync(1, new TimeSpan(9, 0, 0), new TimeSpan(13, 0, 0));
        await using (var db = NewDbContext())
        {
            var expertId = await db.SearchServices.Where(s => s.Id == serviceId)
                .Select(s => s.ExpertProfileId!.Value).SingleAsync();
            db.ExpertAvailabilityExceptions.Add(new ExpertAvailabilityException
            {
                ExpertId = expertId, Date = new DateOnly(2026, 7, 6), IsWorking = false, Timezone = "Europe/Madrid",
            });
            await db.SaveChangesAsync();
        }

        await using var sutDb = NewDbContext();
        var sut = new AvailabilityService(sutDb);
        var summary = await sut.GetAvailabilitySummaryAsync(serviceId, Monday, 7);

        summary.Single(d => d.Date == "2026-07-06").FreeSlots.Should().Be(0);
    }
}
