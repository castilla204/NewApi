using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    /// <summary>
    /// Servicio centralizado para validaciones de Stripe y expertos
    /// </summary>
    public interface IStripeValidationService
    {
        /// <summary>
        /// Valida si un experto puede recibir pagos (excluyendo cuentas rechazadas para manejo administrativo)
        /// </summary>
        /// <param name="expertProfile">Perfil del experto a validar</param>
        /// <param name="operation">Operación que se está intentando realizar</param>
        /// <returns>Resultado de la validación con mensaje de error si aplica</returns>
        Task<(bool IsValid, string ErrorMessage, string StripeStatus, bool RequiresStripeSetup, bool CanRetry)> ValidateExpertCanReceivePaymentsAsync(ExpertProfile expertProfile, string operation = "operation");

        /// <summary>
        /// Valida si un experto puede crear servicios
        /// </summary>
        /// <param name="expertProfile">Perfil del experto a validar</param>
        /// <returns>Resultado de la validación con mensaje de error si aplica</returns>
        Task<(bool IsValid, string ErrorMessage)> ValidateExpertCanCreateServicesAsync(ExpertProfile expertProfile);

        /// <summary>
        /// Valida si un experto puede proponer citas
        /// </summary>
        /// <param name="expertProfile">Perfil del experto a validar</param>
        /// <returns>Resultado de la validación con mensaje de error si aplica</returns>
        Task<(bool IsValid, string ErrorMessage)> ValidateExpertCanProposeAppointmentsAsync(ExpertProfile expertProfile);
    }
}
