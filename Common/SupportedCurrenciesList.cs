using System.Collections.Generic;

namespace newApi.Common
{
    /// <summary>
    /// 🌍 Round 22: Lista canónica de divisas (ISO 4217, código de 3 letras en MAYÚSCULAS)
    /// que el usuario puede seleccionar como `PreferredCurrency` desde el selector de la
    /// barra de navegación.
    ///
    /// ALCANCE
    /// -------
    /// Cubre las divisas necesarias para los países soportados por Stripe Connect (ver
    /// <see cref="SupportedConnectCountries"/>) más los principales mercados emisores
    /// EEA. Mantener sincronizado con <see cref="StripeCurrencyMapping"/>.
    ///
    /// NOTAS
    /// -----
    /// - Se almacena en MAYÚSCULAS en la BD (`User.PreferredCurrency`). En Stripe el
    ///   formato es minúsculas — convertir en la frontera con Stripe si hace falta.
    /// - Null en BD = sin preferencia (auto-detección IP/país). Validar SOLO valores
    ///   no nulos contra esta lista.
    /// </summary>
    public static class SupportedCurrenciesList
    {
        /// <summary>
        /// Códigos ISO 4217 en MAYÚSCULAS. Comparación case-insensitive vía el HashSet.
        /// </summary>
        // 🛡️ Round 28: ampliado para cubrir TODAS las divisas locales de países en
        // SupportedConnectCountries. Antes solo EUR/USD/GBP/CAD/CHF → un experto sueco
        // no podía seleccionar SEK como PreferredCurrency.
        public static readonly HashSet<string> Codes = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            // Zona euro
            "EUR",
            // No-EEA principales
            "USD", // United States
            "GBP", // United Kingdom
            "CAD", // Canada
            "CHF", // Switzerland + Liechtenstein
            // EU/EEA con divisa propia
            "SEK", // Sweden
            "DKK", // Denmark
            "NOK", // Norway
            "PLN", // Poland
            "HUF", // Hungary
            "CZK", // Czech Republic
            "BGN", // Bulgaria
            "RON", // Romania
        };

        /// <summary>
        /// True si <paramref name="currency"/> es ISO 4217 de 3 letras y está en la
        /// lista soportada. Null/vacío → false.
        /// </summary>
        public static bool IsSupported(string? currency)
            => !string.IsNullOrWhiteSpace(currency)
               && currency.Trim().Length == 3
               && Codes.Contains(currency.Trim());

        /// <summary>
        /// Normaliza al formato canónico (MAYÚSCULAS, trim). Devuelve null si no es válida.
        /// </summary>
        public static string? Normalize(string? currency)
        {
            if (string.IsNullOrWhiteSpace(currency)) return null;
            var upper = currency.Trim().ToUpperInvariant();
            return Codes.Contains(upper) ? upper : null;
        }
    }
}
