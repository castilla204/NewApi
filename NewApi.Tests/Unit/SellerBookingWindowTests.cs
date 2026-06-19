// NewApi/NewApi.Tests/Unit/SellerBookingWindowTests.cs
using newApi.Services;
using FluentAssertions;

namespace NewApi.Tests.Unit;

/// <summary>
/// Política PURA de la ventana del modo seller (sin BD). Ancla fija para determinismo.
/// </summary>
public class SellerBookingWindowTests
{
    // Pago el 2026-06-19 a las 14:00 UTC. La ventana se ancla al INICIO del día de pago.
    private static readonly DateTime Anchor = new(2026, 6, 19, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void StartUtc_IsAnchorDatePlusMinLeadDays()
    {
        SellerBookingWindow.StartUtc(Anchor)
            .Should().Be(new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc)); // +3 días, medianoche
    }

    [Fact]
    public void HardEndExclusiveUtc_IsAnchorDatePlus15()
    {
        SellerBookingWindow.HardEndExclusiveUtc(Anchor)
            .Should().Be(new DateTime(2026, 7, 4, 0, 0, 0, DateTimeKind.Utc)); // +15 días, medianoche
    }

    [Fact]
    public void TargetDays_CoversThreeToSevenInclusive()
    {
        SellerBookingWindow.TargetDays.Should().Be(5);
    }

    [Fact]
    public void HardDays_CoversThreeToFourteenInclusive()
    {
        SellerBookingWindow.HardDays.Should().Be(12);
    }

    [Theory]
    [InlineData("2026-06-21", false)] // +2: antes del suelo
    [InlineData("2026-06-22", true)]  // +3: suelo inclusive
    [InlineData("2026-07-03", true)]  // +14: tope inclusive
    [InlineData("2026-07-04", false)] // +15: fuera
    public void IsWithinWindow_RespectsFloorAndHardCap(string startDate, bool expected)
    {
        var start = DateTime.SpecifyKind(DateTime.Parse(startDate), DateTimeKind.Utc).AddHours(10);
        SellerBookingWindow.IsWithinWindow(Anchor, start).Should().Be(expected);
    }
}
