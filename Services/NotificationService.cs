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

            // Create Action Button HTML if needed
            var actionButtonHtml = "";
            if (!string.IsNullOrEmpty(actionText) && !string.IsNullOrEmpty(actionUrl))
            {
                actionButtonHtml = $@"
                    <table align='center' border='0' cellpadding='0' cellspacing='0' class='button-wrapper' style='border-collapse:collapse;border-spacing:0;padding-bottom:0;padding-left:0;padding-right:0;padding-top:20px;text-align:left;vertical-align:top;width:100%'>
                        <tbody>
                            <tr>
                                <td align='center'>
                                    <div class='center' style='text-align:center'>
                                        <!--[if mso]>
                                        <v:roundrect xmlns:v='urn:schemas-microsoft-com:vml' xmlns:w='urn:schemas-microsoft-com:office:word' href='{actionUrl}' style='height:48px;v-text-anchor:middle;width:260px;' arcsize='10%' strokecolor='#1CB0F6' fillcolor='#1CB0F6'>
                                        <w:anchorlock/>
                                        <center style='color:#ffffff;font-family:sans-serif;font-size:16px;font-weight:bold;'>{actionText}</center>
                                        </v:roundrect>
                                        <![endif]-->
                                        <a href='{actionUrl}' style='-webkit-text-size-adjust:none;background-color:#1CB0F6;border:1px solid #1CB0F6;border-radius:12px;box-shadow:0 4px 0 0 #1899D6;color:#fff;display:inline-block;font-family:Helvetica,Arial,sans-serif;font-size:16px;font-weight:700;letter-spacing:.5px;line-height:48px;mso-hide:all;padding:0;text-align:center;text-decoration:none;width:260px;min-width:200px;'>{actionText}</a>
                                    </div>
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
            var subject = isExpert ? "✅ Has confirmado tu cita - Inspecciono" : "✅ ¡Tu cita ha sido confirmada! - Inspecciono";
            var title = isExpert ? "Cita Confirmada Exitosamente" : "¡Cita Confirmada!";
            
            var messageStart = isExpert 
                ? $"Has confirmado la cita para el <strong>{date}</strong>. Recuerda que solo puedes cancelar hasta 12 horas antes sin penalización."
                : $"El experto ha confirmado tu cita para el <strong>{date}</strong>. ¡Todo está listo!";

            var content = $@"
                <p class='greeting'>Hola {userName},</p>
                <p>{messageStart}</p>
                
                <div class='highlight-box'>
                    <div class='detail-item'>
                        <span class='detail-label'>Fecha y Hora</span>
                        <span class='detail-value'>🗓️ {date}</span>
                    </div>
                    <div class='detail-item'>
                        <span class='detail-label'>Ubicación</span>
                        <span class='detail-value'>📍 {location}</span>
                    </div>
                </div>

                <p style='font-size: 14px; color: #718096; margin-top: 20px;'>
                    Si tienes alguna pregunta o necesitas reagendar, por favor contáctanos lo antes posible o gestiona tu cita desde la plataforma.
                </p>";

            var htmlBody = GenerateEmailTemplate(title, content, "Ver Detalles de la Cita", "https://www.inspecciono.com/appointments", "📅");
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true);
        }

        /// <summary>
        /// Job to send the welcome email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendWelcomeEmailJob(string toEmail, string userName)
        {
            var subject = "👋 ¡Bienvenido a Inspecciono!";
            var title = "¡Bienvenido a la comunidad!";

            var content = $@"
                <p class='greeting'>¡Hola {userName}!</p>
                <p>Estamos muy emocionados de tenerte con nosotros. En <strong>Inspecciono</strong>, nuestra misión es conectarte con los mejores expertos para tus necesidades de inspección y validación.</p>
                
                <p>Aquí tienes algunos pasos para empezar:</p>
                <ul style='padding-left: 20px; color: #4a5568;'>
                    <li style='margin-bottom: 10px;'>Completa tu perfil para que los demás usuarios te conozcan mejor.</li>
                    <li style='margin-bottom: 10px;'>Explora los servicios disponibles o publica el tuyo.</li>
                    <li style='margin-bottom: 10px;'>Si tienes dudas, nuestro centro de ayuda está siempre disponible.</li>
                </ul>

                <div class='highlight-box'>
                    <p style='margin: 0;'>🚀 <strong>Tip Pro:</strong> Los usuarios con perfiles completos tienen un 80% más de éxito en sus gestiones.</p>
                </div>";

            var htmlBody = GenerateEmailTemplate(title, content, "Completar mi Perfil", "https://www.inspecciono.com/profile", "👋");
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true);
        }

        /// <summary>
        /// Job to send a general notification email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendGeneralNotificationEmailJob(string toEmail, string userName, string title, string message, string? actionText, string? actionUrl)
        {
            var subject = $"📢 {title} - Inspecciono";
            
            var content = $@"
                <p class='greeting'>Hola {userName},</p>
                <p>{message}</p>
                <p>Si esta notificación requiere tu atención, por favor accede a la plataforma lo antes posible.</p>";

            var htmlBody = GenerateEmailTemplate(title, content, actionText, actionUrl, "📢");
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true);
        }

        /// <summary>
        /// Job to send the service completion email in background
        /// </summary>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendServiceCompletionEmailJob(string toEmail, string userName, string serviceName, string expertName)
        {
            var subject = "⭐ Tu servicio ha finalizado - ¿Cómo fue tu experiencia?";
            var title = "¡Servicio Completado!";

            var content = $@"
                <p class='greeting'>Hola {userName},</p>
                <p>El servicio <strong>{serviceName}</strong> realizado por <strong>{expertName}</strong> ha sido marcado como completado.</p>
                
                <p>Esperamos que hayas tenido una excelente experiencia. Tu opinión es fundamental para nosotros y ayuda a otros usuarios a tomar mejores decisiones.</p>

                <div style='text-align: center; margin: 30px 0;'>
                    <p style='font-size: 18px; font-weight: 600; color: #2d3748;'>¿Qué tal estuvo el servicio?</p>
                    <p style='font-size: 32px; letter-spacing: 10px;'>⭐⭐⭐⭐⭐</p>
                </div>

                <p>Tomará menos de un minuto dejar tu reseña.</p>";

            var htmlBody = GenerateEmailTemplate(title, content, "Dejar una Reseña", "https://www.inspecciono.com/reviews/pending", "⭐");
            await _emailService.SendEmailAsync(toEmail, subject, htmlBody, isHtml: true);
        }

        #endregion
    }
}
