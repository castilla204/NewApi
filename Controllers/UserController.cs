using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using DataLayer.Models.PostGresModels;
using DataLayer.Models;
using Microsoft.EntityFrameworkCore;
using Google.Apis.Auth;
using Twilio;
using Twilio.Rest.Verify.V2.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

[Route("api/[controller]")]
[ApiController]
public class UserController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UserController> _logger;
    private readonly string _twilioAccountSid;
    private readonly string _twilioAuthToken;
    private readonly string _twilioVerificationServiceSid;

    public UserController(
        IConfiguration configuration,
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<UserController> logger)
    {
        _configuration = configuration;
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _twilioAccountSid = configuration["Twilio:AccountSid"];
        _twilioAuthToken = configuration["Twilio:AuthToken"];
        _twilioVerificationServiceSid = configuration["Twilio:VerificationServiceSid"];
        TwilioClient.Init(_twilioAccountSid, _twilioAuthToken);
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

            var users = await _context.Users
                .Select(u => new
                {
                    u.Id,
                    u.Name,
                    u.Email,
                    u.PhoneNumber,
                    u.PhoneVerified,
                    u.IsBlocked,
                    u.CreatedAt,
                    SearchCount = u.Searches.Count(s => s.IsActive),
                    SubscriptionPlan = u.SubscriptionPlan.Name
                })
                .ToListAsync();

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

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            if (user.Email == "dcastillaa@gmail.com")
            {
                return BadRequest(new { message = "Cannot block admin user" });
            }

            user.IsBlocked = !user.IsBlocked;
            await _context.SaveChangesAsync();

            return Ok(new { isBlocked = user.IsBlocked });
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

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            if (user.Email == "dcastillaa@gmail.com")
            {
                return BadRequest(new { message = "Cannot delete admin user" });
            }

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

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

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            // Verificar si el usuario ya tiene el teléfono verificado
            if (user.PhoneVerified)
            {
                return StatusCode(403, new { message = "Phone is already verified" });
            }

            // Verificar formato del número de teléfono
            if (!request.PhoneNumber.StartsWith("+") || request.PhoneNumber.Length < 8)
            {
                return BadRequest(new { message = "Invalid phone number format. Must start with + and country code" });
            }

            var verification = await VerificationResource.CreateAsync(
                to: request.PhoneNumber,
                channel: "sms",
                pathServiceSid: _twilioVerificationServiceSid
            );

            user.PhoneNumber = request.PhoneNumber;
            await _context.SaveChangesAsync();

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
        // Verificar que el usuario está autenticado
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized(new { message = "Invalid user identification" });
            }

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            // Verificar si el usuario ya tiene el teléfono verificado
            if (user.PhoneVerified)
            {
                return StatusCode(403, new { message = "Phone is already verified" });
            }

            // Verificar que el código tenga el formato correcto
            if (request.Code.Length != 6 || !request.Code.All(char.IsDigit))
            {
                return BadRequest(new { message = "Invalid verification code format" });
            }

            // Verificar que el número de teléfono coincide con el enviado previamente
            if (user.PhoneNumber != request.PhoneNumber)
            {
                return BadRequest(new { message = "Phone number does not match" });
            }

            var verificationCheck = await VerificationCheckResource.CreateAsync(
                to: request.PhoneNumber,
                code: request.Code,
                pathServiceSid: _twilioVerificationServiceSid
            );

            // Verificar el estado de la verificación
            if (verificationCheck.Status == "approved")
            {
                user.PhoneVerified = true;
                await _context.SaveChangesAsync();

                // Generar un nuevo token que incluya el estado de verificación
                var token = GenerateJwtToken(user);
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

            return BadRequest(new { message = "Invalid verification code" });
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
            _logger.LogInformation("Starting Google authentication for email: {Email}", request.Email);

            GoogleJsonWebSignature.Payload payload;
            var clientIds = _configuration.GetSection("Google:ClientIds").Get<string[]>() ??
                throw new InvalidOperationException("Google Client IDs not found in configuration");

            try
            {
                var settings = new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = clientIds
                };

                payload = await GoogleJsonWebSignature.ValidateAsync(request.AccessToken, settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating Google token");
                return BadRequest(new { message = "Invalid Google token" });
            }

            if (payload == null)
            {
                _logger.LogWarning("Failed to verify Google token");
                return BadRequest(new { message = "Invalid Google token" });
            }

            // Find or create user
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.GoogleId == payload.Subject);

            if (user == null)
            {
                _logger.LogInformation("Creating new user for Google ID: {GoogleId}", payload.Subject);

                user = new User
                {
                    Name = payload.Name?.Trim(),
                    Email = payload.Email?.Trim(),
                    GoogleId = payload.Subject,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Users.Add(user);
                await _context.SaveChangesAsync();
            }

            // Generate JWT token
            var token = GenerateJwtToken(user);

            _logger.LogInformation("Authentication successful for user ID: {UserId}", user.Id);

            return Ok(new
            {
                token,
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
            _logger.LogError(ex, "Error during Google authentication");
            return StatusCode(500, new { message = "An error occurred during authentication" });
        }
    }

    private string GenerateJwtToken(User user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Name)
        };

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_configuration["Jwt:Key"] ??
            throw new InvalidOperationException("JWT Key not found in configuration")));

        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.Now.AddDays(1),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
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