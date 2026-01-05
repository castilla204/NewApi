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
                await ProcessUserNotificationAsync(userId.Value, message, details, "Debug", relatedEntityType, relatedEntityId);
            }
            
            await Task.CompletedTask; // Método async pero no hace operaciones de BD
        }

        private async Task LogAsync(string logLevel, string message, string? details = null, int? userId = null, string? source = null, string? relatedEntityType = null, int? relatedEntityId = null, object? additionalData = null, bool notifyUser = false)
        {
            // ✅ TIMING: Capturar timestamp preciso al inicio (ISO 8601 con milisegundos)
            var logStartTime = DateTime.UtcNow;
            var logStartTimeUnix = ((DateTimeOffset)logStartTime).ToUnixTimeMilliseconds();
            
            // ✅ CONSOLE OUTPUT: Mostrar log en consola antes de guardarlo en BD
            var consolePrefix = logLevel switch
            {
                "Critical" => "🔴 [CRITICAL]",
                "Error" => "❌ [ERROR]",
                "Warning" => "⚠️  [WARNING]",
                "Information" => "ℹ️  [INFO]",
                "Debug" => "🔍 [DEBUG]",
                _ => "[LOG]"
            };
            
            var consoleMessage = $"{consolePrefix} [{source}] {message}";
            if (!string.IsNullOrEmpty(details))
            {
                consoleMessage += $"\n   Details: {details}";
            }
            if (userId.HasValue)
            {
                consoleMessage += $"\n   UserId: {userId}";
            }
            if (relatedEntityType != null && relatedEntityId.HasValue)
            {
                consoleMessage += $"\n   Related: {relatedEntityType} (ID: {relatedEntityId})";
            }
            if (additionalData != null)
            {
                try
                {
                    var dataJson = JsonSerializer.Serialize(additionalData, new JsonSerializerOptions { WriteIndented = false });
                    consoleMessage += $"\n   Data: {dataJson}";
                }
                catch
                {
                    consoleMessage += "\n   Data: [Error serializing]";
                }
            }
            Console.WriteLine(consoleMessage);
            
            // ✅ BEST PRACTICE: Usar scope separado para logging independiente de transacciones externas
            // Esto asegura que los logs se guarden incluso si hay rollbacks en otras transacciones
            using var scope = _serviceScopeFactory.CreateScope();
            var scopedContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            try
            {
                // ✅ FIX CRÍTICO: Usar Execution Strategy para transacciones manuales con EnableRetryOnFailure
                // Cuando EnableRetryOnFailure está habilitado, las transacciones manuales DEBEN estar dentro de CreateExecutionStrategy
                var strategy = scopedContext.Database.CreateExecutionStrategy();
                
                LogType? logType = null;
                Log? log = null;
                
                await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await scopedContext.Database.BeginTransactionAsync();
                    try
                    {
                        // Obtener o crear el tipo de log usando el contexto scoped
                        logType = await GetLogTypeAsyncInternal(scopedContext, logLevel);
                        if (logType == null)
                        {
                            // Crear tipo de log por defecto si no existe
                            logType = await CreateDefaultLogTypeAsyncInternal(scopedContext, logLevel);
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
                        log = new Log
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

                        scopedContext.Logs.Add(log);
                        
                        // ✅ TIMING: Medir tiempo de ejecución de SaveChangesAsync
                        var saveStartTime = DateTime.UtcNow;
                        await scopedContext.SaveChangesAsync(); // ✅ Guardar en contexto separado
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
                            await scopedContext.SaveChangesAsync(); // ✅ Actualizar en contexto separado
                        }

                        await transaction.CommitAsync();
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                });

                // Si requiere notificación de administrador, procesar (fuera de la transacción)
                if (logType != null && log != null && logType.RequiresAdminNotification)
                {
                    await ProcessAdminNotificationAsync(log, logType);
                }

                // Si se solicita notificar al usuario y hay userId, crear notificación (fuera de la transacción)
                if (notifyUser && userId.HasValue)
                {
                    await ProcessUserNotificationAsync(userId.Value, message, details, logLevel, relatedEntityType, relatedEntityId);
                }
            }
            catch (Exception ex)
            {
                // ✅ BEST PRACTICE: Si falla el logging, intentar loguear el error en el contexto original
                // pero sin lanzar excepción para no interrumpir el flujo principal
                try
                {
                    // Intentar guardar el error de logging en el contexto original (si está disponible)
                    var errorLog = new Log
                    {
                        Message = $"CRITICAL: Failed to save log - {ex.Message}",
                        Details = $"Original log message: {message}. Error: {ex.StackTrace}",
                        UserId = userId,
                        Source = "LoggingService.LogAsync",
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.Logs.Add(errorLog);
                    await _context.SaveChangesAsync();
                }
                catch
                {
                    // Si incluso esto falla, no hacer nada para no interrumpir el flujo principal
                }
            }
        }

        // ✅ Helper methods para usar contexto scoped
        private async Task<LogType?> GetLogTypeAsyncInternal(AppDbContext context, string name)
        {
            return await context.LogTypes
                .FirstOrDefaultAsync(lt => lt.Name == name && lt.IsActive);
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
        private async Task ProcessUserNotificationAsync(int userId, string message, string? details, string logLevel, string? relatedEntityType = null, int? relatedEntityId = null)
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
                        var appointment = await _context.Appointments
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
