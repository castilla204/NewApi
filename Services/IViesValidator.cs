namespace newApi.Services
{
    /// <summary>
    /// Valida NIFs intracomunitarios contra el servicio VIES de la Comisión Europea.
    /// 🔧 PLACEHOLDER: la implementación real (HttpClient SOAP a
    /// https://ec.europa.eu/taxation_customs/vies/services/checkVatService.wsdl) se hará
    /// en la fase posterior al flip. La interfaz se inyecta YA para que el código consumidor
    /// compile y los tests puedan stubbearla.
    ///
    /// CONTRATO: en stub, ViesValidatorStub devuelve siempre Valid=true.
    /// La impl real debe cachear (TTL ~24h, art. 7 Reglamento UE 904/2010) y degradar a
    /// Inconclusive si VIES está caído (no rechazar la factura por incidencia EU).
    /// </summary>
    public interface IViesValidator
    {
        /// <summary>
        /// Valida par (countryCode, vatNumber). countryCode = ISO-3166-1 alpha-2.
        /// </summary>
        Task<ViesValidationResult> ValidateAsync(string countryCode, string vatNumber, CancellationToken ct = default);
    }

    public class ViesValidationResult
    {
        public bool Valid { get; init; }
        public string? RegisteredName { get; init; }
        public string? RegisteredAddress { get; init; }
        /// <summary>true si VIES no respondió y no pudimos validar (no rechazar la operación).</summary>
        public bool Inconclusive { get; init; }
    }
}
