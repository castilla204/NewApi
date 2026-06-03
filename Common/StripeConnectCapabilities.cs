using System.Collections.Generic;
using Stripe;

namespace newApi.Common
{
    /// <summary>
    /// 🛡️ Round 28 — Sprint US: capabilities de Stripe Connect Express por país.
    ///
    /// CONTEXTO DEL BUG
    /// ----------------
    /// Antes el AccountCreateOptions pedía solo <c>transfers</c> para TODOS los países.
    /// Funcionaba para EEA/CH/LI (modelo "separate charges and transfers"), pero Stripe
    /// rechaza la creación en US/CA/GB con:
    ///   "You cannot request the `transfers` capability without the `card_payments`
    ///    capability for accounts in US."
    ///
    /// REGLA DE STRIPE
    /// ---------------
    /// - EEA + EFTA (CH, LI): "transfers" basta cuando la plataforma usa separate charges
    ///   and transfers (cliente paga a la plataforma; plataforma transfiere al experto).
    /// - US, CA, GB: Stripe exige también "card_payments" — aunque el cliente NO pague
    ///   directamente al experto, la cuenta Connect en estos países debe declarar la
    ///   capability "card_payments" como parte del onboarding KYC/AML.
    ///
    /// IMPORTANTE: "card_payments" se solicita pero queda **latente** — la plataforma
    /// sigue cobrando al cliente. El experto US/CA/GB no recibe charges directamente.
    /// Stripe solo lo necesita para completar el onboarding identitario.
    ///
    /// Mantener sincronizado con <see cref="SupportedConnectCountries"/>.
    /// Fuente: https://docs.stripe.com/connect/account-capabilities
    /// </summary>
    public static class StripeConnectCapabilities
    {
        /// <summary>
        /// Países donde Stripe exige <c>card_payments</c> además de <c>transfers</c>
        /// para crear una cuenta Connect Express, INCLUSO si la plataforma usa
        /// separate charges and transfers.
        /// </summary>
        private static readonly HashSet<string> RequiresCardPayments =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "US",
                "CA",
                "GB",
                // AU se añadiría aquí si SupportedConnectCountries lo incluye en el futuro.
            };

        /// <summary>
        /// Devuelve el conjunto correcto de capabilities para una nueva cuenta Connect Express
        /// según el país del experto. Defensivo: si countryCode es null/vacío, devuelve solo
        /// <c>transfers</c> (comportamiento legacy seguro para EEA).
        /// </summary>
        public static AccountCapabilitiesOptions BuildCapabilitiesFor(string? countryCode)
        {
            var capabilities = new AccountCapabilitiesOptions
            {
                Transfers = new AccountCapabilitiesTransfersOptions { Requested = true }
            };

            if (!string.IsNullOrWhiteSpace(countryCode)
                && RequiresCardPayments.Contains(countryCode.Trim()))
            {
                // 🛡️ Stripe exige card_payments para US/CA/GB aunque la capability quede latente
                // (no se usa para cobrar al cliente — sigue siendo separate charges and transfers).
                capabilities.CardPayments = new AccountCapabilitiesCardPaymentsOptions { Requested = true };
            }

            // 🛡️ Round 28 — Sprint US-2 (SUS2-1): tax_reporting_us_1099_misc para sellers US.
            // SIN esta capability:
            //   - Stripe NO captura el W-9 (TIN, legal name) durante el hosted onboarding.
            //   - Stripe NO genera ni filia el 1099-MISC al IRS por la plataforma.
            //   - La plataforma (que usa Stripe como PSE US) queda como responsable última
            //     vía Connect Platform Agreement → multa IRS $290-$630/seller no reportado.
            // CON esta capability Stripe automatiza el ciclo anual sin intervención.
            // NO añadimos 1099_k: Stripe ya lo emite por defecto como PSE — solo habilitarlo
            // transferiría la obligación de filer a la plataforma sin necesidad operativa.
            // CA/GB tienen sus propios regímenes nacionales y NO usan 1099 (Stripe maneja T4A/CT61).
            if (!string.IsNullOrWhiteSpace(countryCode)
                && string.Equals(countryCode.Trim(), "US", System.StringComparison.OrdinalIgnoreCase))
            {
                capabilities.TaxReportingUs1099Misc = new AccountCapabilitiesTaxReportingUs1099MiscOptions { Requested = true };
            }

            return capabilities;
        }

        /// <summary>
        /// True si el país requiere <c>card_payments</c> además de <c>transfers</c>. Útil para
        /// el handler de <c>capability.updated</c> webhook y validación post-onboarding.
        /// </summary>
        public static bool CountryRequiresCardPayments(string? countryCode)
        {
            return !string.IsNullOrWhiteSpace(countryCode)
                   && RequiresCardPayments.Contains(countryCode.Trim());
        }
    }
}
