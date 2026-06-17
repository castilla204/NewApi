using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using FluentAssertions;

namespace NewApi.Tests.Integration;

/// <summary>CRUD de excepciones de disponibilidad por fecha (cerrar/abrir/horas especiales) por HTTP real.</summary>
[Collection("Api")]
public class HttpAvailabilityExceptionsTests
{
    private readonly ApiFactoryFixture _api;
    public HttpAvailabilityExceptionsTests(ApiFactoryFixture api) => _api = api;

    private const string Url = "/api/ExpertAvailability/exceptions";

    private HttpRequestMessage Authed(HttpMethod method, string url, string jwt, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = JsonContent.Create(body);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return req;
    }

    private async Task<(string jwt, int expertProfileId)> SeedExpertAsync(string email)
    {
        int userId, profileId;
        await using (var db = _api.CreateDbContext())
        {
            var u = await new UserBuilder(email).AsExpert().Verified().PersistAsync(db);
            var p = await new ExpertProfileBuilder(u.Id).Approved().PersistAsync(db);
            userId = u.Id; profileId = p.Id;
        }
        return (_api.MintJwtFor(userId, email, role: "Expert"), profileId);
    }

    [Fact(DisplayName = "PUT exceptions hace upsert de una fecha (cerrar) y GET la devuelve")]
    public async Task Put_then_get_closed_exception()
    {
        var (jwt, profileId) = await SeedExpertAsync("exc-close@test.dev");

        var put = await _api.Client.SendAsync(Authed(HttpMethod.Put, Url, jwt,
            new { date = "2026-07-06", isWorking = false, ranges = Array.Empty<object>() }));
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = _api.CreateDbContext())
        {
            var rows = await db.ExpertAvailabilityExceptions
                .Where(e => e.ExpertId == profileId && e.Date == new DateOnly(2026, 7, 6)).ToListAsync();
            rows.Should().ContainSingle();
            rows[0].IsWorking.Should().BeFalse();
        }

        var get = await _api.Client.SendAsync(Authed(HttpMethod.Get, $"{Url}?from=2026-07-01&to=2026-07-31", jwt));
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await get.Content.ReadFromJsonAsync<List<ExceptionRead>>();
        body!.Should().ContainSingle(e => e.Date == "2026-07-06" && e.IsWorking == false);
    }

    [Fact(DisplayName = "PUT exceptions con turnos partidos guarda N franjas; segundo PUT reemplaza")]
    public async Task Put_special_hours_split_then_replace()
    {
        var (jwt, profileId) = await SeedExpertAsync("exc-split@test.dev");

        var put1 = await _api.Client.SendAsync(Authed(HttpMethod.Put, Url, jwt, new
        {
            date = "2026-07-06",
            isWorking = true,
            ranges = new object[] { new { start = "09:00", end = "13:00" }, new { start = "16:00", end = "20:00" } },
        }));
        put1.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = _api.CreateDbContext())
        {
            (await db.ExpertAvailabilityExceptions.CountAsync(e => e.ExpertId == profileId && e.Date == new DateOnly(2026, 7, 6)))
                .Should().Be(2);
        }

        var put2 = await _api.Client.SendAsync(Authed(HttpMethod.Put, Url, jwt, new
        {
            date = "2026-07-06", isWorking = true, ranges = new object[] { new { start = "10:00", end = "12:00" } },
        }));
        put2.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = _api.CreateDbContext())
        {
            (await db.ExpertAvailabilityExceptions.CountAsync(e => e.ExpertId == profileId && e.Date == new DateOnly(2026, 7, 6)))
                .Should().Be(1, "el segundo PUT reemplaza las franjas de esa fecha");
        }
    }

    [Fact(DisplayName = "DELETE exceptions/{date} elimina la excepción")]
    public async Task Delete_removes_exception()
    {
        var (jwt, profileId) = await SeedExpertAsync("exc-del@test.dev");
        await _api.Client.SendAsync(Authed(HttpMethod.Put, Url, jwt,
            new { date = "2026-07-06", isWorking = false, ranges = Array.Empty<object>() }));

        var del = await _api.Client.SendAsync(Authed(HttpMethod.Delete, $"{Url}/2026-07-06", jwt));
        del.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _api.CreateDbContext();
        (await db.ExpertAvailabilityExceptions.AnyAsync(e => e.ExpertId == profileId && e.Date == new DateOnly(2026, 7, 6)))
            .Should().BeFalse();
    }

    [Fact(DisplayName = "PUT exceptions rechaza fecha en el pasado")]
    public async Task Put_rejects_past_date()
    {
        var (jwt, _) = await SeedExpertAsync("exc-past@test.dev");
        var resp = await _api.Client.SendAsync(Authed(HttpMethod.Put, Url, jwt,
            new { date = "2000-01-01", isWorking = false, ranges = Array.Empty<object>() }));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "PUT exceptions/batch aplica varias fechas (cerrar + horas + borrar) en una llamada")]
    public async Task Batch_applies_multiple_dates()
    {
        var (jwt, profileId) = await SeedExpertAsync("exc-batch@test.dev");
        // Pre-crear una excepción que el batch va a BORRAR (remove=true).
        await _api.Client.SendAsync(Authed(HttpMethod.Put, Url, jwt,
            new { date = "2026-07-08", isWorking = false, ranges = Array.Empty<object>() }));

        var batch = await _api.Client.SendAsync(Authed(HttpMethod.Put, $"{Url}/batch", jwt, new
        {
            exceptions = new object[]
            {
                new { date = "2026-07-06", isWorking = false, ranges = Array.Empty<object>() },
                new { date = "2026-07-07", isWorking = true, ranges = new object[] { new { start = "10:00", end = "12:00" }, new { start = "16:00", end = "18:00" } } },
                new { date = "2026-07-08", remove = true, isWorking = false, ranges = Array.Empty<object>() },
            },
        }));
        batch.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _api.CreateDbContext();
        (await db.ExpertAvailabilityExceptions.CountAsync(e => e.ExpertId == profileId && e.Date == new DateOnly(2026, 7, 6) && !e.IsWorking)).Should().Be(1);
        (await db.ExpertAvailabilityExceptions.CountAsync(e => e.ExpertId == profileId && e.Date == new DateOnly(2026, 7, 7) && e.IsWorking)).Should().Be(2);
        (await db.ExpertAvailabilityExceptions.AnyAsync(e => e.ExpertId == profileId && e.Date == new DateOnly(2026, 7, 8)))
            .Should().BeFalse("remove=true borra la excepción de esa fecha");
    }

    [Fact(DisplayName = "PUT exceptions/batch es atómico: una fecha pasada rechaza TODO el lote")]
    public async Task Batch_is_atomic_on_invalid()
    {
        var (jwt, profileId) = await SeedExpertAsync("exc-batch-atomic@test.dev");
        var batch = await _api.Client.SendAsync(Authed(HttpMethod.Put, $"{Url}/batch", jwt, new
        {
            exceptions = new object[]
            {
                new { date = "2026-07-06", isWorking = false, ranges = Array.Empty<object>() },
                new { date = "2000-01-01", isWorking = false, ranges = Array.Empty<object>() }, // pasada → rechaza todo
            },
        }));
        batch.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var db = _api.CreateDbContext();
        (await db.ExpertAvailabilityExceptions.AnyAsync(e => e.ExpertId == profileId))
            .Should().BeFalse("lote atómico: si una fecha falla, no se aplica nada");
    }

    private sealed record ExceptionRead(string Date, bool IsWorking, List<RangeRead> Ranges);
    private sealed record RangeRead(string Start, string End);
}
