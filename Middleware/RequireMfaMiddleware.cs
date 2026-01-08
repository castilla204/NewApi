using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Middleware
{
    /// <summary>
    /// ✅ SEGURIDAD 2025: Middleware para verificar MFA cuando está habilitado
    /// Si un usuario tiene MFA activado, debe verificar el código para acceder
    /// MFA es OPCIONAL para todos los usuarios (no obligatorio)
    /// </summary>
    public class RequireMfaMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequireMfaMiddleware> _logger;

        public RequireMfaMiddleware(RequestDelegate next, ILogger<RequireMfaMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, AppDbContext dbContext)
        {
            var path = context.Request.Path.Value ?? "";
            var method = context.Request.Method;
            
            // ✅ CORRECCIÓN: Rutas públicas que NO requieren autenticación ni MFA
            var publicPaths = new[]
            {
                "/health",
                "/swagger",
                "/api/auth/login",
                "/api/auth/register",
                "/api/ServiceType/public",
                "/api/Categories"
            };

            var isPublicPath = publicPaths.Any(p => 
                context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

            _logger.LogInformation($"[MFA Middleware] 🔍 Procesando: {method} {path}");
            _logger.LogInformation($"[MFA Middleware]    IsPublicPath: {isPublicPath}");

            // Si es ruta pública, permitir acceso sin verificar MFA
            if (isPublicPath)
            {
                _logger.LogInformation($"[MFA Middleware] ✅ Ruta pública - permitiendo acceso sin verificar MFA");
                await _next(context);
                return;
            }

            // Obtener usuario autenticado
            var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var isAuthenticated = context.User?.Identity?.IsAuthenticated ?? false;

            _logger.LogInformation($"[MFA Middleware]    IsAuthenticated: {isAuthenticated}, UserIdClaim: {userIdClaim ?? "N/A"}");

            // Si no está autenticado, continuar (otros middlewares lo manejan)
            if (string.IsNullOrEmpty(userIdClaim))
            {
                _logger.LogInformation($"[MFA Middleware] ✅ No autenticado - permitiendo continuar (otros middlewares manejarán la autorización)");
                await _next(context);
                return;
            }

            // ✅ CORRECCIÓN: Manejar errores de parsing de userId
            if (!int.TryParse(userIdClaim, out var userId))
            {
                _logger.LogWarning($"[MFA Middleware] ⚠️ Invalid userId claim: {userIdClaim}");
                await _next(context);
                return;
            }

            _logger.LogInformation($"[MFA Middleware] 🔍 Verificando MFA para UserId: {userId}");

            try
            {
                // Verificar si tiene MFA habilitado
                _logger.LogInformation($"[MFA Middleware] 📦 Consultando UserMfaSettings para UserId: {userId}");
                var mfaSettings = await dbContext.UserMfaSettings
                    .AsNoTracking() // ✅ MEJORA: No tracking para mejor rendimiento
                    .FirstOrDefaultAsync(m => m.UserId == userId);

                var hasMfaEnabled = mfaSettings != null && mfaSettings.IsEnabled;
                
                _logger.LogInformation($"[MFA Middleware]    MfaSettings encontrado: {mfaSettings != null}, HasMfaEnabled: {hasMfaEnabled}");
                if (mfaSettings != null)
                {
                    _logger.LogInformation($"[MFA Middleware]    LastVerifiedAt: {mfaSettings.LastVerifiedAt}");
                }

                // ✅ Si NO tiene MFA habilitado → PERMITIR acceso (MFA es opcional)
                if (!hasMfaEnabled)
                {
                    _logger.LogInformation($"[MFA Middleware] ✅ MFA no habilitado para UserId {userId} - permitiendo acceso");
                    await _next(context);
                    return;
                }

                // ✅ Si tiene MFA habilitado, verificar si lo ha verificado en esta sesión
                // Rutas permitidas (para que puedan configurar y verificar MFA)
                var allowedPaths = new[]
                {
                    "/api/auth/mfa/setup",
                    "/api/auth/mfa/enable",
                    "/api/auth/mfa/verify",  // ✅ CRÍTICO: Permitir verificar código MFA
                    "/api/auth/mfa/status",
                    "/api/auth/mfa/disable",
                    "/api/auth/logout",
                    "/api/user/profile" // Para que vean su perfil
                };

                var isAllowedPath = allowedPaths.Any(p => 
                    context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

                // Si está en ruta permitida, permitir acceso
                if (isAllowedPath)
                {
                    await _next(context);
                    return;
                }

                // Verificar si LastVerifiedAt es reciente (dentro de las últimas 8 horas)
                // Esto permite que el usuario no tenga que verificar el código en cada request
                var mfaVerificationValidDuration = TimeSpan.FromHours(8);
                var isMfaVerified = mfaSettings.LastVerifiedAt.HasValue && 
                                   (DateTime.UtcNow - mfaSettings.LastVerifiedAt.Value) < mfaVerificationValidDuration;

                _logger.LogInformation($"[MFA Middleware]    Verificando MFA: LastVerifiedAt={mfaSettings.LastVerifiedAt}, IsMfaVerified={isMfaVerified}");

                // ❌ Si MFA está habilitado pero NO está verificado → BLOQUEAR
                if (!isMfaVerified)
                {
                    _logger.LogWarning(
                        $"[MFA Middleware] ❌ MFA verification required: User {userId} attempted to access {path} without verifying MFA code. LastVerifiedAt: {mfaSettings.LastVerifiedAt}");

                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "MFA_VERIFICATION_REQUIRED",
                        message = "Multi-factor authentication code verification is required. Please verify your MFA code to continue.",
                        requiresMfaVerification = true,
                        verifyUrl = "/api/auth/mfa/verify",
                        lastVerifiedAt = mfaSettings.LastVerifiedAt
                    });
                    return;
                }

                // ✅ Si MFA está habilitado y verificado, permitir acceso
                _logger.LogInformation(
                    $"[MFA Middleware] ✅ MFA verified access: User {userId} accessing {path}. LastVerifiedAt: {mfaSettings?.LastVerifiedAt}");

                await _next(context);
            }
            catch (Exception ex)
            {
                // ✅ CORRECCIÓN: Si hay error en la consulta, loguear pero permitir acceso
                // No bloquear todas las peticiones por un error en MFA
                _logger.LogError(ex, $"[MFA Middleware] ❌ ERROR checking MFA for user {userId} on path {path}. Allowing access to prevent blocking all requests.");
                _logger.LogError($"[MFA Middleware]    Exception: {ex.Message}");
                _logger.LogError($"[MFA Middleware]    StackTrace: {ex.StackTrace}");
                await _next(context);
            }
        }
    }

    /// <summary>
    /// Extensión para registrar el middleware
    /// </summary>
    public static class RequireMfaMiddlewareExtensions
    {
        public static IApplicationBuilder UseRequireMfa(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<RequireMfaMiddleware>();
        }
    }
}

