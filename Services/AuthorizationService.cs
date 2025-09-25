using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;

namespace newApi.Services
{
    public class AuthorizationServices : IAuthorizationServices
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AuthorizationServices> _logger;

        public AuthorizationServices(AppDbContext context, ILogger<AuthorizationServices> logger)
        {
            _context = context;
            _logger = logger;
        }

        public bool IsAdmin(ClaimsPrincipal user)
        {
            // 🔐 SEGURIDAD: Verificar rol en lugar de email
            var roleClaim = user.FindFirst(ClaimTypes.Role)?.Value;
            return roleClaim == "Admin";
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