using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;

namespace newApi.Services
{
    public interface IEmailService
    {
        Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = true, bool throwOnError = false);
        Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, byte[] attachmentBytes, string attachmentFileName, string attachmentContentType = "application/pdf", bool isHtml = true);
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

        public async Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = true, bool throwOnError = false)
        {
            try
            {
                // Si no hay configuración de email, no enviar (modo desarrollo)
                if (string.IsNullOrEmpty(_smtpHost) || string.IsNullOrEmpty(_smtpUsername) || string.IsNullOrEmpty(_smtpPassword))
                {
                    // ✅ LOG: Configuración de email faltante
                    Console.WriteLine($"[EMAIL SERVICE] [CONFIG MISSING] Email no enviado - Configuracion SMTP faltante. To: {toEmail}, Subject: {subject}");
                    Console.WriteLine($"[EMAIL SERVICE] [CONFIG MISSING] Host={_smtpHost ?? "NULL"}, Username={_smtpUsername ?? "NULL"}, Password={(string.IsNullOrEmpty(_smtpPassword) ? "NULL" : "***")}");
                    System.Diagnostics.Debug.WriteLine($"[EMAIL SERVICE] CONFIG MISSING: Host={_smtpHost ?? "NULL"}");
                    return;
                }

                // ✅ LOG: Intentando enviar email
                Console.WriteLine($"[EMAIL SERVICE] [START] Intentando enviar email a: {toEmail}, Subject: {subject}");
                System.Diagnostics.Debug.WriteLine($"[EMAIL SERVICE] START: Sending to {toEmail}");

                // Determinar si usar SSL/TLS según el puerto
                // Puerto 465 = SSL implícito (conexión SSL desde el inicio) - puede tener problemas en .NET
                // Puerto 587 = STARTTLS (conexión normal, luego upgrade a TLS) - más confiable
                var useSsl = _smtpPort == 465 || _smtpPort == 587;
                
                Console.WriteLine($"[EMAIL SERVICE] [CONFIG] Configurando cliente SMTP. Host: {_smtpHost}, Port: {_smtpPort}, SSL: {useSsl}");
                Console.WriteLine($"[EMAIL SERVICE] [CONFIG] NOTA: Puerto 465 puede tener problemas. Si falla, prueba puerto 587");
                
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

                Console.WriteLine($"[EMAIL SERVICE] [CONFIG] Cliente SMTP configurado. Timeout: {client.Timeout}ms");

                using var message = new MailMessage
                {
                    From = new MailAddress(_fromEmail, _fromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = isHtml
                };

                message.To.Add(toEmail);
                Console.WriteLine($"[EMAIL SERVICE] [CONFIG] Mensaje preparado. To: {toEmail}, From: {_fromEmail}");

                // ✅ LOG: Antes de enviar
                Console.WriteLine($"[EMAIL SERVICE] [DEBUG] Antes de SendMailAsync. Host: {_smtpHost}, Port: {_smtpPort}, SSL: {useSsl}, From: {_fromEmail}");
                System.Diagnostics.Debug.WriteLine($"[EMAIL SERVICE] DEBUG: Before SendMailAsync. Host: {_smtpHost}, Port: {_smtpPort}");
                
                try
                {
                    Console.WriteLine($"[EMAIL SERVICE] [SEND] Iniciando SendMailAsync...");
                    
                    // Agregar timeout adicional con Task.WhenAny (60 segundos)
                    var sendTask = client.SendMailAsync(message);
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
                    
                    Console.WriteLine($"[EMAIL SERVICE] [SEND] Tareas creadas, esperando resultado...");
                    
                    var completedTask = await Task.WhenAny(sendTask, timeoutTask);
                    
                    Console.WriteLine($"[EMAIL SERVICE] [SEND] Tarea completada. SendTask.IsCompleted: {sendTask.IsCompleted}, TimeoutTask.IsCompleted: {timeoutTask.IsCompleted}");
                    
                    if (completedTask == timeoutTask && !sendTask.IsCompleted)
                    {
                        // El timeout se completó antes que el envío
                        Console.WriteLine($"[EMAIL SERVICE] [SEND] TIMEOUT detectado!");
                        throw new TimeoutException("El envio del email excedio el tiempo limite de 60 segundos");
                    }
                    
                    // Asegurar que se complete o lance excepción
                    if (sendTask.IsFaulted)
                    {
                        Console.WriteLine($"[EMAIL SERVICE] [SEND] SendTask tiene errores, re-lanzando excepcion...");
                        await sendTask; // Esto lanzará la excepción
                    }
                    else if (!sendTask.IsCompleted)
                    {
                        Console.WriteLine($"[EMAIL SERVICE] [SEND] Esperando que SendTask se complete...");
                        await sendTask;
                    }
                    else
                    {
                        Console.WriteLine($"[EMAIL SERVICE] [SEND] SendTask ya completado, obteniendo resultado...");
                        await sendTask; // Asegurar que se procese cualquier excepción
                    }
                    
                    // ✅ LOG: Email enviado exitosamente
                    Console.WriteLine($"[EMAIL SERVICE] [SUCCESS] Email enviado exitosamente a: {toEmail}, Subject: {subject}");
                    System.Diagnostics.Debug.WriteLine($"[EMAIL SERVICE] SUCCESS: Email sent to {toEmail}");
                }
                catch (TimeoutException timeoutEx)
                {
                    // ✅ LOG: Timeout específico
                    Console.WriteLine($"[EMAIL SERVICE] [TIMEOUT ERROR] Timeout al enviar email a: {toEmail}");
                    Console.WriteLine($"[EMAIL SERVICE] [TIMEOUT ERROR] Message: {timeoutEx.Message}");
                    System.Diagnostics.Debug.WriteLine($"[EMAIL SERVICE] TIMEOUT: {timeoutEx.Message}");
                    throw; // Re-lanzar para que el catch externo lo capture
                }
                catch (SmtpException smtpEx)
                {
                    // ✅ LOG: Error SMTP específico
                    Console.WriteLine($"[EMAIL SERVICE] [SMTP ERROR] Error SMTP al enviar email a: {toEmail}");
                    Console.WriteLine($"[EMAIL SERVICE] [SMTP ERROR] StatusCode: {smtpEx.StatusCode}");
                    Console.WriteLine($"[EMAIL SERVICE] [SMTP ERROR] Message: {smtpEx.Message}");
                    System.Diagnostics.Debug.WriteLine($"[EMAIL SERVICE] SMTP ERROR: {smtpEx.StatusCode} - {smtpEx.Message}");
                    throw; // Re-lanzar para que el catch externo lo capture
                }
                catch (Exception sendEx)
                {
                    // ✅ LOG: Error general al enviar
                    Console.WriteLine($"[EMAIL SERVICE] [SEND ERROR] Error al enviar email a: {toEmail}");
                    Console.WriteLine($"[EMAIL SERVICE] [SEND ERROR] Type: {sendEx.GetType().Name}");
                    Console.WriteLine($"[EMAIL SERVICE] [SEND ERROR] Message: {sendEx.Message}");
                    System.Diagnostics.Debug.WriteLine($"[EMAIL SERVICE] SEND ERROR: {sendEx.GetType().Name} - {sendEx.Message}");
                    throw; // Re-lanzar para que el catch externo lo capture
                }
            }
            catch (Exception ex)
            {
                // ✅ LOG: Error al enviar email
                Console.WriteLine($"[EMAIL SERVICE] [FATAL ERROR] ERROR al enviar email a: {toEmail}, Subject: {subject}");
                Console.WriteLine($"[EMAIL SERVICE] [FATAL ERROR] Exception Type: {ex.GetType().FullName}");
                Console.WriteLine($"[EMAIL SERVICE] [FATAL ERROR] Error Message: {ex.Message}");
                Console.WriteLine($"[EMAIL SERVICE] [FATAL ERROR] StackTrace: {ex.StackTrace ?? "NULL"}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[EMAIL SERVICE] [FATAL ERROR] InnerException Type: {ex.InnerException.GetType().FullName}");
                    Console.WriteLine($"[EMAIL SERVICE] [FATAL ERROR] InnerException Message: {ex.InnerException.Message}");
                    Console.WriteLine($"[EMAIL SERVICE] [FATAL ERROR] InnerException StackTrace: {ex.InnerException.StackTrace ?? "NULL"}");
                }
                // También usar System.Diagnostics para asegurar que se vea
                System.Diagnostics.Debug.WriteLine($"[EMAIL SERVICE] ERROR: {ex.Message}");
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

        public async Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, byte[] attachmentBytes, string attachmentFileName, string attachmentContentType = "application/pdf", bool isHtml = true)
        {
            try
            {
                // Si no hay configuración de email, no enviar (modo desarrollo)
                if (string.IsNullOrEmpty(_smtpHost) || string.IsNullOrEmpty(_smtpUsername) || string.IsNullOrEmpty(_smtpPassword))
                {
                    Console.WriteLine($"[EMAIL SERVICE] [CONFIG MISSING] Email con adjunto no enviado - Configuracion SMTP faltante. To: {toEmail}, Subject: {subject}");
                    return;
                }

                Console.WriteLine($"[EMAIL SERVICE] [START] Intentando enviar email con adjunto a: {toEmail}, Subject: {subject}, Attachment: {attachmentFileName}");

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

                Console.WriteLine($"[EMAIL SERVICE] [CONFIG] Mensaje con adjunto preparado. To: {toEmail}, Attachment: {attachmentFileName} ({attachmentBytes.Length} bytes)");

                // Enviar con timeout
                var sendTask = client.SendMailAsync(message);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
                
                var completedTask = await Task.WhenAny(sendTask, timeoutTask);
                
                if (completedTask == timeoutTask && !sendTask.IsCompleted)
                {
                    throw new TimeoutException("El envio del email con adjunto excedio el tiempo limite de 60 segundos");
                }
                
                if (sendTask.IsFaulted)
                {
                    await sendTask; // Esto lanzará la excepción
                }
                else if (!sendTask.IsCompleted)
                {
                    await sendTask;
                }
                else
                {
                    await sendTask;
                }
                
                Console.WriteLine($"[EMAIL SERVICE] [SUCCESS] Email con adjunto enviado exitosamente a: {toEmail}, Subject: {subject}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EMAIL SERVICE] [FATAL ERROR] ERROR al enviar email con adjunto a: {toEmail}, Subject: {subject}");
                Console.WriteLine($"[EMAIL SERVICE] [FATAL ERROR] Exception Type: {ex.GetType().FullName}");
                Console.WriteLine($"[EMAIL SERVICE] [FATAL ERROR] Error Message: {ex.Message}");
                throw;
            }
        }
    }
}

