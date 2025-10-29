using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    /// <summary>
    /// Servicio centralizado para validaciones de Stripe y expertos
    /// </summary>
    public class StripeValidationService : IStripeValidationService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<StripeValidationService> _logger;

        public StripeValidationService(AppDbContext context, ILogger<StripeValidationService> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// Valida si un experto puede recibir pagos (excluyendo cuentas rechazadas para manejo administrativo)
        /// </summary>
        public async Task<(bool IsValid, string ErrorMessage, string StripeStatus, bool RequiresStripeSetup, bool CanRetry)> ValidateExpertCanReceivePaymentsAsync(ExpertProfile expertProfile, string operation = "operation")
        {
            if (expertProfile == null)
            {
                return (false, "Perfil de experto no encontrado", "NotRequested", true, true);
            }

            var stripeStatus = expertProfile.StripeStatus;
            var onboardingCompleted = expertProfile.OnboardingCompleted;

            // Si está aprobado y completado, todo bien
            if (stripeStatus == StripeStatus.Approved && onboardingCompleted)
            {
                return (true, string.Empty, stripeStatus.ToString(), false, false);
            }

            // Bloquear cuentas rechazadas y desautorizadas
            if (stripeStatus == StripeStatus.Rejected)
            {
                _logger.LogWarning("Blocking rejected account: expertId={ExpertId}, operation={Operation}", 
                    expertProfile.UserId, operation);
                return (false, $"No se puede realizar {operation}. La cuenta de pagos del experto ha sido rechazada.", stripeStatus.ToString(), false, true);
            }

            // Bloquear otros casos
            var message = stripeStatus switch
            {
                StripeStatus.NotRequested => $"No se puede realizar {operation}. El experto no ha configurado su cuenta de pagos.",
                StripeStatus.Pending => $"No se puede realizar {operation}. El experto está en proceso de verificación de su cuenta de pagos.",
                StripeStatus.Deauthorized => $"No se puede realizar {operation}. La cuenta de pagos del experto ha sido desautorizada.",
                _ => $"No se puede realizar {operation}. El experto no puede recibir pagos en este momento."
            };

            var requiresStripeSetup = stripeStatus == StripeStatus.NotRequested;
            var canRetry = stripeStatus == StripeStatus.NotRequested || stripeStatus == StripeStatus.Rejected;

            _logger.LogWarning("Expert payment validation failed: expertId={ExpertId}, stripeStatus={StripeStatus}, operation={Operation}", 
                expertProfile.UserId, stripeStatus, operation);

            return (false, message, stripeStatus.ToString(), requiresStripeSetup, canRetry);
        }

        /// <summary>
        /// Valida si un experto puede crear servicios
        /// </summary>
        public async Task<(bool IsValid, string ErrorMessage)> ValidateExpertCanCreateServicesAsync(ExpertProfile expertProfile)
        {
            if (expertProfile == null)
            {
                return (false, "Perfil de experto no encontrado");
            }

            if (expertProfile.StripeStatus != StripeStatus.Approved || !expertProfile.OnboardingCompleted)
            {
                var message = expertProfile.StripeStatus switch
                {
                    StripeStatus.NotRequested => "No puedes crear servicios sin configurar tu cuenta de pagos.",
                    StripeStatus.Pending => "No puedes crear servicios mientras tu cuenta de pagos está siendo verificada.",
                    StripeStatus.Rejected => "Tu cuenta de pagos fue rechazada. Contacta al soporte para más información.",
                    StripeStatus.Deauthorized => "Tu cuenta de pagos ha sido desautorizada. Contacta al soporte para más información.",
                    _ => "No puedes crear servicios en este momento."
                };

                _logger.LogWarning("Expert service creation blocked: expertId={ExpertId}, stripeStatus={StripeStatus}", 
                    expertProfile.UserId, expertProfile.StripeStatus);

                return (false, message);
            }

            return (true, string.Empty);
        }

        /// <summary>
        /// Valida si un experto puede proponer citas
        /// </summary>
        public async Task<(bool IsValid, string ErrorMessage)> ValidateExpertCanProposeAppointmentsAsync(ExpertProfile expertProfile)
        {
            if (expertProfile == null)
            {
                return (false, "Perfil de experto no encontrado");
            }

            if (expertProfile.StripeStatus != StripeStatus.Approved || !expertProfile.OnboardingCompleted)
            {
                var message = expertProfile.StripeStatus switch
                {
                    StripeStatus.NotRequested => "No puedes proponer citas sin configurar tu cuenta de pagos.",
                    StripeStatus.Pending => "No puedes proponer citas mientras tu cuenta de pagos está siendo verificada.",
                    StripeStatus.Rejected => "Tu cuenta de pagos fue rechazada. Contacta al soporte para más información.",
                    StripeStatus.Deauthorized => "Tu cuenta de pagos ha sido desautorizada. Contacta al soporte para más información.",
                    _ => "No puedes proponer citas en este momento."
                };

                _logger.LogWarning("Expert appointment proposal blocked: expertId={ExpertId}, stripeStatus={StripeStatus}", 
                    expertProfile.UserId, expertProfile.StripeStatus);

                return (false, message);
            }

            return (true, string.Empty);
        }
    }
}
