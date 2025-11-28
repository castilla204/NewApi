using Hangfire.Dashboard;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using System.Linq;

namespace newApi.Filters
{
    public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
    {
        private readonly IConfiguration _configuration;

        public HangfireAuthorizationFilter(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();
            
            // 1. Intentar obtener token desde query parameter (para iframes)
            var tokenFromQuery = httpContext.Request.Query["token"].FirstOrDefault();
            
            // 2. Intentar obtener token desde Authorization header
            var authHeader = httpContext.Request.Headers["Authorization"].FirstOrDefault();
            var tokenFromHeader = authHeader?.StartsWith("Bearer ") == true 
                ? authHeader.Substring("Bearer ".Length).Trim() 
                : null;
            
            // 3. Usar token de query o header
            var token = tokenFromQuery ?? tokenFromHeader;
            
            // Log para debugging
            Console.WriteLine($"[HangfireAuth] Request to: {httpContext.Request.Path}");
            Console.WriteLine($"[HangfireAuth] Token from query: {(string.IsNullOrEmpty(tokenFromQuery) ? "None" : "Present")}");
            Console.WriteLine($"[HangfireAuth] Token from header: {(string.IsNullOrEmpty(tokenFromHeader) ? "None" : "Present")}");
            
            // 4. Si hay token, validarlo y extraer claims
            if (!string.IsNullOrEmpty(token))
            {
                try
                {
                    var principal = ValidateJwtToken(token);
                    if (principal != null)
                    {
                        // Establecer el usuario en el contexto HTTP
                        httpContext.User = principal;
                        Console.WriteLine($"[HangfireAuth] Token validated successfully. User: {principal.Identity?.Name}, Roles: {string.Join(", ", principal.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value))}");
                    }
                    else
                    {
                        Console.WriteLine("[HangfireAuth] Token validation returned null");
                    }
                }
                catch (Exception ex)
                {
                    // Log del error
                    Console.WriteLine($"[HangfireAuth] Error validating JWT token: {ex.Message}");
                    Console.WriteLine($"[HangfireAuth] Stack trace: {ex.StackTrace}");
                }
            }
            else
            {
                Console.WriteLine("[HangfireAuth] No token provided in query parameter or header");
            }
            
            // 5. Verificar autenticación
            if (httpContext.User?.Identity?.IsAuthenticated != true)
            {
                Console.WriteLine("[HangfireAuth] User not authenticated");
                return false;
            }
            
            // 6. Verificar rol Admin
            var isAdmin = httpContext.User.IsInRole("Admin") || 
                         httpContext.User.IsInRole("1") || 
                         httpContext.User.HasClaim("Role", "Admin") ||
                         httpContext.User.HasClaim("Role", "1") ||
                         httpContext.User.HasClaim(ClaimTypes.Role, "Admin") ||
                         httpContext.User.HasClaim(ClaimTypes.Role, "1");
            
            Console.WriteLine($"[HangfireAuth] Is Admin: {isAdmin}");
            
            return isAdmin;
        }

        private ClaimsPrincipal? ValidateJwtToken(string token)
        {
            try
            {
                // Obtener configuración JWT (usar la misma que AuthController)
                var jwtKey = _configuration["Jwt:Key"] ??
                            _configuration["JWT:Secret"] ?? 
                            Environment.GetEnvironmentVariable("JWT_SECRET");
                
                var jwtIssuer = _configuration["Jwt:Issuer"] ??
                               _configuration["JWT:Issuer"] ??
                               Environment.GetEnvironmentVariable("JWT_ISSUER");
                
                var jwtAudience = _configuration["Jwt:Audience"] ??
                                 _configuration["JWT:Audience"] ??
                                 Environment.GetEnvironmentVariable("JWT_AUDIENCE");

                if (string.IsNullOrEmpty(jwtKey))
                {
                    Console.WriteLine("JWT Key not configured");
                    return null;
                }

                var tokenHandler = new JwtSecurityTokenHandler();
                var key = Encoding.UTF8.GetBytes(jwtKey);

                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = !string.IsNullOrEmpty(jwtIssuer),
                    ValidIssuer = jwtIssuer,
                    ValidateAudience = !string.IsNullOrEmpty(jwtAudience),
                    ValidAudience = jwtAudience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };

                var principal = tokenHandler.ValidateToken(token, validationParameters, out SecurityToken validatedToken);
                return principal;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"JWT validation error: {ex.Message}");
                return null;
            }
        }
    }
}
