using System.Security.Cryptography;
using System.Text;

namespace newApi.Common
{
    /// <summary>
    /// Genera claves de idempotencia DETERMINISTAS para las Checkout Sessions de Stripe.
    ///
    /// Objetivo (FIX doble cobro + su regresión):
    ///  - La MISMA compra (doble-clic / reintento) produce la MISMA clave => Stripe deduplica la sesión
    ///    => un solo PaymentIntent => una sola captura (cierra el doble cobro).
    ///  - Compras DISTINTAS del mismo (usuario,servicio) producen claves DISTINTAS => no se sobre-deduplica
    ///    y NO salta el idempotency_error (HTTP 400) que Stripe devuelve al reusar una clave con un body
    ///    diferente (p.ej. dos búsquedas/inspecciones distintas del mismo servicio en &lt;24h).
    ///
    /// El 'discriminator' DEBE incluir todo lo que varía en el body de la sesión (importe + parámetros de
    /// búsqueda / searchId / metadata variable). Se hashea para mantener la clave corta y estable.
    /// </summary>
    public static class IdempotencyKeyHelper
    {
        public static string ForCheckout(int userId, int serviceId, params string?[] discriminators)
        {
            var raw = string.Join("|", discriminators.Select(d => d ?? string.Empty));
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            var hash = Convert.ToHexString(bytes, 0, 8).ToLowerInvariant(); // 16 hex chars
            return $"checkout-{userId}-{serviceId}-{hash}";
        }
    }
}
