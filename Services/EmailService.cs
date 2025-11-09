using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;

namespace newApi.Services
{
    public interface IEmailService
    {
        Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = true);
    }

    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly string _smtpHost;
        private readonly int _smtpPort;
        private readonly string _smtpUsername;
        private readonly string _smtpPassword;
        private readonly string _fromEmail;
        private readonly string _fromName;

        public EmailService(IConfiguration configuration)
        {
            _configuration = configuration;
            // Configuración flexible: puede ser Gmail, hosting propio, SendGrid, etc.
            _smtpHost = _configuration["Email:SmtpHost"] ?? "";
            _smtpPort = int.TryParse(_configuration["Email:SmtpPort"], out int port) ? port : 587;
            _smtpUsername = _configuration["Email:SmtpUsername"] ?? "";
            _smtpPassword = _configuration["Email:SmtpPassword"] ?? "";
            _fromEmail = _configuration["Email:FromEmail"] ?? "info@inspecciono.com";
            _fromName = _configuration["Email:FromName"] ?? "Inspecciono";
        }

        public async Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = true)
        {
            try
            {
                // Si no hay configuración de email, no enviar (modo desarrollo)
                if (string.IsNullOrEmpty(_smtpHost) || string.IsNullOrEmpty(_smtpUsername) || string.IsNullOrEmpty(_smtpPassword))
                {
                    // En desarrollo, solo loguear sin enviar
                    return;
                }

                // Determinar si usar SSL/TLS según el puerto (587 = TLS, 465 = SSL, 25 = sin SSL)
                var useSsl = _smtpPort == 465 || _smtpPort == 587;
                
                using var client = new SmtpClient(_smtpHost, _smtpPort)
                {
                    EnableSsl = useSsl,
                    Credentials = new NetworkCredential(_smtpUsername, _smtpPassword),
                    DeliveryMethod = SmtpDeliveryMethod.Network
                };

                using var message = new MailMessage
                {
                    From = new MailAddress(_fromEmail, _fromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = isHtml
                };

                message.To.Add(toEmail);

                await client.SendMailAsync(message);
            }
            catch (Exception ex)
            {
                // No lanzar excepción para no interrumpir el flujo principal
                // El error se puede loguear si es necesario
            }
        }
    }
}

