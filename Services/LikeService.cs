using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services;

public class LikeService : ILikeService
{
    private readonly AppDbContext _context;
    public LikeService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<bool> ToggleLikeAsync(int userId, string adId)
    {
        try
        {
            var ad = await _context.Ads.FirstOrDefaultAsync(a => a.Id == adId);
            if (ad == null)
            {
                return false;
            }

            var existingLike = await _context.Likes.FirstOrDefaultAsync(l => l.UserId == userId && l.AdId == adId);

            if (existingLike != null)
            {
                _context.Likes.Remove(existingLike);
                await _context.SaveChangesAsync();
                return false;
            }

            var like = new Like
            {
                UserId = userId,
                AdId = adId,
                CreatedAt = DateTime.UtcNow
            };

            _context.Likes.Add(like);
            await _context.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            return false;
        }
    }

    public async Task<bool> CheckLikeAsync(int userId, string adId)
    {
        try
        {
            return await _context.Likes.AnyAsync(l => l.UserId == userId && l.AdId == adId);
        }
        catch (Exception ex)
        {
            return false;
        }
    }

    public async Task<List<Ad>> GetUserLikesAsync(int userId)
    {
        try
        {
            return await _context.Likes
                .Where(l => l.UserId == userId)
                .Include(l => l.Ad)
                .Select(l => l.Ad)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            return new List<Ad>();
        }
    }
}
