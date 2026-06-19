using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using FluentAssertions;

namespace NewApi.Tests.Integration;

/// <summary>
/// Ventana del modo seller sobre el endpoint público /api/seller-booking. Usa la HttpClient
/// real del backend (ApiFactoryFixture/WebApplicationFactory) para cubrir la validación de
/// ventana extremo a extremo: la cita debe caer entre el suelo (+3 días) y el tope (+14),
/// ambos anclados al día de pago (hire.CreatedAt).
/// </summary>
[Collection("Api")]
public class SellerBookingWindowHttpTests
{
    private readonly ApiFactoryFixture _api;
    public SellerBookingWindowHttpTests(ApiFactoryFixture api) => _api = api;

    // Crea un hire seller con token y un experto que trabaja TODOS los días 09-18 Madrid.
    private async Task<(string token, int serviceId, DateTime createdAt)> SeedSellerHireAsync(
        DateTime createdAtUtc, bool everyDay = true)
    {
        await using var db = _api.CreateDbContext();
        var expertUser = await new UserBuilder().AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").WithDuration(1).PersistAsync(db);
        if (everyDay)
        {
            for (var dow = 0; dow < 7; dow++)
                db.ExpertAvailabilityRules.Add(new ExpertAvailabilityRule
                {
                    ExpertId = expert.Id, DayOfWeek = dow,
                    StartLocal = new TimeSpan(9, 0, 0), EndLocal = new TimeSpan(18, 0, 0),
                    Timezone = "Europe/Madrid", IsActive = true,
                    EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                });
        }
        var client = await new UserBuilder().AsClient().Verified().PersistAsync(db);
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithStatusValue("pending").PersistAsync(db);
        hire.CreatedAt = createdAtUtc;
        hire.SellerBookingToken = token;
        hire.SellerBookingDeadline = createdAtUtc.AddHours(48);
        hire.ExpertTimezone = "Europe/Madrid";
        await db.SaveChangesAsync();
        return (token, svc.Id, createdAtUtc);
    }

    [Fact]
    public async Task Confirm_RejectsSlotBeforeFloor()
    {
        var createdAt = DateTime.UtcNow;
        var (token, _, _) = await SeedSellerHireAsync(createdAt);

        var start = SellerBookingWindow.StartUtc(createdAt).AddDays(-2).AddHours(10);
        var res = await _api.Client.PostAsJsonAsync($"/api/seller-booking/{token}/confirm", new
        {
            startsAtUtc = start, endsAtUtc = start.AddHours(1), location = (string?)null,
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Confirm_RejectsSlotBeyondHardCap()
    {
        var createdAt = DateTime.UtcNow;
        var (token, _, _) = await SeedSellerHireAsync(createdAt);

        var start = SellerBookingWindow.StartUtc(createdAt).AddDays(17).AddHours(10);
        var res = await _api.Client.PostAsJsonAsync($"/api/seller-booking/{token}/confirm", new
        {
            startsAtUtc = start, endsAtUtc = start.AddHours(1), location = (string?)null,
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Window_NotExtended_WhenExpertHasSlotsWithinTarget()
    {
        var createdAt = DateTime.UtcNow;
        var (token, _, _) = await SeedSellerHireAsync(createdAt, everyDay: true);

        var res = await _api.Client.GetAsync($"/api/seller-booking/{token}/window");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<WindowDto>();

        body!.HasAvailability.Should().BeTrue();
        body.WindowExtended.Should().BeFalse();
        body.Days.Should().Be(SellerBookingWindow.TargetDays); // 5 días (+3..+7)
    }

    [Fact]
    public async Task Window_Extended_WhenTargetEmptyButHardHasSlots()
    {
        var createdAt = DateTime.UtcNow;
        var (token, serviceId, _) = await SeedSellerHireAsync(createdAt, everyDay: true);

        // Cerrar con excepciones TODO el tramo objetivo (+3..+7) → sin huecos en target,
        // pero sí en +8..+14.
        await using (var db = _api.CreateDbContext())
        {
            var expert = await db.SearchServices.Include(s => s.ExpertProfile)
                .Where(s => s.Id == serviceId).Select(s => s.ExpertProfile!).SingleAsync();
            var floor = SellerBookingWindow.StartUtc(createdAt);
            for (var i = 0; i < SellerBookingWindow.TargetDays; i++)
                db.ExpertAvailabilityExceptions.Add(new ExpertAvailabilityException
                {
                    ExpertId = expert.Id,
                    Date = DateOnly.FromDateTime(floor.AddDays(i)),
                    IsWorking = false, Timezone = "Europe/Madrid",
                });
            await db.SaveChangesAsync();
        }

        var body = await (await _api.Client.GetAsync($"/api/seller-booking/{token}/window"))
            .Content.ReadFromJsonAsync<WindowDto>();

        body!.HasAvailability.Should().BeTrue();
        body.WindowExtended.Should().BeTrue();
        body.Days.Should().Be(SellerBookingWindow.HardDays); // 12 días (+3..+14)
    }

    [Fact]
    public async Task Window_NoAvailability_WhenExpertHasNoRules()
    {
        var createdAt = DateTime.UtcNow;
        var (token, _, _) = await SeedSellerHireAsync(createdAt, everyDay: false); // sin reglas

        var body = await (await _api.Client.GetAsync($"/api/seller-booking/{token}/window"))
            .Content.ReadFromJsonAsync<WindowDto>();

        body!.HasAvailability.Should().BeFalse();
    }

    private sealed record WindowDto(string FromYmd, int Days, bool WindowExtended, bool HasAvailability);

    [Fact]
    public async Task Confirm_SetsCompletionDeadlineAfterAppointmentEnd()
    {
        var createdAt = DateTime.UtcNow;
        var (token, serviceId, _) = await SeedSellerHireAsync(createdAt, everyDay: true);

        // Pide al backend un hueco REAL dentro de la ventana para no inventar la hora.
        // Día +4 (suelo+1). Consulta /slots y elige el primero.
        var probeDate = SellerBookingWindow.StartUtc(createdAt).AddDays(1).ToString("yyyy-MM-dd");
        var slotsRes = await _api.Client.GetAsync($"/api/seller-booking/{token}/slots?date={probeDate}");
        slotsRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var slots = await slotsRes.Content.ReadFromJsonAsync<List<SlotDto>>();
        slots.Should().NotBeNullOrEmpty("el experto trabaja ese día dentro de la ventana");
        var slot = slots![0];

        var res = await _api.Client.PostAsJsonAsync($"/api/seller-booking/{token}/confirm", new
        {
            startsAtUtc = slot.StartUtc, endsAtUtc = slot.EndUtc, location = "Calle Mayor 1",
            latitude = "40.0", longitude = "-3.7",
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _api.CreateDbContext();
        var hire = await db.SearchHires.AsNoTracking()
            .SingleAsync(h => h.SearchServiceId == serviceId);
        hire.CompletionDeadline.Should().NotBeNull();
        hire.CompletionDeadline!.Value.Should().BeAfter(slot.EndUtc);
    }

    private sealed record SlotDto(DateTime StartUtc, DateTime EndUtc, string StartLocal, string Timezone);
}
