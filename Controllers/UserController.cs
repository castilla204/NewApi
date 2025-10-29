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
        private readonly IAuthorizationServices _authService;
        private readonly ILoggingService _loggingService;

    public UserController(
        UserService userService,
        ILogger<UserController> logger,
        IAuthorizationServices authService,
        ILoggingService loggingService)
    {
        _userService = userService;
        _logger = logger;
        _authService = authService;
        _loggingService = loggingService;
    }

    [Authorize]
    [HttpGet("all")]
    public async Task<IActionResult> GetAllUsers()
    {
        try
        {
            // 🔐 SEGURIDAD: Verificar rol usando AuthorizationService
            if (!_authService.IsAdmin(User))
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




    // ✅ REMOVED: GetUserBalance endpoint eliminated - balance system removed

    [Authorize]
    [HttpPut("{userId}/block")]
    public async Task<IActionResult> BlockUser(int userId)
    {
        try
        {
            // 🔐 SEGURIDAD: Verificar rol usando AuthorizationService
            if (!_authService.IsAdmin(User))
            {
                return Unauthorized(new { message = "Admin access required" });
            }

            var success = await _userService.BlockUser(userId);
            if (!success)
            {
                // 🚨 LOG CRÍTICO: Fallo al bloquear usuario
                var adminUserId = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int adminId) ? adminId : (int?)null;
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Failed to block user",
                    details: $"Admin {adminUserId} failed to block user {userId} - user may not exist or already blocked",
                    userId: adminUserId,
                    source: "UserController.BlockUser",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "BlockUser",
                        TargetUserId = userId,
                        AdminUserId = adminUserId,
                        Success = false
                    }
                );
                return BadRequest(new { message = "Cannot block this user" });
            }

            // 🚨 LOG CRÍTICO: Usuario bloqueado exitosamente
            var adminIdSuccess = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int adminIdSuccessValue) ? adminIdSuccessValue : (int?)null;
            await _loggingService.LogCriticalAsync(
                message: "CRITICAL: User blocked successfully",
                details: $"Admin {adminIdSuccess} successfully blocked user {userId}",
                userId: adminIdSuccess,
                source: "UserController.BlockUser",
                relatedEntityType: "User",
                relatedEntityId: userId,
                additionalData: new { 
                    Action = "BlockUser",
                    TargetUserId = userId,
                    AdminUserId = adminIdSuccess,
                    Success = true
                }
            );

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error blocking/unblocking user");
            
            // 🚨 LOG CRÍTICO: Error en bloqueo de usuario
            var adminUserId = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int adminId) ? adminId : (int?)null;
            await _loggingService.LogCriticalAsync(
                message: "CRITICAL: Exception during user block operation",
                details: $"Admin {adminUserId} encountered exception while blocking user {userId}: {ex.Message}",
                userId: adminUserId,
                source: "UserController.BlockUser",
                relatedEntityType: "User",
                relatedEntityId: userId,
                additionalData: new { 
                    Action = "BlockUser",
                    TargetUserId = userId,
                    AdminUserId = adminUserId,
                    Exception = ex.Message,
                    StackTrace = ex.StackTrace
                }
            );
            
            return StatusCode(500, new { message = "Failed to update user status" });
        }
    }

    [Authorize]
    [HttpDelete("{userId}")]
    public async Task<IActionResult> DeleteUser(int userId)
    {
        try
        {
            // 🔐 SEGURIDAD: Verificar rol usando AuthorizationService
            if (!_authService.IsAdmin(User))
            {
                return Unauthorized(new { message = "Admin access required" });
            }

            var success = await _userService.DeleteUser(userId);
            if (!success)
            {
                // 🚨 LOG CRÍTICO: Fallo al eliminar usuario
                var adminUserId = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int adminId) ? adminId : (int?)null;
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Failed to delete user",
                    details: $"Admin {adminUserId} failed to delete user {userId} - user may not exist or have active dependencies",
                    userId: adminUserId,
                    source: "UserController.DeleteUser",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "DeleteUser",
                        TargetUserId = userId,
                        AdminUserId = adminUserId,
                        Success = false
                    }
                );
                return BadRequest(new { message = "Cannot delete this user" });
            }

            // 🚨 LOG CRÍTICO: Usuario eliminado exitosamente
            var adminIdSuccess = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int adminIdSuccessValue) ? adminIdSuccessValue : (int?)null;
            await _loggingService.LogCriticalAsync(
                message: "CRITICAL: User deleted successfully",
                details: $"Admin {adminIdSuccess} successfully deleted user {userId}",
                userId: adminIdSuccess,
                source: "UserController.DeleteUser",
                relatedEntityType: "User",
                relatedEntityId: userId,
                additionalData: new { 
                    Action = "DeleteUser",
                    TargetUserId = userId,
                    AdminUserId = adminIdSuccess,
                    Success = true
                }
            );

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting user");
            
            // 🚨 LOG CRÍTICO: Error en eliminación de usuario
            var adminUserId = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int adminId) ? adminId : (int?)null;
            await _loggingService.LogCriticalAsync(
                message: "CRITICAL: Exception during user deletion",
                details: $"Admin {adminUserId} encountered exception while deleting user {userId}: {ex.Message}",
                userId: adminUserId,
                source: "UserController.DeleteUser",
                relatedEntityType: "User",
                relatedEntityId: userId,
                additionalData: new { 
                    Action = "DeleteUser",
                    TargetUserId = userId,
                    AdminUserId = adminUserId,
                    Exception = ex.Message,
                    StackTrace = ex.StackTrace
                }
            );
            
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
                // 🚨 LOG CRÍTICO: Fallo al convertirse en experto
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Failed to become expert",
                    details: $"User {userId} failed to become expert - validation or processing error",
                    userId: userId,
                    source: "UserController.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "BecomeExpert",
                        UserId = userId,
                        Success = false,
                        RequestData = new {
                            Description = request.Description,
                            ProfilePictureSize = request.ProfilePicture?.Length ?? 0
                        }
                    }
                );
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

            // ✅ LOG INFORMATIVO: Usuario se convirtió en experto exitosamente
            await _loggingService.LogInfoAsync(
                message: "User became expert successfully",
                details: $"User {userId} successfully became expert with profile {expertProfile.Id}",
                userId: userId,
                source: "UserController.BecomeExpert",
                relatedEntityType: "User",
                relatedEntityId: userId,
                additionalData: new { 
                    Action = "BecomeExpert",
                    UserId = userId,
                    ExpertProfileId = expertProfile.Id,
                    Success = true,
                    StripeAccountId = expertProfile.StripeAccountId,
                    StripeStatus = expertProfile.StripeStatus
                }
            );

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error becoming expert");
            
            // 🚨 LOG CRÍTICO: Error al convertirse en experto
            var userIdForLog = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int userIdValue) ? userIdValue : (int?)null;
            await _loggingService.LogCriticalAsync(
                message: "CRITICAL: Exception during become expert",
                details: $"User {userIdForLog} encountered exception while becoming expert: {ex.Message}",
                userId: userIdForLog,
                source: "UserController.BecomeExpert",
                relatedEntityType: "User",
                relatedEntityId: userIdForLog,
                additionalData: new { 
                    Action = "BecomeExpert",
                    UserId = userIdForLog,
                    Exception = ex.Message,
                    StackTrace = ex.StackTrace
                }
            );
            
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

    [Authorize(Roles = "Expert")]
    [HttpPost("toggle-vacation-mode")]
    public async Task<IActionResult> ToggleVacationMode()
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized(new { message = "Invalid user identification" });
            }

            var (success, isOnVacation) = await _userService.ToggleVacationMode(userId);
            if (!success)
            {
                return BadRequest(new { message = "Failed to toggle vacation mode" });
            }

            var message = isOnVacation ? "Modo vacaciones activado" : "Modo vacaciones desactivado";
            return Ok(new { 
                message = message,
                isOnVacation = isOnVacation 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling vacation mode");
            return StatusCode(500, new { message = "Failed to toggle vacation mode" });
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