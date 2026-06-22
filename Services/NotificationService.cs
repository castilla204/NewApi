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

        #region Render: Pure subject + html builders

        /// <summary>
        /// Render puro del email de bienvenida (sin envío). Devuelve (subject, html).
        /// </summary>
        public static (string subject, string html) RenderWelcome(string userName)
        {
            var subject = "Bienvenido a Inspecciono";
            var title = "Bienvenido a Inspecciono";
            var content = $@"
                <p style='margin:0 0 16px 0;'>Hola {userName},</p>
                <p style='margin:0 0 16px 0;'>Gracias por registrarte en Inspecciono. Estamos aquí para ayudarte a encontrar expertos verificados para tus inspecciones.</p>
                <p style='margin:0 0 8px 0;'>Para empezar:</p>
                <ul style='margin:0 0 16px 0;padding-left:22px;color:#334155;'>
                    <li style='margin:0 0 6px 0;line-height:26px;'>Completa tu perfil</li>
                    <li style='margin:0 0 6px 0;line-height:26px;'>Explora los servicios disponibles</li>
                    <li style='margin:0;line-height:26px;'>Contacta con nuestro soporte si tienes dudas</li>
                </ul>
                <p style='margin:0;font-size:13px;color:#6B7280;'>Los perfiles completos obtienen mejores resultados.</p>";
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, "Completar perfil", "https://inspecciono.com/profile", "👋");
            return (subject, html);
        }

        /// <summary>
        /// Render puro del email de confirmación de cita (sin envío). Devuelve (subject, html).
        /// </summary>
        public static (string subject, string html) RenderAppointmentConfirmation(string userName, string date, string location, bool isExpert, int searchHireId)
        {
            var subject = isExpert ? "Cita confirmada" : "Tu cita ha sido confirmada";
            var title = isExpert ? "Cita confirmada" : "Tu cita está confirmada";
            var messageStart = isExpert
                ? $"Has confirmado la cita para el {date}."
                : $"El experto ha confirmado tu cita para el {date}.";
            var content = $@"
                <p style='margin:0 0 16px 0;'>Hola {userName},</p>
                <p style='margin:0 0 16px 0;'>{messageStart}</p>
                <table role='presentation' cellpadding='0' cellspacing='0' border='0' style='margin:0 0 16px 0;width:100%;'>
                    <tr>
                        <td class='panel-accent' style='background-color:#F8FAFC;border-left:3px solid #2563EB;border-radius:8px;padding:14px 18px;'>
                            <p class='panel-text' style='margin:0 0 4px 0;font-size:15px;color:#334155;'><strong class='panel-strong' style='color:#0F172A;'>Fecha:</strong> {date}</p>
                            <p class='panel-text' style='margin:0;font-size:15px;color:#334155;'><strong class='panel-strong' style='color:#0F172A;'>Ubicación:</strong> {location}</p>
                        </td>
                    </tr>
                </table>
                <p style='margin:0;font-size:13px;color:#6B7280;'>Si necesitas hacer cambios, accede a tu panel de citas.</p>";
            var actionUrl = $"https://inspecciono.com/searchhire/{searchHireId}";
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, "Ver detalles", actionUrl, "📅");
            return (subject, html);
        }

        /// <summary>
        /// Render puro del email de notificación general (sin envío). Devuelve (subject, html).
        /// </summary>
        public static (string subject, string html) RenderGeneralNotification(string userName, string title, string message, string? actionText, string? actionUrl)
        {
            var subject = title;
            var content = $@"
                <p style='margin:0 0 16px 0;'>Hola {userName},</p>
                <p style='margin:0 0 16px 0;'>{message}</p>
                <p style='margin:0;font-size:13px;color:#6B7280;'>Si necesitas más información, accede a tu cuenta.</p>";
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, actionText, actionUrl, "📢");
            return (subject, html);
        }

        /// <summary>
        /// Render puro del email de servicio completado / solicitud de reseña (sin envío). Devuelve (subject, html).
        /// </summary>
        public static (string subject, string html) RenderServiceCompletion(string userName, string serviceName, string expertName, int searchHireId)
        {
            var subject = "Servicio completado";
            var title = "Tu servicio ha finalizado";
            var content = $@"
                <p style='margin:0 0 16px 0;'>Hola {userName},</p>
                <p style='margin:0 0 16px 0;'>El servicio <strong>{serviceName}</strong> realizado por <strong>{expertName}</strong> ha sido completado.</p>
                <p style='margin:0 0 16px 0;'>Tu valoración ayuda a otros usuarios y mejora la comunidad.</p>
                <p style='margin:0;font-size:13px;color:#6B7280;'>Solo te llevará un momento.</p>";
            var actionUrl = $"https://inspecciono.com/searchhire/{searchHireId}";
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, "Dejar valoración", actionUrl, "⭐");
            return (subject, html);
        }

        /// <summary>
        /// Render puro del email OTP (verificación / reset / step-up). NUNCA lleva link clicable.
        /// Devuelve (subject, html).
        /// </summary>
        public static (string subject, string html) RenderVerificationCode(string code, newApi.DataLayer.Models.PostGresModels.EmailVerificationPurpose purpose, int expirationMinutes)
        {
            var (subject, title, intro) = purpose switch
            {
                newApi.DataLayer.Models.PostGresModels.EmailVerificationPurpose.PasswordReset =>
                    ("Código para restablecer tu contraseña", "Restablece tu contraseña",
                     "Has solicitado restablecer tu contraseña. Usa el siguiente código para confirmar tu identidad:"),
                newApi.DataLayer.Models.PostGresModels.EmailVerificationPurpose.StepUp =>
                    ("Código de confirmación de seguridad", "Confirma tu identidad",
                     "Para completar esta acción necesitamos verificar que eres tú. Introduce el siguiente código:"),
                _ =>
                    ("Verifica tu correo en Inspecciono", "Verifica tu correo",
                     "¡Bienvenido! Para activar tu cuenta introduce el siguiente código de verificación:")
            };
            var content = $@"
                <p style='margin:0;'>{intro}</p>
                <table role='presentation' cellpadding='0' cellspacing='0' border='0' align='center' style='margin:24px auto;'>
                    <tr>
                        <td align='center' class='otp-box' style='background-color:#F1F5F9;border:1px solid #E2E8F0;border-radius:8px;padding:20px 36px;'>
                            <span class='otp-code' style='font-family:""SF Mono"",Menlo,Consolas,""Courier New"",monospace;font-size:30px;line-height:36px;font-weight:700;letter-spacing:6px;margin-right:-6px;color:#0F172A;'>{code}</span>
                        </td>
                    </tr>
                </table>
                <p style='margin:0 0 8px 0;font-size:13px;color:#6B7280;'>Este código caduca en <strong>{expirationMinutes} minutos</strong> y solo puede usarse una vez.</p>
                <p style='margin:0;font-size:13px;color:#6B7280;'>Si no has solicitado esto, ignora este correo. Tu cuenta sigue segura.</p>";
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, actionText: null, actionUrl: null, headerIcon: "🔐");
            return (subject, html);
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
            // El código se renderiza en un bloque destacado, monoespaciado, sin botón de acción
            // (los códigos OTP NO deben ir como enlace clicable — phishing risk).
            var (subject, htmlBody) = RenderVerificationCode(code, purpose, expirationMinutes);
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        #region Background Jobs

        /// <summary>
        /// Job to send the appointment confirmation email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendAppointmentConfirmationEmailJob(string toEmail, string userName, string date, string location, bool isExpert, int searchHireId)
        {
            var (subject, htmlBody) = RenderAppointmentConfirmation(userName, date, location, isExpert, searchHireId);
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        /// <summary>
        /// Job to send the welcome email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendWelcomeEmailJob(string toEmail, string userName)
        {
            var (subject, htmlBody) = RenderWelcome(userName);
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        /// <summary>
        /// Job to send a general notification email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendGeneralNotificationEmailJob(string toEmail, string userName, string title, string message, string? actionText, string? actionUrl)
        {
            var (subject, htmlBody) = RenderGeneralNotification(userName, title, message, actionText, actionUrl);
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        /// <summary>
        /// Job to send the service completion email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendServiceCompletionEmailJob(string toEmail, string userName, string serviceName, string expertName, int searchHireId)
        {
            var (subject, htmlBody) = RenderServiceCompletion(userName, serviceName, expertName, searchHireId);
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        #endregion
    }
}
