using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using System.Text.Json;

namespace newApi.Services
{
    public interface ILoggingService
    {
        Task LogCriticalAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null);
        Task LogErrorAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null);
        Task LogWarningAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null);
        Task LogInfoAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null);
        Task LogDebugAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null);
        Task<LogType?> GetLogTypeAsync(string name);
        Task<LogType> CreateLogTypeAsync(string name, string? description = null, string? severityName = null, bool requiresAdminNotification = false, bool requiresEmailAlert = false, bool requiresSmsAlert = false);
    }

    public class LoggingService : ILoggingService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<LoggingService> _logger;

        public LoggingService(AppDbContext context, ILogger<LoggingService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task LogCriticalAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null)
        {
            await LogAsync("Critical", message, details, userId, source, relatedEntityType, relatedEntityId, additionalData);
        }

        public async Task LogErrorAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null)
        {
            await LogAsync("Error", message, details, userId, source, relatedEntityType, relatedEntityId, additionalData);
        }

        public async Task LogWarningAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null)
        {
            await LogAsync("Warning", message, details, userId, source, relatedEntityType, relatedEntityId, additionalData);
        }

        public async Task LogInfoAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null)
        {
            await LogAsync("Information", message, details, userId, source, relatedEntityType, relatedEntityId, additionalData);
        }

        public async Task LogDebugAsync(string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null)
        {
            await LogAsync("Debug", message, details, userId, source, relatedEntityType, relatedEntityId, additionalData);
        }

        private async Task LogAsync(string logLevel, string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null)
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging message: {Message}", message);
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
                    Title = $"🚨 {logType.Name.ToUpper()} ALERT",
                    Message = $"{log.Message} - {log.Details}",
                    Type = "critical_alert",
                    UserId = null, // Para todos los administradores
                    Read = false,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Notifications.Add(notification);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Created admin notification for log type: {LogTypeName}", logType.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating admin notification for log: {LogId}", log.Id);
            }
        }
    }
}
