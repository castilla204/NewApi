using Hangfire;
using newApi.DataLayer.Models.DTOs;
using System.Globalization;
using System.IO;

namespace newApi.Services
{
    /// <summary>
    /// Service for handling business notifications (emails, sms, etc.)
    /// </summary>
    public interface INotificationService
    {
        /// <summary>
        /// Sends an appointment confirmation email with a nice HTML template
        /// </summary>
        Task SendAppointmentConfirmationEmailAsync(string toEmail, string userName, string date, string location, bool isExpert, int searchHireId);

        /// <summary>
        /// Sends a welcome email to new users
        /// </summary>
        Task SendWelcomeEmailAsync(string toEmail, string userName);

        /// <summary>
        /// Sends a general notification/warning email
        /// </summary>
        Task SendGeneralNotificationEmailAsync(string toEmail, string userName, string title, string message, string? actionText = null, string? actionUrl = null);

        /// <summary>
        /// Sends a service completion email requesting a review
        /// </summary>
        Task SendServiceCompletionEmailAsync(string toEmail, string userName, string serviceName, string expertName, int searchHireId);

        /// <summary>
        /// 🛡️ Round 16: envía un código OTP de verificación de email (registro / reset password / step-up).
        /// El envío es SÍNCRONO con throwOnError=true porque el usuario está esperando en pantalla.
        /// </summary>
        /// <param name="toEmail">Email destinatario.</param>
        /// <param name="code">Código de 6 dígitos en plano (NO se almacena, solo se envía).</param>
        /// <param name="purpose">Propósito del OTP — cambia el copy del email.</param>
        /// <param name="expirationMinutes">Minutos hasta expiración (default 10 — NIST SHALL).</param>
        Task SendVerificationCodeEmailAsync(string toEmail, string code, newApi.DataLayer.Models.PostGresModels.EmailVerificationPurpose purpose, int expirationMinutes = 10);
    }

    /// <summary>
    /// Implementation of INotificationService
    /// </summary>
    public class NotificationService : INotificationService
    {
        private readonly IEmailService _emailService;

        /// <summary>
        /// Constructor
        /// </summary>
        public NotificationService(IEmailService emailService)
        {
            _emailService = emailService;
        }

        #region Helper: Base Template Generator
        
        /// <summary>
        /// Generates the common HTML structure for all emails using the sophisticated template
        /// </summary>
        private string GenerateEmailTemplate(string title, string content, string? actionText = null, string? actionUrl = null, string headerIcon = "📢")
        {
            var year = DateTime.UtcNow.Year.ToString();
            
            // Try to read the template file
            string templateHtml;
            try 
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Resources", "EmailTemplate.html");
                if (File.Exists(path))
                {
                    templateHtml = File.ReadAllText(path);
                }
                else
                {
                    // Fallback to a basic template if file is missing
                    templateHtml = "<html><body><h1>{{TITLE}}</h1><div>{{CONTENT}}</div>{{ACTION_BUTTON}}</body></html>";
                }
            }
            catch
            {
                templateHtml = "<html><body><h1>{{TITLE}}</h1><div>{{CONTENT}}</div>{{ACTION_BUTTON}}</body></html>";
            }

            // Create Action Button HTML if needed - Professional discrete style
            var actionButtonHtml = "";
            if (!string.IsNullOrEmpty(actionText) && !string.IsNullOrEmpty(actionUrl))
            {
                actionButtonHtml = $@"
                    <table align='center' border='0' cellpadding='0' cellspacing='0' style='border-collapse:collapse;border-spacing:0;padding:16px 0 0 0;text-align:center;vertical-align:top;width:100%'>
                        <tbody>
                            <tr>
                                <td align='center' style='padding:0'>
                                    <!--[if mso]>
                                    <v:roundrect xmlns:v='urn:schemas-microsoft-com:vml' xmlns:w='urn:schemas-microsoft-com:office:word' href='{actionUrl}' style='height:36px;v-text-anchor:middle;width:180px;' arcsize='15%' strokecolor='#2563EB' fillcolor='#2563EB'>
                                    <w:anchorlock/>
                                    <center style='color:#ffffff;font-family:Helvetica,Arial,sans-serif;font-size:13px;font-weight:600;'>{actionText}</center>
                                    </v:roundrect>
                                    <![endif]-->
                                    <!--[if !mso]><!-->
                                    <a href='{actionUrl}' style='background-color:#2563EB;border-radius:6px;color:#ffffff;display:inline-block;font-family:Helvetica,Arial,sans-serif;font-size:13px;font-weight:600;line-height:36px;mso-hide:all;padding:0 24px;text-align:center;text-decoration:none;'>{actionText}</a>
                                    <!--<![endif]-->
                                </td>
                            </tr>
                        </tbody>
                    </table>";
            }

