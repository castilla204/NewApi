using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace newApi.Filters
{
    public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();
            
            // Verificar si el usuario está autenticado
            if (httpContext.User?.Identity?.IsAuthenticated != true)
            {
                return false;
            }
            
            // Verificar si el usuario tiene rol Admin
            // El rol puede estar en diferentes claims dependiendo de la configuración
            var isAdmin = httpContext.User.IsInRole("Admin") || 
                         httpContext.User.IsInRole("1") || // Role puede ser un número
                         httpContext.User.HasClaim("Role", "Admin") ||
                         httpContext.User.HasClaim("Role", "1");
            
            return isAdmin;
        }
    }
}
