using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public class AuthorizationServices : IAuthorizationServices
    {
        private readonly AppDbContext _context;
        public AuthorizationServices(AppDbContext context)
        {
            _context = context;
        }

        public bool IsAdmin(ClaimsPrincipal user)
        {
            // 🔐 SEGURIDAD: Solo verificar por rol en JWT firmado (más seguro)
            var roleClaim = user.FindFirst(ClaimTypes.Role)?.Value;
            var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var emailClaim = user.FindFirst(ClaimTypes.Email)?.Value;
            
            // 🔍 DEBUG: Log para ver qué claims está recibiendo
            // Verificar por rol (método principal - JWT firmado es confiable)
            var isAdmin = roleClaim == "Admin" || roleClaim == "2";
            
            // 🔐 SEGURIDAD ADICIONAL: Solo para tokens antiguos con valor numérico
            if (isAdmin && roleClaim == "2" && !string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out int userId))
            {
                // Verificar que el usuario realmente tenga rol Admin en BD
                var userFromDb = _context.Users.Find(userId);
                if ((int)(userFromDb?.Role ?? 0) != 2)
                {
                    isAdmin = false;
                }
            }
            return isAdmin;
        }

        public bool CanAccessSearch(ClaimsPrincipal user, int searchId)
        {
            if (IsAdmin(user)) return true;

            var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return false;
            }

            return _context.Searches.Any(s => s.Id == searchId && s.UserId == userId);
        }

        public async Task<bool> CanAccessSearchAsync(ClaimsPrincipal user, int searchId)
        {
            if (IsAdmin(user)) return true;

            var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return false;
            }

            return await _context.Searches.AnyAsync(s => s.Id == searchId && s.UserId == userId);
        }
    }
}