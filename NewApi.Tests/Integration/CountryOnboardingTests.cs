using newApi.Common;
using Stripe;

namespace NewApi.Tests.Integration;

/// <summary>
/// Matriz de PAÍSES soportados + capabilities Stripe Connect Express por país.
/// Tests puros (sin BD ni testcontainer) sobre la fuente de verdad compartida:
///   - newApi.Common.SupportedConnectCountries (whitelist EEA-27 + NO/LI + US/CA/GB/CH)
///   - newApi.Common.StripeConnectCapabilities (transfers; +card_payments US/CA/GB; +1099-MISC US)
///
/// Estas dos clases las consumen DOS gates del backend que DEBEN coincidir:
///   - UserService.BecomeExpert (paso 1, gate de promoción, UserService.cs:853)
///   - SubscriptionController.CreateExpertOnboarding (paso 2, antes de Account.Create, :1154/:1236)
/// Si la lista o las capabilities divergieran, un experto de un país "medio soportado"
/// pasaría el paso 1 y reventaría en Stripe en el paso 2 (o al revés). Estos tests
/// blindan esa invariante.
/// </summary>
public class CountryOnboardingTests
{
    // Los 33 países soportados (EEA-27 + EFTA NO/LI + US/CA/GB/CH).
    public static readonly string[] SupportedCountries =
    {
        "AT","BE","BG","HR","CY","CZ","DK","EE","FI","FR","DE","GR","HU","IE","IT",
        "LV","LT","LU","MT","NL","PL","PT","RO","SK","SI","ES","SE","NO","LI",
        "US","CA","GB","CH",
    };

    public static IEnumerable<object[]> SupportedCountryCases()
        => SupportedCountries.Select(c => new object[] { c });

    // Países / valores que NO deben estar soportados.
    public static IEnumerable<object[]> UnsupportedCountryCases() => new[]
    {
        new object[] { "IS" },   // Islandia · Stripe no soporta cuentas conectadas
        new object[] { "MX" },   // LatAm · requiere cross-border payouts vía Stripe Sales
        new object[] { "AR" },
        new object[] { "BR" },
        new object[] { "AU" },   // soportable en el futuro, hoy fuera de la whitelist
        new object[] { "JP" },
        new object[] { "RU" },
        new object[] { "CN" },
        new object[] { "ESP" },  // alpha-3, no alpha-2
        new object[] { "es-ES" },// locale, no country code
        new object[] { "EU" },   // no es país
        new object[] { "XX" },
    };

