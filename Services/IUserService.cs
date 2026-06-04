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
        Task<(bool success, string? token, User? user, string? errorReason)> GoogleAuth(GoogleAuthDto request);
        // 🛡️ Round 28: extendido con errorCode/errorMessage/detectedCountry para que el controller
        // mapee a HTTP status + mensaje específico al cliente (en vez del fósil "Google Cloud Storage").
        Task<(bool success, string? token, User? user, ExpertProfile? expertProfile,
              string? errorCode, string? errorMessage, string? detectedCountry)> BecomeExpert(int userId, BecomeExpertRequestDto request);
        Task<ExpertProfileDto?> GetExpertProfile(int userId);
        Task<(bool Success, ExpertProfileDto? UpdatedProfile,
              string? errorCode, string? errorMessage, string? detectedCountry)> UpdateExpertProfile(int userId, UpdateExpertProfileRequestDto request);
        Task<User> GetUserAsync(int userId);
        // ✅ REMOVED: GetUserBalanceAsync method eliminated - balance system removed
    }
}