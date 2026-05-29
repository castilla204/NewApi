using System.Collections.Generic;

namespace newApi.Common
{
    /// <summary>
    /// 🛡️ Round 13 — N9: lista canónica de países UE (27 estados miembros 2026)
    /// a efectos del IVA intracomunitario y la inversión del sujeto pasivo
    /// (Reverse Charge, Art 196 Dir 2006/112/CE / Art 84.Uno.2º Ley 37/1992).
    ///
    /// Países NO incluidos a propósito:
    ///  - GB (post-Brexit): no UE desde 2021 → mecanismo es "extracomunitario", NO reverse charge intracom.
    ///  - CH, NO, IS, LI: EEA o asociados, NO miembros UE. Aunque algunos tengan VAT, la regla
    ///    de reverse charge intracomunitario del Art 196 no aplica fuera de UE.
    ///  - US, CA: no UE.
    ///
    /// El texto "Operación intracomunitaria - Inversión del sujeto pasivo" SOLO debe aparecer
    /// en facturas cuyo cliente tenga VAT válido de un país de esta lista (distinto del país de
    /// la plataforma, ES). Para otros países: aunque IVA == 0 por exención (extracom, Art 21
    /// Ley 37/1992), el texto y referencia legal son distintos.
    /// </summary>
    public static class EuVatCountries
    {
        public static readonly HashSet<string> Codes = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
            "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
            "PL", "PT", "RO", "SK", "SI", "ES", "SE"
            // GB salió por Brexit; CH/NO/LI son EEA pero no UE.
        };

        /// <summary>True si el country code (ISO 3166-1 alpha-2) corresponde a un estado miembro de la UE.</summary>
        public static bool IsEu(string? countryCode)
            => !string.IsNullOrWhiteSpace(countryCode)
               && Codes.Contains(countryCode.Trim());
    }
}
