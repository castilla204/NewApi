using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Middleware
{
    /// <summary>
    /// ✅ SEGURIDAD 2025: Middleware para FORZAR MFA en Admin y Expertos
    /// OWASP/NIST/PCI DSS: MFA obligatorio para cuentas privilegiadas
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
            // Obtener usuario autenticado
            var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var userRoleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value;

            // Si no está autenticado, continuar (otros middlewares lo manejan)
            if (string.IsNullOrEmpty(userIdClaim) || string.IsNullOrEmpty(userRoleClaim))
            {
                await _next(context);
                return;
            }

            // Parsear role
            if (!Enum.TryParse<UserRole>(userRoleClaim, out var userRole))
            {
                await _next(context);
                return;
            }

            // ✅ REGLA: MFA OBLIGATORIO para Admin y Expert
            var requiresMfa = userRole == UserRole.Admin || userRole == UserRole.Expert;

            if (requiresMfa)
            {
                var userId = int.Parse(userIdClaim);

                // Verificar si tiene MFA habilitado
                var mfaSettings = await dbContext.UserMfaSettings
                    .FirstOrDefaultAsync(m => m.UserId == userId);

                var hasMfaEnabled = mfaSettings != null && mfaSettings.IsEnabled;

                // ✅ Rutas permitidas (para que puedan configurar y verificar MFA)
                var allowedPaths = new[]
                {
                    "/api/auth/mfa/setup",
                    "/api/auth/mfa/enable",
                    "/api/auth/mfa/verify",  // ✅ CRÍTICO: Permitir verificar código MFA
                    "/api/auth/mfa/status",
                    "/api/auth/logout",
                    "/api/user/profile" // Para que vean su perfil
                };

                var isAllowedPath = allowedPaths.Any(p => 
                    context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

                // ❌ CASO 1: Si NO tiene MFA habilitado y NO está en ruta permitida → BLOQUEAR
                if (!hasMfaEnabled && !isAllowedPath)
                {
                    _logger.LogWarning(
                        "MFA required: User {UserId} (Role: {Role}) attempted to access {Path} without MFA enabled",
                        userId, userRole, context.Request.Path);

                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "MFA_REQUIRED",
                        message = "Multi-factor authentication is required for your account type. Please enable MFA to continue.",
                        requiresMfaSetup = true,
                        setupUrl = "/api/auth/mfa/setup",
                        userRole = userRole.ToString()
                    });
                    return;
                }

                // ✅ CASO 2: Si tiene MFA habilitado, verificar si lo ha verificado en esta sesión
                if (hasMfaEnabled)
                {
                    // Verificar si LastVerifiedAt es reciente (dentro de las últimas 8 horas)
                    // Esto permite que el usuario no tenga que verificar el código en cada request
                    var mfaVerificationValidDuration = TimeSpan.FromHours(8);
                    var isMfaVerified = mfaSettings.LastVerifiedAt.HasValue && 
                                       (DateTime.UtcNow - mfaSettings.LastVerifiedAt.Value) < mfaVerificationValidDuration;

                    // ❌ Si MFA está habilitado pero NO está verificado y NO está en ruta permitida → BLOQUEAR
                    if (!isMfaVerified && !isAllowedPath)
                    {
                        _logger.LogWarning(
                            "MFA verification required: User {UserId} (Role: {Role}) attempted to access {Path} without verifying MFA code. LastVerifiedAt: {LastVerifiedAt}",
                            userId, userRole, context.Request.Path, mfaSettings.LastVerifiedAt);

                        context.Response.StatusCode = 403;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "MFA_VERIFICATION_REQUIRED",
                            message = "Multi-factor authentication code verification is required. Please verify your MFA code to continue.",
                            requiresMfaVerification = true,
                            verifyUrl = "/api/auth/mfa/verify",
                            lastVerifiedAt = mfaSettings.LastVerifiedAt,
                            userRole = userRole.ToString()
                        });
                        return;
                    }

                    // ✅ Si MFA está habilitado y verificado, permitir acceso
                    if (isMfaVerified)
                    {
                        _logger.LogInformation(
                            "MFA verified access: User {UserId} (Role: {Role}) accessing {Path}. LastVerifiedAt: {LastVerifiedAt}",
                            userId, userRole, context.Request.Path, mfaSettings.LastVerifiedAt);
                    }
                }
            }

            await _next(context);
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

