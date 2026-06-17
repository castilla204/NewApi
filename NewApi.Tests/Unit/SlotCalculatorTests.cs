using newApi.Services;
using FluentAssertions;

namespace NewApi.Tests.Unit;

/// <summary>
/// Tests PUROS del cálculo de huecos (sin BD). Fecha en julio => Europe/Madrid = CEST (UTC+2),
/// así 09:00 local = 07:00 UTC, determinista.
/// </summary>
public class SlotCalculatorTests
{
    private static readonly DateTime Date = new(2026, 7, 6); // local date
    private const string Tz = "Europe/Madrid";
    private static readonly DateTime FarPast = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime Utc(int h) => new(2026, 7, 6, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GeneratesBackToBackSlots()
    {
        var windows = new[] { new AvailabilityWindow(new TimeSpan(9, 0, 0), new TimeSpan(13, 0, 0), Tz) };
        var slots = SlotCalculator.ComputeFreeSlots(windows, Array.Empty<BookingInterval>(), Date, 2, FarPast);

        slots.Should().HaveCount(2);
        slots[0].StartUtc.Should().Be(Utc(7));  // 09:00 Madrid CEST = 07:00 UTC
        slots[0].EndUtc.Should().Be(Utc(9));
        slots[1].StartUtc.Should().Be(Utc(9));   // 11:00 local = 09:00 UTC
        slots[1].EndUtc.Should().Be(Utc(11));
    }

    [Fact]
    public void ExcludesBookedSlot()
    {
        var windows = new[] { new AvailabilityWindow(new TimeSpan(9, 0, 0), new TimeSpan(13, 0, 0), Tz) };
        var bookings = new[] { new BookingInterval(Utc(7), Utc(9)) }; // ocupa el primer hueco

        var slots = SlotCalculator.ComputeFreeSlots(windows, bookings, Date, 2, FarPast);

        slots.Should().HaveCount(1);
        slots[0].StartUtc.Should().Be(Utc(9));
    }

    [Fact]
    public void ExcludesPastSlots()
    {
        var windows = new[] { new AvailabilityWindow(new TimeSpan(9, 0, 0), new TimeSpan(13, 0, 0), Tz) };
        var nowAfter = new DateTime(2026, 7, 6, 23, 0, 0, DateTimeKind.Utc);

        var slots = SlotCalculator.ComputeFreeSlots(windows, Array.Empty<BookingInterval>(), Date, 2, nowAfter);

        slots.Should().BeEmpty();
    }

    [Fact]
    public void EmptyWhenNoWindows()
    {
        var slots = SlotCalculator.ComputeFreeSlots(
            Array.Empty<AvailabilityWindow>(), Array.Empty<BookingInterval>(), Date, 2, FarPast);
        slots.Should().BeEmpty();
    }

    [Fact]
    public void SplitShiftProducesSlotsInBothRanges()
    {
        var windows = new[]
        {
            new AvailabilityWindow(new TimeSpan(9, 0, 0), new TimeSpan(11, 0, 0), Tz),  // mañana
            new AvailabilityWindow(new TimeSpan(16, 0, 0), new TimeSpan(18, 0, 0), Tz), // tarde
        };
        var slots = SlotCalculator.ComputeFreeSlots(windows, Array.Empty<BookingInterval>(), Date, 2, FarPast);

        slots.Should().HaveCount(2);
        slots[0].StartUtc.Should().Be(Utc(7));   // 09:00 local
        slots[1].StartUtc.Should().Be(Utc(14));  // 16:00 local = 14:00 UTC
    }
}
