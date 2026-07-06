using FluentAssertions;
using newApi.Common;

namespace NewApi.Tests.Integration;

/// <summary>
/// FIX HUF (transfer múltiplo-de-100). Ancla dos invariantes:
///   1) Para EUR y cualquier divisa de 2 decimales, el helper es IDÉNTICO al histórico
///      checked((long)Math.Round(amount*100)) → la ruta EUR (99,99% del tráfico) no cambia.
///   2) Para HUF/ISK/TWD (no zero-decimal, pero Stripe exige múltiplos de 100 minor units) el
///      importe saliente se redondea hacia abajo al múltiplo de 100 → Stripe no rechaza el transfer.
/// </summary>
public class StripeMinorUnitsTests
{
    [Theory]
    // EUR / 2 decimales: idéntico a Math.Round(amount*100).
    [InlineData(100.00, "EUR", 10000)]
    [InlineData(199.99, "EUR", 19999)]
    [InlineData(33.33, "EUR", 3333)]
    [InlineData(0, "EUR", 0)]
    [InlineData(70.005, "EUR", 7000)] // ToEven (banker's) como el histórico: 7000.5 → 7000
    [InlineData(50.00, "SEK", 5000)]  // otra de 2 decimales
    // null/empty → EUR.
    [InlineData(12.34, null, 1234)]
    [InlineData(12.34, "", 1234)]
    public void TwoDecimalCurrencies_MatchLegacyRoundTimes100(decimal amount, string? currency, long expected)
    {
        StripeMinorUnits.ToMinorUnitsOutbound(amount, currency).Should().Be(expected);
    }

    [Theory]
    // HUF: múltiplo de 100. 3333,33 HUF → 333333 → floor 333300.
    [InlineData(3333.33, "HUF", 333300)]
    [InlineData(199.99, "HUF", 19900)]
    [InlineData(7000, "HUF", 700000)]   // ya múltiplo de 100 → sin cambio
    [InlineData(1, "HUF", 100)]         // 1 HUF = 100 minor → ya múltiplo de 100 exacto
    [InlineData(1.5, "HUF", 100)]       // 150 → floor 100
    [InlineData(3333.33, "huf", 333300)] // case-insensitive
    [InlineData(3333.33, "ISK", 333300)]
    [InlineData(3333.33, "TWD", 333300)]
    public void MultipleOf100Currencies_FloorToNearest100(decimal amount, string currency, long expected)
    {
        StripeMinorUnits.ToMinorUnitsOutbound(amount, currency).Should().Be(expected);
    }

    [Theory]
    // Zero-decimal (no onboardable hoy, pero robustez): sin ×100.
    [InlineData(168, "JPY", 168)]
    [InlineData(168.4, "JPY", 168)]
    [InlineData(5000, "KRW", 5000)]
    public void ZeroDecimalCurrencies_NoTimes100(decimal amount, string currency, long expected)
    {
        StripeMinorUnits.ToMinorUnitsOutbound(amount, currency).Should().Be(expected);
    }

    [Theory]
    // Tres decimales (no onboardable hoy): ×1000.
    [InlineData(10.500, "BHD", 10500)]
    [InlineData(10.5, "KWD", 10500)]
    public void ThreeDecimalCurrencies_Times1000(decimal amount, string currency, long expected)
    {
        StripeMinorUnits.ToMinorUnitsOutbound(amount, currency).Should().Be(expected);
    }
}
