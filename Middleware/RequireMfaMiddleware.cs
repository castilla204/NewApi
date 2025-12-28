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

            // Si es ruta pública, permitir acceso sin verificar MFA
            if (isPublicPath)
            {
                await _next(context);
                return;
            }

            // Obtener usuario autenticado
            var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            // Si no está autenticado, continuar (otros middlewares lo manejan)
            if (string.IsNullOrEmpty(userIdClaim))
            {
                await _next(context);
                return;
            }

            // ✅ CORRECCIÓN: Manejar errores de parsing de userId
            if (!int.TryParse(userIdClaim, out var userId))
            {
                _logger.LogWarning("Invalid userId claim: {UserIdClaim}", userIdClaim);
                await _next(context);
                return;
            }

            try
            {
                // Verificar si tiene MFA habilitado
                var mfaSettings = await dbContext.UserMfaSettings
                    .AsNoTracking() // ✅ MEJORA: No tracking para mejor rendimiento
                    .FirstOrDefaultAsync(m => m.UserId == userId);

                var hasMfaEnabled = mfaSettings != null && mfaSettings.IsEnabled;

                // ✅ Si NO tiene MFA habilitado → PERMITIR acceso (MFA es opcional)
                if (!hasMfaEnabled)
                {
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

                // ❌ Si MFA está habilitado pero NO está verificado → BLOQUEAR
                if (!isMfaVerified)
                {
                    _logger.LogWarning(
                        "MFA verification required: User {UserId} attempted to access {Path} without verifying MFA code. LastVerifiedAt: {LastVerifiedAt}",
                        userId, context.Request.Path, mfaSettings.LastVerifiedAt);

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
                    "MFA verified access: User {UserId} accessing {Path}. LastVerifiedAt: {LastVerifiedAt}",
                    userId, context.Request.Path, mfaSettings?.LastVerifiedAt);

                await _next(context);
            }
            catch (Exception ex)
            {
                // ✅ CORRECCIÓN: Si hay error en la consulta, loguear pero permitir acceso
                // No bloquear todas las peticiones por un error en MFA
                _logger.LogError(ex, "Error checking MFA for user {UserId} on path {Path}. Allowing access to prevent blocking all requests.", userId, context.Request.Path);
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

