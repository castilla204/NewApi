using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Google.Apis.Auth;
using Google.Cloud.Storage.V1;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Twilio.Rest.Verify.V2.Service;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using newApi.ScrapperGateway.DataLayer.Models.DTOs.newApi.ScrapperGateway.DataLayer.Models.DTOs;
using static UserController;
using Twilio;

namespace newApi.Services
{
    public class UserService : IUserService
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<UserService> _logger;
        private readonly StorageClient _storageClient;
        private readonly string _twilioVerificationServiceSid;
        private readonly string _twilioauthToken;

        public UserService(
            AppDbContext context,
            IConfiguration configuration,
            ILogger<UserService> logger,
            StorageClient storageClient)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
            _storageClient = storageClient;
            _twilioVerificationServiceSid = configuration["Twilio:VerificationServiceSid"];
            _twilioauthToken = configuration["Twilio:AuthToken"];
        }

        public async Task<User> GetUserAsync(int userId)
        {
            return await _context.Users.FindAsync(userId);
        }

        public async Task<IEnumerable<object>> GetAllUsers()
        {
            return await _context.Users
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
                    Role = u.Role.ToString()
                })
                .ToListAsync();
        }

        public async Task<bool> BlockUser(int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null || user.Email == "dcastillaa@gmail.com")
                return false;

            user.IsBlocked = !user.IsBlocked;
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DeleteUser(int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null || user.Email == "dcastillaa@gmail.com")
                return false;

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> SendVerification(int userId, string phoneNumber)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null || user.PhoneVerified)
                return false;
            TwilioClient.Init(_configuration["Twilio:AccountSid"], _twilioauthToken);


            await VerificationResource.CreateAsync(
                to: phoneNumber,
                channel: "sms",
                pathServiceSid: _twilioVerificationServiceSid
            );

            user.PhoneNumber = phoneNumber;
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<(bool success, string token, User user)> VerifyCode(int userId, string phoneNumber, string code)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null || user.PhoneVerified || user.PhoneNumber != phoneNumber)
                return (false, null, null);

            var verificationCheck = await VerificationCheckResource.CreateAsync(
                to: phoneNumber,
                code: code,
                pathServiceSid: _twilioVerificationServiceSid
            );

            if (verificationCheck.Status != "approved")
                return (false, null, null);

            user.PhoneVerified = true;
            await _context.SaveChangesAsync();

            var token = GenerateJwtToken(user);
            return (true, token, user);
        }

        public async Task<(bool success, string token, User user)> GoogleAuth(GoogleAuthDto request)
        {
            var clientIds = _configuration.GetSection("Google:ClientIds").Get<string[]>();
            var settings = new GoogleJsonWebSignature.ValidationSettings { Audience = clientIds };
            var payload = await GoogleJsonWebSignature.ValidateAsync(request.AccessToken, settings);

            var user = await _context.Users.FirstOrDefaultAsync(u => u.GoogleId == payload.Subject);

            if (user == null)
            {
                user = new User
                {
                    Name = payload.Name?.Trim(),
                    Email = payload.Email?.Trim(),
                    GoogleId = payload.Subject,
                    CreatedAt = DateTime.UtcNow,
                    Role = UserRole.Client
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
            return (true, token, user);
        }

        public async Task<(bool success, string token, User user, ExpertProfile expertProfile)> BecomeExpert(
            int userId,
            BecomeExpertRequestDto request)
        {
            var user = await _context.Users
                .Include(u => u.ExpertProfile)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null || user.Role == UserRole.Expert || user.ExpertProfile != null)
                return (false, null, null, null);

            var bucketName = _configuration["GoogleCloud:BucketName"];
            var extension = Path.GetExtension(request.ProfilePicture.FileName).ToLowerInvariant();
            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            var objectName = $"experts/{uniqueFileName}";

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

            var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
            
            // Update user role to Expert
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
            return (true, token, user, expertProfile);
        }

        public async Task<ExpertProfileDto> GetExpertProfile(int userId)
        {
            var expertProfile = await _context.ExpertProfiles
                .Include(ep => ep.User)
                .FirstOrDefaultAsync(ep => ep.UserId == userId);

            if (expertProfile == null)
                return null;

            return new ExpertProfileDto
            {
                Id = expertProfile.Id,
                ProfilePictureUrl = expertProfile.ProfilePictureUrl,
                StripeAccountId = expertProfile.StripeAccountId,
                Description = expertProfile.Description,
                CreatedAt = expertProfile.CreatedAt,
                User = new UserDto
                {
                    Name = expertProfile.User.Name,
                    Email = expertProfile.User.Email
                }
            };
        }

        private string GenerateJwtToken(User user)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.Name),
                new Claim(ClaimTypes.Role, user.Role.ToString())
            };

            var key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]));

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
}