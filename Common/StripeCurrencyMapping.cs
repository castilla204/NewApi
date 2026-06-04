using System.Collections.Generic;

namespace newApi.Common
{
    /// <summary>
    /// 🌍 Round 9 — A5 FIX: Mapping país (ISO 3166-1 alpha-2) → divisa Stripe (ISO 4217 lower-case).
    ///
    /// CONTEXTO DEL BUG
    /// ----------------
    /// La plataforma cobra al cliente en EUR (los clientes están en EEA), pero los expertos
    /// pueden estar en GB, CH, US, CA. Sus cuentas Stripe Connect tienen `default_currency`
    /// distinto (gbp/chf/usd/cad). Si transferimos con `Currency = "eur"` a una cuenta cuyo
    /// `default_currency = "gbp"`, Stripe responde con StripeException "currency_mismatch"
    /// y la transferencia falla — el experto se queda sin cobrar y el dinero queda atascado
    /// en el balance de la plataforma.
    ///
    /// SOLUCIÓN
    /// --------
    /// Antes de crear el `TransferCreateOptions`, derivar la divisa correcta:
    ///   - Preferir <c>expertAccount.DefaultCurrency</c> (fuente de verdad real de Stripe),
    ///   - Fallback: <see cref="GetCurrencyForCountry"/> usando el código ISO del experto.
    ///
    /// NOTAS
    /// -----
    /// - El balance EUR de la plataforma puede transferirse a una cuenta GBP/CHF/USD/CAD:
    ///   Stripe hace la conversión automáticamente y aplica su tipo de cambio (consultable
    ///   en el objeto Transfer post-creación).
    /// - Países EEA + NO + LI → "eur" (zona euro o asimilados a efectos de la plataforma).
    /// - Mantener sincronizado con <see cref="SupportedConnectCountries"/>.
    /// </summary>
    public static class StripeCurrencyMapping
    {
        /// <summary>
        /// Divisa por país (lowercase, formato Stripe). Devuelve "eur" como default
        /// para cualquier país EEA o desconocido (no rompe — Stripe rechazaría más tarde
        /// con un error claro si fuese país no soportado).
        /// </summary>
        // 🛡️ Round 28: corregido mapping. Antes SE/DK/NO/LI/PL/HU/CZ/BG/RO estaban mapeados
        // a "eur" (INCORRECTO). Cada país tiene su propia divisa local que Stripe espera
        // como default_currency de la cuenta Connect. Fuente: docs.stripe.com/api/country_specs
        private static readonly Dictionary<string, string> CountryCurrency =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                // ── Zona euro (20 países usan EUR como moneda oficial)
                { "AT", "eur" }, { "BE", "eur" }, { "CY", "eur" }, { "EE", "eur" },
                { "FI", "eur" }, { "FR", "eur" }, { "DE", "eur" }, { "GR", "eur" },
                { "HR", "eur" }, // Croacia adoptó EUR en 2023-01-01
                { "IE", "eur" }, { "IT", "eur" }, { "LV", "eur" }, { "LT", "eur" },
                { "LU", "eur" }, { "MT", "eur" }, { "NL", "eur" }, { "PT", "eur" },
                { "SK", "eur" }, { "SI", "eur" }, { "ES", "eur" },
                // ── EU/EEA no eurozona — divisa local
                { "SE", "sek" },  // Suecia — corona sueca
                { "DK", "dkk" },  // Dinamarca — corona danesa
                { "NO", "nok" },  // Noruega — corona noruega (EEA, no UE)
                { "PL", "pln" },  // Polonia — złoty
                { "HU", "huf" },  // Hungría — forint (zero-decimal)
                { "CZ", "czk" },  // República Checa — corona checa
                { "BG", "bgn" },  // Bulgaria — lev
                { "RO", "ron" },  // Rumanía — leu
                // ── Suiza y Liechtenstein — franco suizo
                { "CH", "chf" },
                { "LI", "chf" },  // Liechtenstein usa CHF (no EUR)
                // ── No-EEA soportados
                { "GB", "gbp" },  // Reino Unido — libra esterlina
                { "US", "usd" },
                { "CA", "cad" },
            };

        /// <summary>
        /// Devuelve la divisa Stripe (lowercase) para un país. Si el país es null/vacío
        /// o desconocido, devuelve "eur" como fallback seguro (la mayoría del tráfico).
        /// </summary>
        public static string GetCurrencyForCountry(string? countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode))
            {
                return "eur";
            }

            return CountryCurrency.TryGetValue(countryCode.Trim(), out var currency)
                ? currency
                : "eur";
        }

        /// <summary>
        /// Resolución preferida: si `accountDefaultCurrency` viene poblado por Stripe
        /// (objeto Account.DefaultCurrency), úsalo directamente. Si está vacío,
        /// fallback al mapping por país. Esto cubre el caso edge en el que un experto
        /// configura una divisa distinta a la de su país en su cuenta Connect.
        /// </summary>
        public static string ResolveTransferCurrency(string? accountDefaultCurrency, string? countryCode)
        {
            if (!string.IsNullOrWhiteSpace(accountDefaultCurrency))
            {
                return accountDefaultCurrency.Trim().ToLowerInvariant();
            }
            return GetCurrencyForCountry(countryCode);
        }
    }
}
