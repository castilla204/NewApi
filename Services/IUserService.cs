using DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public interface IUserService
    {
        Task<User> GetUserAsync(int userId);
    }
}