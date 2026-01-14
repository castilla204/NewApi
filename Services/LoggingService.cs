using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using System.Text.Json;
using System.Linq;

namespace newApi.Services
{
    public interface ILoggingService
    {
        /// <summary>
        /// Logs a critical error message with optional details and metadata.
        /// </summary>
        /// <param name="message">The main error message.</param>
        /// <param name="details">Optional detailed description of the error.</param>
        /// <param name="userId">Optional ID of the user associated with the error.</param>
        /// <param name="source">Optional source location (e.g., class name, method name).</param>
        /// <param name="relatedEntityType">Optional type of related entity (e.g., "User", "Order").</param>
        /// <param name="relatedEntityId">Optional ID of the related entity.</param>
        /// <param name="additionalData">Optional additional data object to include in the log.</param>
        /// <param name="notifyUser">Whether to notify the user about this critical error.</param>
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
                // ✅ SCOPE INDEPENDIENTE: Debug no guarda en BD, pero la notificación SÍ requiere BD
                // Creamos un scope para asegurar que el contexto no esté dispuesto
                using var scope = _serviceScopeFactory.CreateScope();
                var scopedContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await ProcessUserNotificationAsync(scopedContext, userId.Value, message, details, "Debug", relatedEntityType, relatedEntityId);
            }
            
