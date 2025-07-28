using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.ScrapperGateway.DataLayer.Models.DTOs;

using static UserController;

namespace newApi.Services
{
    public interface IUserService
    {
        Task<IEnumerable<object>> GetAllUsers();
        Task<bool> BlockUser(int userId);
        Task<bool> DeleteUser(int userId);
        Task<bool> SendVerification(int userId, string phoneNumber);
        Task<(bool success, string token, User user)> VerifyCode(int userId, string phoneNumber, string code);
        Task<(bool success, string token, User user)> GoogleAuth(GoogleAuthDto request);
        Task<(bool success, string token, User user, ExpertProfile expertProfile)> BecomeExpert(int userId, BecomeExpertRequestDto request);
        Task<ExpertProfileDto> GetExpertProfile(int userId);
        Task<User> GetUserAsync(int userId);
    }
}