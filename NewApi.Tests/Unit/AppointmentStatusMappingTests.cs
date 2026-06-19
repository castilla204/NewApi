using newApi.DataLayer.Models.enums;

namespace NewApi.Tests.Unit;

/// <summary>
/// Round-trip del mapeo enum &lt;-&gt; string para AppointmentStatus.
/// Protege en particular el nuevo estado NO-FINAL appointment_pending_expert_confirmation:
/// FromStringValue lanza ArgumentException para valores desconocidos, así que este test falla
/// si el caso se cae del switch.
/// </summary>
public class AppointmentStatusMappingTests
{
    [Fact]
    public void PendingExpertConfirmation_ToStringValue_ReturnsSnakeCase()
    {
        AppointmentStatus.AppointmentPendingExpertConfirmation
            .ToStringValue()
            .Should().Be("appointment_pending_expert_confirmation");
    }

    [Fact]
    public void PendingExpertConfirmation_FromStringValue_ReturnsEnum()
    {
        AppointmentStatusExtensions
            .FromStringValue("appointment_pending_expert_confirmation")
            .Should().Be(AppointmentStatus.AppointmentPendingExpertConfirmation);
    }

    [Fact]
    public void PendingExpertConfirmation_RoundTrips()
    {
        var original = AppointmentStatus.AppointmentPendingExpertConfirmation;
        AppointmentStatusExtensions
            .FromStringValue(original.ToStringValue())
            .Should().Be(original);
    }
}
