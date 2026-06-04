using Hangfire;
using newApi.Common; // 🛡️ MUD-BA: BackgroundLogger
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
        Task EmitAdminDigestAsync();
        Task EmitRefundFailedDigestAsync();
    }

    public class LoggingService : ILoggingService
    {
        private readonly AppDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IMemoryCache _cache;
        // 🛡️ LOTE D · D-25 — Base URL del frontend para los links del email.
        // Antes: hardcoded "https://inspecciono.com" en 4 sitios → en dev (localhost:5173)
        // los emails ofrecían links rotos que no apuntaban al stack local. Ahora se lee
        // del mismo App:FrontendBaseUrl que ya usa todo Stripe checkout/onboarding.
        private readonly string _frontendBaseUrl;

        // P3-2: dedup/throttle de alertas de admin. Los primeros 3 hits con el mismo (source|message|entity)
        // dentro de la ventana siempre van por email; los siguientes se reagrupan en un digest periódico.
        // Mantenemos un ConcurrentDictionary paralelo a IMemoryCache porque MemoryCache no expone enumeración.
        private static readonly ConcurrentDictionary<string, AlertDedupEntry> _alertDedupTracker = new();
        private const int AdminAlertWindowMinutes = 15;
        private const int AdminAlertImmediateEmailThreshold = 3;

        private sealed class AlertDedupEntry
        {
            public int Count;
            public DateTime FirstSeenUtc;
            public DateTime LastSeenUtc;
            public DateTime ExpiresUtc;
            public string Source = string.Empty;
            public string Message = string.Empty;
            public string Details = string.Empty;
            public string EntityType = string.Empty;
            public int? EntityId;
        }

        public LoggingService(AppDbContext context, IEmailService emailService, IServiceScopeFactory serviceScopeFactory, IMemoryCache cache, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            _context = context;
            _emailService = emailService;
            _serviceScopeFactory = serviceScopeFactory;
            _cache = cache;
            // 🛡️ LOTE D · D-25 — Normalizar la base URL (sin trailing slash) UNA vez en el ctor.
            // Default `https://inspecciono.com` por si el setting falta en algún appsettings.
            var baseUrl = configuration["App:FrontendBaseUrl"];
            _frontendBaseUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? "https://inspecciono.com"
                : baseUrl.TrimEnd('/');
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

            // ✅ CONSOLE OUTPUT: Mostrar en consola en ROJO (errores de menor importancia)
            var separator = new string('=', 80);
            var consoleMessage = $"\n{separator}\n";
            consoleMessage += $"🔍 [DEBUG] [{source ?? "Unknown"}]\n";
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
            try
            {
                var originalColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(consoleMessage);
                Console.WriteLine(consoleMessage);
                Console.ForegroundColor = originalColor;
            }
            catch
            {
                Console.WriteLine(consoleMessage);
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
            
            // 🛡️ E2: Para CRITICAL, propagar SIEMPRE a canal alternativo (Slack/Telegram) si está
            // configurado, ANTES del save a BD. Render logs son efímeros (~24h) y si SMTP no está
            // configurado o falla, este es el único rastro durable garantizado para ops.
            // Fire-and-forget: no bloquea el flujo principal de logging ni gateado por éxito del save.
            if (logLevel == "Critical")
            {
                try
                {
                    using var alertScope = _serviceScopeFactory.CreateScope();
                    var alertChannel = alertScope.ServiceProvider.GetService<IAlertChannelService>();
                    if (alertChannel != null)
                    {
                        var criticalPayload = new
                        {
                            Source = source,
                            UserId = userId,
                            RelatedEntityType = relatedEntityType,
                            RelatedEntityId = relatedEntityId,
                            Details = details,
                            AdditionalData = additionalData
                        };
                        var criticalMsg = $"[CRITICAL] {message}";
                        BackgroundLogger.FireAndForget(async () =>
                        /* 🛡️ MUD-BA: captura excepciones */
                        {
                            try { await alertChannel.SendCriticalAsync(criticalMsg, criticalPayload); }
                            catch (Exception alertEx)
                            {
                                Console.Error.WriteLine($"[LOGGING SERVICE] [ALERT CHANNEL CRITICAL] Falló envío: {alertEx.Message}");
                            }
                        });
                    }
                    else
                    {
                        Console.Error.WriteLine($"[LOGGING SERVICE] [ALERT CHANNEL CRITICAL] IAlertChannelService no resuelto. Configura Slack/Telegram webhook para SLA. Original: {message}");
                    }
                }
                catch (Exception alertResolveEx)
                {
                    Console.Error.WriteLine($"[LOGGING SERVICE] [ALERT CHANNEL CRITICAL] No se pudo resolver IAlertChannelService: {alertResolveEx.Message}");
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

                // ✅ ALERTA REAL: además del aviso in-app, enviar EMAIL a los administradores.
                // Antes esto solo creaba la fila in-app (UserId=null) y nadie se enteraba si no
                // miraba el panel. Ahora cualquier log que requiera notificación de admin (todos los
                // Critical) avisa por email vía el mismo job de Hangfire con reintentos.
                try
                {
                    var adminEmails = await context.Users
                        .AsNoTracking()
                        .Where(u => u.Role == newApi.DataLayer.Models.PostGresModels.UserRole.Admin
                                    && u.Email != null && u.Email != "")
                        .Select(u => u.Email)
                        .ToListAsync();

                    if (adminEmails.Count > 0)
                    {
                        // P3-2: dedup/throttle dentro de ventana de 15 min. Los primeros 3 hits del mismo
                        // (source|message|entity) se envían siempre; del 4to en adelante se acumulan y los
                        // resume EmitAdminDigestAsync (Hangfire cada 30 min).
                        var dedupHash = ComputeAlertHash(log.Source, log.Message, log.RelatedEntityType, log.RelatedEntityId);
                        var nowUtc = DateTime.UtcNow;
                        var expiresUtc = nowUtc.AddMinutes(AdminAlertWindowMinutes);
                        var entry = _alertDedupTracker.AddOrUpdate(
                            dedupHash,
                            _ => new AlertDedupEntry
                            {
                                Count = 1,
                                FirstSeenUtc = nowUtc,
                                LastSeenUtc = nowUtc,
                                ExpiresUtc = expiresUtc,
                                Source = log.Source ?? string.Empty,
                                Message = log.Message ?? string.Empty,
                                Details = log.Details ?? string.Empty,
                                EntityType = log.RelatedEntityType ?? string.Empty,
                                EntityId = log.RelatedEntityId
                            },
                            (_, existing) =>
                            {
                                if (existing.ExpiresUtc <= nowUtc)
                                {
                                    existing.Count = 1;
                                    existing.FirstSeenUtc = nowUtc;
                                    existing.ExpiresUtc = expiresUtc;
                                }
                                else
                                {
                                    existing.Count++;
                                }
                                existing.LastSeenUtc = nowUtc;
                                existing.Details = log.Details ?? existing.Details;
                                return existing;
                            });
                        _cache.Set(dedupHash, entry.Count, new MemoryCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(AdminAlertWindowMinutes)
                        });

                        // Severidades que NUNCA se suprimen (siempre van por email).
                        bool isFatalSeverity = string.Equals(logType.Severity?.Name, "Fatal", StringComparison.OrdinalIgnoreCase);
                        bool requiresEmailAlert = logType.RequiresEmailAlert;

                        bool sendImmediateEmail = entry.Count <= AdminAlertImmediateEmailThreshold || isFatalSeverity || requiresEmailAlert;

                        if (sendImmediateEmail)
                        {
                            var subject = $"🚨 {logType.Name.ToUpper()} ALERT - {log.Message}";
                            var bodyHtml = $@"<html><body style='font-family:sans-serif;'>
                                <h2 style='color:#b91c1c;'>🚨 {System.Net.WebUtility.HtmlEncode(logType.Name)} ALERT</h2>
                                <p><strong>{System.Net.WebUtility.HtmlEncode(log.Message)}</strong></p>
                                <p>{System.Net.WebUtility.HtmlEncode(log.Details ?? "")}</p>
                                <p style='color:#6b7280;font-size:12px;'>Source: {System.Net.WebUtility.HtmlEncode(log.Source ?? "")} · {log.RelatedEntityType} #{log.RelatedEntityId} · {log.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC · Hit {entry.Count}/{AdminAlertImmediateEmailThreshold} en ventana de {AdminAlertWindowMinutes} min</p>
                                </body></html>";

                            foreach (var adminEmail in adminEmails)
                            {
                                BackgroundJob.Enqueue<LoggingService>(s => s.SendEmailBackgroundJob(adminEmail!, subject, bodyHtml, null));
                            }
                            Console.WriteLine($"[LOGGING SERVICE] [ADMIN ALERT] Email de alerta encolado para {adminEmails.Count} admin(s) (hit {entry.Count}): {log.Message}");
                        }
                        else
                        {
                            Console.WriteLine($"[LOGGING SERVICE] [ADMIN ALERT] Alerta suprimida (hit {entry.Count} > {AdminAlertImmediateEmailThreshold}) hasta digest. Source={log.Source}, Message={log.Message}");
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine($"[LOGGING SERVICE] ⚠️ [ADMIN ALERT] No hay admins con email para alertar. Alerta: {log.Message} - {log.Details}");
                    }
                }
                catch (Exception emailEx)
                {
                    // No interrumpir el flujo; dejar rastro DURABLE en consola como último recurso.
                    Console.Error.WriteLine($"[LOGGING SERVICE] ⚠️ [ADMIN ALERT] Error encolando email de alerta: {emailEx.Message}. Alerta original: {log.Message} - {log.Details}");

                    // P3-3: fallback no bloqueante a canal alternativo (Slack/Telegram) si está configurado.
                    try
                    {
                        using var scope = _serviceScopeFactory.CreateScope();
                        var alertChannel = scope.ServiceProvider.GetService<IAlertChannelService>();
                        if (alertChannel != null)
                        {
                            var fallbackMsg = $"[ADMIN ALERT fallback SMTP] {log.Message} | Details: {log.Details} | Source: {log.Source}";
                            BackgroundLogger.FireAndForget(async () =>
                            /* 🛡️ MUD-BA: captura excepciones */
                            {
                                try { await alertChannel.SendCriticalAsync(fallbackMsg, new { log.Source, log.RelatedEntityType, log.RelatedEntityId }); }
                                catch (Exception fbEx) { Console.Error.WriteLine($"[LOGGING SERVICE] [ALERT FALLBACK] {fbEx.Message}"); }
                            });
                        }
                    }
                    catch (Exception fallbackResolveEx)
                    {
                        Console.Error.WriteLine($"[LOGGING SERVICE] [ALERT FALLBACK] No se pudo resolver AlertChannelService: {fallbackResolveEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // ✅ Antes era un catch VACÍO → la alerta se perdía en silencio. Dejar rastro DURABLE.
                Console.Error.WriteLine($"[LOGGING SERVICE] 🔴 [ADMIN ALERT] No se pudo crear la notificación de administrador: {ex.Message}. Alerta original: {log?.Message} - {log?.Details}");
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

                // 🛡️ LOTE D · D-24 (refinado por MUD-DL) — Throttle global por (UserId, Title, Message).
                //
                // ANTES MUD-DL: throttle solo por (UserId, Title). Pero el Title se colapsa a 5
                // strings genéricos en GetNotificationTitleAndType ("❌ Error", "⚠️ Advertencia"...)
                // → un Error de payout.failed suprimía durante 5min un Error de transfer.failed o
                // refund.failed completamente NO relacionados. Pérdida silenciosa de avisos
                // legítimos. Auditoría adversarial de 5 agentes detectó esto como P0.
                //
                // AHORA: añade el Message exacto al hash. Dos avisos con mismo Title pero distinto
                // Message ya no chocan. Mismo Title+Message en <5min (verdadero duplicado: mismo
                // webhook retried) sí se suprime. Granularidad correcta sin perder señales.
                var fullMessage = message;
                if (!string.IsNullOrEmpty(details))
                {
                    fullMessage += $" - {details}";
                }
                var throttleWindowStart = DateTime.UtcNow.AddMinutes(-5);
                var alreadyNotified = await context.Notifications
                    .AsNoTracking()
                    .AnyAsync(n => n.UserId == userId
                                && n.Title == title
                                && n.Message == fullMessage
                                && n.CreatedAt > throttleWindowStart);
                if (alreadyNotified)
                {
                    Console.WriteLine($"[LOGGING SERVICE] [D-24/DL THROTTLE] Notificación duplicada omitida para UserId={userId} Title='{title}' (ventana 5min, match exacto title+message).");
                    return;
                }

                // 🛡️ MUD-DK — calcular Url (deep-link) para la fila Notification.
                // ANTES: Url quedaba null porque ProcessUserNotificationAsync solo construía la URL
                // del email. El inbox in-app mostraba el item pero sin "Ver detalles" porque la
                // columna Url estaba vacía. Auditoría detectó que el cliente no podía navegar al
                // recurso afectado desde el drawer.
                string? notificationUrl = null;
                if (relatedEntityType == "SearchHire" && relatedEntityId.HasValue)
                {
                    notificationUrl = $"/detalles/{relatedEntityId.Value}";
                }
                else if (relatedEntityType == "Appointment" && relatedEntityId.HasValue)
                {
                    var appt = await context.Appointments
                        .AsNoTracking()
                        .Select(a => new { a.Id, a.SearchHireId })
                        .FirstOrDefaultAsync(a => a.Id == relatedEntityId.Value);
                    notificationUrl = appt != null ? $"/detalles/{appt.SearchHireId}" : "/appointments";
                }
                else if (relatedEntityType == "ExpertProfile")
                {
                    notificationUrl = "/expert-panel";
                }
                else if (relatedEntityType == "Refund" || relatedEntityType == "Payout" || relatedEntityType == "Transfer" || relatedEntityType == "Dispute")
                {
                    notificationUrl = "/transacciones";
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
                    CreatedAt = DateTime.UtcNow,
                    Url = notificationUrl, // 🛡️ MUD-DK
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
                        var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Resources", "EmailTemplate.html");
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

                    // 🛡️ MUD-DM — HtmlEncode antes de inyectar en el email.
                    // ANTES: el `fullMessage` se interpolaba SIN encode en el body del email →
                    // XSS via campos user-controlled (nombres de servicio, mensajes de error con
                    // HTML, etc.). ProcessAdminNotificationAsync ya lo hace; aplicamos aquí también.
                    var encodedMessage = System.Net.WebUtility.HtmlEncode(fullMessage ?? "");
                    var formattedMessage = encodedMessage.Replace("\n", "<br>");
                    
                    var contentHtml = $@"
                        <p style='margin:0 0 12px 0;font-size:14px;line-height:22px;color:#374151;'>
                            {formattedMessage}
                        </p>
                        <p style='margin:0;font-size:13px;color:#6B7280;'>
                            Accede a tu panel para más detalles.
                        </p>";

                    // 🛡️ LOTE D · D-25 — URLs del email leídas desde App:FrontendBaseUrl.
                    // Antes hardcoded a inspecciono.com → links rotos en dev/staging.
                    string actionUrl = $"{_frontendBaseUrl}/notifications";
                    string actionText = "Ver notificaciones";

                    if (relatedEntityType == "SearchHire" && relatedEntityId.HasValue)
                    {
                        actionUrl = $"{_frontendBaseUrl}/detalles/{relatedEntityId}";
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
                             actionUrl = $"{_frontendBaseUrl}/detalles/{appointment.SearchHireId}";
                             actionText = "Ver detalles";
                        }
                        else
                        {
                             actionUrl = $"{_frontendBaseUrl}/appointments";
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
                await _emailService.SendEmailAsync(toEmail, subject, body, isHtml: true, throwOnError: true);
                
                Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE SUCCESS] Email enviado exitosamente a: {toEmail}, UserId: {userId}");
                System.Diagnostics.Debug.WriteLine($"[LOGGING SERVICE] HANGFIRE SUCCESS: Email sent to {toEmail}");
            }
            catch (Exception emailEx)
            {
                // ✅ LOG: Error al enviar email en Hangfire (se reintentará automáticamente)
                // 🛡️ E2: aplanar cadena completa de InnerException para no perder contexto cuando
                // el async unwrap de Task.WhenAny (EmailService) trunca el stack trace al frame de
                // SendMailAsync. ExceptionChainToString recorre toda la cadena.
                var exceptionChain = ExceptionChainToString(emailEx);
                Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] ERROR al enviar email a: {toEmail}, UserId: {userId}");
                Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] Exception Type: {emailEx.GetType().FullName}");
                Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] Error Message: {emailEx.Message}");
                Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] StackTrace: {emailEx.StackTrace ?? "NULL"}");
                Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] ExceptionChain:\n{exceptionChain}");
                if (emailEx.InnerException != null)
                {
                    Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] InnerException Type: {emailEx.InnerException.GetType().FullName}");
                    Console.WriteLine($"[LOGGING SERVICE] [HANGFIRE ERROR] InnerException Message: {emailEx.InnerException.Message}");
                }
                System.Diagnostics.Debug.WriteLine($"[LOGGING SERVICE] HANGFIRE ERROR: {emailEx.Message}");

                // 🛡️ N27 FIX: detectar errores SMTP permanentes (550-559 series) y NO reintentar.
                // SmtpStatusCode 550=Mailbox unavailable / 551=User not local / 552=Mailbox storage limit /
                // 553=Mailbox name not allowed / 554=Transaction failed (rejected). Estos errores NO mejoran
                // con reintento → quema 3*60-600s antes de Mover a Failed sin razón. Detectable por:
                // 1) SmtpFailedRecipientException con StatusCode permanent, o 2) mensaje contiene "550 "/"552".
                var msg = emailEx.Message ?? "";
                var innerMsg = emailEx.InnerException?.Message ?? "";
                bool isPermanentSmtp =
                    emailEx is System.Net.Mail.SmtpFailedRecipientException smtpFr
                        && (int)smtpFr.StatusCode >= 500 && (int)smtpFr.StatusCode < 600
                    || msg.Contains("550 ", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("551 ", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("552 ", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("553 ", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("554 ", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("mailbox unavailable", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("address rejected", StringComparison.OrdinalIgnoreCase)
                    || innerMsg.Contains("550 ", StringComparison.OrdinalIgnoreCase)
                    || innerMsg.Contains("552 ", StringComparison.OrdinalIgnoreCase);

                if (isPermanentSmtp)
                {
                    Console.Error.WriteLine($"[LOGGING SERVICE] [N27] Email PERMANENT FAIL to {toEmail}: {msg}. NO reintento (gastaría hasta 16 min sin éxito). Marcado como Failed.");
                    // No throw → Hangfire marca como Succeeded (no-op). Si quieres que aparezca en Failed
                    // queue, se podría re-tipificar la excepción, pero el comportamiento más limpio es no
                    // contaminar la queue con jobs irrecuperables.
                    return;
                }

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

        private static string ComputeAlertHash(string? source, string? message, string? entityType, int? entityId)
        {
            var raw = $"{source}|{message}|{entityType}|{entityId}";
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes);
        }

        /// <summary>
        /// 🛡️ E2: Aplana recursivamente toda la cadena ex.InnerException (incluido AggregateException)
        /// en una cadena con formato "Type: Message\nStackTrace\n → InnerType: InnerMessage\n...".
        /// Mitiga la pérdida de contexto en async unwrap (Task.WhenAny en EmailService) donde el
        /// stack trace del frame externo (Hangfire job → SendMailAsync) queda truncado.
        /// </summary>
        private static string ExceptionChainToString(Exception? ex, int depth = 0, int maxDepth = 10)
        {
            if (ex == null || depth > maxDepth) return string.Empty;
            var sb = new StringBuilder();
            var indent = new string(' ', depth * 2);
            sb.Append(indent).Append(ex.GetType().FullName).Append(": ").AppendLine(ex.Message);
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                sb.Append(indent).Append("StackTrace: ").AppendLine(ex.StackTrace);
            }
            if (ex is AggregateException agg)
            {
                int i = 0;
                foreach (var inner in agg.InnerExceptions)
                {
                    sb.Append(indent).Append(" → [AggregateInner ").Append(i++).AppendLine("]");
                    sb.Append(ExceptionChainToString(inner, depth + 1, maxDepth));
                }
            }
            else if (ex.InnerException != null)
            {
                sb.Append(indent).AppendLine(" → InnerException:");
                sb.Append(ExceptionChainToString(ex.InnerException, depth + 1, maxDepth));
            }
            return sb.ToString();
        }

        /// <summary>
        /// P3-2: Emite un email digest agrupando alertas suprimidas (count > umbral) dentro de la ventana
        /// activa. Llamado por Hangfire RecurringJob cada 30 min. No emite nada si no hay suprimidas.
        /// </summary>
        // 🛡️ N13: evita ejecución concurrente en HPA multi-replica de Render (job RecurringJob cada 30 min).
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 1200)]
        [AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 60, 300 })]
        public async Task EmitAdminDigestAsync()
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var suppressed = new List<AlertDedupEntry>();
                foreach (var kvp in _alertDedupTracker.ToArray())
                {
                    var entry = kvp.Value;
                    if (entry.ExpiresUtc <= nowUtc)
                    {
                        _alertDedupTracker.TryRemove(kvp.Key, out _);
                        continue;
                    }
                    if (entry.Count > AdminAlertImmediateEmailThreshold)
                    {
                        suppressed.Add(entry);
                    }
                }

                if (suppressed.Count == 0)
                {
                    return;
                }

                using var scope = _serviceScopeFactory.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var adminEmails = await ctx.Users
                    .AsNoTracking()
                    .Where(u => u.Role == newApi.DataLayer.Models.PostGresModels.UserRole.Admin
                                && u.Email != null && u.Email != "")
                    .Select(u => u.Email)
                    .ToListAsync();

                if (adminEmails.Count == 0)
                {
                    Console.Error.WriteLine($"[LOGGING SERVICE] [ADMIN DIGEST] No hay admins con email; {suppressed.Count} grupos suprimidos no enviados.");
                    return;
                }

                var rows = string.Join("", suppressed.OrderByDescending(e => e.Count).Select(e =>
                    $"<tr><td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(e.Source)}</td>" +
                    $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(e.Message)}</td>" +
                    $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(e.EntityType)} #{e.EntityId}</td>" +
                    $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;text-align:right;'><strong>{e.Count}</strong></td>" +
                    $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;'>{e.FirstSeenUtc:HH:mm} → {e.LastSeenUtc:HH:mm} UTC</td></tr>"));

                var subject = $"📊 Admin Digest - {suppressed.Count} grupo(s) de alertas críticas suprimidas";
                var bodyHtml = $@"<html><body style='font-family:sans-serif;'>
                    <h2 style='color:#b91c1c;'>📊 Digest de alertas admin</h2>
                    <p>Ventana activa de {AdminAlertWindowMinutes} min. Solo se listan grupos con count &gt; {AdminAlertImmediateEmailThreshold}.</p>
                    <table style='border-collapse:collapse;width:100%;font-size:13px;'>
                        <thead><tr style='background:#f3f4f6;'><th style='padding:8px 10px;text-align:left;'>Source</th><th style='padding:8px 10px;text-align:left;'>Message</th><th style='padding:8px 10px;text-align:left;'>Entity</th><th style='padding:8px 10px;text-align:right;'>Count</th><th style='padding:8px 10px;text-align:left;'>Ventana</th></tr></thead>
                        <tbody>{rows}</tbody>
                    </table>
                    </body></html>";

                foreach (var adminEmail in adminEmails)
                {
                    BackgroundJob.Enqueue<LoggingService>(s => s.SendEmailBackgroundJob(adminEmail!, subject, bodyHtml, null));
                }
                Console.WriteLine($"[LOGGING SERVICE] [ADMIN DIGEST] Digest encolado con {suppressed.Count} grupos a {adminEmails.Count} admin(s).");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LOGGING SERVICE] [ADMIN DIGEST] Error emitiendo digest: {ex.Message}");
            }
        }

        /// <summary>
        /// P3-1 (versión mínima): Digest diario de SearchHires marcados con RefundFailedAt en las
        /// últimas 24h (el campo se setea en el filtro de Hangfire cuando RetryMoneyDistributionJobAsync
        /// agota todos los reintentos). Envía un email a los admins listando los hires que requieren
        /// reconciliación manual. No emite nada si no hay hires marcados.
        /// </summary>
        // 🛡️ N13: evita ejecución concurrente en HPA multi-replica (job diario @7am UTC).
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 1800)]
        [AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 300, 1800 })]
        public async Task EmitRefundFailedDigestAsync()
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var since = DateTime.UtcNow.AddHours(-24);

                var failedHires = await ctx.SearchHires
                    .AsNoTracking()
                    .Where(h => h.RefundFailedAt != null && h.RefundFailedAt >= since)
                    .OrderByDescending(h => h.RefundFailedAt)
                    .Select(h => new
                    {
                        h.Id,
                        h.ClientId,
                        h.ExpertId,
                        h.Amount,
                        h.RefundFailedAt,
                        StatusValue = h.Status != null ? h.Status.StatusValue : null
                    })
                    .Take(200)
                    .ToListAsync();

                if (failedHires.Count == 0)
                {
                    return;
                }

                var adminEmails = await ctx.Users
                    .AsNoTracking()
                    .Where(u => u.Role == newApi.DataLayer.Models.PostGresModels.UserRole.Admin
                                && u.Email != null && u.Email != "")
                    .Select(u => u.Email)
                    .ToListAsync();

                if (adminEmails.Count == 0)
                {
                    Console.Error.WriteLine($"[LOGGING SERVICE] [REFUND-FAILED-DIGEST] No hay admins con email; {failedHires.Count} hires con RefundFailedAt sin notificar.");
                    return;
                }

                var rows = string.Join("", failedHires.Select(h =>
                    $"<tr><td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;'>#{h.Id}</td>" +
                    $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(h.StatusValue ?? string.Empty)}</td>" +
                    $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;'>C:{h.ClientId} / E:{h.ExpertId}</td>" +
                    $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;text-align:right;'>{h.Amount:0.00} €</td>" +
                    $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;'>{h.RefundFailedAt:yyyy-MM-dd HH:mm} UTC</td></tr>"));

                var subject = $"⚠️ Refund-failed digest — {failedHires.Count} hire(s) requieren reconciliación manual";
                var bodyHtml = $@"<html><body style='font-family:sans-serif;'>
                    <h2 style='color:#b91c1c;'>⚠️ SearchHires con distribución de dinero fallida</h2>
                    <p>Hires marcados con RefundFailedAt en las últimas 24h (Hangfire agotó reintentos en RetryMoneyDistributionJobAsync).</p>
                    <table style='border-collapse:collapse;width:100%;font-size:13px;'>
                        <thead><tr style='background:#f3f4f6;'><th style='padding:8px 10px;text-align:left;'>SearchHireId</th><th style='padding:8px 10px;text-align:left;'>Estado</th><th style='padding:8px 10px;text-align:left;'>Cliente/Experto</th><th style='padding:8px 10px;text-align:right;'>Importe</th><th style='padding:8px 10px;text-align:left;'>RefundFailedAt</th></tr></thead>
                        <tbody>{rows}</tbody>
                    </table>
                    <p style='color:#6b7280;font-size:12px;'>Acción: revisar manualmente cada hire (transfer/refund) y, al resolverlo, poner RefundFailedAt a NULL.</p>
                    </body></html>";

                foreach (var adminEmail in adminEmails)
                {
                    BackgroundJob.Enqueue<LoggingService>(s => s.SendEmailBackgroundJob(adminEmail!, subject, bodyHtml, null));
                }
                Console.WriteLine($"[LOGGING SERVICE] [REFUND-FAILED-DIGEST] Digest encolado con {failedHires.Count} hires a {adminEmails.Count} admin(s).");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LOGGING SERVICE] [REFUND-FAILED-DIGEST] Error emitiendo digest: {ex.Message}");
            }
        }
    }
}
