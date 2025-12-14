using Hangfire;
using newApi.DataLayer.Models.DTOs;
using System.Globalization;

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
        Task SendAppointmentConfirmationEmailAsync(string toEmail, string userName, string date, string location, bool isExpert);

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
        Task SendServiceCompletionEmailAsync(string toEmail, string userName, string serviceName, string expertName);
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
                var path = Path.Combine(Directory.GetCurrentDirectory(), "Resources", "EmailTemplate.html");
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

        #endregion

        /// <inheritdoc />
        public async Task SendAppointmentConfirmationEmailAsync(string toEmail, string userName, string date, string location, bool isExpert)
        {
            BackgroundJob.Enqueue(() => SendAppointmentConfirmationEmailJob(toEmail, userName, date, location, isExpert));
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
        public async Task SendServiceCompletionEmailAsync(string toEmail, string userName, string serviceName, string expertName)
        {
            BackgroundJob.Enqueue(() => SendServiceCompletionEmailJob(toEmail, userName, serviceName, expertName));
            await Task.CompletedTask;
        }

        #region Background Jobs

        /// <summary>
        /// Job to send the appointment confirmation email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendAppointmentConfirmationEmailJob(string toEmail, string userName, string date, string location, bool isExpert)
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

            var htmlBody = GenerateEmailTemplate(title, content, "Ver cita", "https://inspecciono.com/appointments", "📅");
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true);
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
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true);
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
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true);
        }

        /// <summary>
        /// Job to send the service completion email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendServiceCompletionEmailJob(string toEmail, string userName, string serviceName, string expertName)
        {
            var subject = "Servicio completado";
            var title = "Tu servicio ha finalizado";

            var content = $@"
                <p style='margin:0 0 12px 0;'>Hola {userName},</p>
                <p style='margin:0 0 12px 0;'>El servicio <strong>{serviceName}</strong> realizado por <strong>{expertName}</strong> ha sido completado.</p>
                <p style='margin:0 0 16px 0;'>Tu valoración ayuda a otros usuarios y mejora la comunidad.</p>
                <p style='margin:0;font-size:13px;color:#6B7280;'>Solo te llevará un momento.</p>";

            var htmlBody = GenerateEmailTemplate(title, content, "Dejar valoración", "https://inspecciono.com/reviews/pending", "⭐");
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true);
        }

        #endregion
    }
}