    // ─────────────────────────────────────────────────────────────────────────
    // CTRY-01 · whitelist · cada país soportado pasa el gate
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "CTRY-01 · IsSupported true para los 33 países soportados")]
    [MemberData(nameof(SupportedCountryCases))]
    public void Supported_country_passes_gate(string code)
    {
        SupportedConnectCountries.IsSupported(code)
            .Should().BeTrue($"{code} está en la whitelist EEA/EFTA/US/CA/GB/CH");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CTRY-02 · whitelist · países / valores fuera de la lista NO pasan
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "CTRY-02 · IsSupported false para países no soportados / valores inválidos")]
    [MemberData(nameof(UnsupportedCountryCases))]
    public void Unsupported_country_is_rejected(string code)
    {
        SupportedConnectCountries.IsSupported(code)
            .Should().BeFalse($"{code} no debe poder onboardear (reventaría en Stripe Account.Create)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CTRY-03 · null / vacío / whitespace → false (Mapbox falló = CountryDetectionFailed)
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "CTRY-03 · IsSupported false para null/vacío/whitespace")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Null_or_blank_country_is_rejected(string? code)
    {
        SupportedConnectCountries.IsSupported(code).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CTRY-04 · case-insensitive + trim (Mapbox puede devolver "es", " ES ")
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "CTRY-04 · IsSupported es case-insensitive y tolera espacios")]
    [InlineData("es")]
    [InlineData("Es")]
    [InlineData("ES")]
    [InlineData(" ES ")]
    [InlineData("us")]
    [InlineData("gB")]
    public void IsSupported_is_case_insensitive_and_trims(string code)
    {
        SupportedConnectCountries.IsSupported(code).Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CTRY-05 · capabilities · TODOS los países piden transfers
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "CTRY-05 · BuildCapabilitiesFor pide transfers en todos los países soportados")]
    [MemberData(nameof(SupportedCountryCases))]
    public void Every_supported_country_requests_transfers(string code)
    {
        var caps = StripeConnectCapabilities.BuildCapabilitiesFor(code);
        caps.Transfers.Should().NotBeNull();
        caps.Transfers.Requested.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CTRY-06 · capabilities · EEA/EFTA/CH → SOLO transfers (sin card_payments)
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "CTRY-06 · EEA/EFTA/CH → transfers sin card_payments ni 1099")]
    [InlineData("ES")]
    [InlineData("FR")]
    [InlineData("DE")]
    [InlineData("IT")]
    [InlineData("NO")]
    [InlineData("LI")]
    [InlineData("CH")]
    public void Eea_countries_request_transfers_only(string code)
    {
        var caps = StripeConnectCapabilities.BuildCapabilitiesFor(code);
        caps.Transfers!.Requested.Should().BeTrue();
        caps.CardPayments.Should().BeNull($"{code} usa separate charges & transfers, no necesita card_payments");
        caps.TaxReportingUs1099Misc.Should().BeNull($"{code} no es US, no lleva 1099-MISC");
        StripeConnectCapabilities.CountryRequiresCardPayments(code).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CTRY-07 · capabilities · US/CA/GB → transfers + card_payments (KYC Stripe)
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "CTRY-07 · US/CA/GB → transfers + card_payments (requisito KYC Stripe)")]
    [InlineData("US")]
    [InlineData("CA")]
    [InlineData("GB")]
    public void UsCaGb_request_card_payments(string code)
    {
        var caps = StripeConnectCapabilities.BuildCapabilitiesFor(code);
        caps.Transfers!.Requested.Should().BeTrue();
        caps.CardPayments.Should().NotBeNull($"Stripe exige card_payments para crear cuenta Connect en {code}");
        caps.CardPayments!.Requested.Should().BeTrue();
        StripeConnectCapabilities.CountryRequiresCardPayments(code).Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CTRY-08 · capabilities · 1099-MISC SOLO para US (no CA/GB)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "CTRY-08 · tax_reporting_us_1099_misc SOLO en US (no CA ni GB ni EEA)")]
    public void Only_us_requests_1099_misc()
    {
        StripeConnectCapabilities.BuildCapabilitiesFor("US").TaxReportingUs1099Misc
            .Should().NotBeNull("US: Stripe captura W-9 y filia 1099-MISC al IRS");
        StripeConnectCapabilities.BuildCapabilitiesFor("US").TaxReportingUs1099Misc!.Requested
            .Should().BeTrue();

        StripeConnectCapabilities.BuildCapabilitiesFor("CA").TaxReportingUs1099Misc
            .Should().BeNull("CA tiene su propio régimen (T4A), no 1099");
        StripeConnectCapabilities.BuildCapabilitiesFor("GB").TaxReportingUs1099Misc
            .Should().BeNull("GB tiene su propio régimen (CT61), no 1099");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CTRY-09 · capabilities · defensivo · null/vacío → solo transfers (legacy EEA)
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "CTRY-09 · BuildCapabilitiesFor(null/vacío) → solo transfers (defensivo)")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_country_defaults_to_transfers_only(string? code)
    {
        var caps = StripeConnectCapabilities.BuildCapabilitiesFor(code);
        caps.Transfers!.Requested.Should().BeTrue();
        caps.CardPayments.Should().BeNull();
        caps.TaxReportingUs1099Misc.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CTRY-10 · INVARIANTE de sincronía · todo país que requiere card_payments
    //           DEBE estar en la whitelist de soportados (si no, código muerto/incoherente)
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "CTRY-10 · todo país con card_payments está en la whitelist soportada")]
    [InlineData("US")]
    [InlineData("CA")]
    [InlineData("GB")]
    public void Card_payment_countries_are_all_supported(string code)
    {
        SupportedConnectCountries.IsSupported(code)
            .Should().BeTrue("un país con capabilities especiales que no esté soportado sería incoherente");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CTRY-11 · la whitelist tiene exactamente los 33 países esperados (sin drift)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "CTRY-11 · la whitelist soportada contiene exactamente 33 países")]
    public void Whitelist_has_exactly_expected_countries()
    {
        SupportedConnectCountries.Codes.Should().HaveCount(33);
        SupportedConnectCountries.Codes.Should().BeEquivalentTo(SupportedCountries);
        // IS explícitamente fuera (Stripe no lo soporta)
        SupportedConnectCountries.Codes.Should().NotContain("IS");
    }
}
