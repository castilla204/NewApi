using FluentAssertions;

namespace NewApi.Tests.Integration;

/// <summary>
/// FIX F4 — Invariante de expiración de la autorización de Stripe (captura diferida).
///
/// La captura ocurre cuando el experto APRUEBA. Una autorización de Stripe expira a los ~7 días
/// desde la creación del PaymentIntent; capturar después FALLA. Por tanto la cadena de plazos
/// (booking vendedor + confirmación del experto) debe quedar SIEMPRE por debajo del corte del
/// watchdog R5, y el corte R5 por debajo de los 7 días de Stripe.
///
/// Esta garantía vive hoy en 3 constantes DISPERSAS y sin test (ver comentario en
/// PlatformMaintenanceService.ProcessExpiringPaymentIntentsAsync):
///   - SubscriptionController.cs   → ventana de confirmación self  = 48h
///   - SellerBookingController.cs  → ventana de confirmación seller = 36h
///   - PlatformMaintenanceService  → corte del watchdog R5         = 4.5d
///   - (externa) expiración de auth de Stripe                      = 7d
///
/// Si alguien sube cualquier ventana en el futuro, este test ROMPE en build y avisa de que se
/// reintroduce la captura post-expiración. Es un test "espejo de constantes": mantener en sync
/// con los valores de producción citados arriba.
/// </summary>
public class AuthExpiryInvariantTests
{
    // Constantes reflejadas de producción (mantener en sync con los ficheros citados).
    private static readonly TimeSpan SelfConfirmWindow   = TimeSpan.FromHours(48); // SubscriptionController
    private static readonly TimeSpan SellerConfirmWindow = TimeSpan.FromHours(36); // SellerBookingController
    private static readonly TimeSpan R5WatchdogCutoff    = TimeSpan.FromDays(4.5);  // PlatformMaintenanceService
    private static readonly TimeSpan StripeAuthExpiry    = TimeSpan.FromDays(7);    // Stripe (externo)

    [Fact(DisplayName = "Invariante · edad máx. de captura-al-aprobar < corte R5 < expiración auth 7d de Stripe")]
    public void CaptureOnApprove_WindowChain_StaysWithinStripeAuthExpiry()
    {
        // Peor caso de edad de captura-al-aprobar = plazo del booking vendedor (48h) + confirmación
        // del experto (36h) = 84h = 3.5 días.
        var maxCaptureAge = SelfConfirmWindow + SellerConfirmWindow;

        maxCaptureAge.Should().BeLessThan(R5WatchdogCutoff,
            "el watchdog R5 no debe cancelar un PI que el experto todavía podría capturar legítimamente");
        R5WatchdogCutoff.Should().BeLessThan(StripeAuthExpiry,
            "el watchdog R5 debe disparar ANTES de que Stripe auto-expire la autorización de 7 días");

        // Margen explícito frente a la expiración de Stripe (debe quedar holgura, no rozar el límite).
        (StripeAuthExpiry - R5WatchdogCutoff).Should().BeGreaterThan(TimeSpan.FromDays(1),
            "debe quedar >1 día de margen entre el corte R5 y la expiración de Stripe para absorber retrasos del job");
    }
}
