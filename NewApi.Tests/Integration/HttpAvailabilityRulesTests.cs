using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using FluentAssertions;

namespace NewApi.Tests.Integration;

/// <summary>
/// Fase E1: CRUD de ExpertAvailabilityRule por HTTP real (PUT reemplaza el set semanal,
/// effective-dating; GET devuelve las activas).
/// </summary>
[Collection("Api")]
public class HttpAvailabilityRulesTests
{
    private readonly ApiFactoryFixture _api;
    public HttpAvailabilityRulesTests(ApiFactoryFixture api) => _api = api;

    private const string Url = "/api/ExpertAvailability/rules";

    private HttpRequestMessage Authed(HttpMethod method, string jwt, object? body = null)
    {
        var req = new HttpRequestMessage(method, Url);
        if (body != null) req.Content = JsonContent.Create(body);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return req;
    }

    [Fact(DisplayName = "E1 · PUT rules reemplaza el set semanal (effective-dating) y GET devuelve las activas")]
    public async Task PutRules_replaces_weekly_set()
    {
        int expertUserId, expertProfileId;
        await using (var db = _api.CreateDbContext())
        {
            var u = await new UserBuilder("avail-exp@test.dev").AsExpert().Verified().PersistAsync(db);
            var p = await new ExpertProfileBuilder(u.Id).Approved().PersistAsync(db);
            expertUserId = u.Id;
            expertProfileId = p.Id;
        }
        var jwt = _api.MintJwtFor(expertUserId, "avail-exp@test.dev", role: "Expert");

        // Lunes turno partido (09-13, 16-20) + Martes (10-14) => 3 reglas.
        var body1 = new
        {
            rules = new object[]
            {
                new { dayOfWeek = 1, startLocal = "09:00", endLocal = "13:00" },
                new { dayOfWeek = 1, startLocal = "16:00", endLocal = "20:00" },
                new { dayOfWeek = 2, startLocal = "10:00", endLocal = "14:00" },
            },
        };
        (await _api.Client.SendAsync(Authed(HttpMethod.Put, jwt, body1))).StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = _api.CreateDbContext())
        {
            (await db.ExpertAvailabilityRules.CountAsync(r => r.ExpertId == expertProfileId && r.IsActive && r.EffectiveTo == null))
                .Should().Be(3);
        }

        // Segundo PUT (solo lunes 08-12) => reemplaza todo.
        var body2 = new { rules = new object[] { new { dayOfWeek = 1, startLocal = "08:00", endLocal = "12:00" } } };
        (await _api.Client.SendAsync(Authed(HttpMethod.Put, jwt, body2))).StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = _api.CreateDbContext())
        {
            (await db.ExpertAvailabilityRules.CountAsync(r => r.ExpertId == expertProfileId && r.IsActive && r.EffectiveTo == null))
                .Should().Be(1, "el segundo PUT reemplaza todas las reglas activas");
            (await db.ExpertAvailabilityRules.CountAsync(r => r.ExpertId == expertProfileId))
                .Should().Be(4, "las 3 viejas quedan desactivadas (histórico) + 1 nueva");
        }

        // GET devuelve la regla activa.
        var getRes = await _api.Client.SendAsync(Authed(HttpMethod.Get, jwt));
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await getRes.Content.ReadAsStringAsync()).Should().Contain("08:00");
    }

    [Fact(DisplayName = "E1 · PUT rules rechaza franja invertida (fin <= inicio)")]
    public async Task PutRules_rejects_inverted_range()
    {
        int expertUserId;
        await using (var db = _api.CreateDbContext())
        {
            var u = await new UserBuilder("avail-exp2@test.dev").AsExpert().Verified().PersistAsync(db);
            await new ExpertProfileBuilder(u.Id).Approved().PersistAsync(db);
            expertUserId = u.Id;
        }
        var jwt = _api.MintJwtFor(expertUserId, "avail-exp2@test.dev", role: "Expert");

        var bad = new { rules = new object[] { new { dayOfWeek = 1, startLocal = "13:00", endLocal = "09:00" } } };
        (await _api.Client.SendAsync(Authed(HttpMethod.Put, jwt, bad))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
