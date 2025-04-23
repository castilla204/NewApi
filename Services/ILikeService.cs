using newApi.ScrapperGateway.DataLayer.Models.PostGresModels;

namespace newApi.Services;

public interface ILikeService
{
    Task<bool> ToggleLikeAsync(int userId, string adId);
    Task<bool> CheckLikeAsync(int userId, string adId);
    Task<List<Ad>> GetUserLikesAsync(int userId);
}
