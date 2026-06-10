using Microsoft.EntityFrameworkCore;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using NewApi.Tests.Simulators;

namespace NewApi.Tests.Integration;

/// <summary>
/// Journey multi-país de become-expert + onboarding Stripe Connect, contra Postgres real.
/// Complementa <see cref="ExpertOnboardingTests"/> (que solo cubría España/EUR) con:
///   - el gate de país de UserService.BecomeExpert (UserService.cs:853): países soportados
///     crean el profile; no soportados rebotan con CountryNotSupported SIN crear profile.
///   - el journey completo (become → onboarding → account.updated → Approved → crea servicio)
///     para países representativos de cada régimen de capabilities (ES EEA, US 1099, GB card_payments).
///   - recuperación de onboarding incompleto (ActionRequired → arregla → Approved).
///   - re-onboarding tras desconexión (deauthorization → vuelve a conectar).
/// </summary>
public class ExpertCountryJourneyTests : IntegrationTestBase
{
    public ExpertCountryJourneyTests(PostgresContainerFixture fixture) : base(fixture) { }

    // Un país por cada régimen de capabilities distinto.
    public static IEnumerable<object[]> RepresentativeCountries() => new[]
    {
        new object[] { "ES" },  // EEA · solo transfers
        new object[] { "FR" },  // EEA · solo transfers
        new object[] { "US" },  // transfers + card_payments + 1099-MISC
        new object[] { "CA" },  // transfers + card_payments (sin 1099)
        new object[] { "GB" },  // transfers + card_payments
        new object[] { "CH" },  // EFTA · solo transfers
        new object[] { "NO" },  // EFTA · solo transfers
        new object[] { "LI" },  // EEA pequeño · solo transfers
    };

    public static IEnumerable<object[]> UnsupportedCountries() => new[]
    {
        new object[] { "IS" },
        new object[] { "MX" },
        new object[] { "AR" },
        new object[] { "AU" },
        new object[] { "JP" },
    };