            // Replace Placeholders
            var finalHtml = templateHtml
                .Replace("{{TITLE}}", title)
                .Replace("{{CONTENT}}", content)
                .Replace("{{ACTION_BUTTON}}", actionButtonHtml)
                .Replace("{{YEAR}}", year);

            return finalHtml;
        }

        /// <summary>
        /// Round 24 — formatea un importe con currency real (charge) y opcional con conversión a la
        /// moneda preferida del usuario en línea secundaria. Devuelve HTML listo para inyectar en
        /// el {{CONTENT}} de la plantilla.
        ///
        /// Ejemplos:
        ///   FormatCurrencyAmount(120.50m, "EUR", null, null)           → "120,50 EUR"
        ///   FormatCurrencyAmount(120.50m, "EUR", "USD", 130m)          → "120,50 EUR <small>(≈ 130,00 USD)</small>"
        ///
        /// NO hace conversión por sí mismo — el caller debe pasar el converted amount precalculado
        /// (típicamente vía ExchangeRateService.ConvertAsync) para mantener este método sin
        /// dependencias y testeable como string formatter puro.
        /// </summary>
        public static string FormatCurrencyAmount(
            decimal amount,
            string chargeCurrency = "EUR",
            string? userPreferredCurrency = null,
            decimal? convertedAmount = null)
        {
            var charge = string.IsNullOrEmpty(chargeCurrency) ? "EUR" : chargeCurrency.ToUpperInvariant();
            var primary = amount.ToString("N2", CultureInfo.GetCultureInfo("es-ES"));
            if (string.IsNullOrEmpty(userPreferredCurrency)
                || userPreferredCurrency.Equals(charge, StringComparison.OrdinalIgnoreCase)
                || !convertedAmount.HasValue)
            {
                return $"<strong>{primary} {charge}</strong>";
            }
            var pref = userPreferredCurrency.ToUpperInvariant();
            var secondary = convertedAmount.Value.ToString("N2", CultureInfo.GetCultureInfo("es-ES"));
            return $"<strong>{primary} {charge}</strong> <small style='color:#6B7280;'>(≈ {secondary} {pref})</small>";
        }

        #endregion

