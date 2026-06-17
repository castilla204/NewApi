using Microsoft.EntityFrameworkCore;
using Npgsql;
using newApi.DataLayer.Models;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using FluentAssertions;

namespace NewApi.Tests.Integration;

public class AppointmentOverlapConstraintTests : IntegrationTestBase
{
    public AppointmentOverlapConstraintTests(PostgresContainerFixture fixture) : base(fixture) { }

    private async Task<(int expertUserId, int hireId)> SeedHireAsync(AppDbContext db, string slug)
    {
        var client = await new UserBuilder($"ov-{slug}-cli@test.dev").AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder($"ov-{slug}-exp@test.dev").AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").PersistAsync(db);
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithStatusValue("pending").PersistAsync(db);
        return (expertUser.Id, hire.Id);
    }

    [Fact]
    public async Task RejectsOverlappingForSameExpert()
    {
        await using var db = NewDbContext();
        var (expertUserId, hireId) = await SeedHireAsync(db, "rej");
        var (_, hireId2) = await SeedHireAsync(db, "rej2");
        var start = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);

        await new AppointmentBuilder(hireId).WithStatusValue("appointment_confirmed")
            .WithSlot(expertUserId, start, start.AddHours(2), true).PersistAsync(db);

        // Segunda cita del MISMO experto que solapa (10:00-12:00 vs 09:00-11:00).
        var act = async () =>
            await new AppointmentBuilder(hireId2).WithStatusValue("appointment_confirmed")
                .WithSlot(expertUserId, start.AddHours(1), start.AddHours(3), true).PersistAsync(db);

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        (ex.Which.InnerException as PostgresException)!.SqlState.Should().Be("23P01");
    }

    [Fact]
    public async Task AllowsAdjacentNonOverlapping()
    {
        await using var db = NewDbContext();
        var (expertUserId, hireId) = await SeedHireAsync(db, "adj");
        var (_, hireId2) = await SeedHireAsync(db, "adj2");
        var start = new DateTime(2026, 7, 2, 9, 0, 0, DateTimeKind.Utc);

        await new AppointmentBuilder(hireId).WithStatusValue("appointment_confirmed")
            .WithSlot(expertUserId, start, start.AddHours(2), true).PersistAsync(db);
        // Empieza justo cuando acaba la anterior: '[)' => no choca en el borde.
        await new AppointmentBuilder(hireId2).WithStatusValue("appointment_confirmed")
            .WithSlot(expertUserId, start.AddHours(2), start.AddHours(4), true).PersistAsync(db);

        (await db.Appointments.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task AllowsOverlapForDifferentExperts()
    {
        await using var db = NewDbContext();
        var (expertA, hireA) = await SeedHireAsync(db, "exA");
        var (expertB, hireB) = await SeedHireAsync(db, "exB");
        var start = new DateTime(2026, 7, 3, 9, 0, 0, DateTimeKind.Utc);

        await new AppointmentBuilder(hireA).WithStatusValue("appointment_confirmed")
            .WithSlot(expertA, start, start.AddHours(2), true).PersistAsync(db);
        await new AppointmentBuilder(hireB).WithStatusValue("appointment_confirmed")
            .WithSlot(expertB, start, start.AddHours(2), true).PersistAsync(db);

        (await db.Appointments.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task RejectsInvertedInterval()
    {
        await using var db = NewDbContext();
        var (expertUserId, hireId) = await SeedHireAsync(db, "inv");
        var start = new DateTime(2026, 7, 5, 9, 0, 0, DateTimeKind.Utc);

        // Fin anterior al inicio => viola ck_appointment_interval_order (23514).
        var act = async () =>
            await new AppointmentBuilder(hireId).WithStatusValue("appointment_confirmed")
                .WithSlot(expertUserId, start, start.AddHours(-1), true).PersistAsync(db);

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        (ex.Which.InnerException as PostgresException)!.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task AllowsOverlapWhenNotBlocking()
    {
        await using var db = NewDbContext();
        var (expertUserId, hireId) = await SeedHireAsync(db, "free");
        var (_, hireId2) = await SeedHireAsync(db, "free2");
        var start = new DateTime(2026, 7, 4, 9, 0, 0, DateTimeKind.Utc);

        await new AppointmentBuilder(hireId).WithStatusValue("appointment_cancelled_by_client")
            .WithSlot(expertUserId, start, start.AddHours(2), blocksCalendar: false).PersistAsync(db);
        // Solapa, pero la primera no bloquea (cancelada) => permitido.
        await new AppointmentBuilder(hireId2).WithStatusValue("appointment_confirmed")
            .WithSlot(expertUserId, start.AddHours(1), start.AddHours(3), blocksCalendar: true).PersistAsync(db);

        (await db.Appointments.CountAsync()).Should().Be(2);
    }
}
