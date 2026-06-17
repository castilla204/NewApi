using newApi.Services;
using FluentAssertions;

namespace NewApi.Tests.Unit;

/// <summary>Matriz pura de cancelación escalonada (Fase D). Umbrales 24h/6h.</summary>
public class CancellationPolicyTests
{
    // N=0 (default estricto): ninguna cancelación penalty-free; hasta >24h cae a 50/50.
    [Theory]
    [InlineData(48, 0, 0, "appointment_cancelled_by_client_6to24h")]  // >24h, N=0 -> 50/50
    [InlineData(12, 0, 0, "appointment_cancelled_by_client_6to24h")]  // 6-24h -> 50/50
    [InlineData(3, 0, 0, "appointment_cancelled_by_client_lt6h")]     // <6h -> 0/100
    public void N0_IsStrict(double hours, int used, int n, string expected)
    {
        CancellationPolicy.ResolveClientStatus(hours, used, 24, 6, n).Should().Be(expected);
    }

    // N=1: la primera >24h es gratis (100%); agotado el cupo, baja a 50/50.
    [Theory]
    [InlineData(48, 0, 1, "appointment_cancelled_by_client_gt24h")]   // cupo disponible -> 100%
    [InlineData(48, 1, 1, "appointment_cancelled_by_client_6to24h")]  // cupo agotado -> 50/50
    public void N1_GrantsOneFree(double hours, int used, int n, string expected)
    {
        CancellationPolicy.ResolveClientStatus(hours, used, 24, 6, n).Should().Be(expected);
    }

    [Theory]
    [InlineData(24, "appointment_cancelled_by_client_6to24h")] // borde 24h con N=0
    [InlineData(6, "appointment_cancelled_by_client_6to24h")]  // borde 6h
    [InlineData(5.99, "appointment_cancelled_by_client_lt6h")] // justo por debajo de 6h
    public void Boundaries_WithN0(double hours, string expected)
    {
        CancellationPolicy.ResolveClientStatus(hours, 0, 24, 6, 0).Should().Be(expected);
    }
}
