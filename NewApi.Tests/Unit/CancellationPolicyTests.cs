using newApi.Services;
using FluentAssertions;

namespace NewApi.Tests.Unit;

/// <summary>Matriz pura de cancelación escalonada (Fase D). Umbrales 24h/6h.</summary>
public class CancellationPolicyTests
{
    // BUG #2 FIX: la 1ª cancelación con antelación >24h es SIEMPRE 100% (honra la promesa del checkout
    // "cancelación sin coste antes de la revisión"), incluso con N=0 (default). El cupo N penaliza solo
    // la REPETICIÓN (cancelaciones >24h previas del mismo cliente en la ventana móvil).
    [Theory]
    [InlineData(48, 0, 0, "appointment_cancelled_by_client_gt24h")]   // >24h, 1ª cancelación -> 100% (promesa)
    [InlineData(48, 1, 0, "appointment_cancelled_by_client_6to24h")]  // >24h, 2ª en ventana (abuso) -> 50/50
    [InlineData(12, 0, 0, "appointment_cancelled_by_client_6to24h")]  // 6-24h -> 50/50
    [InlineData(3, 0, 0, "appointment_cancelled_by_client_lt6h")]     // <6h -> 0/100
    public void N0_FirstAdvanceCancellationIsFree(double hours, int used, int n, string expected)
    {
        CancellationPolicy.ResolveClientStatus(hours, used, 24, 6, n).Should().Be(expected);
    }

    // N=1: las DOS primeras >24h gratis (used 0 y 1 ≤ 1); la tercera (used=2) baja a 50/50.
    [Theory]
    [InlineData(48, 0, 1, "appointment_cancelled_by_client_gt24h")]   // 1ª -> 100%
    [InlineData(48, 1, 1, "appointment_cancelled_by_client_gt24h")]   // 2ª (dentro del cupo) -> 100%
    [InlineData(48, 2, 1, "appointment_cancelled_by_client_6to24h")]  // 3ª (cupo superado) -> 50/50
    public void N1_GrantsTwoFree(double hours, int used, int n, string expected)
    {
        CancellationPolicy.ResolveClientStatus(hours, used, 24, 6, n).Should().Be(expected);
    }

    [Theory]
    [InlineData(24, "appointment_cancelled_by_client_gt24h")]  // borde 24h, 1ª cancelación -> 100%
    [InlineData(6, "appointment_cancelled_by_client_6to24h")]  // borde 6h -> 50/50
    [InlineData(5.99, "appointment_cancelled_by_client_lt6h")] // justo por debajo de 6h -> 0/100
    public void Boundaries_WithN0_FirstCancellation(double hours, string expected)
    {
        CancellationPolicy.ResolveClientStatus(hours, 0, 24, 6, 0).Should().Be(expected);
    }
}
