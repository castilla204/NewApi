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
    [HttpGet("user-balance")]
    public async Task<IActionResult> GetUserBalance()
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return BadRequest(new { message = "Invalid or missing user ID." });
            }

            var balance = await _userService.GetUserBalanceAsync(userId);
            if (balance == null)
            {
                return NotFound(new { message = "User or balance not found." });
            }

            return Ok(new { balance });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving user balance for user ID: {UserId}");
            return StatusCode(500, new { message = "Failed to retrieve user balance." });
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
    [RequestSizeLimit(10 * 1024 * 1024)] // 10MB limit
    public async Task<IActionResult> BecomeExpert([FromForm] BecomeExpertRequestDto request)
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized(new { message = "Invalid user identification" });
            }

            // Validaciones previas
            if (request.ProfilePicture == null)
            {
                return BadRequest(new { message = "Profile picture is required" });
            }

            if (request.ProfilePicture.Length > 5 * 1024 * 1024)
            {
                return BadRequest(new { message = "Profile picture must be smaller than 5MB" });
            }

            var extension = Path.GetExtension(request.ProfilePicture.FileName).ToLowerInvariant();
            if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(extension))
            {
                return BadRequest(new { message = "Profile picture must be a JPG or PNG image" });
            }

            var (success, token, user, expertProfile) = await _userService.BecomeExpert(userId, request);
            if (!success)
            {
                return BadRequest(new { message = "Failed to become expert" });
            }

            var response = new BecomeExpertResponseDto
            {
                Message = "Successfully became an expert",
                Token = token,
                User = new UserInfoDto
                {
                    Id = user.Id,
                    Name = user.Name,
                    Email = user.Email,
                    PhoneVerified = user.PhoneVerified,
                    Role = user.Role.ToString(),
                    ExpertProfile = new ExpertProfileInfoDto
                    {
                        Id = expertProfile.Id,
                        ProfilePictureUrl = expertProfile.ProfilePictureUrl,
                        Description = expertProfile.Description,
                        StripeAccountId = expertProfile.StripeAccountId,
                        CreatedAt = expertProfile.CreatedAt,
                        Latitude = expertProfile.Latitude,
                        Longitude = expertProfile.Longitude,
                        StripeStatus = expertProfile.StripeStatus,
                        StripeStatusDetails = expertProfile.StripeStatusDetails,
                        OnboardingCompleted = expertProfile.OnboardingCompleted
                    }
                }
            };

            return Ok(response);
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

    [Authorize]
    [HttpPut("expert-profile")]
    public async Task<IActionResult> UpdateExpertProfile([FromForm] UpdateExpertProfileRequestDto request)
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized(new { message = "Invalid user identification" });
            }

            // Validaciones básicas
            if (string.IsNullOrWhiteSpace(request.Description))
            {
                return BadRequest(new { message = "La descripción es requerida" });
            }

            if (string.IsNullOrWhiteSpace(request.Latitude) || string.IsNullOrWhiteSpace(request.Longitude))
            {
                return BadRequest(new { message = "Latitud y Longitud son requeridas" });
            }

            _logger.LogInformation("Received request to update expert profile for user {UserId} with data: {RequestData}",
                userId,
                new
                {
                    request.Description,
                    request.Latitude,
                    request.Longitude,
                    HasProfilePicture = request.ProfilePicture != null
                });

            var (success, updatedProfile) = await _userService.UpdateExpertProfile(userId, request);
            if (!success)
            {
                return BadRequest(new { message = "Failed to update expert profile. Please check your data and try again." });
            }

            var response = new UpdateExpertProfileResponseDto
            {
                Message = "Expert profile updated successfully",
                ExpertProfile = updatedProfile
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating expert profile");
            return StatusCode(500, new { message = "Failed to update expert profile", detail = ex.Message });
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