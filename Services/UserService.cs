using Microsoft.EntityFrameworkCore;
using newApi.ScrapperGateway.DataLayer.Models;
using newApi.ScrapperGateway.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public class UserService : IUserService
    {
        private readonly AppDbContext _context;

        public UserService(AppDbContext context)
        {
            _context = context;
        }

        public async Task<User> GetUserAsync(int userId)
        {
            return await _context.Users.FindAsync(userId);
        }
    }
}