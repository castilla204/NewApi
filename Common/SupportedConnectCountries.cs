namespace newApi.Common
{
    /// <summary>
    /// Lista ÚNICA de países soportados para recibir pagos vía Stripe Connect
    /// (separate charges &amp; transfers desde una plataforma EEA): EEA-27 + NO/LI + US/CA/GB/CH.
    /// IS (Islandia) queda fuera: Stripe no lo soporta. LI (Liechtenstein) SÍ es cuenta conectada soportada (EEA).
    /// LatAm (MX, AR...) requiere Cross-border payouts vía Stripe Sales → no se habilita aquí.
    ///
    /// Fuente de verdad compartida por:
    ///  - UserService.BecomeExpert (paso 1: promoción a Expert)  ← gate añadido
    ///  - SubscriptionController.CreateExpertOnboarding (paso 2: onboarding Stripe)
    /// Mantener ambos pasos con la MISMA lista para que no diverjan.
    /// </summary>
    public static class SupportedConnectCountries
    {
        public static readonly HashSet<string> Codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AT","BE","BG","HR","CY","CZ","DK","EE","FI","FR","DE","GR","HU","IE","IT",
            "LV","LT","LU","MT","NL","PL","PT","RO","SK","SI","ES","SE","NO","LI", // IS fuera (Stripe no lo soporta); LI SÍ soportado
            "US","CA","GB","CH"
        };

        /// <summary>True si el país (ISO 3166-1 alpha-2) puede recibir pagos. Null/vacío → false.</summary>
        public static bool IsSupported(string? countryCode)
            => !string.IsNullOrWhiteSpace(countryCode)
               && Codes.Contains(countryCode.Trim());
    }
}
