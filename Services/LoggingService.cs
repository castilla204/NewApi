using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using System.Text.Json;

namespace newApi.Services
{
    public interface ILoggingService
    {
        Task LogCriticalAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false);
        Task LogErrorAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false);
        Task LogWarningAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false);
        Task LogInfoAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false);
        Task LogDebugAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false);
        Task<LogType?> GetLogTypeAsync(string name);
        Task<LogType> CreateLogTypeAsync(string name, string? description = null, string? severityName = null, bool requiresAdminNotification = false, bool requiresEmailAlert = false, bool requiresSmsAlert = false);
    }

    public class LoggingService : ILoggingService
    {
        private readonly AppDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public LoggingService(AppDbContext context, IEmailService emailService, IServiceScopeFactory serviceScopeFactory)
        {
            _context = context;
            _emailService = emailService;
            _serviceScopeFactory = serviceScopeFactory;
        }

        public async Task LogCriticalAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false)
        {
            await LogAsync("Critical", message, details, userId, source, relatedEntityType, relatedEntityId, additionalData, notifyUser);
        }

        public async Task LogErrorAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false)
        {
            await LogAsync("Error", message, details, userId, source, relatedEntityType, relatedEntityId, additionalData, notifyUser);
        }

        public async Task LogWarningAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false)
        {
            await LogAsync("Warning", message, details, userId, source, relatedEntityType, relatedEntityId, additionalData, notifyUser);
        }

        public async Task LogInfoAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false)
        {
            await LogAsync("Information", message, details, userId, source, relatedEntityType, relatedEntityId, additionalData, notifyUser);
        }

        public async Task LogDebugAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false)
        {
            // ✅ DEBUG: Solo loguear en aplicación, NO guardar en BD (para evitar saturar la base de datos)
            // Debug es para desarrollo/depuración, no necesita persistencia
            var logMessage = $"[{source}] {message}";
            if (!string.IsNullOrEmpty(details))
            {
                logMessage += $" - {details}";
            }
            if (userId.HasValue)
            {
                logMessage += $" [UserId: {userId}]";
            }
            if (additionalData != null)
            {
                logMessage += $" [Data: {JsonSerializer.Serialize(additionalData)}]";
            }
            
            // Si se solicita notificar al usuario, crear notificación (aunque sea debug)
            if (notifyUser && userId.HasValue)
            {
                await ProcessUserNotificationAsync(userId.Value, message, details, "Debug");
            }
            
            await Task.CompletedTask; // Método async pero no hace operaciones de BD
        }

        private async Task LogAsync(string logLevel, string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false)
        {
            // ✅ TIMING: Capturar timestamp preciso al inicio (ISO 8601 con milisegundos)
            var logStartTime = DateTime.UtcNow;
            var logStartTimeUnix = ((DateTimeOffset)logStartTime).ToUnixTimeMilliseconds();
            
            try
            {
                // Obtener o crear el tipo de log
                var logType = await GetLogTypeAsync(logLevel);
                if (logType == null)
                {
                    // Crear tipo de log por defecto si no existe
                    logType = await CreateDefaultLogTypeAsync(logLevel);
                }

                // ✅ TIMING: Agregar información de timing automáticamente al additionalData
                // Serializar additionalData original si existe
                string? originalAdditionalDataJson = null;
                if (additionalData != null)
                {
                    originalAdditionalDataJson = JsonSerializer.Serialize(additionalData);
                }

                // Combinar additionalData original con timing info en un diccionario
                var enhancedAdditionalData = new Dictionary<string, object>();
                
                // Si hay additionalData original, deserializarlo y agregarlo al diccionario
                if (!string.IsNullOrEmpty(originalAdditionalDataJson))
                {
                    var originalDict = JsonSerializer.Deserialize<Dictionary<string, object>>(originalAdditionalDataJson);
                    if (originalDict != null)
                    {
                        foreach (var kvp in originalDict)
                        {
                            enhancedAdditionalData[kvp.Key] = kvp.Value;
                        }
                    }
                }

                // ✅ TIMING: Agregar información de timing (siempre presente en todos los logs)
                enhancedAdditionalData["LogStartTime"] = logStartTime.ToString("O"); // ISO 8601
                enhancedAdditionalData["LogStartTimeUnix"] = logStartTimeUnix;
                
                // Serializar el diccionario completo con timing incluido
                var additionalDataJson = JsonSerializer.Serialize(enhancedAdditionalData);

                // Crear el log con timestamp preciso
                var log = new Log
                {
                    Message = message,
                    Details = details,
                    UserId = userId,
                    Source = source,
                    LogTypeId = logType.Id,
                    RelatedEntityType = relatedEntityType,
                    RelatedEntityId = relatedEntityId,
                    AdditionalData = additionalDataJson,
                    CreatedAt = logStartTime // ✅ TIMING: Usar timestamp capturado al inicio
                };

                _context.Logs.Add(log);
                
                // ✅ TIMING: Medir tiempo de ejecución de SaveChangesAsync
                var saveStartTime = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                var saveElapsedMs = (DateTime.UtcNow - saveStartTime).TotalMilliseconds;
                var logEndTime = DateTime.UtcNow;
                var totalLogElapsedMs = (logEndTime - logStartTime).TotalMilliseconds;
                
                // ✅ TIMING: Actualizar log con información de timing completa (SaveElapsedMs, LogEndTime, TotalLogElapsedMs)
                var finalAdditionalDataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(additionalDataJson);
                if (finalAdditionalDataDict != null)
                {
                    finalAdditionalDataDict["SaveElapsedMs"] = saveElapsedMs;
                    finalAdditionalDataDict["LogEndTime"] = logEndTime.ToString("O");
                    finalAdditionalDataDict["TotalLogElapsedMs"] = totalLogElapsedMs;
                    log.AdditionalData = JsonSerializer.Serialize(finalAdditionalDataDict);
                    await _context.SaveChangesAsync(); // Actualizar con timing completo
                }

                // Si requiere notificación de administrador, procesar
                if (logType.RequiresAdminNotification)
                {
                    await ProcessAdminNotificationAsync(log, logType);
                }

                // Si se solicita notificar al usuario y hay userId, crear notificación
                if (notifyUser && userId.HasValue)
                {
                    await ProcessUserNotificationAsync(userId.Value, message, details, logLevel);
                }
            }
            catch (Exception ex)
            {
            }
        }

        public async Task<LogType?> GetLogTypeAsync(string name)
        {
            return await _context.LogTypes
                .FirstOrDefaultAsync(lt => lt.Name == name && lt.IsActive);
        }

        public async Task<LogType> CreateLogTypeAsync(string name, string? description = null, string? severityName = null, bool requiresAdminNotification = false, bool requiresEmailAlert = false, bool requiresSmsAlert = false)
        {
            var logType = new LogType
            {
                Name = name,
                Description = description ?? $"Log type for {name}",
                RequiresAdminNotification = requiresAdminNotification,
                RequiresEmailAlert = requiresEmailAlert,
                RequiresSmsAlert = requiresSmsAlert,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            // Solo asignar SeverityId si se proporciona severityName (principalmente para tipos Error)
            if (!string.IsNullOrEmpty(severityName))
            {
                var severity = await GetOrCreateSeverityAsync(severityName);
                logType.SeverityId = severity.Id;
            }

            _context.LogTypes.Add(logType);
            await _context.SaveChangesAsync();

            return logType;
        }

        private async Task<Severity> GetOrCreateSeverityAsync(string severityName)
        {
            var severity = await _context.Severities
                .FirstOrDefaultAsync(s => s.Name == severityName && s.IsActive);

            if (severity == null)
            {
                var sortOrder = severityName switch
                {
                    "Critical" => 1,
                    "High" => 2,
                    "Medium" => 3,
                    "Low" => 4,
                    _ => 5
                };

                severity = new Severity
                {
                    Name = severityName,
                    Description = $"{severityName} severity level",
                    SortOrder = sortOrder,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Severities.Add(severity);
                await _context.SaveChangesAsync();
            }

            return severity;
        }

        private async Task<LogType> CreateDefaultLogTypeAsync(string logLevel)
        {
            var severityName = logLevel switch
            {
                "Critical" => "Critical",
                "Error" => "High",
                _ => null // No severity for Warning, Information, Debug
            };

            var requiresAdminNotification = logLevel == "Critical";

            return await CreateLogTypeAsync(
                logLevel,
                $"Auto-generated log type for {logLevel}",
                severityName,
                requiresAdminNotification
            );
        }

        private async Task ProcessAdminNotificationAsync(Log log, LogType logType)
        {
            try
            {
                // Crear notificación para administradores
                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    Title = $"🚨 {logType.Name.ToUpper()} ALERT",
                    Message = $"{log.Message} - {log.Details}",
                    Type = "critical_alert",
                    UserId = null, // Para todos los administradores
                    Read = false,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Notifications.Add(notification);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
            }
        }

        /// <summary>
        /// Crea una notificación para el usuario cuando se solicita explícitamente
        /// </summary>
        private async Task ProcessUserNotificationAsync(int userId, string message, string? details, string logLevel)
        {
            try
            {
                // Verificar que el usuario existe
                var user = await _context.Users.FindAsync(userId);
                if (user == null)
                {
                    return; // Usuario no encontrado, no crear notificación
                }

                // Determinar el título y tipo de notificación según el nivel de log
                var (title, notificationType) = GetNotificationTitleAndType(logLevel);

                // Crear mensaje completo
                var fullMessage = message;
                if (!string.IsNullOrEmpty(details))
                {
                    fullMessage += $" - {details}";
                }

                // Crear notificación para el usuario
                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    Title = title,
                    Message = fullMessage,
                    Type = notificationType,
                    UserId = userId,
                    Read = false,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Notifications.Add(notification);
                await _context.SaveChangesAsync();

                // ✅ Enviar email al usuario si tiene email configurado (FIRE-AND-FORGET: no bloquea la API)
                if (!string.IsNullOrEmpty(user.Email))
                {
                    // Capturar variables para el closure
                    var userEmail = user.Email;
                    var emailSubject = title;
                    var emailBody = $@"
<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{ 
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; 
            line-height: 1.6; 
            color: #333333; 
            background-color: #f4f4f4;
            padding: 20px;
        }}
        .email-container {{ 
            max-width: 600px; 
            margin: 0 auto; 
            background-color: #ffffff;
            border-radius: 12px;
            overflow: hidden;
            box-shadow: 0 4px 6px rgba(0, 0, 0, 0.1);
        }}
        .header {{ 
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white; 
            padding: 40px 30px; 
            text-align: center;
        }}
        .header h1 {{
            font-size: 28px;
            font-weight: 600;
            margin: 0;
            letter-spacing: -0.5px;
        }}
        .content {{ 
            padding: 40px 30px;
            background-color: #ffffff;
        }}
        .message-box {{ 
            background: linear-gradient(135deg, #f5f7fa 0%, #c3cfe2 100%);
            padding: 25px;
            margin: 25px 0;
            border-radius: 8px;
            border-left: 5px solid #667eea;
            box-shadow: 0 2px 4px rgba(0, 0, 0, 0.05);
        }}
        .message-box p {{
            font-size: 16px;
            line-height: 1.8;
            color: #2d3748;
            margin: 0;
        }}
        .info-text {{
            color: #718096;
            font-size: 14px;
            margin-top: 20px;
            padding: 15px;
            background-color: #edf2f7;
            border-radius: 6px;
        }}
        .footer {{ 
            text-align: center; 
            padding: 30px;
            background-color: #f7fafc;
            color: #718096;
            font-size: 12px;
            border-top: 1px solid #e2e8f0;
        }}
        .footer p {{
            margin: 5px 0;
        }}
        .logo {{
            font-size: 24px;
            font-weight: bold;
            margin-bottom: 10px;
        }}
        @media only screen and (max-width: 600px) {{
            .email-container {{
                width: 100% !important;
                border-radius: 0;
            }}
            .header, .content {{
                padding: 25px 20px !important;
            }}
            .header h1 {{
                font-size: 24px !important;
            }}
        }}
    </style>
</head>
<body>
    <div class='email-container'>
        <div class='header'>
            <div class='logo'>📧 Inspecciono</div>
            <h1>{title}</h1>
        </div>
        <div class='content'>
            <div class='message-box'>
                <p>{fullMessage}</p>
            </div>
            <div class='info-text'>
                💡 Puedes ver más detalles en tu panel de notificaciones.
            </div>
        </div>
        <div class='footer'>
            <p><strong>Inspecciono</strong></p>
            <p>Este es un email automático. Por favor, no respondas a este mensaje.</p>
            <p style='margin-top: 15px; font-size: 11px; color: #a0aec0;'>© {DateTime.UtcNow.Year} Inspecciono. Todos los derechos reservados.</p>
        </div>
    </div>
</body>
</html>";

                    // ✅ HANGFIRE: Enviar email en segundo plano usando Hangfire (mejor práctica)
                    // Hangfire proporciona: persistencia, reintentos automáticos, monitoreo, y no bloquea la API
                    // Usar el tipo concreto para que Hangfire pueda invocar el método
                    BackgroundJob.Enqueue<LoggingService>(service => 
                        service.SendEmailBackgroundJob(userEmail, emailSubject, emailBody, userId));
                    
                    // ✅ LOG: Email encolado en Hangfire (no bloquea la API)
                    Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ENQUEUED] Email encolado en Hangfire para envío en segundo plano a: {userEmail}, UserId: {userId}");
                    System.Diagnostics.Debug.WriteLine($"[LOGGING SERVICE] HANGFIRE ENQUEUED: Email queued in Hangfire to {userEmail}");
                }
                else
                {
                    // ✅ LOG: Usuario sin email configurado
                    Console.WriteLine($"[LOGGING SERVICE] ⚠️ Usuario {userId} no tiene email configurado, no se enviará email");
                }
            }
            catch (Exception ex)
            {
                // No lanzar excepción para no interrumpir el flujo principal de logging
            }
        }

        /// <summary>
        /// Método para Hangfire: Envía un email en segundo plano
        /// Este método es invocado por Hangfire y no bloquea la API
        /// Hangfire maneja la inyección de dependencias automáticamente a través del IServiceScopeFactory
        /// </summary>
        /// <remarks>
        /// IMPORTANTE: Hangfire crea una nueva instancia del servicio, así que usamos _emailService
        /// que será inyectado por Hangfire a través del DI container.
        /// El timeout de SMTP (60 segundos) está manejado por EmailService.
        /// Hangfire tiene InvisibilityTimeout de 30 minutos, suficiente para emails que tardan hasta 60 segundos.
        /// </remarks>
        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendEmailBackgroundJob(string toEmail, string subject, string body, int? userId = null)
        {
            try
            {
                // Hangfire crea un nuevo scope e inyecta las dependencias automáticamente
                // _emailService está disponible porque Hangfire usa el IServiceScopeFactory configurado
                Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE] Iniciando envio de email en segundo plano a: {toEmail}, UserId: {userId}");
                System.Diagnostics.Debug.WriteLine($"[LOGGING SERVICE] HANGFIRE: Email to {toEmail}, UserId: {userId}");
                
                // EmailService tiene timeout de 60 segundos configurado internamente
                // Si Hostinger tarda más, lanzará TimeoutException y Hangfire reintentará
                await _emailService.SendEmailAsync(toEmail, subject, body, isHtml: true);
                
                Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE SUCCESS] Email enviado exitosamente a: {toEmail}, UserId: {userId}");
                System.Diagnostics.Debug.WriteLine($"[LOGGING SERVICE] HANGFIRE SUCCESS: Email sent to {toEmail}");
            }
            catch (Exception emailEx)
            {
                // ✅ LOG: Error al enviar email en Hangfire (se reintentará automáticamente)
                Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] ERROR al enviar email a: {toEmail}, UserId: {userId}");
                Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] Exception Type: {emailEx.GetType().FullName}");
                Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] Error Message: {emailEx.Message}");
                Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] StackTrace: {emailEx.StackTrace ?? "NULL"}");
                if (emailEx.InnerException != null)
                {
                    Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] InnerException Type: {emailEx.InnerException.GetType().FullName}");
                    Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] InnerException Message: {emailEx.InnerException.Message}");
                }
                System.Diagnostics.Debug.WriteLine($"[LOGGING SERVICE] HANGFIRE ERROR: {emailEx.Message}");
                // Lanzar excepción para que Hangfire reintente automáticamente
                // Hangfire reintentará 3 veces: después de 60s, 5min, y 10min
                throw;
            }
        }

        /// <summary>
        /// Obtiene el título y tipo de notificación según el nivel de log
        /// </summary>
        private (string Title, string Type) GetNotificationTitleAndType(string logLevel)
        {
            return logLevel switch
            {
                "Critical" => ("🚨 Alerta Crítica", "critical_alert"),
                "Error" => ("❌ Error", "error_alert"),
                "Warning" => ("⚠️ Advertencia", "warning_alert"),
                "Information" => ("ℹ️ Información", "info_notification"),
                "Debug" => ("🔍 Debug", "debug_notification"),
                _ => ("📢 Notificación", "general_notification")
            };
        }
    }
}
