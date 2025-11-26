using Hangfire.Dashboard;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace newApi.Filters
{
    public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();
            
            // Si no hay usuario, denegar acceso
            if (httpContext.User == null || httpContext.User.Identity == null)
            {
                return false;
            }
            
            // Verificar si el usuario está autenticado
            if (!httpContext.User.Identity.IsAuthenticated)
            {
                // No autenticado - Hangfire mostrará su propia página
                // pero necesitamos permitir que Hangfire procese la petición
                return false;
            }
            
            // Usuario autenticado - verificar rol Admin
            // El rol puede estar almacenado de diferentes formas
            var isAdmin = httpContext.User.IsInRole("Admin") || 
                         httpContext.User.IsInRole("1") || 
                         httpContext.User.HasClaim("Role", "Admin") ||
                         httpContext.User.HasClaim("Role", "1") ||
                         httpContext.User.HasClaim(ClaimTypes.Role, "Admin") ||
                         httpContext.User.HasClaim(ClaimTypes.Role, "1");
            
            return isAdmin;
        }
    }
}