        /// <inheritdoc />
        public async Task SendAppointmentConfirmationEmailAsync(string toEmail, string userName, string date, string location, bool isExpert, int searchHireId)
        {
            BackgroundJob.Enqueue(() => SendAppointmentConfirmationEmailJob(toEmail, userName, date, location, isExpert, searchHireId));
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task SendWelcomeEmailAsync(string toEmail, string userName)
        {
            BackgroundJob.Enqueue(() => SendWelcomeEmailJob(toEmail, userName));
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task SendGeneralNotificationEmailAsync(string toEmail, string userName, string title, string message, string? actionText = null, string? actionUrl = null)
        {
            BackgroundJob.Enqueue(() => SendGeneralNotificationEmailJob(toEmail, userName, title, message, actionText, actionUrl));
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task SendServiceCompletionEmailAsync(string toEmail, string userName, string serviceName, string expertName, int searchHireId)
        {
            BackgroundJob.Enqueue(() => SendServiceCompletionEmailJob(toEmail, userName, serviceName, expertName, searchHireId));
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task SendVerificationCodeEmailAsync(string toEmail, string code, newApi.DataLayer.Models.PostGresModels.EmailVerificationPurpose purpose, int expirationMinutes = 10)
        {
            // 🛡️ ENVÍO SÍNCRONO: el usuario espera la pantalla "introduce tu código". No podemos
            // encolar en Hangfire (latencia variable). throwOnError=true para que el endpoint
            // pueda devolver 503 si SMTP cae (el cliente pedirá reenvío).
            var (subject, title, intro) = purpose switch
            {
                newApi.DataLayer.Models.PostGresModels.EmailVerificationPurpose.PasswordReset =>
                    ("Código para restablecer tu contraseña",
                     "Restablece tu contraseña",
                     "Has solicitado restablecer tu contraseña. Usa el siguiente código para confirmar tu identidad:"),
                newApi.DataLayer.Models.PostGresModels.EmailVerificationPurpose.StepUp =>
                    ("Código de confirmación de seguridad",
                     "Confirma tu identidad",
                     "Para completar esta acción necesitamos verificar que eres tú. Introduce el siguiente código:"),
                _ => // EmailVerification
                    ("Verifica tu correo en Inspecciono",
                     "Verifica tu correo",
                     "¡Bienvenido! Para activar tu cuenta introduce el siguiente código de verificación:")
            };

            // El código se renderiza en un bloque destacado, monoespaciado, sin botón de acción
            // (los códigos OTP NO deben ir como enlace clicable — phishing risk).
            var content = $@"
                <p style='margin:0 0 16px 0;'>{intro}</p>
                <table role='presentation' cellpadding='0' cellspacing='0' style='margin:24px auto;'>
                    <tr>
                        <td align='center' style='background-color:#F3F4F6;border:1px solid #E5E7EB;border-radius:10px;padding:20px 36px;'>
                            <span style='font-family:""SF Mono"",Menlo,Consolas,monospace;font-size:32px;font-weight:700;letter-spacing:10px;color:#111827;'>{code}</span>
                        </td>
                    </tr>
                </table>
                <p style='margin:0 0 8px 0;font-size:13px;color:#6B7280;'>Este código caduca en <strong>{expirationMinutes} minutos</strong> y solo puede usarse una vez.</p>
                <p style='margin:0;font-size:13px;color:#9CA3AF;'>Si no has solicitado esto, ignora este correo. Tu cuenta sigue segura.</p>";

            // Sin actionUrl — explícitamente. Los OTPs NUNCA llevan link clicable.
            var htmlBody = GenerateEmailTemplate(title, content, actionText: null, actionUrl: null, headerIcon: "🔐");
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        #region Background Jobs

        /// <summary>
        /// Job to send the appointment confirmation email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendAppointmentConfirmationEmailJob(string toEmail, string userName, string date, string location, bool isExpert, int searchHireId)
        {
            var subject = isExpert ? "Cita confirmada" : "Tu cita ha sido confirmada";
            var title = isExpert ? "Cita confirmada" : "Tu cita está confirmada";
            
            var messageStart = isExpert 
                ? $"Has confirmado la cita para el {date}."
                : $"El experto ha confirmado tu cita para el {date}.";

            var content = $@"
                <p style='margin:0 0 12px 0;'>Hola {userName},</p>
                <p style='margin:0 0 16px 0;'>{messageStart}</p>
                <p style='margin:0 0 6px 0;color:#374151;'><strong>Fecha:</strong> {date}</p>
                <p style='margin:0 0 16px 0;color:#374151;'><strong>Ubicación:</strong> {location}</p>
                <p style='margin:0;font-size:13px;color:#6B7280;'>Si necesitas hacer cambios, accede a tu panel de citas.</p>";

            var actionUrl = $"https://inspecciono.com/detalles/{searchHireId}";
            var htmlBody = GenerateEmailTemplate(title, content, "Ver detalles", actionUrl, "📅");
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        /// <summary>
        /// Job to send the welcome email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendWelcomeEmailJob(string toEmail, string userName)
        {
            var subject = "Bienvenido a Inspecciono";
            var title = "Bienvenido a Inspecciono";

            var content = $@"
                <p style='margin:0 0 12px 0;'>Hola {userName},</p>
                <p style='margin:0 0 12px 0;'>Gracias por registrarte en Inspecciono. Estamos aquí para ayudarte a encontrar expertos verificados para tus inspecciones.</p>
                <p style='margin:0 0 12px 0;'>Para empezar:</p>
                <p style='margin:0 0 6px 0;padding-left:12px;'>• Completa tu perfil</p>
                <p style='margin:0 0 6px 0;padding-left:12px;'>• Explora los servicios disponibles</p>
                <p style='margin:0 0 16px 0;padding-left:12px;'>• Contacta con nuestro soporte si tienes dudas</p>
                <p style='margin:0;font-size:13px;color:#6B7280;'>Los perfiles completos obtienen mejores resultados.</p>";

            var htmlBody = GenerateEmailTemplate(title, content, "Completar perfil", "https://inspecciono.com/profile", "👋");
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        /// <summary>
        /// Job to send a general notification email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendGeneralNotificationEmailJob(string toEmail, string userName, string title, string message, string? actionText, string? actionUrl)
        {
            var subject = title;
            
            var content = $@"
                <p style='margin:0 0 12px 0;'>Hola {userName},</p>
                <p style='margin:0 0 12px 0;'>{message}</p>
                <p style='margin:0;font-size:13px;color:#6B7280;'>Si necesitas más información, accede a tu cuenta.</p>";

            var htmlBody = GenerateEmailTemplate(title, content, actionText, actionUrl, "📢");
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        /// <summary>
        /// Job to send the service completion email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendServiceCompletionEmailJob(string toEmail, string userName, string serviceName, string expertName, int searchHireId)
        {
            var subject = "Servicio completado";
            var title = "Tu servicio ha finalizado";

            var content = $@"
                <p style='margin:0 0 12px 0;'>Hola {userName},</p>
                <p style='margin:0 0 12px 0;'>El servicio <strong>{serviceName}</strong> realizado por <strong>{expertName}</strong> ha sido completado.</p>
                <p style='margin:0 0 16px 0;'>Tu valoración ayuda a otros usuarios y mejora la comunidad.</p>
                <p style='margin:0;font-size:13px;color:#6B7280;'>Solo te llevará un momento.</p>";

            var actionUrl = $"https://inspecciono.com/detalles/{searchHireId}";
            var htmlBody = GenerateEmailTemplate(title, content, "Dejar valoración", actionUrl, "⭐");
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        #endregion
    }
}
