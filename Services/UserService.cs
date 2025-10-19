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
using newApi.ScrapperGateway.DataLayer.Models.DTOs;
using Twilio;
using static UserController;
using System.Globalization;

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
            try
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
            }catch(Exception ex)
            {
                throw ex;
            }
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
                // 🔐 SEGURIDAD: Asignar rol de Admin solo si el email es el autorizado
                var emailToCheck = payload.Email?.Trim().ToLowerInvariant();
                var isAdminEmail = emailToCheck == "dcastillaa@gmail.com";
                var userRole = isAdminEmail ? UserRole.Admin : UserRole.Client;
                
                // 🔍 DEBUG: Log para ver qué está pasando
                _logger.LogInformation("Creating new user - Email: '{Email}', IsAdminEmail: {IsAdminEmail}, AssignedRole: {UserRole}", 
                    emailToCheck, isAdminEmail, userRole);

                user = new User
                {
                    Name = payload.Name?.Trim(),
                    Email = payload.Email?.Trim(),
                    GoogleId = payload.Subject,
                    CreatedAt = DateTime.UtcNow,
                    Role = userRole
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
            else
            {
                // 🔍 DEBUG: Log para usuarios existentes
                _logger.LogInformation("Existing user login - Email: '{Email}', CurrentRole: {Role}", 
                    user.Email, user.Role);
            }

            var token = GenerateJwtToken(user);
            return (true, token, user);
        }

        public async Task<(bool success, string token, User user, ExpertProfile expertProfile)> BecomeExpert(
            int userId,
            BecomeExpertRequestDto request)
        {
            _logger.LogInformation("Attempting to make user with ID {UserId} an expert", userId);

            var user = await _context.Users
                .Include(u => u.ExpertProfile)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
            {
                _logger.LogWarning("User with ID {UserId} not found", userId);
                return (false, null, null, null);
            }

            if (user.Role == UserRole.Expert)
            {
                _logger.LogWarning("User with ID {UserId} is already an expert", userId);
                return (false, null, null, null);
            }

            if (user.ExpertProfile != null)
            {
                _logger.LogWarning("User with ID {UserId} already has an expert profile", userId);
                return (false, null, null, null);
            }

            // Validar Latitude y Longitude
            if (string.IsNullOrEmpty(request.Latitude) || string.IsNullOrEmpty(request.Longitude))
            {
                _logger.LogWarning("Latitude or Longitude is empty for user ID {UserId}", userId);
                return (false, null, null, null);
            }

            if (!decimal.TryParse(request.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var latitude))
            {
                _logger.LogWarning("Invalid Latitude format for user ID {UserId}: {Latitude}", userId, request.Latitude);
                return (false, null, null, null);
            }

            if (!decimal.TryParse(request.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var longitude))
            {
                _logger.LogWarning("Invalid Longitude format for user ID {UserId}: {Longitude}", userId, request.Longitude);
                return (false, null, null, null);
            }

            if (latitude < -90m || latitude > 90m)
            {
                _logger.LogWarning("Latitude {Latitude} out of range for user ID {UserId}", latitude, userId);
                return (false, null, null, null);
            }

            if (longitude < -180m || longitude > 180m)
            {
                _logger.LogWarning("Longitude {Longitude} out of range for user ID {UserId}", longitude, userId);
                return (false, null, null, null);
            }

            // Validar tamaño del archivo (5MB límite para imágenes de perfil)
            if (request.ProfilePicture.Length > 5 * 1024 * 1024)
            {
                _logger.LogWarning("Profile picture file size {FileSize} exceeds 5MB limit for user ID {UserId}", request.ProfilePicture.Length, userId);
                return (false, null, null, null);
            }

            // Validar tipo de archivo
            var extension = Path.GetExtension(request.ProfilePicture.FileName).ToLowerInvariant();
            if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(extension))
            {
                _logger.LogWarning("Invalid profile picture file type {Extension} for user ID {UserId}", extension, userId);
                return (false, null, null, null);
            }

            _logger.LogInformation("Processing profile picture upload for user ID {UserId}", userId);
            var bucketName = _configuration["GoogleCloud:BucketName"];
            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            var objectName = $"experts/{uniqueFileName}";

            try
            {
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading profile picture for user ID {UserId}", userId);
                return (false, null, null, null);
            }

            var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";

            _logger.LogInformation("Updating user role and creating expert profile for user ID {UserId}", userId);
            user.Role = UserRole.Expert;

            var expertProfile = new ExpertProfile
            {
                UserId = user.Id,
                ProfilePictureUrl = imageUrl,
                ProfilePictureObjectName = objectName,
                Description = request.Description,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                StripeAccountId = null, // No guardar StripeAccountId, se genera en el onboarding
                CreatedAt = DateTime.UtcNow
            };

            _context.ExpertProfiles.Add(expertProfile);
            await _context.SaveChangesAsync();

            var token = GenerateJwtToken(user);
            _logger.LogInformation("Successfully created expert profile for user ID {UserId}", userId);
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
                },
                Latitude = expertProfile.Latitude,
                Longitude = expertProfile.Longitude,
                StripeStatus = expertProfile.StripeStatus,
                StripeStatusDetails = expertProfile.StripeStatusDetails,
                OnboardingCompleted = expertProfile.OnboardingCompleted,
                IsOnVacation = expertProfile.IsOnVacation
            };
        }


        public async Task<(bool Success, ExpertProfileDto UpdatedProfile)> UpdateExpertProfile(int userId, UpdateExpertProfileRequestDto request)
        {
            try
            {
                _logger.LogInformation("Updating expert profile for user ID {UserId}", userId);

                // Buscar el perfil de experto existente
                var expertProfile = await _context.ExpertProfiles
                    .Include(ep => ep.User)
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    _logger.LogWarning("Expert profile not found for user ID {UserId}", userId);
                    return (false, null);
                }

                // Validar coordenadas
                if (!decimal.TryParse(request.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var latitude) ||
                    latitude < -90m || latitude > 90m)
                {
                    _logger.LogWarning("Invalid latitude provided: {Latitude}", request.Latitude);
                    return (false, null);
                }

                if (!decimal.TryParse(request.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var longitude) ||
                    longitude < -180m || longitude > 180m)
                {
                    _logger.LogWarning("Invalid longitude provided: {Longitude}", request.Longitude);
                    return (false, null);
                }

                // Actualizar los campos básicos
                expertProfile.Description = request.Description;
                expertProfile.Latitude = request.Latitude;
                expertProfile.Longitude = request.Longitude;

                // Procesar nueva imagen de perfil si se proporciona
                if (request.ProfilePicture != null)
                {
                    // Validar tamaño del archivo (5MB límite para imágenes de perfil)
                    if (request.ProfilePicture.Length > 5 * 1024 * 1024)
                    {
                        _logger.LogWarning("Profile picture file size {FileSize} exceeds 5MB limit for user ID {UserId}", request.ProfilePicture.Length, userId);
                        return (false, null);
                    }

                    // Validar tipo de archivo
                    var extension = Path.GetExtension(request.ProfilePicture.FileName).ToLowerInvariant();
                    if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(extension))
                    {
                        _logger.LogWarning("Invalid profile picture file type {Extension} for user ID {UserId}", extension, userId);
                        return (false, null);
                    }

                    var bucketName = _configuration["GoogleCloud:BucketName"];
                    var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                    var objectName = $"experts/{uniqueFileName}";

                    try
                    {
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

                        // Eliminar la imagen anterior si existe
                        if (!string.IsNullOrEmpty(expertProfile.ProfilePictureObjectName))
                        {
                            try
                            {
                                await _storageClient.DeleteObjectAsync(bucketName, expertProfile.ProfilePictureObjectName);
                                _logger.LogInformation("Deleted old profile picture: {ObjectName}", expertProfile.ProfilePictureObjectName);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Could not delete old profile picture: {ObjectName}", expertProfile.ProfilePictureObjectName);
                            }
                        }

                        // Actualizar URLs de la nueva imagen
                        var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
                        expertProfile.ProfilePictureUrl = imageUrl;
                        expertProfile.ProfilePictureObjectName = objectName;

                        _logger.LogInformation("Successfully uploaded new profile picture for user ID {UserId}", userId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error uploading new profile picture for user ID {UserId}", userId);
                        return (false, null);
                    }
                }

                await _context.SaveChangesAsync();

                // Devolver el perfil actualizado
                var updatedProfileDto = new ExpertProfileDto
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
                    },
                    Latitude = expertProfile.Latitude,
                    Longitude = expertProfile.Longitude,
                    StripeStatus = expertProfile.StripeStatus,
                    StripeStatusDetails = expertProfile.StripeStatusDetails,
                    OnboardingCompleted = expertProfile.OnboardingCompleted,
                    IsOnVacation = expertProfile.IsOnVacation
                };

                _logger.LogInformation("Successfully updated expert profile for user ID {UserId}", userId);
                return (true, updatedProfileDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating expert profile for user ID {UserId}", userId);
                return (false, null);
            }
        }

        // ✅ REMOVED: GetUserBalanceAsync method eliminated - balance system removed

        public async Task<(bool Success, bool IsOnVacation)> ToggleVacationMode(int userId)
        {
            try
            {
                _logger.LogInformation("Toggling vacation mode for user ID {UserId}", userId);

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    _logger.LogError("Expert profile not found for user ID {UserId}", userId);
                    return (false, false);
                }

                // Cambiar el estado de vacaciones
                expertProfile.IsOnVacation = !expertProfile.IsOnVacation;
                await _context.SaveChangesAsync();

                _logger.LogInformation("Successfully toggled vacation mode for user ID {UserId}. New status: {IsOnVacation}", 
                    userId, expertProfile.IsOnVacation);

                return (true, expertProfile.IsOnVacation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling vacation mode for user ID {UserId}", userId);
                return (false, false);
            }
        }



        private string GenerateJwtToken(User user)
        {
            // Convertir el valor numérico del enum al nombre del enum
            var roleName = user.Role switch
            {
                UserRole.Client => "Client",
                UserRole.Expert => "Expert", 
                UserRole.Admin => "Admin",
                _ => "Client"
            };

            // 🔍 DEBUG: Log para ver qué rol se está generando
            _logger.LogInformation("Generating JWT for user {UserId} ({Email}) with Role: {UserRole} -> {RoleName}", 
                user.Id, user.Email, user.Role, roleName);

            var claims = new[]
            {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Role, roleName) // Ahora siempre devuelve el nombre del enum
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
