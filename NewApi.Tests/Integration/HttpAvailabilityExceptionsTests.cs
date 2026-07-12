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

        // 🗓️ FIX (2026-07-13): fecha FUTURA dinámica. El endpoint rechaza fechas pasadas (correcto),
        // y las fechas fijas del test (2026-07-06) caducaron → 400. AddDays(30) siempre es futuro.
        var d = DateTime.UtcNow.Date.AddDays(30);
        var dStr = d.ToString("yyyy-MM-dd");
        var dOnly = DateOnly.FromDateTime(d);

        var put = await _api.Client.SendAsync(Authed(HttpMethod.Put, Url, jwt,
            new { date = dStr, isWorking = false, ranges = Array.Empty<object>() }));
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = _api.CreateDbContext())
        {
            var rows = await db.ExpertAvailabilityExceptions
                .Where(e => e.ExpertId == profileId && e.Date == dOnly).ToListAsync();
            rows.Should().ContainSingle();
            rows[0].IsWorking.Should().BeFalse();
        }

        var from = d.AddDays(-5).ToString("yyyy-MM-dd");
        var to = d.AddDays(25).ToString("yyyy-MM-dd");
        var get = await _api.Client.SendAsync(Authed(HttpMethod.Get, $"{Url}?from={from}&to={to}", jwt));
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await get.Content.ReadFromJsonAsync<List<ExceptionRead>>();
        body!.Should().ContainSingle(e => e.Date == dStr && e.IsWorking == false);
    }

    [Fact(DisplayName = "PUT exceptions con turnos partidos guarda N franjas; segundo PUT reemplaza")]
    public async Task Put_special_hours_split_then_replace()
    {
        var (jwt, profileId) = await SeedExpertAsync("exc-split@test.dev");

        // 🗓️ FIX (2026-07-13): fecha FUTURA dinámica (ver Put_then_get_closed_exception).
        var d = DateTime.UtcNow.Date.AddDays(30);
        var dStr = d.ToString("yyyy-MM-dd");
        var dOnly = DateOnly.FromDateTime(d);

        var put1 = await _api.Client.SendAsync(Authed(HttpMethod.Put, Url, jwt, new
        {
            date = dStr,
            isWorking = true,
            ranges = new object[] { new { start = "09:00", end = "13:00" }, new { start = "16:00", end = "20:00" } },
        }));
        put1.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = _api.CreateDbContext())
        {
            (await db.ExpertAvailabilityExceptions.CountAsync(e => e.ExpertId == profileId && e.Date == dOnly))
                .Should().Be(2);
        }

        var put2 = await _api.Client.SendAsync(Authed(HttpMethod.Put, Url, jwt, new
        {
            date = dStr, isWorking = true, ranges = new object[] { new { start = "10:00", end = "12:00" } },
        }));
        put2.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = _api.CreateDbContext())
        {
            (await db.ExpertAvailabilityExceptions.CountAsync(e => e.ExpertId == profileId && e.Date == dOnly))
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

        // 🗓️ FIX (2026-07-13): 3 fechas FUTURAS dinámicas distintas (ver Put_then_get_closed_exception).
        var d6 = DateTime.UtcNow.Date.AddDays(30);
        var d7 = DateTime.UtcNow.Date.AddDays(31);
        var d8 = DateTime.UtcNow.Date.AddDays(32);
        string S(DateTime x) => x.ToString("yyyy-MM-dd");
        var o6 = DateOnly.FromDateTime(d6);
        var o7 = DateOnly.FromDateTime(d7);
        var o8 = DateOnly.FromDateTime(d8);

        // Pre-crear una excepción que el batch va a BORRAR (remove=true).
        await _api.Client.SendAsync(Authed(HttpMethod.Put, Url, jwt,
            new { date = S(d8), isWorking = false, ranges = Array.Empty<object>() }));

        var batch = await _api.Client.SendAsync(Authed(HttpMethod.Put, $"{Url}/batch", jwt, new
        {
            exceptions = new object[]
            {
                new { date = S(d6), isWorking = false, ranges = Array.Empty<object>() },
                new { date = S(d7), isWorking = true, ranges = new object[] { new { start = "10:00", end = "12:00" }, new { start = "16:00", end = "18:00" } } },
                new { date = S(d8), remove = true, isWorking = false, ranges = Array.Empty<object>() },
            },
        }));
        batch.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _api.CreateDbContext();
        (await db.ExpertAvailabilityExceptions.CountAsync(e => e.ExpertId == profileId && e.Date == o6 && !e.IsWorking)).Should().Be(1);
        (await db.ExpertAvailabilityExceptions.CountAsync(e => e.ExpertId == profileId && e.Date == o7 && e.IsWorking)).Should().Be(2);
        (await db.ExpertAvailabilityExceptions.AnyAsync(e => e.ExpertId == profileId && e.Date == o8))
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

    [Fact(DisplayName = "PUT exceptions/batch rechaza lotes por encima del tope defensivo")]
    public async Task Batch_rejects_oversized()
    {
        var (jwt, profileId) = await SeedExpertAsync("exc-batch-cap@test.dev");
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(1);
        var exceptions = Enumerable.Range(0, 801)
            .Select(i => new { date = start.AddDays(i).ToString("yyyy-MM-dd"), isWorking = false, ranges = Array.Empty<object>() })
            .ToArray();

        var resp = await _api.Client.SendAsync(Authed(HttpMethod.Put, $"{Url}/batch", jwt, new { exceptions }));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var db = _api.CreateDbContext();
        (await db.ExpertAvailabilityExceptions.AnyAsync(e => e.ExpertId == profileId))
            .Should().BeFalse("un lote por encima del tope no debe aplicar nada");
    }

    private sealed record ExceptionRead(string Date, bool IsWorking, List<RangeRead> Ranges);
    private sealed record RangeRead(string Start, string End);
}
