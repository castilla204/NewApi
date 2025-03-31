using System.Security.Claims;

namespace newApi.Services
{
    public interface IAuthorizationServices
    {
        bool IsAdmin(ClaimsPrincipal user);
        bool CanAccessSearch(ClaimsPrincipal user, int searchId);
        Task<bool> CanAccessSearchAsync(ClaimsPrincipal user, int searchId);
    }
}