    // ─────────────────────────────────────────────────────────────────────────
    // ECJ-01 · journey feliz por país soportado → Approved → crea servicio
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "ECJ-01 · become-expert(país soportado) → onboarding → Approved → crea servicio")]
    [MemberData(nameof(RepresentativeCountries))]
    public async Task Happy_journey_per_supported_country(string country)
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder($"ecj01-{country}@test.dev").AsExpert().Verified().PersistAsync(db);

        // become-expert con el país auto-detectado: profile persiste con Country
        var profile = await MarketplaceFlowSimulator.BecomeExpertAsync(db, user.Id, country);
        profile.Country.Should().Be(country);
        profile.StripeStatus.Should().Be(StripeStatus.NotRequested);

        // onboarding → Pending → account.updated(charges+payouts) → Approved
        var acct = await MarketplaceFlowSimulator.StartStripeOnboardingAsync(db, profile.Id);
        var approved = await MarketplaceFlowSimulator.SimulateAccountApprovedAsync(db, acct);
        approved.StripeStatus.Should().Be(StripeStatus.Approved);
        approved.OnboardingCompleted.Should().BeTrue();
        approved.Country.Should().Be(country, "el país no cambia durante el onboarding");

        // gating abierto → crea servicio de verdad
        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id)).Should().BeTrue();
        var svc = await new SearchServiceBuilder(profile.Id).WithPrice(100m, "EUR").PersistAsync(db);
        svc.Id.Should().BeGreaterThan(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ECJ-02 · país NO soportado → CountryNotSupported, NO se crea profile
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "ECJ-02 · become-expert(país no soportado) → CountryNotSupported sin crear profile")]
    [MemberData(nameof(UnsupportedCountries))]
    public async Task Unsupported_country_blocks_and_creates_no_profile(string country)
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder($"ecj02-{country}@test.dev").AsExpert().Verified().PersistAsync(db);

        var ex = await Assert.ThrowsAsync<MarketplaceFlowSimulator.CountryNotSupportedException>(
            () => MarketplaceFlowSimulator.BecomeExpertAsync(db, user.Id, country));
        ex.ErrorCode.Should().Be("CountryNotSupported");
        ex.CountryCode.Should().Be(country);

        // el gate fira ANTES de tocar la BD: no debe quedar ningún ExpertProfile huérfano
        await using var verify = NewDbContext();
        (await verify.ExpertProfiles.AnyAsync(ep => ep.UserId == user.Id))
            .Should().BeFalse("el zombie-expert debe evitarse: el profile nunca se crea");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ECJ-03 · default sin país explícito → ES (back-compat con tests previos)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "ECJ-03 · become-expert sin país explícito → Country=ES (default)")]
    public async Task Default_country_is_spain()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("ecj03@test.dev").AsExpert().Verified().PersistAsync(db);

        var profile = await MarketplaceFlowSimulator.BecomeExpertAsync(db, user.Id);
        profile.Country.Should().Be("ES");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ECJ-04 · recuperación · ActionRequired → arregla documentos → Approved
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "ECJ-04 · onboarding incompleto (ActionRequired) → arregla → Approved → crea servicio")]
    public async Task Incomplete_onboarding_recovers_to_approved()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("ecj04-us@test.dev").AsExpert().Verified().PersistAsync(db);
        var profile = await MarketplaceFlowSimulator.BecomeExpertAsync(db, user.Id, "US");
        var acct = await MarketplaceFlowSimulator.StartStripeOnboardingAsync(db, profile.Id);

        // 1ª account.updated: Stripe pide documento → ActionRequired (bloqueado)
        var blocked = await MarketplaceFlowSimulator.SimulateAccountUpdatedAsync(
            db, acct,
            chargesEnabled: false, payoutsEnabled: false,
            currentlyDue: new[] { "individual.verification.document" });
        blocked.StripeStatus.Should().Be(StripeStatus.ActionRequired);
        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id)).Should().BeFalse();

        // el experto sube el documento → 2ª account.updated con todo OK → Approved
        var recovered = await MarketplaceFlowSimulator.SimulateAccountApprovedAsync(db, acct);
        recovered.StripeStatus.Should().Be(StripeStatus.Approved);
        recovered.OnboardingCompleted.Should().BeTrue();
        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id)).Should().BeTrue();

        var svc = await new SearchServiceBuilder(profile.Id).WithPrice(80m, "EUR").PersistAsync(db);
        svc.Id.Should().BeGreaterThan(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ECJ-05 · re-onboarding · deauthorization → reconecta → Approved de nuevo
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "ECJ-05 · Approved → deauthorized → re-onboarding → Approved otra vez")]
    public async Task Reonboarding_after_deauthorization()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("ecj05-gb@test.dev").AsExpert().Verified().PersistAsync(db);
        var profile = await MarketplaceFlowSimulator.BecomeExpertAsync(db, user.Id, "GB");

        // primer onboarding hasta Approved
        var acct1 = await MarketplaceFlowSimulator.StartStripeOnboardingAsync(db, profile.Id);
        await MarketplaceFlowSimulator.SimulateAccountApprovedAsync(db, acct1);
        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id)).Should().BeTrue();

        // el experto desconecta la cuenta (account.application.deauthorized)
        var deauth = await MarketplaceFlowSimulator.SimulateDeauthorizationAsync(db, acct1);
        deauth.StripeStatus.Should().Be(StripeStatus.Deauthorized);
        deauth.StripeAccountId.Should().BeNull();
        deauth.OnboardingCompleted.Should().BeFalse();
        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id))
            .Should().BeFalse("tras desconectar no puede operar");

        // vuelve a conectar: nuevo onboarding → nueva cuenta → Approved
        var acct2 = await MarketplaceFlowSimulator.StartStripeOnboardingAsync(db, profile.Id);
        acct2.Should().NotBe(acct1, "el re-onboarding genera una cuenta Stripe nueva");
        var reapproved = await MarketplaceFlowSimulator.SimulateAccountApprovedAsync(db, acct2);
        reapproved.StripeStatus.Should().Be(StripeStatus.Approved);
        reapproved.StripeAccountId.Should().Be(acct2);
        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id)).Should().BeTrue();
    }
}
