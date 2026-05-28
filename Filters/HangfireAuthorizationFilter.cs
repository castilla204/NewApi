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
            var path = httpContext.Request.Path.Value ?? "";
            
            // ✅ PERMITIR recursos estáticos sin autenticación (CSS, JS, imágenes, fuentes)
            // Estos recursos no exponen información sensible y son necesarios para que el dashboard funcione
            if (path.Contains("/css") || 
                path.Contains("/js") || 
                path.Contains("/img") || 
                path.Contains("/fonts") ||
                path.Contains("/font") ||
                path.EndsWith(".css") ||
                path.EndsWith(".js") ||
                path.EndsWith(".png") ||
                path.EndsWith(".jpg") ||
                path.EndsWith(".jpeg") ||
                path.EndsWith(".gif") ||
                path.EndsWith(".svg") ||
                path.EndsWith(".woff") ||
                path.EndsWith(".woff2") ||
                path.EndsWith(".ttf") ||
                path.EndsWith(".eot"))
            {
                // Permitir recursos estáticos sin autenticación (son archivos públicos)
                // No necesitan validación de token ya que no exponen información sensible
                return true;
            }
            
            // ✅ 1. PRIMERO: Verificar cookie de sesión (para mantener autenticación durante navegación)
            var tokenFromCookie = httpContext.Request.Cookies["HangfireAuthToken"];
            
            // 2. Intentar obtener token desde query parameter (para iframes y primera carga)
            var tokenFromQuery = httpContext.Request.Query["token"].FirstOrDefault();
            
            // 3. Intentar obtener token desde Authorization header
            var authHeader = httpContext.Request.Headers["Authorization"].FirstOrDefault();
            var tokenFromHeader = authHeader?.StartsWith("Bearer ") == true 
                ? authHeader.Substring("Bearer ".Length).Trim() 
                : null;
            
            // 4. ✅ INTENTAR EXTRAER TOKEN DEL REFERRER (para navegación dentro del dashboard)
            string? tokenFromReferer = null;
            var referer = httpContext.Request.Headers["Referer"].FirstOrDefault();
            if (!string.IsNullOrEmpty(referer) && string.IsNullOrEmpty(tokenFromQuery) && string.IsNullOrEmpty(tokenFromHeader) && string.IsNullOrEmpty(tokenFromCookie))
            {
                try
                {
                    var refererUri = new Uri(referer);
                    tokenFromReferer = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(refererUri.Query)["token"].FirstOrDefault();
                }
                catch
                {
                    // Si falla parsear el referrer, continuar sin token del referrer
                }
            }
            
            // 5. Intentar validar tokens en orden de prioridad: cookie primero, luego query/header/referrer
            // Si la cookie es inválida, intentar con el token del query parameter
            string? validToken = null;
            ClaimsPrincipal? validPrincipal = null;
            
            // Log para debugging
            Console.WriteLine($"[HangfireAuth] Request to: {httpContext.Request.Path}");
            Console.WriteLine($"[HangfireAuth] Token from cookie: {(string.IsNullOrEmpty(tokenFromCookie) ? "None" : "Present")}");
            Console.WriteLine($"[HangfireAuth] Token from query: {(string.IsNullOrEmpty(tokenFromQuery) ? "None" : "Present")}");
            Console.WriteLine($"[HangfireAuth] Token from header: {(string.IsNullOrEmpty(tokenFromHeader) ? "None" : "Present")}");
            Console.WriteLine($"[HangfireAuth] Token from referer: {(string.IsNullOrEmpty(tokenFromReferer) ? "None" : "Present")}");
            
            // Intentar validar token de cookie primero
            if (!string.IsNullOrEmpty(tokenFromCookie))
            {
                try
                {
                    var principal = ValidateJwtToken(tokenFromCookie);
                    if (principal != null && IsAdmin(principal))
                    {
                        validToken = tokenFromCookie;
                        validPrincipal = principal;
                        Console.WriteLine("[HangfireAuth] ✅ Token de cookie válido");
                    }
                    else
                    {
                        Console.WriteLine("[HangfireAuth] ⚠️ Token de cookie inválido o expirado, intentando con query parameter...");
                        // Eliminar cookie inválida
                        httpContext.Response.Cookies.Delete("HangfireAuthToken");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HangfireAuth] ⚠️ Error validando token de cookie: {ex.Message}, intentando con query parameter...");
                    // Eliminar cookie con error
                    httpContext.Response.Cookies.Delete("HangfireAuthToken");
                }
            }
            
            // Si la cookie no fue válida, intentar con query/header/referrer
            // 🛡️ N26 FIX: priorizar Authorization header sobre query/referrer (token en URL queda en
            // logs de servidor, browser history, referer headers → riesgo de filtración). Si la auth
            // se consigue vía query o referer, log Critical para que el admin migre al header (o a
            // un endpoint POST que setee solo la cookie). NO eliminamos el query/referer fallback
            // porque rompería el bootstrap del dashboard si el frontend solo lo soporta así.
            if (validToken == null)
            {
                var fallbackToken = tokenFromHeader ?? tokenFromQuery ?? tokenFromReferer;
                if (!string.IsNullOrEmpty(fallbackToken))
                {
                    try
                    {
                        var principal = ValidateJwtToken(fallbackToken);
                        if (principal != null && IsAdmin(principal))
                        {
                            validToken = fallbackToken;
                            validPrincipal = principal;
                            // Avisar si la auth llegó por una vía insegura
                            if (string.ReferenceEquals(fallbackToken, tokenFromQuery) || string.ReferenceEquals(fallbackToken, tokenFromReferer))
                            {
                                Console.Error.WriteLine("[HangfireAuth] ⚠️ N26: token autenticado vía query/referer — TODO: migrar a Authorization header o endpoint POST que setee cookie. Token en URL queda expuesto en access logs, browser history y referer headers a terceros.");
                            }
                            Console.WriteLine("[HangfireAuth] ✅ Token de query/header/referrer válido");
                        }
                        else
                        {
                            Console.WriteLine("[HangfireAuth] ❌ Token de query/header/referrer inválido o usuario no es Admin");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[HangfireAuth] ❌ Error validando token de query/header/referrer: {ex.Message}");
                    }
                }
            }
            
            // 6. Si tenemos un token válido, establecer usuario y cookie
            if (validToken != null && validPrincipal != null)
            {
                // Establecer el usuario en el contexto HTTP
                httpContext.User = validPrincipal;
                
                // ✅ ESTABLECER/ACTUALIZAR COOKIE DE SESIÓN si el token viene de query/header/referrer
                // O si la cookie anterior era inválida (para renovarla)
                if (string.IsNullOrEmpty(tokenFromCookie) || tokenFromCookie != validToken)
                {
                    // Establecer cookie HttpOnly y SameSite para seguridad
                    // ✅ PRODUCCIÓN: SameSite=None con Secure=true para iframes cross-origin
                    var isHttps = httpContext.Request.IsHttps || 
                                 httpContext.Request.Headers["X-Forwarded-Proto"].ToString().Equals("https", StringComparison.OrdinalIgnoreCase);
                    
                    var cookieOptions = new Microsoft.AspNetCore.Http.CookieOptions
                    {
                        HttpOnly = true, // Previene acceso desde JavaScript (seguridad)
                        // SameSite=None es necesario para iframes cross-origin (inspecciono.com -> api.atrapo.io)
                        // Secure=true es obligatorio cuando SameSite=None
                        SameSite = isHttps ? Microsoft.AspNetCore.Http.SameSiteMode.None : Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                        Secure = isHttps, // Obligatorio en producción (HTTPS), opcional en desarrollo
                        Expires = DateTimeOffset.UtcNow.AddHours(1), // Expira en 1 hora (o cuando expire el token)
                        Path = "/hangfire", // Solo válida para rutas de Hangfire
                        // Domain no se establece para permitir que funcione en subdominios
                    };
                    httpContext.Response.Cookies.Append("HangfireAuthToken", validToken, cookieOptions);
                    Console.WriteLine($"[HangfireAuth] Session cookie set/updated for Hangfire dashboard (SameSite={cookieOptions.SameSite}, Secure={cookieOptions.Secure})");
                }
                
                Console.WriteLine($"[HangfireAuth] Token validated successfully. User: {validPrincipal.Identity?.Name}, Roles: {string.Join(", ", validPrincipal.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value))}");
                return true; // Usuario autenticado y es Admin
            }
            
            Console.WriteLine("[HangfireAuth] ❌ No se encontró token válido en ningún lugar");
            
            // 7. Si llegamos aquí, el usuario no está autenticado
            Console.WriteLine("[HangfireAuth] User not authenticated");
            return false;
        }

        private bool IsAdmin(ClaimsPrincipal? principal)
        {
            if (principal == null) return false;
            
            return principal.IsInRole("Admin") || 
                   principal.IsInRole("1") || 
                   principal.HasClaim("Role", "Admin") ||
                   principal.HasClaim("Role", "1") ||
                   principal.HasClaim(ClaimTypes.Role, "Admin") ||
                   principal.HasClaim(ClaimTypes.Role, "1");
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

                // Decodificar el token para ver qué issuer/audience tiene
                var jsonToken = tokenHandler.ReadJwtToken(token);
                var tokenIssuer = jsonToken.Issuer;
                var tokenAudience = jsonToken.Audiences?.FirstOrDefault();
                
                Console.WriteLine($"[HangfireAuth] Token issuer: {tokenIssuer}, audience: {tokenAudience}");
                Console.WriteLine($"[HangfireAuth] Config issuer: {jwtIssuer ?? "null"}, audience: {jwtAudience ?? "null"}");

                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true
                };
                
                // Configurar validación de issuer/audience de forma flexible
                // Si el token tiene "YourIssuer"/"YourAudience" (valores placeholder), no validar issuer/audience
                // Si la config tiene valores reales, validar contra ellos
                // Si el token tiene valores diferentes pero la config no está establecida, aceptar el token
                var isPlaceholderIssuer = tokenIssuer == "YourIssuer" || string.IsNullOrEmpty(tokenIssuer);
                var isPlaceholderAudience = tokenAudience == "YourAudience" || string.IsNullOrEmpty(tokenAudience);
                
                if (!string.IsNullOrEmpty(jwtIssuer) && !isPlaceholderIssuer)
                {
                    validationParameters.ValidateIssuer = true;
                    validationParameters.ValidIssuer = jwtIssuer;
                    Console.WriteLine($"[HangfireAuth] Validating issuer against: {jwtIssuer}");
                }
                else
                {
                    validationParameters.ValidateIssuer = false;
                    Console.WriteLine($"[HangfireAuth] Skipping issuer validation (token: {tokenIssuer}, config: {jwtIssuer ?? "null"})");
                }
                
                if (!string.IsNullOrEmpty(jwtAudience) && !isPlaceholderAudience)
                {
                    validationParameters.ValidateAudience = true;
                    validationParameters.ValidAudience = jwtAudience;
                    Console.WriteLine($"[HangfireAuth] Validating audience against: {jwtAudience}");
                }
                else
                {
                    validationParameters.ValidateAudience = false;
                    Console.WriteLine($"[HangfireAuth] Skipping audience validation (token: {tokenAudience}, config: {jwtAudience ?? "null"})");
                }

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
