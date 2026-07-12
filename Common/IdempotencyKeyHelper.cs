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
        // 🛡️ Round 28 MUD-8: prefijo bumped a "v2" para INVALIDAR sesiones Stripe cacheadas
        // con la lógica anterior. Stripe deduplica idempotency keys 24h — si pre-fix se creó
        // una session con Currency="eur" hardcoded usando esta key, Stripe devolvería la
        // session vieja aunque el código nuevo (MUD-7) pase Currency="usd" → el override no
        // se aplica. Con el bump v2, las keys cacheadas pre-MUD-7 quedan invalidadas y se
        // crean Sessions frescas con el currency correcto.
        // 🛡️ FIX [W18b] (2026-07-13): bump a "v3" porque los checkouts ahora añaden Metadata al
        // PaymentIntentData (propagar pendingHire/userId al PI). Cambiar el body de la sesión con la
        // MISMA idempotency key dispara idempotency_error (400) de Stripe durante las 24h posteriores al
        // despliegue si un cliente reintenta un checkout iniciado antes. El bump invalida esas keys.
        private const string CHECKOUT_KEY_VERSION = "v3";

        public static string ForCheckout(int userId, int serviceId, params string?[] discriminators)
        {
            var raw = string.Join("|", discriminators.Select(d => d ?? string.Empty));
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            var hash = Convert.ToHexString(bytes, 0, 8).ToLowerInvariant(); // 16 hex chars
            return $"checkout-{CHECKOUT_KEY_VERSION}-{userId}-{serviceId}-{hash}";
        }
    }
}
