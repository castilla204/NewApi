namespace newApi.Services
{
    /// <summary>
    /// Stub para fase pre-VIES. Devuelve siempre Valid=true (no validación real).
    /// CRÍTICO: NO usar en producción una vez se haga el flip y se vaya a aplicar reverse-charge
    /// automático — sustituir por ViesValidatorReal antes de marcar reverse-charge en facturas formales.
    /// </summary>
    public class ViesValidatorStub : IViesValidator
    {
        public Task<ViesValidationResult> ValidateAsync(string countryCode, string vatNumber, CancellationToken ct = default)
        {
            return Task.FromResult(new ViesValidationResult
            {
                Valid = true,
                Inconclusive = false,
                RegisteredName = null,
                RegisteredAddress = null
            });
        }
    }
}
