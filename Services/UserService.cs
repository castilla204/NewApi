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
        private readonly StorageClient _storageClient;
        private readonly ILoggingService _loggingService;
        private readonly ISignedUrlService _signedUrlService;
        private readonly string _twilioVerificationServiceSid;
        private readonly string _twilioauthToken;

        public UserService(
            AppDbContext context,
            IConfiguration configuration,
            StorageClient storageClient,
            ILoggingService loggingService,
            ISignedUrlService signedUrlService)
        {
            _context = context;
            _configuration = configuration;
            _storageClient = storageClient;
            _loggingService = loggingService;
            _signedUrlService = signedUrlService;
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

        // ✅ COMENTADO: Verificación de teléfono ya no es necesaria
        // Método stub para cumplir con la interfaz - ya no hace nada
        public Task<bool> SendVerification(int userId, string phoneNumber)
        {
            // Verificación de teléfono deshabilitada
            return Task.FromResult(false);
        }
        
        /*
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
                
                // ✅ LOG INFORMATIVO: Código de verificación enviado
                await _loggingService.LogInfoAsync(
                    message: "Verification code sent",
                    details: $"Verification code sent via SMS to {phoneNumber} for user {userId}",
                    userId: userId,
                    source: "UserService.SendVerification",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "SendVerificationCode",
                        PhoneNumber = phoneNumber
                    }
                );
                
                return true;
            }catch(Exception ex)
            {
                throw ex;
            }
        }
        */

        // ✅ COMENTADO: Verificación de teléfono ya no es necesaria
        // Método stub para cumplir con la interfaz - ya no hace nada
        public Task<(bool success, string token, User user)> VerifyCode(int userId, string phoneNumber, string code)
        {
            // Verificación de teléfono deshabilitada
            return Task.FromResult<(bool, string?, User?)>((false, null, null));
        }
        
        /*
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
            {
                // ✅ LOG INFORMATIVO: Verificación de teléfono fallida
                await _loggingService.LogInfoAsync(
                    message: "Phone verification failed",
                    details: $"Phone verification failed for user {userId}. Phone: {phoneNumber}, Status: {verificationCheck.Status}",
                    userId: userId,
                    source: "UserService.VerifyCode",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "PhoneVerification",
                        PhoneNumber = phoneNumber,
                        Status = verificationCheck.Status
                    }
                );
                return (false, null, null);
            }

            user.PhoneVerified = true;
            await _context.SaveChangesAsync();
            
            // ✅ LOG INFORMATIVO: Verificación de teléfono exitosa
            await _loggingService.LogInfoAsync(
                message: "Phone verification successful",
                details: $"Phone number verified successfully for user {userId}. Phone: {phoneNumber}",
                userId: userId,
                source: "UserService.VerifyCode",
                relatedEntityType: "User",
                relatedEntityId: userId,
                additionalData: new { 
                    Action = "PhoneVerification",
                    PhoneNumber = phoneNumber,
                    Status = "approved"
                }
            );

            var token = GenerateJwtToken(user);
            return (true, token, user);
        }
        */

        public async Task<(bool success, string token, User user)> GoogleAuth(GoogleAuthDto request)
        {
            var clientIds = _configuration.GetSection("Google:ClientIds").Get<string[]>();
            var settings = new GoogleJsonWebSignature.ValidationSettings { Audience = clientIds };
            var payload = await GoogleJsonWebSignature.ValidateAsync(request.AccessToken, settings);

            // ✅ MEJORA: Buscar primero usuarios activos (sin IgnoreQueryFilters)
            var user = await _context.Users.FirstOrDefaultAsync(u => u.GoogleId == payload.Subject);

            // ✅ MEJORA: Si no se encuentra usuario activo, buscar usuarios eliminados (soft deleted)
            // Esto permite restaurar cuentas eliminadas cuando el usuario se vuelve a registrar
            if (user == null)
            {
                var deletedUser = await _context.Users
                    .IgnoreQueryFilters() // ✅ Ignorar query filter para buscar usuarios eliminados
                    .FirstOrDefaultAsync(u => u.GoogleId == payload.Subject && u.IsDeleted);
                
                if (deletedUser != null)
                {
                    // ✅ RESTAURAR usuario eliminado en lugar de crear uno nuevo
                    var previouslyDeletedAt = deletedUser.DeletedAt; // Guardar antes de limpiar
                    deletedUser.IsDeleted = false;
                    deletedUser.DeletedAt = null;
                    deletedUser.Name = payload.Name?.Trim(); // Actualizar nombre por si cambió
                    deletedUser.Email = payload.Email?.Trim(); // Actualizar email por si cambió
                    
                    await _context.SaveChangesAsync();
                    
                    user = deletedUser;
                    
                    // ✅ LOG INFORMATIVO: Usuario restaurado
                    await _loggingService.LogInfoAsync(
                        message: "User account restored after deletion",
                        details: $"User account was restored after being deleted. Email: {user.Email}, UserId: {user.Id}, Previously deleted at: {previouslyDeletedAt:O}. " +
                                $"Note: User data was anonymized during deletion and cannot be fully restored. User will need to reconfigure settings.",
                        userId: user.Id,
                        source: "UserService.GoogleAuth",
                        relatedEntityType: "User",
                        relatedEntityId: user.Id,
                        additionalData: new { 
                            Action = "UserRestoration",
                            Email = user.Email,
                            Name = user.Name,
                            Role = user.Role.ToString(),
                            GoogleId = user.GoogleId,
                            PreviouslyDeletedAt = previouslyDeletedAt
                        }
                    );
                }
            }

            if (user == null)
            {
                // ✅ Usuario completamente nuevo - crear desde cero
                // 🔐 SEGURIDAD: Asignar rol de Admin solo si el email es el autorizado
                var emailToCheck = payload.Email?.Trim().ToLowerInvariant();
                var isAdminEmail = emailToCheck == "dcastillaa@gmail.com";
                var userRole = isAdminEmail ? UserRole.Admin : UserRole.Client;
                
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

                // ✅ Crear UserSettings solo si no existen (puede que existan si el usuario fue restaurado)
                var existingSettings = await _context.UserSettings.FirstOrDefaultAsync(us => us.UserId == user.Id);
                if (existingSettings == null)
                {
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
                
                // ✅ LOG INFORMATIVO: Usuario creado exitosamente
                await _loggingService.LogInfoAsync(
                    message: "User created successfully",
                    details: $"New user created via Google Auth. Email: {user.Email}, Role: {userRole}, UserId: {user.Id}",
                    userId: user.Id,
                    source: "UserService.GoogleAuth",
                    relatedEntityType: "User",
                    relatedEntityId: user.Id,
                    additionalData: new { 
                        Action = "UserCreation",
                        Email = user.Email,
                        Name = user.Name,
                        Role = userRole.ToString(),
                        GoogleId = user.GoogleId,
                        IsAdminEmail = isAdminEmail
                    }
                );
            }
            else if (!user.IsDeleted)
            {
                // ✅ Usuario existente y activo - login normal
                await _loggingService.LogInfoAsync(
                    message: "User login successful",
                    details: $"User logged in via Google Auth. Email: {user.Email}, Role: {user.Role}, UserId: {user.Id}",
                    userId: user.Id,
                    source: "UserService.GoogleAuth",
                    relatedEntityType: "User",
                    relatedEntityId: user.Id,
                    additionalData: new { 
                        Action = "UserLogin",
                        Email = user.Email,
                        Role = user.Role.ToString(),
                        GoogleId = user.GoogleId
                    }
                );
            }

            // ✅ SEGURIDAD 2025: Generar Access Token + Refresh Token
            var accessToken = GenerateJwtToken(user);
            var refreshToken = await GenerateRefreshTokenAsync(user.Id, "GoogleAuth");
            
            // Devolver ambos tokens separados por pipe (el frontend los separará)
            var combinedToken = $"{accessToken}|{refreshToken}";
            
            return (true, combinedToken, user);
        }

        public async Task<(bool success, string token, User user, ExpertProfile expertProfile)> BecomeExpert(
            int userId,
            BecomeExpertRequestDto request)
        {
            var user = await _context.Users
                .Include(u => u.ExpertProfile)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
            {
                await _loggingService.LogWarningAsync(
                    message: "User not found in BecomeExpert request",
                    details: $"User with ID {userId} not found in database",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                return (false, null, null, null);
            }

            if (user.Role == UserRole.Expert)
            {
                await _loggingService.LogWarningAsync(
                    message: "User already is Expert in BecomeExpert request",
                    details: $"User {userId} ({user.Email}) attempted to become expert but is already an Expert",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                return (false, null, null, null);
            }

            if (user.ExpertProfile != null)
            {
                await _loggingService.LogWarningAsync(
                    message: "User already has ExpertProfile in BecomeExpert request",
                    details: $"User {userId} ({user.Email}) attempted to become expert but already has ExpertProfile with ID {user.ExpertProfile.Id}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                return (false, null, null, null);
            }

            // 🚨 VALIDACIÓN CRÍTICA: Verificar que todas las contrataciones como cliente estén finalizadas
            // ✅ IMPORTANTE: Un usuario no puede convertirse en experto si tiene contrataciones activas como cliente
            var activeContractsAsClient = await _context.SearchHires
                .Include(sh => sh.Status)
                .Where(sh => sh.ClientId == userId && sh.Status != null && !sh.Status.IsFinalizationStatus)
                .ToListAsync();

            if (activeContractsAsClient.Any())
            {
                // ✅ LOG: Usuario intentó convertirse en experto con contrataciones activas
                await _loggingService.LogWarningAsync(
                    message: "User attempted to become expert with active contracts as client",
                    details: $"User {userId} attempted to become expert but has {activeContractsAsClient.Count} active contract(s) as client. " +
                            $"All contracts must be in a finalization status before becoming an expert. " +
                            $"Active contract IDs: {string.Join(", ", activeContractsAsClient.Select(sh => sh.Id))}.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "BecomeExpert",
                        UserId = userId,
                        ActiveContractsCount = activeContractsAsClient.Count,
                        ActiveContractIds = activeContractsAsClient.Select(sh => sh.Id).ToList()
                    }
                );
                
                return (false, null, null, null);
            }

            // Validar Latitude y Longitude
            if (string.IsNullOrEmpty(request.Latitude) || string.IsNullOrEmpty(request.Longitude))
            {
                await _loggingService.LogWarningAsync(
                    message: "Latitude or Longitude missing in BecomeExpert request",
                    details: $"User {userId} attempted to become expert but Latitude or Longitude is missing. Latitude: {request.Latitude}, Longitude: {request.Longitude}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                return (false, null, null, null);
            }

            if (!decimal.TryParse(request.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var latitude))
            {
                await _loggingService.LogWarningAsync(
                    message: "Invalid Latitude format in BecomeExpert request",
                    details: $"User {userId} attempted to become expert but Latitude format is invalid: {request.Latitude}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                return (false, null, null, null);
            }

            if (!decimal.TryParse(request.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var longitude))
            {
                await _loggingService.LogWarningAsync(
                    message: "Invalid Longitude format in BecomeExpert request",
                    details: $"User {userId} attempted to become expert but Longitude format is invalid: {request.Longitude}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                return (false, null, null, null);
            }

            if (latitude < -90m || latitude > 90m)
            {
                await _loggingService.LogWarningAsync(
                    message: "Latitude out of range in BecomeExpert request",
                    details: $"User {userId} attempted to become expert but Latitude is out of range: {latitude} (must be between -90 and 90)",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                return (false, null, null, null);
            }

            if (longitude < -180m || longitude > 180m)
            {
                await _loggingService.LogWarningAsync(
                    message: "Longitude out of range in BecomeExpert request",
                    details: $"User {userId} attempted to become expert but Longitude is out of range: {longitude} (must be between -180 and 180)",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                return (false, null, null, null);
            }

            // Validar tamaño del archivo (5MB límite para imágenes de perfil)
            if (request.ProfilePicture.Length > 5 * 1024 * 1024)
            {
                await _loggingService.LogWarningAsync(
                    message: "Profile picture file too large in BecomeExpert request",
                    details: $"User {userId} attempted to become expert but profile picture is too large: {request.ProfilePicture.Length} bytes (max 5MB)",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                return (false, null, null, null);
            }

            // Validar tipo de archivo
            var extension = Path.GetExtension(request.ProfilePicture.FileName).ToLowerInvariant();
            if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(extension))
            {
                await _loggingService.LogWarningAsync(
                    message: "Invalid profile picture file type in BecomeExpert request",
                    details: $"User {userId} attempted to become expert but profile picture has invalid extension: {extension} (allowed: .jpg, .jpeg, .png). FileName: {request.ProfilePicture.FileName}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                return (false, null, null, null);
            }
            // ✅ VALIDACIÓN: Verificar que StorageClient esté configurado
            if (_storageClient == null)
            {
                await _loggingService.LogWarningAsync(
                    message: "Google Cloud Storage client not configured",
                    details: $"StorageClient is null. Cannot upload profile picture for user {userId}.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "ProfilePictureUpload",
                        UserId = userId,
                        Error = "StorageClient is null"
                    }
                );
                return (false, null, null, null);
            }

            var bucketName = _configuration["GoogleCloud:BucketName"];
            if (string.IsNullOrEmpty(bucketName))
            {
                await _loggingService.LogWarningAsync(
                    message: "Google Cloud bucket name not configured",
                    details: $"Bucket name is null or empty. Cannot upload profile picture for user {userId}.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "ProfilePictureUpload",
                        UserId = userId,
                        Error = "BucketName is null or empty"
                    }
                );
                return (false, null, null, null);
            }

            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            var objectName = $"experts/{uniqueFileName}";

            try
            {
                // ✅ VALIDACIÓN: Verificar que el archivo sea válido
                if (request.ProfilePicture == null || request.ProfilePicture.Length == 0)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Profile picture is null or empty",
                        details: $"Profile picture file is null or has 0 length for user {userId}",
                        userId: userId,
                        source: "UserService.BecomeExpert",
                        relatedEntityType: "User",
                        relatedEntityId: userId
                    );
                    return (false, null, null, null);
                }

                using (var inputStream = request.ProfilePicture.OpenReadStream())
                {
                    // ✅ VALIDACIÓN: Verificar que el stream sea válido
                    if (inputStream == null || inputStream.Length == 0)
                    {
                        await _loggingService.LogWarningAsync(
                            message: "Profile picture stream is null or empty",
                            details: $"Profile picture stream is null or has 0 length for user {userId}",
                            userId: userId,
                            source: "UserService.BecomeExpert",
                            relatedEntityType: "User",
                            relatedEntityId: userId
                        );
                        return (false, null, null, null);
                    }

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

                            // ✅ VALIDACIÓN: Verificar que el outputStream tenga datos
                            if (outputStream.Length == 0)
                            {
                                await _loggingService.LogWarningAsync(
                                    message: "Processed image stream is empty",
                                    details: $"After processing, the image stream has 0 length for user {userId}",
                                    userId: userId,
                                    source: "UserService.BecomeExpert",
                                    relatedEntityType: "User",
                                    relatedEntityId: userId
                                );
                                return (false, null, null, null);
                            }

                            // ✅ LOG: Intentando subir imagen a Google Cloud Storage
                            await _loggingService.LogInfoAsync(
                                message: "Attempting to upload profile picture to Google Cloud Storage",
                                details: $"Uploading profile picture for user {userId} to bucket {bucketName}, object {objectName}. File size: {outputStream.Length} bytes",
                                userId: userId,
                                source: "UserService.BecomeExpert",
                                relatedEntityType: "User",
                                relatedEntityId: userId,
                                additionalData: new { 
                                    Action = "ProfilePictureUpload",
                                    UserId = userId,
                                    ObjectName = objectName,
                                    BucketName = bucketName,
                                    ContentType = "image/jpeg",
                                    FileSize = outputStream.Length,
                                    Status = "Uploading"
                                }
                            );

                            // ✅ FIX: Quitar PredefinedAcl cuando el bucket tiene uniform bucket-level access habilitado
                            await _storageClient.UploadObjectAsync(
                                bucket: bucketName,
                                objectName: objectName,
                                contentType: "image/jpeg",
                                source: outputStream
                                // ✅ REMOVIDO: PredefinedAcl no es compatible con uniform bucket-level access
                                // options: new UploadObjectOptions { PredefinedAcl = PredefinedObjectAcl.Private }
                            );
                        
                            // ✅ LOG INFORMATIVO: Imagen subida exitosamente
                            await _loggingService.LogInfoAsync(
                                message: "Profile picture uploaded successfully",
                                details: $"Profile picture uploaded successfully for user {userId} to {objectName}",
                                userId: userId,
                                source: "UserService.BecomeExpert",
                                relatedEntityType: "User",
                                relatedEntityId: userId,
                                additionalData: new { 
                                    Action = "ProfilePictureUpload",
                                    UserId = userId,
                                    ObjectName = objectName,
                                    BucketName = bucketName,
                                    ContentType = "image/jpeg",
                                    FileSize = outputStream.Length,
                                    Success = true
                                }
                            );
                        }
                    }
                }
            }
            catch (ImageFormatException ex)
            {
                // ✅ LOG: Error específico de formato de imagen
                await _loggingService.LogWarningAsync(
                    message: "Invalid image format",
                    details: $"Image format is not supported for user {userId}: {ex.Message}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "ProfilePictureUpload",
                        UserId = userId,
                        ExceptionType = "ImageFormatException",
                        ExceptionMessage = ex.Message,
                        FileName = request.ProfilePicture?.FileName
                    }
                );
                return (false, null, null, null);
            }
            catch (Google.GoogleApiException ex)
            {
                // ✅ LOG: Error específico de Google Cloud Storage (WARNING, no crítico)
                await _loggingService.LogWarningAsync(
                    message: "Google Cloud Storage API error",
                    details: $"Failed to upload profile picture to Google Cloud Storage for user {userId}: {ex.Message}. Status: {ex.HttpStatusCode}, Error: {ex.Error?.Message}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "ProfilePictureUpload",
                        UserId = userId,
                        ExceptionType = "GoogleApiException",
                        ExceptionMessage = ex.Message,
                        HttpStatusCode = ex.HttpStatusCode.ToString(),
                        ErrorCode = ex.Error?.Code,
                        BucketName = bucketName,
                        ObjectName = objectName
                    }
                );
                return (false, null, null, null);
            }
            catch (Exception ex)
            {
                // ✅ LOG: Error genérico en subida de imagen (WARNING, no crítico)
                await _loggingService.LogWarningAsync(
                    message: "Profile picture upload failed",
                    details: $"Profile picture upload failed for user {userId}: {ex.Message}. Exception type: {ex.GetType().Name}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "ProfilePictureUpload",
                        UserId = userId,
                        ExceptionType = ex.GetType().Name,
                        ExceptionMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        FileName = request.ProfilePicture?.FileName,
                        FileSize = request.ProfilePicture?.Length,
                        BucketName = bucketName,
                        ObjectName = objectName,
                        Success = false
                    }
                );
                
                return (false, null, null, null);
            }

            var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";

            // ✅ MEJORA: Crear ExpertProfile ANTES de cambiar el rol
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

            // ✅ MEJORA: Try-catch con logging detallado para guardar ExpertProfile
            try
            {
                _context.ExpertProfiles.Add(expertProfile);
                await _context.SaveChangesAsync();

                // ✅ VALIDACIÓN: Verificar que el ID se haya generado correctamente
                if (expertProfile.Id == 0)
                {
                    await _loggingService.LogWarningAsync(
                        message: "ExpertProfile ID not generated",
                        details: $"ExpertProfile was created but ID is 0 for user {userId}. This indicates a database issue.",
                        userId: userId,
                        source: "UserService.BecomeExpert",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new { 
                            Action = "CreateExpertProfile",
                            UserId = userId,
                            Error = "ExpertProfile.Id is 0"
                        }
                    );
                    return (false, null, null, null);
                }
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
            {
                // ✅ LOG: Error específico de base de datos (WARNING, no crítico)
                var innerException = dbEx.InnerException;
                var sqlState = (innerException as Npgsql.NpgsqlException)?.SqlState ?? "UNKNOWN";
                var errorMessage = innerException?.Message ?? dbEx.Message;

                await _loggingService.LogWarningAsync(
                    message: "Database error creating ExpertProfile",
                    details: $"Failed to create ExpertProfile for user {userId}: {errorMessage}. SQL State: {sqlState}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "CreateExpertProfile",
                        UserId = userId,
                        ExceptionType = "DbUpdateException",
                        ExceptionMessage = dbEx.Message,
                        InnerExceptionMessage = errorMessage,
                        SqlState = sqlState,
                        StackTrace = dbEx.StackTrace
                    }
                );
                return (false, null, null, null);
            }
            catch (Exception ex)
            {
                // ✅ LOG: Error genérico al crear ExpertProfile (WARNING, no crítico)
                await _loggingService.LogWarningAsync(
                    message: "Error creating ExpertProfile",
                    details: $"Failed to create ExpertProfile for user {userId}: {ex.Message}. Exception type: {ex.GetType().Name}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "CreateExpertProfile",
                        UserId = userId,
                        ExceptionType = ex.GetType().Name,
                        ExceptionMessage = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                return (false, null, null, null);
            }

            // ✅ VALIDACIÓN: La disponibilidad horaria es OBLIGATORIA al crear un perfil de experto
            // (Ya validado en controller, pero verificamos de nuevo por seguridad)
            if (request.AvailabilityDaysOfWeek == null || request.AvailabilityDaysOfWeek.Count == 0 ||
                string.IsNullOrEmpty(request.AvailabilityStartTime) || string.IsNullOrEmpty(request.AvailabilityEndTime))
            {
                // Eliminar el perfil creado si falta la disponibilidad
                await _loggingService.LogWarningAsync(
                    message: "Availability data missing after ExpertProfile creation",
                    details: $"ExpertProfile {expertProfile.Id} was created but availability data is missing. Removing profile.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                return (false, null, null, null);
            }

            // Parsear y validar tiempos
            if (!TimeSpan.TryParse(request.AvailabilityStartTime, out var startTime) ||
                !TimeSpan.TryParse(request.AvailabilityEndTime, out var endTime))
            {
                // Eliminar el perfil creado si los tiempos son inválidos
                await _loggingService.LogWarningAsync(
                    message: "Invalid time format after ExpertProfile creation",
                    details: $"ExpertProfile {expertProfile.Id} was created but time format is invalid. StartTime: {request.AvailabilityStartTime}, EndTime: {request.AvailabilityEndTime}. Removing profile.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                return (false, null, null, null);
            }

            // Validar días válidos
            var validDays = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
            var invalidDays = request.AvailabilityDaysOfWeek.Except(validDays, StringComparer.OrdinalIgnoreCase).ToList();
            
            if (invalidDays.Any())
            {
                // Eliminar el perfil creado si los días son inválidos
                await _loggingService.LogWarningAsync(
                    message: "Invalid days of week after ExpertProfile creation",
                    details: $"ExpertProfile {expertProfile.Id} was created but days are invalid: {string.Join(", ", invalidDays)}. Removing profile.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                return (false, null, null, null);
            }

            if (startTime >= endTime)
            {
                // Eliminar el perfil creado si el rango de tiempo es inválido
                await _loggingService.LogWarningAsync(
                    message: "Invalid time range after ExpertProfile creation",
                    details: $"ExpertProfile {expertProfile.Id} was created but time range is invalid (start >= end). Removing profile.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId
                );
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                return (false, null, null, null);
            }

            // Crear disponibilidad horaria inicial (obligatoria)
            try
            {
                var now = DateTime.UtcNow;
                var availability = new ExpertAvailability
                {
                    ExpertId = expertProfile.Id, // ✅ Ya verificado que no es 0
                    DaysOfWeek = System.Text.Json.JsonSerializer.Serialize(request.AvailabilityDaysOfWeek),
                    StartTime = startTime,
                    EndTime = endTime,
                    EffectiveFrom = now,
                    EffectiveTo = null,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                _context.ExpertAvailabilities.Add(availability);
                await _context.SaveChangesAsync();
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
            {
                // ✅ LOG: Error específico de base de datos al crear disponibilidad (WARNING, no crítico)
                var innerException = dbEx.InnerException;
                var sqlState = (innerException as Npgsql.NpgsqlException)?.SqlState ?? "UNKNOWN";
                var errorMessage = innerException?.Message ?? dbEx.Message;

                await _loggingService.LogWarningAsync(
                    message: "Database error creating ExpertAvailability",
                    details: $"Failed to create ExpertAvailability for ExpertProfile {expertProfile.Id} (user {userId}): {errorMessage}. SQL State: {sqlState}. Removing ExpertProfile.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "CreateExpertAvailability",
                        UserId = userId,
                        ExpertProfileId = expertProfile.Id,
                        ExceptionType = "DbUpdateException",
                        ExceptionMessage = dbEx.Message,
                        InnerExceptionMessage = errorMessage,
                        SqlState = sqlState,
                        StackTrace = dbEx.StackTrace
                    }
                );

                // Si falla la creación de disponibilidad, eliminar el perfil
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                return (false, null, null, null);
            }
            catch (Exception ex)
            {
                // ✅ LOG: Error genérico al crear disponibilidad (WARNING, no crítico)
                await _loggingService.LogWarningAsync(
                    message: "Error creating ExpertAvailability",
                    details: $"Failed to create ExpertAvailability for ExpertProfile {expertProfile.Id} (user {userId}): {ex.Message}. Exception type: {ex.GetType().Name}. Removing ExpertProfile.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "CreateExpertAvailability",
                        UserId = userId,
                        ExpertProfileId = expertProfile.Id,
                        ExceptionType = ex.GetType().Name,
                        ExceptionMessage = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );

                // Si falla la creación de disponibilidad, eliminar el perfil
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                return (false, null, null, null);
            }

            // ✅ MEJORA: Cambiar el rol SOLO después de confirmar que todo salió bien
            user.Role = UserRole.Expert;
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // ✅ LOG: Error al actualizar el rol (WARNING, no crítico)
                await _loggingService.LogWarningAsync(
                    message: "Error updating user role to Expert",
                    details: $"Failed to update user {userId} role to Expert after creating profile {expertProfile.Id}: {ex.Message}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "UpdateUserRole",
                        UserId = userId,
                        ExpertProfileId = expertProfile.Id,
                        ExceptionType = ex.GetType().Name,
                        ExceptionMessage = ex.Message
                    }
                );
                // Aunque falle, el perfil ya está creado, así que devolvemos éxito parcial
                // El rol se puede actualizar manualmente si es necesario
            }

            var token = GenerateJwtToken(user);
            // ✅ LOG INFORMATIVO: Usuario se convirtió en experto exitosamente (ya existe en UserController, pero también aquí para consistencia)
            await _loggingService.LogInfoAsync(
                message: "User became expert successfully",
                details: $"User {userId} successfully became expert with profile {expertProfile.Id}",
                userId: userId,
                source: "UserService.BecomeExpert",
                relatedEntityType: "User",
                relatedEntityId: userId,
                additionalData: new { 
                    Action = "BecomeExpert",
                    ExpertProfileId = expertProfile.Id,
                    StripeAccountId = expertProfile.StripeAccountId,
                    StripeStatus = expertProfile.StripeStatus
                }
            );
            
            // ✅ SEGURIDAD 2025: Generar Access Token + Refresh Token
            var accessToken = GenerateJwtToken(user);
            var refreshToken = await GenerateRefreshTokenAsync(user.Id, "BecomeExpert");
            var combinedToken = $"{accessToken}|{refreshToken}";
            
            return (true, combinedToken, user, expertProfile);
        }

        public async Task<ExpertProfileDto> GetExpertProfile(int userId)
        {
            var expertProfile = await _context.ExpertProfiles
                .Include(ep => ep.User)
                .FirstOrDefaultAsync(ep => ep.UserId == userId);

            if (expertProfile == null)
                return null;

            // Obtener la disponibilidad actual activa
            var currentAvailability = await _context.ExpertAvailabilities
                .Where(ea => ea.ExpertId == expertProfile.Id && ea.IsActive && ea.EffectiveTo == null)
                .OrderByDescending(ea => ea.EffectiveFrom)
                .FirstOrDefaultAsync();

            CurrentExpertAvailabilityDto? availabilityDto = null;
            if (currentAvailability != null)
            {
                var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(currentAvailability.DaysOfWeek) ?? new List<string>();
                availabilityDto = new CurrentExpertAvailabilityDto
                {
                    Id = currentAvailability.Id,
                    DaysOfWeek = daysOfWeek,
                    StartTime = currentAvailability.StartTime,
                    EndTime = currentAvailability.EndTime,
                    EffectiveFrom = currentAvailability.EffectiveFrom
                };
            }

            return new ExpertProfileDto
            {
                Id = expertProfile.Id,
                ProfilePictureUrl = ResolveProfilePictureUrl(expertProfile),
                StripeAccountId = expertProfile.StripeAccountId,
                Description = expertProfile.Description,
                CreatedAt = expertProfile.CreatedAt,
                User = new UserDto
                {
                    Name = expertProfile.User.Name,
                    Email = expertProfile.User.Email
                },
                Reviews = new List<ReviewDto>(), // Inicializar lista vacía para mantener compatibilidad
                Latitude = expertProfile.Latitude,
                Longitude = expertProfile.Longitude,
                StripeStatus = expertProfile.StripeStatus,
                StripeStatusDetails = expertProfile.StripeStatusDetails,
                OnboardingCompleted = expertProfile.OnboardingCompleted,
                IsOnVacation = expertProfile.IsOnVacation,
                CurrentAvailability = availabilityDto,
                // ✅ FUTURE REQUIREMENTS
                StripeFutureRequirements = expertProfile.StripeFutureRequirements,
                StripeFutureDueAt = expertProfile.StripeFutureDueAt
            };
        }


        public async Task<(bool Success, ExpertProfileDto UpdatedProfile)> UpdateExpertProfile(int userId, UpdateExpertProfileRequestDto request)
        {
            try
            {
                // Buscar el perfil de experto existente
                var expertProfile = await _context.ExpertProfiles
                    .Include(ep => ep.User)
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return (false, null);
                }

                // Validar coordenadas
                if (!decimal.TryParse(request.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var latitude) ||
                    latitude < -90m || latitude > 90m)
                {
                    return (false, null);
                }

                if (!decimal.TryParse(request.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var longitude) ||
                    longitude < -180m || longitude > 180m)
                {
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
                        return (false, null);
                    }

                    // Validar tipo de archivo
                    var extension = Path.GetExtension(request.ProfilePicture.FileName).ToLowerInvariant();
                    if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(extension))
                    {
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
                                // ✅ FIX: Quitar PredefinedAcl cuando el bucket tiene uniform bucket-level access habilitado
                                // El acceso se controla mediante IAM policies del bucket, no ACLs por objeto
                                await _storageClient.UploadObjectAsync(
                                    bucket: bucketName,
                                    objectName: objectName,
                                    contentType: "image/jpeg",
                                    source: outputStream
                                    // ✅ REMOVIDO: PredefinedAcl no es compatible con uniform bucket-level access
                                    // options: new UploadObjectOptions { PredefinedAcl = PredefinedObjectAcl.Private }
                                );
                            }
                        }

                        // Eliminar la imagen anterior si existe
                        if (!string.IsNullOrEmpty(expertProfile.ProfilePictureObjectName))
                        {
                            try
                            {
                                await _storageClient.DeleteObjectAsync(bucketName, expertProfile.ProfilePictureObjectName);
                            }
                            catch (Exception ex)
                            {
                            }
                        }

                        // Actualizar URLs de la nueva imagen
                        var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
                        expertProfile.ProfilePictureUrl = imageUrl;
                        expertProfile.ProfilePictureObjectName = objectName;
                    }
                    catch (Exception ex)
                    {
                        return (false, null);
                    }
                }

                // ✅ VALIDACIÓN: Verificar si el experto tiene disponibilidad activa
                var currentAvailability = await _context.ExpertAvailabilities
                    .Where(ea => ea.ExpertId == expertProfile.Id && ea.IsActive && ea.EffectiveTo == null)
                    .OrderByDescending(ea => ea.EffectiveFrom)
                    .FirstOrDefaultAsync();

                // ✅ VALIDACIÓN: Si el experto tiene disponibilidad, DEBE actualizarla (no puede omitirla)
                // Si no tiene disponibilidad, DEBE proporcionarla
                bool hasAvailabilityProvided = request.AvailabilityDaysOfWeek != null && 
                                               request.AvailabilityDaysOfWeek.Count > 0 &&
                                               !string.IsNullOrEmpty(request.AvailabilityStartTime) && 
                                               !string.IsNullOrEmpty(request.AvailabilityEndTime);

                if (currentAvailability != null && !hasAvailabilityProvided)
                {
                    return (false, null);
                }

                if (!hasAvailabilityProvided)
                {
                    return (false, null);
                }

                // Parsear y validar tiempos
                if (!TimeSpan.TryParse(request.AvailabilityStartTime, out var startTime) ||
                    !TimeSpan.TryParse(request.AvailabilityEndTime, out var endTime))
                {
                    return (false, null);
                }

                // Validar días válidos
                var validDays = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
                var invalidDays = request.AvailabilityDaysOfWeek?.Except(validDays, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
                
                if (invalidDays.Any())
                {
                    return (false, null);
                }

                if (startTime >= endTime)
                {
                    return (false, null);
                }

                // Actualizar disponibilidad horaria
                try
                {
                    var now = DateTime.UtcNow;

                    // Si existe una disponibilidad activa, marcarla como inactiva
                    if (currentAvailability != null)
                    {
                        currentAvailability.IsActive = false;
                        currentAvailability.EffectiveTo = now;
                        currentAvailability.UpdatedAt = now;
                    }

                    // Crear nueva disponibilidad
                    var newAvailability = new ExpertAvailability
                    {
                        ExpertId = expertProfile.Id,
                        DaysOfWeek = System.Text.Json.JsonSerializer.Serialize(request.AvailabilityDaysOfWeek),
                        StartTime = startTime,
                        EndTime = endTime,
                        EffectiveFrom = now,
                        EffectiveTo = null,
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    };

                    _context.ExpertAvailabilities.Add(newAvailability);
                }
                catch (Exception ex)
                {
                    return (false, null);
                }

                await _context.SaveChangesAsync();

                // Devolver el perfil actualizado
                var updatedProfileDto = new ExpertProfileDto
                {
                    Id = expertProfile.Id,
                    ProfilePictureUrl = ResolveProfilePictureUrl(expertProfile),
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
                    // ✅ FUTURE REQUIREMENTS
                    StripeFutureRequirements = expertProfile.StripeFutureRequirements,
                    StripeFutureDueAt = expertProfile.StripeFutureDueAt,
                    IsOnVacation = expertProfile.IsOnVacation
                };
                return (true, updatedProfileDto);
            }
            catch (Exception ex)
            {
                return (false, null);
            }
        }

        // ✅ REMOVED: GetUserBalanceAsync method eliminated - balance system removed

        public async Task<(bool Success, bool IsOnVacation)> ToggleVacationMode(int userId)
        {
            try
            {
                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return (false, false);
                }

                // Cambiar el estado de vacaciones
                expertProfile.IsOnVacation = !expertProfile.IsOnVacation;
                await _context.SaveChangesAsync();
                return (true, expertProfile.IsOnVacation);
            }
            catch (Exception ex)
            {
                return (false, false);
            }
        }


        public string GenerateJwtToken(User user)
        {
            // Convertir el valor numérico del enum al nombre del enum
            var roleName = user.Role switch
            {
                UserRole.Client => "Client",
                UserRole.Expert => "Expert", 
                UserRole.Admin => "Admin",
                _ => "Client"
            };

            var claims = new[]
            {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Role, roleName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()) // ✅ SEGURIDAD: ID único del token para revocación
        };

            var key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]));

            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(30), // ✅ SEGURIDAD 2025: 30 minutos (antes: 24h)
                notBefore: DateTime.UtcNow, // ✅ SEGURIDAD: Token válido desde ahora
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// ✅ SEGURIDAD 2025: Generar Refresh Token criptográficamente seguro
        /// </summary>
        public async Task<string> GenerateRefreshTokenAsync(int userId, string ipAddress)
        {
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            var randomBytes = new byte[64];
            rng.GetBytes(randomBytes);
            var token = Convert.ToBase64String(randomBytes);

            // Verificar que el token sea único
            while (await _context.RefreshTokens.AnyAsync(rt => rt.Token == token))
            {
                rng.GetBytes(randomBytes);
                token = Convert.ToBase64String(randomBytes);
            }

            var refreshToken = new RefreshToken
            {
                Token = token,
                UserId = userId,
                ExpiresAt = DateTime.UtcNow.AddDays(7), // ✅ Best Practice: 7 días
                CreatedByIp = ipAddress,
                DeviceInfo = GetDeviceInfo()
            };

            _context.RefreshTokens.Add(refreshToken);
            await _context.SaveChangesAsync();

            return token;
        }

        /// <summary>
        /// Obtener IP del cliente (para auditoría de seguridad)
        /// </summary>
        private string GetClientIpAddress()
        {
            // TODO: Implementar obteniendo el HttpContext
            return "unknown";
        }

        /// <summary>
        /// Obtener información del dispositivo (para auditoría)
        /// </summary>
        private string? GetDeviceInfo()
        {
            // TODO: Implementar obteniendo el User-Agent del HttpContext
            return null;
        }

        private string ResolveProfilePictureUrl(ExpertProfile? expertProfile)
        {
            if (expertProfile == null)
            {
                return "/default-avatar.png";
            }

            var fallback = string.IsNullOrWhiteSpace(expertProfile.ProfilePictureUrl)
                ? "/default-avatar.png"
                : expertProfile.ProfilePictureUrl;

            return _signedUrlService.GetSignedUrl(expertProfile.ProfilePictureObjectName ?? string.Empty) ?? fallback;
        }
    }
}
