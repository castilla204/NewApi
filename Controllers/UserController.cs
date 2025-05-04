using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Google.Apis.Auth;
using Twilio;
using Twilio.Rest.Verify.V2.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models.PostGresModels;
using Microsoft.AspNetCore.Mvc;
using System.IO;


using Microsoft.AspNetCore.Http;
using Google.Cloud.Storage.V1;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using newApi.ScrapperGateway.DataLayer.Models.DTOs.newApi.ScrapperGateway.DataLayer.Models.DTOs;
using Google.Apis.Auth.OAuth2;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

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
    private readonly StorageClient _storageClient;

    public UserController(
        IConfiguration configuration,
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<UserController> logger,
           StorageClient storageClient)
    {
        _configuration = configuration;
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _storageClient = storageClient;
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
                    SubscriptionPlan = u.SubscriptionPlan.Name,
                    Role = u.Role.ToString() // Añadimos el rol
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
                    CreatedAt = DateTime.UtcNow,
                    Role = UserRole.Client // Asignamos "Client" por defecto
                };

                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                var userSettings = new UserSetting
                {
                    UserId = user.Id,
                    IsWhatsAppEnabled = true,
                    IsEmailEnabled = true,
                    Theme = "light",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.UserSettings.Add(userSettings);
                await _context.SaveChangesAsync();
            }

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

            var user = await _context.Users
                .Include(u => u.Searches)
                .Include(u => u.SearchHiresAsClient)
                .Include(u => u.ExpertProfile)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            if (user.Role == UserRole.Expert)
            {
                return BadRequest(new { message = "User is already an expert" });
            }

            //if (user.Searches.Any() || user.SearchHiresAsClient.Any())
            //{
            //    return BadRequest(new { message = "Users with active searches or client hires cannot become experts" });
            //}

            if (user.ExpertProfile != null)
            {
                return BadRequest(new { message = "User already has an expert profile" });
            }

            if (request.ProfilePicture == null)
            {
                return BadRequest(new { message = "Profile picture is required" });
            }

            if (string.IsNullOrWhiteSpace(request.Description) || string.IsNullOrWhiteSpace(request.StripeAccountId))
            {
                return BadRequest(new { message = "Description and Stripe account ID are required" });
            }

            // Validar tipo y tamaño de la imagen
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png" };
            var extension = Path.GetExtension(request.ProfilePicture.FileName).ToLowerInvariant();
            if (!allowedExtensions.Contains(extension))
            {
                return BadRequest(new { message = "Only JPG and PNG images are allowed" });
            }

            if (request.ProfilePicture.Length > 5 * 1024 * 1024) // Límite de 5MB
            {
                return BadRequest(new { message = "Image size must be less than 5MB" });
            }

            // Subir la imagen a Google Cloud Storage
            var bucketName = _configuration["GoogleCloud:BucketName"];
            if (string.IsNullOrEmpty(bucketName))
            {
                _logger.LogError("Google Cloud bucket name not found in configuration");
                return StatusCode(500, new { message = "Google Cloud bucket name not configured" });
            }

            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            var objectName = $"experts/{uniqueFileName}";

            // Redimensionar la imagen para optimizar almacenamiento
            using (var inputStream = request.ProfilePicture.OpenReadStream())
            using (var image = Image.Load(inputStream))
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(200, 200),
                    Mode = ResizeMode.Max
                }));

                using (var outputStream = new MemoryStream())
                {
                    image.SaveAsJpeg(outputStream);
                    outputStream.Position = 0;
                    await _storageClient.UploadObjectAsync(
                        bucket: bucketName,
                        objectName: objectName,
                        contentType: "image/jpeg",
                        source: outputStream
                    );
                }
            }

            // Generar la URL pública
            var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";

            user.Role = UserRole.Expert;

            var expertProfile = new ExpertProfile
            {
                UserId = user.Id,
                ProfilePictureUrl = imageUrl,
                ProfilePictureObjectName = objectName,
                Description = request.Description,
                StripeAccountId = request.StripeAccountId,
                CreatedAt = DateTime.UtcNow
            };

            _context.ExpertProfiles.Add(expertProfile);
            await _context.SaveChangesAsync();

            var token = GenerateJwtToken(user);

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
                        expertProfile.CreatedAt
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
    [HttpGet("refresh-expert-profile-picture-url/{expertProfileId}")]
    public async Task<IActionResult> RefreshExpertProfilePictureUrl(int expertProfileId)
    {
        try
        {
            var expertProfile = await _context.ExpertProfiles
                .FirstOrDefaultAsync(ep => ep.Id == expertProfileId);

            if (expertProfile == null)
            {
                return NotFound(new { message = "Expert profile not found" });
            }

            if (string.IsNullOrEmpty(expertProfile.ProfilePictureObjectName))
            {
                return BadRequest(new { message = "No profile picture object name found" });
            }

            // Generar una nueva URL firmada
            var bucketName = _configuration["GoogleCloud:BucketName"];
            if (string.IsNullOrEmpty(bucketName))
            {
                _logger.LogError("Google Cloud bucket name not found in configuration");
                return StatusCode(500, new { message = "Google Cloud bucket name not configured" });
            }

            var credential = GoogleCredential.GetApplicationDefault();
            var urlSigner = UrlSigner.FromCredential(credential);
            var imageUrl = urlSigner.Sign(
                bucketName,
                expertProfile.ProfilePictureObjectName,
                TimeSpan.FromDays(30),
                HttpMethod.Get
            );

            // Actualizar la URL en la base de datos
            expertProfile.ProfilePictureUrl = imageUrl;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Profile picture URL refreshed successfully",
                profilePictureUrl = imageUrl
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing expert profile picture URL");
            return StatusCode(500, new { message = "Failed to refresh profile picture URL" });
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

            var expertProfile = await _context.ExpertProfiles
                .FirstOrDefaultAsync(ep => ep.UserId == userId);

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






    private string GenerateJwtToken(User user)
    {
        var claims = new[]
        {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Email, user.Email),
        new Claim(ClaimTypes.Name, user.Name),
        new Claim(ClaimTypes.Role, user.Role.ToString()) // Incluimos el rol en el token
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