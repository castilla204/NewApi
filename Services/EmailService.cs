using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace newApi.Services
{
    public interface IEmailService
    {
        Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = true, bool throwOnError = false);
        Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, byte[] attachmentBytes, string attachmentFileName, string attachmentContentType = "application/pdf", bool isHtml = true, bool throwOnError = false);
    }

    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;
        private readonly string _smtpHost;
        private readonly int _smtpPort;
        private readonly string _smtpUsername;
        private readonly string _smtpPassword;
        private readonly string _fromEmail;
        private readonly string _fromName;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
            // Configuración flexible: puede ser Gmail, hosting propio, SendGrid, etc.
            _smtpHost = _configuration["Email:SmtpHost"] ?? "";
            _smtpPort = int.TryParse(_configuration["Email:SmtpPort"], out int port) ? port : 587;
            _smtpUsername = _configuration["Email:SmtpUsername"] ?? "";
            _smtpPassword = _configuration["Email:SmtpPassword"] ?? "";
            _fromEmail = _configuration["Email:FromEmail"] ?? "info@inspecciono.com";
            _fromName = _configuration["Email:FromName"] ?? "Inspecciono";
        }

        public async Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = true, bool throwOnError = false)
        {
            try
            {
                // Si no hay configuración de email, no enviar (modo desarrollo)
                if (string.IsNullOrEmpty(_smtpHost) || string.IsNullOrEmpty(_smtpUsername) || string.IsNullOrEmpty(_smtpPassword))
                {
                    // ✅ LOG: Configuración de email faltante (estructurado vía ILogger)
                    _logger.LogWarning(
                        "[EMAIL SERVICE] SMTP no configurado - Email no enviado. To: {To}, Subject: {Subject}, Host: {Host}",
                        toEmail,
                        subject,
                        string.IsNullOrEmpty(_smtpHost) ? "NULL" : _smtpHost);
                    // ✅ FIX: si el llamador exige propagación (Hangfire / jobs críticos), no nos podemos
                    // tragar el "config missing" en silencio porque el job creería que el email salió.
                    if (throwOnError)
                    {
                        throw new InvalidOperationException("SMTP not configured: SmtpHost/Username/Password missing");
                    }
                    return;
                }

                // ✅ LOG: Intentando enviar email (estructurado vía ILogger)
                _logger.LogInformation(
                    "[EMAIL SERVICE] [START] Intentando enviar email. To: {To}, Subject: {Subject}",
                    toEmail,
                    subject);

                // Determinar si usar SSL/TLS según el puerto
                // Puerto 465 = SSL implícito (conexión SSL desde el inicio) - puede tener problemas en .NET
                // Puerto 587 = STARTTLS (conexión normal, luego upgrade a TLS) - más confiable
                var useSsl = _smtpPort == 465 || _smtpPort == 587;

                _logger.LogDebug(
                    "[EMAIL SERVICE] [CONFIG] Configurando cliente SMTP. Host: {Host}, Port: {Port}, SSL: {Ssl}",
                    _smtpHost,
                    _smtpPort,
                    useSsl);

                // ⚠️ IMPORTANTE: Para puerto 465, puede haber problemas de timeout
                // Recomendación: Usar puerto 587 con STARTTLS que es más confiable
                using var client = new SmtpClient(_smtpHost, _smtpPort)
                {
                    EnableSsl = useSsl,
                    Credentials = new NetworkCredential(_smtpUsername, _smtpPassword),
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Timeout = 60000 // 60 segundos
                };

                // Para puerto 587, .NET usa STARTTLS automáticamente cuando EnableSsl = true
                // Para puerto 465, .NET intenta SSL implícito pero puede tener problemas

                using var message = new MailMessage
                {
                    From = new MailAddress(_fromEmail, _fromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = isHtml
                };

                message.To.Add(toEmail);

                _logger.LogDebug(
                    "[EMAIL SERVICE] [CONFIG] Mensaje preparado. To: {To}, From: {From}, Timeout: {TimeoutMs}ms",
                    toEmail,
                    _fromEmail,
                    client.Timeout);

                try
                {
                    // Agregar timeout adicional con Task.WhenAny (60 segundos)
                    var sendTask = client.SendMailAsync(message);
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));

                    var completedTask = await Task.WhenAny(sendTask, timeoutTask);

                    if (completedTask == timeoutTask && !sendTask.IsCompleted)
                    {
                        // El timeout se completó antes que el envío
                        _logger.LogError(
                            "[EMAIL SERVICE] [SEND] TIMEOUT detectado al enviar email. To: {To}, Subject: {Subject}",
                            toEmail,
                            subject);
                        throw new TimeoutException("El envio del email excedio el tiempo limite de 60 segundos");
                    }

                    // Asegurar que se complete o lance excepción
                    await sendTask; // Si está faulted, esto lanza la excepción

                    // ✅ LOG: Email enviado exitosamente
                    _logger.LogInformation(
                        "[EMAIL SERVICE] [SUCCESS] Email enviado exitosamente. To: {To}, Subject: {Subject}",
                        toEmail,
                        subject);
                }
                catch (TimeoutException timeoutEx)
                {
                    // ✅ LOG: Timeout específico (estructurado)
                    _logger.LogError(
                        timeoutEx,
                        "[EMAIL SERVICE] [TIMEOUT ERROR] Timeout al enviar email. To: {To}, Subject: {Subject}",
                        toEmail,
                        subject);
                    throw; // Re-lanzar para que el catch externo lo capture
                }
                catch (SmtpException smtpEx)
                {
                    // ✅ LOG: Error SMTP específico (estructurado)
                    _logger.LogError(
                        smtpEx,
                        "[EMAIL SERVICE] [SMTP ERROR] Error SMTP al enviar email. To: {To}, Subject: {Subject}, StatusCode: {StatusCode}",
                        toEmail,
                        subject,
                        smtpEx.StatusCode);
                    throw; // Re-lanzar para que el catch externo lo capture
                }
                catch (Exception sendEx)
                {
                    // ✅ LOG: Error general al enviar (estructurado)
                    _logger.LogError(
                        sendEx,
                        "[EMAIL SERVICE] [SEND ERROR] Error al enviar email. To: {To}, Subject: {Subject}, ExceptionType: {ExceptionType}",
                        toEmail,
                        subject,
                        sendEx.GetType().Name);
                    throw; // Re-lanzar para que el catch externo lo capture
                }
            }
            catch (Exception ex)
            {
                // ✅ LOG: Error al enviar email (estructurado vía ILogger)
                // Incluye ex completo → InnerException, StackTrace, Type accesibles por las sinks
                _logger.LogError(
                    ex,
                    "[EMAIL SERVICE] [FATAL ERROR] ERROR al enviar email. To: {To}, Subject: {Subject}, ExceptionType: {ExceptionType}, InnerExceptionType: {InnerExceptionType}",
                    toEmail,
                    subject,
                    ex.GetType().FullName,
                    ex.InnerException?.GetType().FullName ?? "NONE");
                // ✅ FIX: si el llamador lo pide (jobs de Hangfire), PROPAGAR el fallo para que el job
                // pueda reintentar/escalar. Antes se tragaba SIEMPRE → las alertas por email se perdían
                // en silencio y los reintentos quedaban muertos. Llamadores fire-and-forget (default
                // throwOnError=false) conservan el comportamiento de no interrumpir su flujo.
                if (throwOnError)
                {
                    throw;
                }
            }
        }

        public async Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, byte[] attachmentBytes, string attachmentFileName, string attachmentContentType = "application/pdf", bool isHtml = true, bool throwOnError = false)
        {
            try
            {
                // Si no hay configuración de email, no enviar (modo desarrollo)
                if (string.IsNullOrEmpty(_smtpHost) || string.IsNullOrEmpty(_smtpUsername) || string.IsNullOrEmpty(_smtpPassword))
                {
                    _logger.LogWarning(
                        "[EMAIL SERVICE] SMTP no configurado - Email con adjunto no enviado. To: {To}, Subject: {Subject}, Host: {Host}",
                        toEmail,
                        subject,
                        string.IsNullOrEmpty(_smtpHost) ? "NULL" : _smtpHost);
                    if (throwOnError)
                    {
                        throw new InvalidOperationException("SMTP not configured: SmtpHost/Username/Password missing");
                    }
                    return;
                }

                _logger.LogInformation(
                    "[EMAIL SERVICE] [START] Intentando enviar email con adjunto. To: {To}, Subject: {Subject}, Attachment: {AttachmentFileName}",
                    toEmail,
                    subject,
                    attachmentFileName);

                var useSsl = _smtpPort == 465 || _smtpPort == 587;

                using var client = new SmtpClient(_smtpHost, _smtpPort)
                {
                    EnableSsl = useSsl,
                    Credentials = new NetworkCredential(_smtpUsername, _smtpPassword),
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Timeout = 60000
                };

                using var message = new MailMessage
                {
                    From = new MailAddress(_fromEmail, _fromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = isHtml
                };

                message.To.Add(toEmail);

                // Agregar adjunto
                using var attachmentStream = new MemoryStream(attachmentBytes);
                var attachment = new Attachment(attachmentStream, attachmentFileName, attachmentContentType);
                message.Attachments.Add(attachment);

                _logger.LogDebug(
                    "[EMAIL SERVICE] [CONFIG] Mensaje con adjunto preparado. To: {To}, Attachment: {AttachmentFileName}, SizeBytes: {SizeBytes}",
                    toEmail,
                    attachmentFileName,
                    attachmentBytes.Length);

                // Enviar con timeout
                var sendTask = client.SendMailAsync(message);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));

                var completedTask = await Task.WhenAny(sendTask, timeoutTask);

                if (completedTask == timeoutTask && !sendTask.IsCompleted)
                {
                    throw new TimeoutException("El envio del email con adjunto excedio el tiempo limite de 60 segundos");
                }

                await sendTask; // Si está faulted, esto lanza la excepción

                _logger.LogInformation(
                    "[EMAIL SERVICE] [SUCCESS] Email con adjunto enviado exitosamente. To: {To}, Subject: {Subject}, Attachment: {AttachmentFileName}",
                    toEmail,
                    subject,
                    attachmentFileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[EMAIL SERVICE] [FATAL ERROR] ERROR al enviar email con adjunto. To: {To}, Subject: {Subject}, Attachment: {AttachmentFileName}, ExceptionType: {ExceptionType}",
                    toEmail,
                    subject,
                    attachmentFileName,
                    ex.GetType().FullName);
                throw;
            }
        }
    }
}