            await Task.CompletedTask;
        }

        private async Task LogAsync(string logLevel, string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false)
        {
            // ✅ TIMING: Capturar timestamp preciso al inicio (ISO 8601 con milisegundos)
            var logStartTime = DateTime.UtcNow;
            var logStartTimeUnix = ((DateTimeOffset)logStartTime).ToUnixTimeMilliseconds();
            
            // ✅ CONSOLE OUTPUT: Mostrar log en consola SIEMPRE, antes de guardarlo en BD
            // Esto asegura que los logs se vean incluso si falla guardar en BD
            var consolePrefix = logLevel switch
            {
                "Critical" => "🔴 [CRITICAL]",
                "Error" => "❌ [ERROR]",
                "Warning" => "⚠️  [WARNING]",
                "Information" => "ℹ️  [INFO]",
                "Debug" => "🔍 [DEBUG]",
                _ => "[LOG]"
            };
            
            // ✅ MEJORADO: Formato más visible y estructurado
            var separator = new string('=', 80);
            var consoleMessage = $"\n{separator}\n";
            consoleMessage += $"{consolePrefix} [{source ?? "Unknown"}]\n";
            consoleMessage += $"{separator}\n";
            consoleMessage += $"Message: {message}\n";
            
            if (!string.IsNullOrEmpty(details))
            {
                consoleMessage += $"\nDetails:\n{details}\n";
            }
            
            if (userId.HasValue)
            {
                consoleMessage += $"\nUserId: {userId}\n";
            }
            
            if (relatedEntityType != null && relatedEntityId.HasValue)
            {
                consoleMessage += $"\nRelated Entity: {relatedEntityType} (ID: {relatedEntityId})\n";
            }
            
            if (additionalData != null)
            {
                try
                {
                    var dataJson = JsonSerializer.Serialize(additionalData, new JsonSerializerOptions { WriteIndented = true });
                    consoleMessage += $"\nAdditional Data:\n{dataJson}\n";
                }
                catch
                {
                    consoleMessage += "\nAdditional Data: [Error serializing]\n";
                }
            }
            
            consoleMessage += $"{separator}\n";
            
            // ✅ CRÍTICO: Escribir en consola SIEMPRE, incluso si falla guardar en BD
            // ✅ COLORES: Usar color rojo para errores críticos y errores para mejor visibilidad
            var originalColor = Console.ForegroundColor;
            try
            {
                if (logLevel == "Critical" || logLevel == "Error")
                {
                    // ✅ COLOR ROJO: Errores críticos y errores en rojo para distinguirlos fácilmente
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(consoleMessage);
                    Console.ForegroundColor = originalColor;
                    // También escribir en stdout con color
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(consoleMessage);
                    Console.ForegroundColor = originalColor;
                }
                else if (logLevel == "Warning")
                {
                    // ✅ COLOR AMARILLO: Warnings en amarillo
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(consoleMessage);
                    Console.ForegroundColor = originalColor;
                }
                else
                {
                    // Info y Debug en color normal
                    Console.WriteLine(consoleMessage);
                }
            }
            catch
            {
                // Si falla cambiar el color (por ejemplo, en algunos entornos), escribir sin color
                Console.WriteLine(consoleMessage);
                if (logLevel == "Critical" || logLevel == "Error")
                {
                    Console.Error.WriteLine(consoleMessage);
                }
            }
            
            // ✅ BEST PRACTICE: Usar scope separado para logging independiente de transacciones externas
            // Esto asegura que los logs se guarden incluso si hay rollbacks en otras transacciones
            using var scope = _serviceScopeFactory.CreateScope();
            var scopedContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            try
            {
                LogType? logType = null;
                Log? log = null;
                
                // ✅ FIX CRÍTICO: Intentar obtener LogType SIN usar ExecutionStrategy primero
                // Si la conexión está disposed, ExecutionStrategy también fallará
                try
                {
                    logType = await GetLogTypeAsyncInternal(scopedContext, logLevel);
                    if (logType == null)
                    {
                        // Crear tipo de log por defecto si no existe
                        logType = await CreateDefaultLogTypeAsyncInternal(scopedContext, logLevel);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Si la conexión está disposed, crear LogType temporal
                    Console.WriteLine($"[LOGGING SERVICE] ⚠️ Connection disposed al obtener LogType '{logLevel}'. Usando LogType temporal.");
                    logType = new LogType
                    {
                        Id = 0, // ID temporal, se ignorará (NULL en BD)
                        Name = logLevel,
                        Description = $"Temporary log type for {logLevel}",
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                }
                catch (Exception logTypeEx)
                {
                    // Si falla al obtener/crear LogType (por ejemplo, conexión disposed), crear uno en memoria
                    Console.WriteLine($"[LOGGING SERVICE] ⚠️ Error al obtener/crear LogType '{logLevel}': {logTypeEx.Message}. Creando LogType temporal.");
                    logType = new LogType
                    {
                        Id = 0, // ID temporal, se ignorará (NULL en BD)
                        Name = logLevel,
                        Description = $"Temporary log type for {logLevel}",
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                }
                
                // ✅ FIX CRÍTICO: NO usar transacciones manuales en LoggingService
                // Las transacciones manuales con EnableRetryOnFailure causan ObjectDisposedException
                // porque EF Core intenta crear savepoints automáticamente
                // El logging no es crítico - si falla, ya se mostró en consola
                try
                {
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
                    log = new Log
                    {
                        Message = message,
                        Details = details,
                        UserId = userId,
                        Source = source,
                        LogTypeId = logType?.Id > 0 ? logType.Id : null, // Si LogType es temporal (ID=0), usar NULL
                        RelatedEntityType = relatedEntityType,
                        RelatedEntityId = relatedEntityId,
                        AdditionalData = additionalDataJson,
                        CreatedAt = logStartTime // ✅ TIMING: Usar timestamp capturado al inicio
                    };

                    scopedContext.Logs.Add(log);
                    
                    // ✅ TIMING: Medir tiempo de ejecución de SaveChangesAsync
                    var saveStartTime = DateTime.UtcNow;
                    await scopedContext.SaveChangesAsync(); // ✅ Guardar sin transacción manual
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
                        await scopedContext.SaveChangesAsync(); // ✅ Actualizar sin transacción manual
                    }
                }
                catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, NO reintentar
                    // El log ya se mostró en consola, no es necesario guardarlo en BD
                    // NO hacer throw - el log ya se mostró en consola
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, NO reintentar
                    // El log ya se mostró en consola, no es necesario guardarlo en BD
                    // NO hacer throw - el log ya se mostró en consola
                    return;
                }
                catch (Exception saveEx)
                {
                    // ✅ Si falla guardar el log, no es crítico - ya se mostró en consola
                    // Solo loguear el error en consola, no intentar guardarlo en BD (evitar bucles)
                    Console.WriteLine($"[LOGGING SERVICE] ⚠️ Error guardando log en BD: {saveEx.Message}");
                    // NO hacer throw - el log ya se mostró en consola
                    return;
                }

                // Si requiere notificación de administrador, procesar (fuera de la transacción)
                if (logType != null && log != null && logType.RequiresAdminNotification)
                {
                    await ProcessAdminNotificationAsync(scopedContext, log, logType);
                }
                // Si se solicita notificar al usuario y hay userId, crear notificación (fuera de la transacción)
                if (notifyUser && userId.HasValue)
                {
                    await ProcessUserNotificationAsync(scopedContext, userId.Value, message, details, logLevel, relatedEntityType, relatedEntityId);
                }
            }
            catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
            {
                // ✅ FIX CRÍTICO: Si la conexión está disposed dentro de DbUpdateException, NO intentar guardar un log de error
                // porque eso causaría un bucle infinito de errores
                // ✅ MEJORADO: Log más visible en consola
                var ex = dbEx.InnerException as ObjectDisposedException;
                var errorSeparator = new string('=', 80);
                var errorMsg = $"\n{errorSeparator}\n";
                errorMsg += $"🔴 [LOGGING SERVICE ERROR] Connection Disposed (DbUpdateException)\n";
                errorMsg += $"{errorSeparator}\n";
                errorMsg += $"Original log message: {message}\n";
                if (!string.IsNullOrEmpty(details))
                {
                    errorMsg += $"Original details: {details}\n";
                }
                errorMsg += $"Error: {ex?.Message ?? dbEx.Message}\n";
                errorMsg += $"Stack Trace: {ex?.StackTrace ?? dbEx.StackTrace}\n";
                errorMsg += $"{errorSeparator}\n";
                // ✅ COLOR ROJO: Errores de logging en rojo
                var originalColor1 = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(errorMsg);
                    Console.WriteLine(errorMsg);
                    Console.ForegroundColor = originalColor1;
                }
                catch
                {
                    Console.Error.WriteLine(errorMsg);
                    Console.WriteLine(errorMsg);
                }
                // NO intentar guardar nada más, solo loguear en consola
            }
            catch (ObjectDisposedException ex)
            {
                // ✅ FIX CRÍTICO: Si la conexión está disposed, NO intentar guardar un log de error
                // porque eso causaría un bucle infinito de errores
                // ✅ MEJORADO: Log más visible en consola
                var errorSeparator2 = new string('=', 80);
                var errorMsg2 = $"\n{errorSeparator2}\n";
                errorMsg2 += $"🔴 [LOGGING SERVICE ERROR] Connection Disposed\n";
                errorMsg2 += $"{errorSeparator2}\n";
                errorMsg2 += $"Original log message: {message}\n";
                if (!string.IsNullOrEmpty(details))
                {
                    errorMsg2 += $"Original details: {details}\n";
                }
                errorMsg2 += $"Error: {ex.Message}\n";
                errorMsg2 += $"Stack Trace: {ex.StackTrace}\n";
                errorMsg2 += $"{errorSeparator2}\n";
                // ✅ COLOR ROJO: Errores de logging en rojo
                var originalColor2 = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(errorMsg2);
                    Console.WriteLine(errorMsg2);
                    Console.ForegroundColor = originalColor2;
                }
                catch
                {
                    Console.Error.WriteLine(errorMsg2);
                    Console.WriteLine(errorMsg2);
                }
                // NO intentar guardar nada más, solo loguear en consola
            }
            catch (Exception ex)
            {
                // ✅ BEST PRACTICE: Si falla el logging, intentar loguear el error en el contexto original
                // pero sin lanzar excepción para no interrumpir el flujo principal
                // ✅ MEJORADO: Log más visible en consola
                var errorSeparator3 = new string('=', 80);
                var errorMsg3 = $"\n{errorSeparator3}\n";
                errorMsg3 += $"🔴 [LOGGING SERVICE ERROR] Failed to Save Log\n";
                errorMsg3 += $"{errorSeparator3}\n";
                errorMsg3 += $"Original log message: {message}\n";
                if (!string.IsNullOrEmpty(details))
                {
                    errorMsg3 += $"Original details: {details}\n";
                }
                errorMsg3 += $"Error Type: {ex.GetType().FullName}\n";
                errorMsg3 += $"Error Message: {ex.Message}\n";
                errorMsg3 += $"Stack Trace: {ex.StackTrace}\n";
                errorMsg3 += $"{errorSeparator3}\n";
                // ✅ COLOR ROJO: Errores de logging en rojo
                var originalColor3 = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(errorMsg3);
                    Console.WriteLine(errorMsg3);
                    Console.ForegroundColor = originalColor3;
                }
                catch
                {
                    Console.Error.WriteLine(errorMsg3);
                    Console.WriteLine(errorMsg3);
                }
                
                // ✅ FIX: Solo intentar guardar si NO es ObjectDisposedException (para evitar bucles)
                if (ex is not ObjectDisposedException)
                {
                    try
                    {
                        // Intentar guardar el error de logging en el contexto scoped (que es más seguro)
                        var errorLog = new Log
                        {
                            Message = $"CRITICAL: Failed to save log - {ex.Message}",
                            Details = $"Original log message: {message}. Error: {ex.StackTrace}",
                            UserId = userId,
                            Source = "LoggingService.LogAsync",
                            CreatedAt = DateTime.UtcNow
                        };
                        scopedContext.Logs.Add(errorLog);
                        await scopedContext.SaveChangesAsync();
                    }
                    catch
                    {
                        // Si incluso esto falla, el mensaje ya se mostró en consola arriba
                    }
                }
            }
        }

        // ✅ Helper methods para usar contexto scoped
        private async Task<LogType?> GetLogTypeAsyncInternal(AppDbContext context, string name)
        {
            try
            {
                // Usar AsNoTracking para evitar problemas con el contexto
                return await context.LogTypes
                    .AsNoTracking()
                    .FirstOrDefaultAsync(lt => lt.Name == name && lt.IsActive);
            }
            catch (ObjectDisposedException)
            {
                // Si el contexto está disposed, retornar null para que se cree uno por defecto
                Console.WriteLine($"[LOGGING SERVICE] ⚠️ Context disposed al obtener LogType '{name}'. Se creará uno por defecto.");
                return null;
            }
            catch (Exception ex)
            {
                // Cualquier otro error también retorna null
                Console.WriteLine($"[LOGGING SERVICE] ⚠️ Error al obtener LogType '{name}': {ex.Message}. Se creará uno por defecto.");
                return null;
            }
        }

        private async Task<LogType> CreateDefaultLogTypeAsyncInternal(AppDbContext context, string logLevel)
        {
            string severityName;
            switch (logLevel)
            {
                case "Critical":
                    severityName = "Critical";
                    break;
                case "Error":
                    severityName = "High";
                    break;
                case "Warning":
                    severityName = "Medium";
                    break;
                case "Information":
                    severityName = "Low";
                    break;
                case "Debug":
                    severityName = "Low";
                    break;
                default:
                    severityName = "Low";
                    break;
            }

            var requiresAdminNotification = logLevel == "Critical";

            return await CreateLogTypeAsyncInternal(context, logLevel,
                $"Auto-generated log type for {logLevel}",
                severityName,
                requiresAdminNotification);
        }

        private async Task<LogType> CreateLogTypeAsyncInternal(AppDbContext context, string name, string? description = null, string? severityName = null, bool requiresAdminNotification = false, bool requiresEmailAlert = false, bool requiresSmsAlert = false)
        {
            var logType = new LogType
            {
                Name = name,
                Description = description ?? $"Log type for {name}",
                IsActive = true,
                RequiresAdminNotification = requiresAdminNotification,
                RequiresEmailAlert = requiresEmailAlert,
                RequiresSmsAlert = requiresSmsAlert,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Solo asignar SeverityId si se proporciona severityName (principalmente para tipos Error)
            if (!string.IsNullOrEmpty(severityName))
            {
                var severity = await GetOrCreateSeverityAsyncInternal(context, severityName);
                logType.SeverityId = severity.Id;
            }

            context.LogTypes.Add(logType);
            await context.SaveChangesAsync();

            return logType;
        }

        private async Task<Severity> GetOrCreateSeverityAsyncInternal(AppDbContext context, string severityName)
        {
            var severity = await context.Severities
                .FirstOrDefaultAsync(s => s.Name == severityName);

            if (severity == null)
            {
                int level;
                switch (severityName)
                {
                    case "Critical":
                        level = 1;
                        break;
                    case "High":
                        level = 2;
                        break;
                    case "Medium":
                        level = 3;
                        break;
                    case "Low":
                        level = 4;
                        break;
                    case "Info":
                        level = 5;
                        break;
                    default:
                        level = 5;
                        break;
                }

                severity = new Severity
                {
                    Name = severityName,
                    SortOrder = level,
                    Description = $"Severity level: {severityName}",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                context.Severities.Add(severity);
                await context.SaveChangesAsync();
            }

            return severity;
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

        private async Task ProcessAdminNotificationAsync(AppDbContext context, Log log, LogType logType)
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

                context.Notifications.Add(notification);
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
            }
        }

        /// <summary>
        /// Crea una notificación para el usuario cuando se solicita explícitamente
        /// </summary>
        private async Task ProcessUserNotificationAsync(AppDbContext context, int userId, string message, string? details, string logLevel, string? relatedEntityType = null, int? relatedEntityId = null)
        {
            try
            {
                // Verificar que el usuario existe
                var user = await context.Users.FindAsync(userId);
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

                context.Notifications.Add(notification);
                await context.SaveChangesAsync();

                // ✅ Enviar email al usuario si tiene email configurado (FIRE-AND-FORGET: no bloquea la API)
                if (!string.IsNullOrEmpty(user.Email))
                {
                    // Capturar variables para el closure
                    var userEmail = user.Email;
                    var emailSubject = title;
                    
                    // ---------------------------------------------------------
                    // 🎨 NUEVO DISEÑO "HERO/DUOLINGO" (Lectura de plantilla)
                    // ---------------------------------------------------------
                    string templateHtml;
                    try 
                    {
                        var path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Resources", "EmailTemplate.html");
                        if (System.IO.File.Exists(path))
                        {
                            templateHtml = System.IO.File.ReadAllText(path);
                        }
                        else
                        {
                            // Fallback básico si no encuentra el archivo
                            templateHtml = "<html><body style='font-family:sans-serif;'><h1>{{TITLE}}</h1><div>{{CONTENT}}</div>{{ACTION_BUTTON}}</body></html>";
                        }
                    }
                    catch
                    {
                        templateHtml = "<html><body style='font-family:sans-serif;'><h1>{{TITLE}}</h1><div>{{CONTENT}}</div>{{ACTION_BUTTON}}</body></html>";
                    }

                    // Preparar contenido (convertir saltos de línea a <br>)
                    var formattedMessage = fullMessage?.Replace("\n", "<br>") ?? "";
                    
                    var contentHtml = $@"
                        <p style='margin:0 0 12px 0;font-size:14px;line-height:22px;color:#374151;'>
                            {formattedMessage}
                        </p>
                        <p style='margin:0;font-size:13px;color:#6B7280;'>
                            Accede a tu panel para más detalles.
                        </p>";

                    // Determinar URL y Texto del botón
                    string actionUrl = "https://inspecciono.com/notifications";
                    string actionText = "Ver notificaciones";

                    if (relatedEntityType == "SearchHire" && relatedEntityId.HasValue)
                    {
                        actionUrl = $"https://inspecciono.com/detalles/{relatedEntityId}";
                        actionText = "Ver detalles";
                    }
                    else if (relatedEntityType == "Appointment" && relatedEntityId.HasValue)
                    {
                        // Obtener el SearchHireId de la cita para redirigir correctamente
                        var appointment = await context.Appointments
                            .AsNoTracking()
                            .Select(a => new { a.Id, a.SearchHireId })
                            .FirstOrDefaultAsync(a => a.Id == relatedEntityId.Value);

                        if (appointment != null)
                        {
                             actionUrl = $"https://inspecciono.com/detalles/{appointment.SearchHireId}";
                             actionText = "Ver detalles";
                        }
                        else
                        {
                             actionUrl = "https://inspecciono.com/appointments";
                             actionText = "Ver cita";
                        }
                    }

                    // Botón de acción - Estilo profesional y discreto
                    var actionButtonHtml = $@"
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

                    var emailBody = templateHtml
                        .Replace("{{TITLE}}", title)
                        .Replace("{{CONTENT}}", contentHtml)
                        .Replace("{{ACTION_BUTTON}}", actionButtonHtml)
                        .Replace("{{YEAR}}", DateTime.UtcNow.Year.ToString());

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
