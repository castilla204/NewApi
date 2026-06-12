using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;

namespace NewApi.Tests.Integration;

/// <summary>
/// Radio de trabajo configurable por experto (ExpertProfiles.WorkRadiusKm).
///
/// Contrato:
///   - 0   = solo trabaja en su taller/punto fijo (el cliente se desplaza).
///   - 200 = máximo permitido.
///   - default 100 (la cobertura fija que anunciaba el frontend antes del campo).
///   - PUT /api/User/expert-profile acepta WorkRadiusKm opcional en el form;
///     si no se envía, se conserva el valor actual.
///   - Fuera de [0,200] → 400 (validación en UserService.UpdateExpertProfile).
///
/// Tests por HTTP real (WebApplicationFactory): pipeline JWT + model binding
/// multipart + controller + service + Postgres del testcontainer.
/// </summary>
[Collection("Api")]
public class ExpertWorkRadiusTests
{
    private readonly ApiFactoryFixture _api;

    public ExpertWorkRadiusTests(ApiFactoryFixture api) => _api = api;

    // El builder persiste el perfil con coords fijas (40.4168, -3.7038). El PUT
    // reenvía las MISMAS coords para que coordinatesChanged sea false y el update
    // no dispare el geocoding de Mapbox (sin red en tests).
    private static MultipartFormDataContent ProfileForm(int? workRadiusKm)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("Perfil actualizado por test de radio"), "Description" },
            { new StringContent("40.4168"), "Latitude" },
            { new StringContent("-3.7038"), "Longitude" },
            { new StringContent("Monday"), "AvailabilityDaysOfWeek" },
            { new StringContent("Tuesday"), "AvailabilityDaysOfWeek" },
            { new StringContent("09:00"), "AvailabilityStartTime" },
            { new StringContent("18:00"), "AvailabilityEndTime" },
        };
        if (workRadiusKm.HasValue)
            form.Add(new StringContent(workRadiusKm.Value.ToString()), "WorkRadiusKm");
        return form;
    }

    private async Task<(int userId, string jwt)> SeedExpertAsync(string email)
    {
        int userId;
        await using (var db = _api.CreateDbContext())
        {
            var user = await new UserBuilder(email).AsExpert().Verified().PersistAsync(db);
            await new ExpertProfileBuilder(user.Id).Approved().PersistAsync(db);
            userId = user.Id;
        }
        return (userId, _api.MintJwtFor(userId, email, role: "Expert"));
    }

    private async Task<HttpResponseMessage> PutProfileAsync(string jwt, int? workRadiusKm)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/User/expert-profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Content = ProfileForm(workRadiusKm);
        return await _api.Client.SendAsync(request);
    }

    private async Task<int> GetWorkRadiusAsync(string jwt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/User/expert-profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var response = await _api.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // La API serializa PascalCase, pero se aceptan ambos casings por robustez.
        var root = doc.RootElement;
        return root.TryGetProperty("WorkRadiusKm", out var pascal)
            ? pascal.GetInt32()
            : root.GetProperty("workRadiusKm").GetInt32();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WR-01 · perfil nuevo → default 100 km
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "WR-01 · perfil recién creado expone WorkRadiusKm = 100 (default)")]
    public async Task New_profile_defaults_to_100km()
    {
        var (_, jwt) = await SeedExpertAsync("wr01@test.dev");
        (await GetWorkRadiusAsync(jwt)).Should().Be(100,
            "el default preserva la cobertura de 100 km que el frontend anunciaba antes del campo");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WR-02 · PUT con 0 ("solo en mi taller") → 200 y round-trip
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "WR-02 · PUT WorkRadiusKm=0 (solo taller) → 200 y GET devuelve 0")]
    public async Task Radius_zero_only_workshop_round_trips()
    {
        var (_, jwt) = await SeedExpertAsync("wr02@test.dev");

        var put = await PutProfileAsync(jwt, 0);
        put.StatusCode.Should().Be(HttpStatusCode.OK, "0 es un valor válido: solo en su taller");

        (await GetWorkRadiusAsync(jwt)).Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WR-03 · PUT con 200 (máximo) → 200 y round-trip
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "WR-03 · PUT WorkRadiusKm=200 (máximo) → 200 y GET devuelve 200")]
    public async Task Radius_max_200_round_trips()
    {
        var (_, jwt) = await SeedExpertAsync("wr03@test.dev");

        var put = await PutProfileAsync(jwt, 200);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetWorkRadiusAsync(jwt)).Should().Be(200);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WR-04 · fuera de rango → 400 y el valor NO cambia
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "WR-04 · PUT WorkRadiusKm fuera de [0,200] → 400 sin persistir")]
    [InlineData(201)]
    [InlineData(250)]
    [InlineData(-1)]
    public async Task Radius_out_of_range_rejected(int invalid)
    {
        var (_, jwt) = await SeedExpertAsync($"wr04_{invalid + 10}@test.dev");

        var put = await PutProfileAsync(jwt, invalid);
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "el radio debe estar entre 0 (solo taller) y 200 km");

        (await GetWorkRadiusAsync(jwt)).Should().Be(100, "el rechazo no debe tocar el valor persistido");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WR-05 · PUT sin el campo → conserva el valor anterior
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "WR-05 · PUT sin WorkRadiusKm conserva el valor configurado")]
    public async Task Omitting_field_keeps_current_value()
    {
        var (_, jwt) = await SeedExpertAsync("wr05@test.dev");

        (await PutProfileAsync(jwt, 30)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetWorkRadiusAsync(jwt)).Should().Be(30);

        // Update posterior sin el campo (cliente antiguo) → no debe resetearlo.
        (await PutProfileAsync(jwt, null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetWorkRadiusAsync(jwt)).Should().Be(30,
            "un form sin WorkRadiusKm (frontend antiguo) no debe resetear la elección del experto");
    }
}
