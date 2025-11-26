using Hangfire.Dashboard;
using System.Security.Claims;

namespace newApi.Filters
{
    public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();
            
            // Si no hay usuario o no está autenticado, denegar
            if (httpContext.User?.Identity?.IsAuthenticated != true)
            {
                return false;
            }
            
            // Usuario autenticado - verificar rol Admin
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
