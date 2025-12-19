using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.ScrapperGateway.DataLayer.Models.DTOs;

using static UserController;

namespace newApi.Services
{
    public interface IUserService
    {
        Task<(IEnumerable<object> users, int totalCount)> GetAllUsers(int page, int pageSize);
        Task<bool> BlockUser(int userId);
        Task<bool> DeleteUser(int userId);
        // ✅ COMENTADO: Verificación de teléfono ya no es necesaria - métodos stub devuelven false/null
        Task<bool> SendVerification(int userId, string phoneNumber);
        Task<(bool success, string token, User user)> VerifyCode(int userId, string phoneNumber, string code);
        Task<(bool success, string token, User user)> GoogleAuth(GoogleAuthDto request);
        Task<(bool success, string token, User user, ExpertProfile expertProfile)> BecomeExpert(int userId, BecomeExpertRequestDto request);
        Task<ExpertProfileDto> GetExpertProfile(int userId);
        Task<(bool Success, ExpertProfileDto UpdatedProfile)> UpdateExpertProfile(int userId, UpdateExpertProfileRequestDto request);
        Task<User> GetUserAsync(int userId);
        // ✅ REMOVED: GetUserBalanceAsync method eliminated - balance system removed
    }
}