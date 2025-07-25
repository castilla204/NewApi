using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using newApi.Services;
using newApi.ScrapperGateway.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.DTOs;

[Route("api/[controller]")]
[ApiController]
public class UserController : ControllerBase
{
    private readonly UserService _userService;
    private readonly ILogger<UserController> _logger;

    public UserController(
        UserService userService,
        ILogger<UserController> logger)
    {
        _userService = userService;
        _logger = logger;
    }

    [Authorize]
    [HttpGet("all")]
    public async Task<IActionResult> GetAllUsers()
    {
        try
        {
            var adminEmail = User.FindFirst(ClaimTypes.Email)?.Value;
            if (adminEmail != "dcastillaa@gmail.com")
            {
                return Unauthorized(new { message = "Admin access required" });
            }

            var users = await _userService.GetAllUsers();
            return Ok(users);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving users");
            return StatusCode(500, new { message = "Failed to retrieve users" });
        }
    }

    [Authorize]
    [HttpPut("{userId}/block")]
    public async Task<IActionResult> BlockUser(int userId)
    {
        try
        {
            var adminEmail = User.FindFirst(ClaimTypes.Email)?.Value;
            if (adminEmail != "dcastillaa@gmail.com")
            {
                return Unauthorized(new { message = "Admin access required" });
            }

            var success = await _userService.BlockUser(userId);
            if (!success)
            {
                return BadRequest(new { message = "Cannot block this user" });
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error blocking/unblocking user");
            return StatusCode(500, new { message = "Failed to update user status" });
        }
    }

    [Authorize]
    [HttpDelete("{userId}")]
    public async Task<IActionResult> DeleteUser(int userId)
    {
        try
        {
            var adminEmail = User.FindFirst(ClaimTypes.Email)?.Value;
            if (adminEmail != "dcastillaa@gmail.com")
            {
                return Unauthorized(new { message = "Admin access required" });
            }

            var success = await _userService.DeleteUser(userId);
            if (!success)
            {
                return BadRequest(new { message = "Cannot delete this user" });
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting user");
            return StatusCode(500, new { message = "Failed to delete user" });
        }
    }

    [Authorize]
    [HttpPost("send-verification")]
    public async Task<IActionResult> SendVerification([FromBody] SendVerificationRequest request)
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized(new { message = "Invalid user identification" });
            }

            var success = await _userService.SendVerification(userId, request.PhoneNumber);
            if (!success)
            {
                return BadRequest(new { message = "Failed to send verification code" });
            }

            return Ok(new { message = "Verification code sent" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending verification code");
            return StatusCode(500, new { message = "Failed to send verification code" });
        }
    }

    [Authorize]
    [HttpPost("verify-code")]
    public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeRequest request)
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized(new { message = "Invalid user identification" });
            }

            var (success, token, user) = await _userService.VerifyCode(userId, request.PhoneNumber, request.Code);
            if (!success)
            {
                return BadRequest(new { message = "Invalid verification code" });
            }

            return Ok(new
            {
                message = "Phone number verified successfully",
                token = token,
                user = new
                {
                    user.Id,
                    user.Name,
                    user.Email,
                    user.PhoneVerified
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying code");
            return StatusCode(500, new { message = "Failed to verify code" });
        }
    }

    [HttpPost("google-auth")]
    public async Task<IActionResult> GoogleAuth([FromBody] GoogleAuthDto request)
    {
        try
        {
            var (success, token, user) = await _userService.GoogleAuth(request);
            if (!success)
            {
                return BadRequest(new { message = "Authentication failed" });
            }

            return Ok(new
            {
                token,
                user = new
                {
                    user.Id,
                    user.Name,
                    user.Email,
                    user.PhoneVerified,
                    Role = user.Role.ToString()
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Google authentication");
            return StatusCode(500, new { message = "An error occurred during authentication" });
        }
    }

    [Authorize]
    [HttpPost("become-expert")]
    public async Task<IActionResult> BecomeExpert([FromForm] BecomeExpertRequestDto request)
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized(new { message = "Invalid user identification" });
            }

            var (success, token, user, expertProfile) = await _userService.BecomeExpert(userId, request);
            if (!success)
            {
                return BadRequest(new { message = "Failed to become expert" });
            }

            return Ok(new
            {
                message = "Successfully became an expert",
                token = token,
                user = new
                {
                    user.Id,
                    user.Name,
                    user.Email,
                    user.PhoneVerified,
                    Role = user.Role.ToString(),
                    ExpertProfile = new
                    {
                        expertProfile.Id,
                        expertProfile.ProfilePictureUrl,
                        expertProfile.Description,
                        expertProfile.StripeAccountId,
                        expertProfile.CreatedAt,
                        expertProfile.Latitude,
                        expertProfile.Longitude
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error becoming expert");
            return StatusCode(500, new { message = "Failed to become expert" });
        }
    }

    [Authorize]
    [HttpGet("expert-profile")]
    public async Task<IActionResult> GetExpertProfile()
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized(new { message = "Invalid user identification" });
            }

            var expertProfile = await _userService.GetExpertProfile(userId);
            if (expertProfile == null)
            {
                return NotFound(new { message = "Expert profile not found" });
            }

            return Ok(expertProfile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving expert profile");
            return StatusCode(500, new { message = "Failed to retrieve expert profile" });
        }
    }

    public class GoogleAuthDto
    {
        public string AccessToken { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string GoogleId { get; set; } = string.Empty;
    }

    public class SendVerificationRequest
    {
        public string PhoneNumber { get; set; }
    }

    public class VerifyCodeRequest
    {
        public string PhoneNumber { get; set; }
        public string Code { get; set; }
    }
}