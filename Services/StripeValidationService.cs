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
        public StripeValidationService(AppDbContext context)
        {
            _context = context;
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

            // ✅ A4: PendingVerification solo es aceptable si Stripe confirma EN VIVO que la cuenta
            // puede cobrar. Antes se permitía SIEMPRE (el flag local podía estar obsoleto), de modo
            // que se podía cobrar al cliente por un experto cuyos pagos estaban deshabilitados:
            // el dinero entraba pero el payout al experto quedaba bloqueado (intervención manual).
            // Esta validación se usa solo en los puntos de cobro (crear sesión de pago), por lo que
            // ante la duda fallamos CERRADO (no cobrar) en lugar de confiar en un flag potencialmente viejo.
            if (stripeStatus == StripeStatus.PendingVerification)
            {
                if (string.IsNullOrEmpty(expertProfile.StripeAccountId))
                {
                    return (false, $"No se puede realizar {operation}. El experto no ha completado la configuración de su cuenta de pagos.", stripeStatus.ToString(), true, true);
                }

                try
                {
                    var account = await new Stripe.AccountService().GetAsync(expertProfile.StripeAccountId);
                    // 🔧 FIX F4: separate charges & transfers — el experto onboarda SOLO con la capability
                    // "transfers", así que ChargesEnabled es false de forma LEGÍTIMA. El criterio de "puede
                    // cobrar" debe ser el MISMO que el transfer real (RefundService.cs:1087): capability
                    // transfers activa + payouts. Exigir ChargesEnabled bloqueaba a expertos transfers-only
                    // válidos (PI cancelado en la captura B5 → contratación perdida).
                    if (account.PayoutsEnabled && account.Capabilities?.Transfers == "active")
                    {
                        return (true, string.Empty, stripeStatus.ToString(), false, false);
                    }

                    return (false, $"No se puede realizar {operation}. La cuenta de pagos del experto aún no está habilitada para recibir pagos.", stripeStatus.ToString(), false, true);
                }
                catch (Exception)
                {
                    // Fail-closed: si no podemos verificar el estado en vivo, no permitir el cobro.
                    return (false, $"No se puede verificar la cuenta de pagos del experto en este momento. Inténtalo de nuevo en unos minutos.", stripeStatus.ToString(), false, true);
                }
            }

            // Bloquear cuentas rechazadas y desautorizadas
            if (stripeStatus == StripeStatus.Rejected)
            {
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

            // ✅ FIX: Permitir PendingVerification si charges_enabled: true
            // PendingVerification es informativo, no bloqueante si Stripe permite operar
            if (expertProfile.StripeStatus == StripeStatus.PendingVerification)
            {
                // PendingVerification no bloquea si la cuenta puede operar
                // El estado se actualizará en el próximo webhook account.updated
                return (true, string.Empty);
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

            // ✅ FIX: Permitir PendingVerification si charges_enabled: true
            // PendingVerification es informativo, no bloqueante si Stripe permite operar
            if (expertProfile.StripeStatus == StripeStatus.PendingVerification)
            {
                // PendingVerification no bloquea si la cuenta puede operar
                return (true, string.Empty);
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
                return (false, message);
            }

            return (true, string.Empty);
        }
    }
}
