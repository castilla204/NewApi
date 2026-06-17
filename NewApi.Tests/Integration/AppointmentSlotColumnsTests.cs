using Microsoft.EntityFrameworkCore;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using FluentAssertions;

namespace NewApi.Tests.Integration;

public class AppointmentSlotColumnsTests : IntegrationTestBase
{
    public AppointmentSlotColumnsTests(PostgresContainerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task PersistsUtcIntervalAndBlocksCalendar()
    {
        await using var db = NewDbContext();
        var client = await new UserBuilder().AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder().AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").PersistAsync(db);
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithStatusValue("pending").PersistAsync(db);

        var start = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
        var appt = await new AppointmentBuilder(hire.Id)
            .WithStatusValue("awaiting_appointment")
            .WithSlot(expertUser.Id, start, start.AddHours(2), blocksCalendar: true)
            .PersistAsync(db);

        await using var db2 = NewDbContext();
        var reloaded = await db2.Appointments.SingleAsync(a => a.Id == appt.Id);
        reloaded.ExpertId.Should().Be(expertUser.Id);
        reloaded.StartsAtUtc.Should().Be(start);
        reloaded.EndsAtUtc.Should().Be(start.AddHours(2));
        reloaded.BlocksCalendar.Should().BeTrue();
    }
}
