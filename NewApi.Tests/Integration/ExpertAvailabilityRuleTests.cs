using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models.PostGresModels;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using FluentAssertions;

namespace NewApi.Tests.Integration;

public class ExpertAvailabilityRuleTests : IntegrationTestBase
{
    public ExpertAvailabilityRuleTests(PostgresContainerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RoundTrips_MultipleRangesPerDay()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder().AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(user.Id).Approved().PersistAsync(db);

        // Lunes turno partido (09-13, 16-20) + Martes (10-14) => 3 filas
        db.ExpertAvailabilityRules.AddRange(
            new ExpertAvailabilityRule { ExpertId = expert.Id, DayOfWeek = 1, StartLocal = new TimeSpan(9, 0, 0), EndLocal = new TimeSpan(13, 0, 0), Timezone = "Europe/Madrid", IsActive = true },
            new ExpertAvailabilityRule { ExpertId = expert.Id, DayOfWeek = 1, StartLocal = new TimeSpan(16, 0, 0), EndLocal = new TimeSpan(20, 0, 0), Timezone = "Europe/Madrid", IsActive = true },
            new ExpertAvailabilityRule { ExpertId = expert.Id, DayOfWeek = 2, StartLocal = new TimeSpan(10, 0, 0), EndLocal = new TimeSpan(14, 0, 0), Timezone = "Europe/Madrid", IsActive = true }
        );
        await db.SaveChangesAsync();

        await using var db2 = NewDbContext();
        var rules = await db2.ExpertAvailabilityRules
            .Where(r => r.ExpertId == expert.Id && r.IsActive)
            .OrderBy(r => r.DayOfWeek).ThenBy(r => r.StartLocal)
            .ToListAsync();

        rules.Should().HaveCount(3);
        rules.Count(r => r.DayOfWeek == 1).Should().Be(2, "lunes tiene turno partido");
        rules[0].StartLocal.Should().Be(new TimeSpan(9, 0, 0));
    }
}
