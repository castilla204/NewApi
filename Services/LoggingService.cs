using Microsoft.EntityFrameworkCore;
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

        public LoggingService(AppDbContext context, IEmailService emailService)
        {
            _context = context;
            _emailService = emailService;
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

                // ✅ Enviar email al usuario si tiene email configurado
                if (!string.IsNullOrEmpty(user.Email))
                {
                    try
                    {
                        var emailSubject = title;
                        var emailBody = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background-color: #4CAF50; color: white; padding: 20px; text-align: center; border-radius: 5px 5px 0 0; }}
        .content {{ background-color: #f9f9f9; padding: 20px; border-radius: 0 0 5px 5px; }}
        .message {{ background-color: white; padding: 15px; margin: 15px 0; border-left: 4px solid #4CAF50; }}
        .footer {{ text-align: center; margin-top: 20px; color: #666; font-size: 12px; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>{title}</h1>
        </div>
        <div class='content'>
            <div class='message'>
                <p>{fullMessage}</p>
            </div>
            <p>Puedes ver más detalles en tu panel de notificaciones.</p>
        </div>
        <div class='footer'>
            <p>Este es un email automático de Inspecciono. Por favor, no respondas a este mensaje.</p>
        </div>
    </div>
</body>
</html>";

                        await _emailService.SendEmailAsync(user.Email, emailSubject, emailBody, isHtml: true);
                    }
                    catch (Exception emailEx)
                    {
                        // No lanzar excepción - el fallo del email no debe interrumpir el logging
                        // El email es opcional, la notificación en BD es lo importante
                    }
                }
            }
            catch (Exception ex)
            {
                // No lanzar excepción para no interrumpir el flujo principal de logging
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
