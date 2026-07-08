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
        /// Envía (encola) el email de magic-link al VENDEDOR (tercero sin cuenta, modo Coordínalo).
        /// reminder=true usa el copy de recordatorio ("el plazo termina pronto").
        /// </summary>
        Task SendSellerMagicLinkEmailAsync(string toEmail, string link, bool reminder = false);

        /// <summary>
        /// Envía (encola) el acuse al VENDEDOR de que su reserva quedó registrada (fecha/lugar),
        /// pendiente de que el técnico la confirme.
        /// </summary>
        Task SendSellerBookingConfirmedEmailAsync(string toEmail, string when, string? location);

        /// <summary>
        /// Job Hangfire (con reintentos): envía un email YA RENDERIZADO (subject+html). Para rutas que
        /// arman el HTML fuera del servicio pero necesitan la fiabilidad de Hangfire (reintentos) en
        /// vez de un SMTP síncrono sin red de seguridad — p.ej. el magic-link al vendedor en el webhook.
        /// </summary>
        Task SendRawEmailJob(string toEmail, string subject, string htmlBody);

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
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, "Completar perfil", "https://inspecciono.com/profile");
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
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, "Ver detalles", actionUrl);
            return (subject, html);
        }

        /// <summary>
        /// Render puro del email de notificación general (sin envío). Devuelve (subject, html).
        /// </summary>
        public static (string subject, string html) RenderGeneralNotification(string userName, string title, string message, string? actionText, string? actionUrl)
        {
            var subject = title;
            // Sin nombre (p.ej. vendedor tercero SIN cuenta) se omiten el saludo (antes salía
            // "Hola ," con la coma colgando) y la coletilla "accede a tu cuenta" (no tiene).
            var hasName = !string.IsNullOrWhiteSpace(userName);
            var greeting = hasName ? $"<p style='margin:0 0 16px 0;'>Hola {userName},</p>" : "";
            var footerLine = hasName
                ? "<p style='margin:0;font-size:13px;color:#6B7280;'>Si necesitas más información, accede a tu cuenta.</p>"
                : "";
            var content = $@"
                {greeting}
                <p style='margin:0 0 16px 0;'>{message}</p>
                {footerLine}";
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, actionText, actionUrl);
            return (subject, html);
        }

        /// <summary>
        /// Render puro del email de magic-link al VENDEDOR (tercero SIN cuenta, modo Coordínalo).
        /// Antes se enviaba como HTML crudo (4 &lt;p&gt; sin marca ni footer) → aspecto de phishing y
        /// riesgo de spam en un correo EN FRÍO. Ahora: plantilla de marca + botón CTA + la URL
        /// visible en texto debajo (fallback de detección iOS/Android y señal de confianza: el
        /// destinatario ve que apunta a inspecciono.com). Sin saludo (no conocemos su nombre).
        /// </summary>
        public static (string subject, string html) RenderSellerMagicLink(string link, bool reminder = false)
        {
            var subject = reminder
                ? "Recordatorio: coordina la inspección de tu vehículo"
                : "Coordina la inspección de tu vehículo";
            var title = subject;
            var intro = reminder
                ? "<p style='margin:0 0 16px 0;'>Te recordamos que un comprador interesado en tu vehículo ha <strong>pagado una inspección profesional independiente</strong> y sigue esperando a que elijas cuándo y dónde puede verlo el técnico.</p>" +
                  "<p style='margin:0 0 16px 0;'><strong>El plazo termina pronto:</strong> si no eliges un hueco, la inspección se cancelará y no podrá realizarse. No necesitas cuenta, solo te llevará un minuto.</p>"
                : "<p style='margin:0 0 16px 0;'>Un comprador interesado en tu vehículo ha <strong>pagado una inspección profesional independiente</strong> antes de comprarlo.</p>" +
                  "<p style='margin:0 0 16px 0;'>Solo falta que elijas cuándo y dónde puede verlo el técnico. No necesitas cuenta.</p>";
            var content = $@"
                {intro}
                <p style='margin:0 0 6px 0;font-size:13px;color:#6B7280;'>Si el botón no funciona, copia este enlace en tu navegador:</p>
                <p style='margin:0;font-size:13px;line-height:20px;word-break:break-all;'><a href='{link}' style='color:#2563EB;'>{link}</a></p>";
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, "Elegir día y hora", link);
            return (subject, html);
        }

        /// <summary>
        /// Render puro del acuse al VENDEDOR tras reservar por el magic-link: fecha/lugar por escrito
        /// y qué pasa después (el técnico confirma). Sin botón (no tiene cuenta ni nada que hacer).
        /// </summary>
        public static (string subject, string html) RenderSellerBookingConfirmed(string when, string? location)
        {
            var subject = "Reserva registrada: inspección de tu vehículo";
            var title = "Tu reserva está registrada";
            var locationRow = string.IsNullOrWhiteSpace(location)
                ? ""
                : $"<p class='panel-text' style='margin:0;font-size:15px;color:#334155;'><strong class='panel-strong' style='color:#0F172A;'>Lugar:</strong> {System.Net.WebUtility.HtmlEncode(location)}</p>";
            var content = $@"
                <p style='margin:0 0 16px 0;'>Has elegido el día y el lugar para la inspección de tu vehículo. Esta es tu reserva:</p>
                <table role='presentation' cellpadding='0' cellspacing='0' border='0' style='margin:0 0 16px 0;width:100%;'>
                    <tr>
                        <td class='panel-accent' style='background-color:#F8FAFC;border-left:3px solid #2563EB;border-radius:8px;padding:14px 18px;'>
                            <p class='panel-text' style='margin:0 0 4px 0;font-size:15px;color:#334155;'><strong class='panel-strong' style='color:#0F172A;'>Fecha:</strong> {System.Net.WebUtility.HtmlEncode(when)}</p>
                            {locationRow}
                        </td>
                    </tr>
                </table>
                <p style='margin:0 0 16px 0;'>El técnico confirmará la cita en las próximas horas. Si no pudiera atenderla, se cancelará sin coste para nadie y avisaremos al comprador.</p>
                <p style='margin:0;font-size:13px;color:#6B7280;'>No tienes que hacer nada más. Guarda este correo como comprobante.</p>";
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, actionText: null, actionUrl: null);
            return (subject, html);
        }

        /// <summary>
        /// Render puro del email de NOTIFICACIÓN de usuario (el de la campana: Stripe aprobado,
        /// avisos de hire/cita, etc.). Es EXACTAMENTE lo que envía LoggingService, extraído aquí
        /// para que el preview admin muestre el email real y no diverja (cero drift). El mensaje se
        /// HtmlEncode-a (los saltos de línea → &lt;br&gt;) porque puede traer datos user-controlled.
        /// </summary>
        public static (string subject, string html) RenderUserNotification(string title, string message, string? actionText, string? actionUrl)
        {
            var subject = title;
            var encoded = System.Net.WebUtility.HtmlEncode(message ?? "").Replace("\n", "<br>");
            var content = $@"
                <p style='margin:0 0 12px 0;font-size:14px;line-height:22px;color:#374151;'>
                    {encoded}
                </p>
                <p style='margin:0;font-size:13px;color:#6B7280;'>
                    Accede a tu panel para más detalles.
                </p>";
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, actionText, actionUrl);
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
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, "Dejar valoración", actionUrl);
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
            // Código en celdas por dígito (look de campo de verificación, no de "pastilla").
            var digitCells = new System.Text.StringBuilder();
            for (int i = 0; i < code.Length; i++)
            {
                if (i > 0)
                    digitCells.Append("<td style='width:10px;font-size:0;line-height:0;'>&nbsp;</td>");
                digitCells.Append(
                    $@"<td align='center' valign='middle' class='otp-cell' bgcolor='#F8FAFC' width='46' height='56' style='width:46px;height:56px;background-color:#F8FAFC;border:1px solid #E2E8F0;border-radius:8px;'>
                            <span class='otp-code' style='font-family:""SF Mono"",Menlo,Consolas,""Courier New"",monospace;font-size:26px;line-height:56px;font-weight:700;color:#0F172A;'>{code[i]}</span>
                        </td>");
            }
            var content = $@"
                <p style='margin:0;'>{intro}</p>
                <table role='presentation' cellpadding='0' cellspacing='0' border='0' align='center' style='margin:32px auto;'>
                    <tr>
                        {digitCells}
                    </tr>
                </table>
                <p style='margin:0 0 8px 0;font-size:13px;color:#6B7280;'>Este código caduca en <strong>{expirationMinutes} minutos</strong> y solo puede usarse una vez.</p>
                <p style='margin:0;font-size:13px;color:#6B7280;'>Si no has solicitado esto, ignora este correo. Tu cuenta sigue segura.</p>";
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, actionText: null, actionUrl: null);
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
        public async Task SendSellerMagicLinkEmailAsync(string toEmail, string link, bool reminder = false)
        {
            BackgroundJob.Enqueue(() => SendSellerMagicLinkEmailJob(toEmail, link, reminder));
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task SendSellerBookingConfirmedEmailAsync(string toEmail, string when, string? location)
        {
            BackgroundJob.Enqueue(() => SendSellerBookingConfirmedEmailJob(toEmail, when, location));
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

        /// <summary>
        /// Job: email de magic-link (inicial o recordatorio) al vendedor en background.
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendSellerMagicLinkEmailJob(string toEmail, string link, bool reminder)
        {
            var (subject, htmlBody) = RenderSellerMagicLink(link, reminder);
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        /// <summary>
        /// Job: acuse de reserva registrada al vendedor en background.
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendSellerBookingConfirmedEmailJob(string toEmail, string when, string? location)
        {
            var (subject, htmlBody) = RenderSellerBookingConfirmed(when, location);
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        /// <inheritdoc />
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendRawEmailJob(string toEmail, string subject, string htmlBody)
        {
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true, throwOnError: true);
        }

        #endregion
    }
}
