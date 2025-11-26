using Hangfire.Dashboard;
using System.Security.Claims;

namespace newApi.Filters
{
    public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();
            
            // Permitir acceso si hay un usuario (autenticado o no)
            // Hangfire manejará la autenticación internamente
            if (httpContext.User?.Identity?.IsAuthenticated == true)
            {
                // Usuario autenticado - verificar rol Admin
                var isAdmin = httpContext.User.IsInRole("Admin") || 
                             httpContext.User.IsInRole("1") || 
                             httpContext.User.HasClaim("Role", "Admin") ||
                             httpContext.User.HasClaim("Role", "1") ||
                             httpContext.User.HasClaim(ClaimTypes.Role, "Admin") ||
                             httpContext.User.HasClaim(ClaimTypes.Role, "1");
                
                return isAdmin;
            }
            
            // No autenticado - permitir que Hangfire muestre su página
            // El usuario necesitará autenticarse para ver el contenido
            return true;
        }
    }
}
