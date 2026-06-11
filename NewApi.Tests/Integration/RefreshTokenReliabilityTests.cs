using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models.PostGresModels;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;

namespace NewApi.Tests.Integration;

/// <summary>
/// R29 · Fiabilidad del refresh de sesión (AuthController.RefreshToken).
///
/// Cubre los dos fixes que eliminan el "cierra sesión y vuelve a entrar":
///   1. Ventana de gracia (60s) para rotación concurrente: dos pestañas o un retry
///      tras timeout refrescan con el mismo token → respuesta idempotente con el
///      token de reemplazo en vez de revocar TODAS las sesiones del usuario.
///   2. Prioridad body &gt; cookie: una cookie `refresh_token` obsoleta de una sesión
///      anterior ya no pisa al token vivo que el cliente envía en el body.
/// </summary>
[Collection("Api")]
public class RefreshTokenReliabilityTests
{
    private readonly ApiFactoryFixture _api;

    public RefreshTokenReliabilityTests(ApiFactoryFixture api) => _api = api;

    private static RefreshToken NewToken(int userId, string prefix) => new()
    {
        UserId = userId,
        Token = prefix + "_" + Guid.NewGuid().ToString("N"),
        ExpiresAt = DateTime.UtcNow.AddDays(90),
        CreatedAt = DateTime.UtcNow,
        CreatedByIp = "127.0.0.1",
    };

