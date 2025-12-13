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
        /// Generates the common HTML structure for all emails
        /// </summary>
        private string GenerateEmailTemplate(string title, string content, string? actionText = null, string? actionUrl = null, string headerIcon = "📢")
        {
            var year = DateTime.UtcNow.Year;
            var actionButtonHtml = !string.IsNullOrEmpty(actionText) && !string.IsNullOrEmpty(actionUrl)
                ? $"<a href='{actionUrl}' class='cta-button'>{actionText}</a>"
                : "";

            return $@"
<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>{title}</title>
    <style>
        body {{
            font-family: 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            background-color: #f4f7f6;
            margin: 0;
            padding: 0;
            color: #333;
            line-height: 1.6;
        }}
        .container {{
            max-width: 600px;
            margin: 0 auto;
            background-color: #ffffff;
            border-radius: 8px;
            overflow: hidden;
            box-shadow: 0 4px 15px rgba(0,0,0,0.05);
            margin-top: 20px;
            margin-bottom: 20px;
        }}
        .header {{
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            padding: 30px;
            text-align: center;
            color: white;
        }}
        .header h1 {{
            margin: 0;
            font-size: 24px;
            font-weight: 600;
        }}
        .logo {{
            font-size: 32px;
            margin-bottom: 10px;
            display: block;
        }}
        .content {{
            padding: 40px 30px;
        }}
        .cta-button {{
            display: block;
            width: 100%;
            text-align: center;
            background-color: #667eea;
            color: white;
            text-decoration: none;
            padding: 15px 0;
            border-radius: 6px;
            font-weight: 600;
            font-size: 16px;
            margin-top: 30px;
            transition: background-color 0.3s;
        }}
        .cta-button:hover {{
            background-color: #5a67d8;
        }}
        .footer {{
            background-color: #edf2f7;
            padding: 20px;
            text-align: center;
            font-size: 12px;
            color: #718096;
        }}
        .highlight-box {{
            background-color: #f8fafc;
            border-left: 4px solid #667eea;
            padding: 20px;
            margin: 20px 0;
            border-radius: 4px;
        }}
        /* Estilos específicos para contenido dinámico */
        .detail-item {{ margin-bottom: 10px; font-size: 15px; }}
        .detail-label {{ font-weight: 600; color: #4a5568; display: block; font-size: 12px; text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 4px; }}
        .detail-value {{ color: #2d3748; font-size: 16px; }}
        .greeting {{ font-size: 18px; margin-bottom: 20px; color: #2d3748; font-weight: 500; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <span class='logo'>{headerIcon}</span>
            <h1>{title}</h1>
        </div>
        <div class='content'>
            {content}
            {actionButtonHtml}
        </div>
        <div class='footer'>
            <p>© {year} Inspecciono. Todos los derechos reservados.</p>
            <p>Has recibido este correo porque tienes una cuenta activa en Inspecciono.</p>
            <p style='margin-top: 10px;'><a href='#' style='color: #718096; text-decoration: underline;'>Preferencias de notificación</a></p>
        </div>
    </div>
</body>
</html>";
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
