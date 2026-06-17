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
}