    /// <summary>
    /// Cliente SIN CookieContainer: los tests controlan la cabecera Cookie a mano y
    /// evitan que el Set-Cookie de un test contamine a los siguientes (el Client
    /// compartido del fixture persiste cookies entre requests).
    /// </summary>
    private HttpClient NoCookieClient() =>
        _api.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    // ─────────────────────────────────────────────────────────────────────────
    // AUTH-G1 · rotación concurrente dentro de la ventana de gracia
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "AUTH-G1 · refresh con token rotado hace <60s → 200 idempotente con el reemplazo (no nuke)")]
    public async Task Refresh_within_grace_window_returns_replacement()
    {
        int userId;
        string oldValue, replacementValue;
        await using (var db = _api.CreateDbContext())
        {
            var user = await new UserBuilder("auth-g1@test.dev").AsClient().Verified().PersistAsync(db);
            userId = user.Id;

            var replacement = NewToken(userId, "rtg1new");
            var old = NewToken(userId, "rtg1old");
            old.IsRevoked = true;
            old.RevokedAt = DateTime.UtcNow.AddSeconds(-5);
            old.RevokedByIp = "127.0.0.1";
            old.ReplacedByToken = replacement.Token;

            db.RefreshTokens.AddRange(old, replacement);
            await db.SaveChangesAsync();
            oldValue = old.Token;
            replacementValue = replacement.Token;
        }

        using var client = NoCookieClient();
        var response = await client.PostAsJsonAsync("/api/Auth/refresh-token", new { refreshToken = oldValue });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "una rotación concurrente (2 pestañas, o retry tras timeout donde el server SÍ procesó el 1er intento) no es un ataque");

        // La API serializa PascalCase (Program.cs: PropertyNamingPolicy = null).
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("RefreshToken").GetString().Should().Be(replacementValue,
            "la respuesta idempotente converge a la cadena ya rotada");
        doc.RootElement.GetProperty("AccessToken").GetString().Should().NotBeNullOrEmpty();

        await using var verify = _api.CreateDbContext();
        var replacementFromDb = await verify.RefreshTokens.SingleAsync(t => t.Token == replacementValue);
        replacementFromDb.IsRevoked.Should().BeFalse("el reemplazo sigue siendo el token vivo de la sesión");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AUTH-G2 · reuse real (fuera de la ventana) → revocación total
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "AUTH-G2 · refresh con token rotado hace >60s → 401 y revoca TODAS las sesiones (reuse real)")]
    public async Task Refresh_outside_grace_window_nukes_all_sessions()
    {
        int userId;
        string oldValue, otherDeviceValue;
        await using (var db = _api.CreateDbContext())
        {
            var user = await new UserBuilder("auth-g2@test.dev").AsClient().Verified().PersistAsync(db);
            userId = user.Id;

            var replacement = NewToken(userId, "rtg2new");
            var old = NewToken(userId, "rtg2old");
            old.IsRevoked = true;
            old.RevokedAt = DateTime.UtcNow.AddMinutes(-5);
            old.RevokedByIp = "127.0.0.1";
            old.ReplacedByToken = replacement.Token;

            // Sesión activa en "otro dispositivo": debe caer también si hay reuse real.
            var otherDevice = NewToken(userId, "rtg2dev");

            db.RefreshTokens.AddRange(old, replacement, otherDevice);
            await db.SaveChangesAsync();
            oldValue = old.Token;
            otherDeviceValue = otherDevice.Token;
        }

        using var client = NoCookieClient();
        var response = await client.PostAsJsonAsync("/api/Auth/refresh-token", new { refreshToken = oldValue });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "fuera de la ventana de gracia el reuse se trata como robo de token");

        await using var verify = _api.CreateDbContext();
        var otherFromDb = await verify.RefreshTokens.SingleAsync(t => t.Token == otherDeviceValue);
        otherFromDb.IsRevoked.Should().BeTrue("la detección de reuse revoca todas las sesiones del usuario");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AUTH-G3 · revocado sin reemplazo (logout) → 401 aunque sea reciente
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "AUTH-G3 · token revocado por logout (sin ReplacedByToken) → 401 aunque la revocación sea reciente")]
    public async Task Refresh_with_logged_out_token_is_rejected_even_within_window()
    {
        int userId;
        string loggedOutValue;
        await using (var db = _api.CreateDbContext())
        {
            var user = await new UserBuilder("auth-g3@test.dev").AsClient().Verified().PersistAsync(db);
            userId = user.Id;

            var loggedOut = NewToken(userId, "rtg3out");
            loggedOut.IsRevoked = true;
            loggedOut.RevokedAt = DateTime.UtcNow.AddSeconds(-5);
            loggedOut.RevokedByIp = "127.0.0.1";
            // Sin ReplacedByToken: revocación por logout, no por rotación → sin gracia.

            db.RefreshTokens.Add(loggedOut);
            await db.SaveChangesAsync();
            loggedOutValue = loggedOut.Token;
        }

        using var client = NoCookieClient();
        var response = await client.PostAsJsonAsync("/api/Auth/refresh-token", new { refreshToken = loggedOutValue });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"un token revocado por logout no tiene cadena de reemplazo y no debe renovarse jamás (body: {body})");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AUTH-G4 · body con token vivo gana a cookie obsoleta
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "AUTH-G4 · body con token vivo + cookie obsoleta revocada → 200 (el body gana)")]
    public async Task Live_body_token_wins_over_stale_cookie()
    {
        int userId;
        string liveValue, staleCookieValue;
        await using (var db = _api.CreateDbContext())
        {
            var user = await new UserBuilder("auth-g4@test.dev").AsClient().Verified().PersistAsync(db);
            userId = user.Id;

            var live = NewToken(userId, "rtg4live");

            // Cookie huérfana de una sesión anterior, revocada hace tiempo y sin reemplazo:
            // con la prioridad antigua (cookie > body) esto disparaba reuse-detection y
            // tumbaba la sesión nueva en bucle, porque los logins no actualizan la cookie.
            var staleCookie = NewToken(userId, "rtg4stale");
            staleCookie.IsRevoked = true;
            staleCookie.RevokedAt = DateTime.UtcNow.AddHours(-2);
            staleCookie.RevokedByIp = "127.0.0.1";

            db.RefreshTokens.AddRange(live, staleCookie);
            await db.SaveChangesAsync();
            liveValue = live.Token;
            staleCookieValue = staleCookie.Token;
        }

        using var client = NoCookieClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Auth/refresh-token")
        {
            Content = JsonContent.Create(new { refreshToken = liveValue }),
        };
        request.Headers.Add("Cookie", $"refresh_token={staleCookieValue}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "el refresh vivo del body debe ganar a la cookie obsoleta de una sesión anterior");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("RefreshToken").GetString().Should().NotBe(liveValue,
            "rotación normal: el token del body se rota y se devuelve uno nuevo");

        await using var verify = _api.CreateDbContext();
        var liveFromDb = await verify.RefreshTokens.SingleAsync(t => t.Token == liveValue);
        liveFromDb.IsRevoked.Should().BeTrue("rotación normal del token del body");
        liveFromDb.ReplacedByToken.Should().NotBeNullOrEmpty();
    }
}
