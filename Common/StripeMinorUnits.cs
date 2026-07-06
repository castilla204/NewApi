using System;
using System.Collections.Generic;

namespace newApi.Common
{
    /// <summary>
    /// Conversión importe decimal → minor units (céntimos) de Stripe respetando las reglas por divisa.
    ///
    /// Para las divisas de 2 decimales (EUR y la inmensa mayoría) el resultado es BYTE-IDÉNTICO al
    /// histórico <c>checked((long)Math.Round(amount * 100))</c>, por lo que la ruta EUR NO cambia.
    ///
    /// Casos especiales de Stripe:
    ///   • Múltiplo-de-100 (HUF, ISK, TWD): NO son zero-decimal (se siguen usando ×100), pero Stripe
    ///     EXIGE que los importes de charge/transfer/payout/refund sean múltiplos de 100 minor units;
    ///     si no, la API responde 400 (amount_must_be_multiple_of_...). Para importes SALIENTES
    ///     (transfer al experto, refund al cliente, reversal) redondeamos hacia ABAJO al múltiplo de
    ///     100 más cercano: nunca se paga de más, y el remanente (&lt;1 unidad mayor) lo retiene la
    ///     plataforma como parte de su cuota residual → la conservación del dinero se mantiene.
    ///   • Zero-decimal (JPY, KRW, ...) y tres-decimales (BHD, KWD, ...): hoy NINGUNA es onboardable
    ///     en SupportedConnectCountries, pero se tratan correctamente por robustez ante cambios futuros.
    /// </summary>
    public static class StripeMinorUnits
    {
        // https://docs.stripe.com/currencies#zero-decimal
        private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
        { "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF" };

        // https://docs.stripe.com/currencies#three-decimal
        private static readonly HashSet<string> ThreeDecimal = new(StringComparer.OrdinalIgnoreCase)
        { "BHD", "JOD", "KWD", "OMR", "TND" };

        // https://docs.stripe.com/currencies#special-cases — NO zero-decimal, pero el importe debe ser
        // múltiplo de 100 minor units.
        private static readonly HashSet<string> MultipleOf100 = new(StringComparer.OrdinalIgnoreCase)
        { "HUF", "ISK", "TWD" };

        private static string Norm(string? currency) =>
            string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();

        /// <summary>
        /// Importe SALIENTE (transfer / refund / transfer-reversal). Para divisas múltiplo-de-100
        /// redondea hacia ABAJO al múltiplo de 100 válido (nunca paga de más; el remanente lo retiene
        /// la plataforma). Para el resto de divisas devuelve exactamente <c>Math.Round(amount*100)</c>
        /// (2 decimales) / <c>Math.Round(amount)</c> (zero-decimal) / <c>Math.Round(amount*1000)</c>
        /// (tres decimales). Overflow → OverflowException (checked), como el código original.
        /// </summary>
        public static long ToMinorUnitsOutbound(decimal amount, string? currency)
        {
            var c = Norm(currency);

            // ⚠️ CRÍTICO: usar Math.Round SIN especificar MidpointRounding = default ToEven (banker's),
            // EXACTAMENTE como el código histórico `Math.Round(x * 100)`. Especificar AwayFromZero
            // cambiaría la ruta EUR en los midpoints (x,xx5) → NO hacerlo.
            if (ZeroDecimal.Contains(c))
                return checked((long)Math.Round(amount));

            if (ThreeDecimal.Contains(c))
                return checked((long)Math.Round(amount * 1000m));

            var minor = checked((long)Math.Round(amount * 100m));

            if (MultipleOf100.Contains(c))
            {
                var rem = minor % 100;
                if (rem != 0)
                {
                    // Saliente: floor hacia cero (importes aquí son >= 0). Nunca de más.
                    minor -= rem;
                }
            }
            return minor;
        }
    }
}
