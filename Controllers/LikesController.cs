using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using newApi.Services;

namespace newApi.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class LikesController : ControllerBase
{
    private readonly ILikeService _likeService;
    public LikesController(ILikeService likeService)
    {
        _likeService = likeService;
    }

    [HttpPost("{adId}")]
    public async Task<IActionResult> ToggleLike([FromRoute] string adId)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
        {
            return Unauthorized(new { message = "Invalid user identification" });
        }

        var liked = await _likeService.ToggleLikeAsync(userId, adId);
        return Ok(new { liked });
    }

    [HttpGet("check/{adId}")]
    public async Task<IActionResult> CheckLike([FromRoute] string adId)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
        {
            return Unauthorized(new { message = "Invalid user identification" });
        }

        var exists = await _likeService.CheckLikeAsync(userId, adId);
        return Ok(new { liked = exists });
    }

    [HttpGet("user")]
    public async Task<IActionResult> GetUserLikes()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
        {
            return Unauthorized(new { message = "Invalid user identification" });
        }

        var likedAds = await _likeService.GetUserLikesAsync(userId);
        return Ok(likedAds);
    }
}
