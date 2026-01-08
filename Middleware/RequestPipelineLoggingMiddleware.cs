using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Linq;

namespace newApi.Middleware
{
    /// <summary>
    /// ✅ Middleware de logging completo del pipeline de requests
    /// Basado en mejores prácticas para diagnosticar por qué algunos endpoints funcionan y otros no
    /// </summary>
    public class RequestPipelineLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestPipelineLoggingMiddleware> _logger;

        public RequestPipelineLoggingMiddleware(RequestDelegate next, ILogger<RequestPipelineLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var requestId = Guid.NewGuid().ToString("N")[..8];
            var stopwatch = Stopwatch.StartNew();
            var path = context.Request.Path.Value ?? "";
            var method = context.Request.Method;
            
            // ✅ PASO 1: Logging inicial de la request
            _logger.LogInformation($"[PIPELINE] ========================================");
            _logger.LogInformation($"[PIPELINE] 📥 REQUEST INICIADA - RequestId: {requestId}");
            _logger.LogInformation($"[PIPELINE]    Method: {method}");
            _logger.LogInformation($"[PIPELINE]    Path: {path}");
            _logger.LogInformation($"[PIPELINE]    QueryString: {context.Request.QueryString}");
            _logger.LogInformation($"[PIPELINE]    RemoteIp: {context.Connection.RemoteIpAddress}");
            _logger.LogInformation($"[PIPELINE]    UserAgent: {context.Request.Headers["User-Agent"].ToString().Substring(0, Math.Min(50, context.Request.Headers["User-Agent"].ToString().Length))}");
            
            // ✅ PASO 2: Verificar headers importantes
            var hasAuth = context.Request.Headers.ContainsKey("Authorization");
            var origin = context.Request.Headers["Origin"].ToString();
            var contentType = context.Request.ContentType ?? "N/A";
            
            _logger.LogInformation($"[PIPELINE]    Headers:");
            _logger.LogInformation($"[PIPELINE]      - Origin: {origin}");
            _logger.LogInformation($"[PIPELINE]      - Content-Type: {contentType}");
            _logger.LogInformation($"[PIPELINE]      - HasAuthorization: {hasAuth}");
            if (hasAuth)
            {
                var authHeader = context.Request.Headers["Authorization"].ToString();
                _logger.LogInformation($"[PIPELINE]      - Authorization: {authHeader.Substring(0, Math.Min(30, authHeader.Length))}...");
            }
            
            // ✅ PASO 3: Verificar estado ANTES del pipeline
            _logger.LogInformation($"[PIPELINE]    Estado ANTES del pipeline:");
            _logger.LogInformation($"[PIPELINE]      - IsAuthenticated: {context.User?.Identity?.IsAuthenticated ?? false}");
            _logger.LogInformation($"[PIPELINE]      - Endpoint: {context.GetEndpoint()?.DisplayName ?? "NULL (no mapeado aún)"}");
            
            // ✅ PASO 4: Capturar el estado del endpoint ANTES de ejecutar middlewares
            var endpointBefore = context.GetEndpoint();
            _logger.LogInformation($"[PIPELINE]    Endpoint ANTES de middlewares: {endpointBefore?.DisplayName ?? "NULL"}");
            
            // ✅ PASO 5: Ejecutar el siguiente middleware y capturar tiempo
            var middlewareStartTime = stopwatch.ElapsedMilliseconds;
            Exception? pipelineException = null;
            
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                pipelineException = ex;
                _logger.LogError(ex, $"[PIPELINE] ❌ EXCEPCIÓN en pipeline - RequestId: {requestId}");
                _logger.LogError($"[PIPELINE]    Exception Type: {ex.GetType().Name}");
                _logger.LogError($"[PIPELINE]    Exception Message: {ex.Message}");
                _logger.LogError($"[PIPELINE]    StackTrace: {ex.StackTrace}");
                throw; // Re-lanzar para que otros middlewares lo manejen
            }
            finally
            {
                stopwatch.Stop();
                var middlewareDuration = stopwatch.ElapsedMilliseconds - middlewareStartTime;
                
                // ✅ PASO 6: Verificar estado DESPUÉS del pipeline
                var endpointAfter = context.GetEndpoint();
                var routeData = context.GetRouteData();
                
                _logger.LogInformation($"[PIPELINE]    Estado DESPUÉS del pipeline:");
                _logger.LogInformation($"[PIPELINE]      - IsAuthenticated: {context.User?.Identity?.IsAuthenticated ?? false}");
                _logger.LogInformation($"[PIPELINE]      - Endpoint: {endpointAfter?.DisplayName ?? "NULL"}");
                
                // ✅ PASO 7: Verificar si el endpoint se mapeó correctamente
                if (endpointAfter == null)
                {
                    _logger.LogWarning($"[PIPELINE] ⚠️ ADVERTENCIA: Endpoint NO mapeado para {method} {path}");
                    _logger.LogWarning($"[PIPELINE]    Esto puede indicar un problema de routing");
                }
                else
                {
                    _logger.LogInformation($"[PIPELINE] ✅ Endpoint mapeado correctamente:");
                    _logger.LogInformation($"[PIPELINE]      - DisplayName: {endpointAfter.DisplayName}");
                    
                    // Obtener información de la ruta desde RouteData si está disponible
                    if (routeData != null && routeData.Values.Any())
                    {
                        var routeInfo = string.Join(", ", routeData.Values.Select(kvp => $"{kvp.Key}={kvp.Value}"));
                        _logger.LogInformation($"[PIPELINE]      - RouteValues: {routeInfo}");
                    }
                    
                    // Verificar metadata del endpoint
                    var allowAnonymous = endpointAfter.Metadata?.GetMetadata<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() != null;
                    var authorize = endpointAfter.Metadata?.GetMetadata<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>() != null;
                    var httpMethod = endpointAfter.Metadata?.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>();
                    
                    _logger.LogInformation($"[PIPELINE]      - AllowAnonymous: {allowAnonymous}");
                    _logger.LogInformation($"[PIPELINE]      - Authorize: {authorize}");
                    if (httpMethod != null)
                    {
                        _logger.LogInformation($"[PIPELINE]      - HttpMethods: {string.Join(", ", httpMethod.HttpMethods)}");
                    }
                }
                
                // ✅ PASO 8: Verificar route data
                if (routeData != null && routeData.Values.Any())
                {
                    _logger.LogInformation($"[PIPELINE]    Route Data:");
                    foreach (var kvp in routeData.Values)
                    {
                        _logger.LogInformation($"[PIPELINE]      - {kvp.Key}: {kvp.Value}");
                    }
                }
                
                // ✅ PASO 9: Logging de la respuesta
                var statusCode = context.Response.StatusCode;
                var responseContentType = context.Response.ContentType ?? "N/A";
                var responseHeaders = new StringBuilder();
                
                _logger.LogInformation($"[PIPELINE]    Response:");
                _logger.LogInformation($"[PIPELINE]      - StatusCode: {statusCode}");
                _logger.LogInformation($"[PIPELINE]      - ContentType: {responseContentType}");
                
                // Logging de headers CORS si están presentes
                if (context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"))
                {
                    _logger.LogInformation($"[PIPELINE]      - CORS Headers:");
                    _logger.LogInformation($"[PIPELINE]        - Access-Control-Allow-Origin: {context.Response.Headers["Access-Control-Allow-Origin"]}");
                    if (context.Response.Headers.ContainsKey("Access-Control-Allow-Methods"))
                    {
                        _logger.LogInformation($"[PIPELINE]        - Access-Control-Allow-Methods: {context.Response.Headers["Access-Control-Allow-Methods"]}");
                    }
                }
                
                // ✅ PASO 10: Análisis de resultados
                _logger.LogInformation($"[PIPELINE]    Análisis:");
                _logger.LogInformation($"[PIPELINE]      - Duración total: {stopwatch.ElapsedMilliseconds}ms");
                _logger.LogInformation($"[PIPELINE]      - Duración middlewares: {middlewareDuration}ms");
                
                if (statusCode >= 400)
                {
                    _logger.LogWarning($"[PIPELINE] ⚠️ ERROR RESPONSE: {statusCode}");
                    
                    if (statusCode == 401)
                    {
                        _logger.LogWarning($"[PIPELINE]    Razón: Unauthorized - Token inválido o faltante");
                        _logger.LogWarning($"[PIPELINE]    Verificar: JWT configuration, token expiration, AllowAnonymous attribute");
                    }
                    else if (statusCode == 403)
                    {
                        _logger.LogWarning($"[PIPELINE]    Razón: Forbidden - Usuario autenticado pero sin permisos");
                        _logger.LogWarning($"[PIPELINE]    Verificar: Roles, MFA requirements, authorization policies");
                    }
                    else if (statusCode == 404)
                    {
                        _logger.LogWarning($"[PIPELINE]    Razón: Not Found - Endpoint no encontrado");
                        _logger.LogWarning($"[PIPELINE]    Verificar: Route configuration, endpoint mapping, path matching");
                    }
                    else if (statusCode == 500)
                    {
                        _logger.LogError($"[PIPELINE]    Razón: Internal Server Error - Excepción no manejada");
                        _logger.LogError($"[PIPELINE]    Verificar: Exception logs, database connection, service dependencies");
                    }
                    else if (statusCode == 502)
                    {
                        _logger.LogError($"[PIPELINE]    Razón: Bad Gateway - Problema de proxy o servidor upstream");
                        _logger.LogError($"[PIPELINE]    Verificar: Server configuration, port binding, health checks");
                    }
                }
                else
                {
                    _logger.LogInformation($"[PIPELINE] ✅ SUCCESS: {statusCode}");
                }
                
                // ✅ PASO 11: Comparación con endpoints que funcionan
                var isPublicEndpoint = path.Contains("/ServiceType/public") || 
                                     path.Contains("/Categories") || 
                                     path.Contains("/homepage-wall") ||
                                     path.Contains("/health");
                
                if (!isPublicEndpoint && statusCode >= 400)
                {
                    _logger.LogWarning($"[PIPELINE] 🔍 DIAGNÓSTICO: Endpoint protegido falló mientras endpoints públicos funcionan");
                    _logger.LogWarning($"[PIPELINE]    Posibles causas:");
                    _logger.LogWarning($"[PIPELINE]      1. Problema de autenticación/autorización");
                    _logger.LogWarning($"[PIPELINE]      2. MFA requirement no cumplido");
                    _logger.LogWarning($"[PIPELINE]      3. Problema de routing específico para este endpoint");
                    _logger.LogWarning($"[PIPELINE]      4. Error en el controlador específico");
                }
                
                _logger.LogInformation($"[PIPELINE] 📤 REQUEST FINALIZADA - RequestId: {requestId}");
                _logger.LogInformation($"[PIPELINE] ========================================");
            }
        }
    }
}
