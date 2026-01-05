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
using Microsoft.Extensions.DependencyInjection;

namespace newApi.Services
{
    public class UserService : IUserService
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly StorageClient _storageClient;
        private readonly ILoggingService _loggingService;
        private readonly ITimezoneService _timezoneService;
        private readonly INotificationService _notificationService;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly string _twilioVerificationServiceSid;
        private readonly string _twilioauthToken;

        public UserService(
     AppDbContext context,
     IConfiguration configuration,

     StorageClient storageClient,
     ILoggingService loggingService,
     ITimezoneService timezoneService,
     INotificationService notificationService,
     IServiceScopeFactory serviceScopeFactory)
        {
            _context = context;
            _configuration = configuration;
            _storageClient = storageClient;
            _loggingService = loggingService;
            _timezoneService = timezoneService;
            _notificationService = notificationService;
            _serviceScopeFactory = serviceScopeFactory;
            _twilioVerificationServiceSid = configuration["Twilio:VerificationServiceSid"];
            _twilioauthToken = configuration["Twilio:AuthToken"];
        }

        public async Task<User> GetUserAsync(int userId)
        {
            return await _context.Users.FindAsync(userId);
        }

        public async Task<(IEnumerable<object> users, int totalCount)> GetAllUsers(int page, int pageSize)
        {
            var query = _context.Users
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
                });

            var totalCount = await query.CountAsync();
            
            var users = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (users, totalCount);
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
            // ✅ OPTIMIZACIÓN: Leer Client IDs de múltiples formas para compatibilidad (caché en memoria)
            string[]? clientIds = null;
            
            // Opción 1: Intentar leer como array JSON (formato preferido)
            var clientIdsJson = _configuration["Google:ClientIds"];
            if (!string.IsNullOrEmpty(clientIdsJson) && clientIdsJson.TrimStart().StartsWith("["))
            {
                try
                {
                    clientIds = System.Text.Json.JsonSerializer.Deserialize<string[]>(clientIdsJson);
                }
                catch
                {
                    // Error silencioso, intentar siguiente opción
                }
            }
            
            // Opción 2: Si no funciona como JSON, intentar con GetSection().Get<string[]>()
            if (clientIds == null || clientIds.Length == 0)
            {
                clientIds = _configuration.GetSection("Google:ClientIds").Get<string[]>();
            }
            
            // Opción 3: Si aún no funciona, leer claves indexadas manualmente
            if (clientIds == null || clientIds.Length == 0)
            {
                var clientIdsList = new List<string>();
                int index = 0;
                while (true)
                {
                    var clientId = _configuration[$"Google:ClientIds:{index}"];
                    if (string.IsNullOrEmpty(clientId))
                        break;
                    clientIdsList.Add(clientId);
                    index++;
                }
                if (clientIdsList.Count > 0)
                {
                    clientIds = clientIdsList.ToArray();
                }
            }
            
            // ✅ VALIDACIÓN: Verificar que se cargaron los Client IDs correctamente
            if (clientIds == null || clientIds.Length == 0)
            {
                throw new InvalidOperationException("Google Client IDs not configured");
            }
            
            // ✅ OPTIMIZACIÓN: Validar token de Google
            var settings = new GoogleJsonWebSignature.ValidationSettings { Audience = clientIds };
            var payload = await GoogleJsonWebSignature.ValidateAsync(request.AccessToken, settings);

            // ✅ CRITICAL FIX: Crear scope independiente ANTES de cualquier operación DB
            // Este scope NO está ligado al HttpContext del request
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            // ✅ FIX CRÍTICO: Usar transacción directa sin ExecutionStrategy para evitar problemas de ciclo de vida
            await using var transaction = await context.Database.BeginTransactionAsync();
            try
            {
                // ✅ Query user dentro de la transacción para evitar condiciones de carrera
                var user = await context.Users
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(u => u.GoogleId == payload.Subject);

                bool wasNew = user == null;
                bool wasRestored = user != null && user.IsDeleted;

                // ✅ VALIDACIÓN CRÍTICA: Verificar si el usuario está bloqueado
                if (user != null && user.IsBlocked)
                {
                    await transaction.RollbackAsync();
                    return (false, null, null);
                }

                // Si era nuevo, crear usuario y generar ID
                if (wasNew)
                {
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

                    context.Users.Add(user);
                    await context.SaveChangesAsync();  // ✅ CRÍTICO: Generar user.Id inmediatamente
                }
                else if (wasRestored && user != null)
                {
                    // Restaurar usuario eliminado (user ya cargado y tracked)
                    user.IsDeleted = false;
                    user.DeletedAt = null;
                    user.Name = payload.Name?.Trim();
                    user.Email = payload.Email?.Trim();
                }

                // ✅ OPTIMIZACIÓN: Crear UserSettings si no existen
                if (wasNew && user != null)
                {
                    // Ahora user.Id está garantizado
                    var userSettings = new UserSetting
                    {
                        UserId = user.Id,
                        IsWhatsAppEnabled = true,
                        IsEmailEnabled = true,
                        Theme = "light",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    context.UserSettings.Add(userSettings);
                }
                else if (wasRestored && user != null)
                {
                    var existingSettings = await context.UserSettings.FirstOrDefaultAsync(us => us.UserId == user.Id);
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
                        context.UserSettings.Add(userSettings);
                    }
                }

                // ✅ OPTIMIZACIÓN: Generar refresh token (ahora user.Id está garantizado)
                if (user == null)
                {
                    await transaction.RollbackAsync();
                    return (false, null, null);
                }

                var refreshToken = GenerateSecureRefreshToken();
                var refreshTokenEntity = new RefreshToken
                {
                    Token = refreshToken,
                    UserId = user.Id,  // ✅ Ahora garantizado válido
                    ExpiresAt = DateTime.UtcNow.AddDays(30),
                    CreatedByIp = "GoogleAuth",
                    DeviceInfo = null
                };
                context.RefreshTokens.Add(refreshTokenEntity);

                // ✅ OPTIMIZACIÓN: Una sola llamada a SaveChanges para las operaciones restantes
                await context.SaveChangesAsync();
                await transaction.CommitAsync();

                // ✅ OPTIMIZACIÓN: Generar access token DESPUÉS de SaveChanges (más rápido)
                // ✅ CRÍTICO: Generar tokens ANTES de que el scope se disponga
                var accessToken = GenerateJwtToken(user);
                var combinedToken = $"{accessToken}|{refreshToken}";

                // ✅ CRÍTICO: Crear una copia del usuario para retornar (desconectado del contexto)
                var returnedUser = new User
                {
                    Id = user.Id,
                    Name = user.Name,
                    Email = user.Email,
                    GoogleId = user.GoogleId,
                    Role = user.Role,
                    CreatedAt = user.CreatedAt,
                    IsBlocked = user.IsBlocked,
                    IsDeleted = user.IsDeleted
                };

                return (true, combinedToken, returnedUser);
            }
            catch (Exception ex)
            {
                // ✅ FIX: Manejar errores de disposición durante rollback
                try
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // ✅ FIX: Transacción ya dispuesta - no hacer nada
                }
                catch (InvalidOperationException rollbackEx) when (rollbackEx.Message.Contains("disposed", StringComparison.OrdinalIgnoreCase))
                {
                    // ✅ FIX: Transacción ya dispuesta - no hacer nada
                }
                catch (InvalidOperationException rollbackEx) when (rollbackEx.Message.Contains("multiplexing", StringComparison.OrdinalIgnoreCase))
                {
                    // ✅ FIX: Error de multiplexing - no hacer nada, el rollback ya se hizo o no es necesario
                }
                throw; // Re-lanzar el error original
            }
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
                return (false, null, null, null);
            }

            // ✅ VALIDACIÓN: Usuario bloqueado no puede convertirse en experto
            if (user.IsBlocked)
            {
                 await _loggingService.LogWarningAsync(
                    message: "Blocked user attempted to become expert",
                    details: $"Blocked user {user.Id} ({user.Email}) attempted to become expert",
                    userId: user.Id,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: user.Id
                );
                return (false, null, null, null);
            }

            if (user.Role == UserRole.Expert)
            {
                return (false, null, null, null);
            }

            if (user.ExpertProfile != null)
            {
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
                return (false, null, null, null);
            }

            if (!decimal.TryParse(request.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var latitude))
            {
                return (false, null, null, null);
            }

            if (!decimal.TryParse(request.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var longitude))
            {
                return (false, null, null, null);
            }

            if (latitude < -90m || latitude > 90m)
            {
                return (false, null, null, null);
            }

            if (longitude < -180m || longitude > 180m)
            {
                return (false, null, null, null);
            }

            // Validar tamaño del archivo (5MB límite para imágenes de perfil)
            if (request.ProfilePicture.Length > 5 * 1024 * 1024)
            {
                return (false, null, null, null);
            }

            // Validar tipo de archivo
            var extension = Path.GetExtension(request.ProfilePicture.FileName).ToLowerInvariant();
            if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(extension))
            {
                return (false, null, null, null);
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
                        
                        // 🚨 LOG CRÍTICO: Imagen subida exitosamente
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Profile picture uploaded successfully",
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
                                Success = true
                            }
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                // 🚨 LOG CRÍTICO: Error en subida de imagen
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Profile picture upload failed",
                    details: $"Profile picture upload failed for user {userId}: {ex.Message}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "ProfilePictureUpload",
                        UserId = userId,
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace,
                        Success = false
                    }
                );
                
                return (false, null, null, null);
            }

            var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
            user.Role = UserRole.Expert;

            // ✅ DETECTAR TIMEZONE Y COUNTRY automáticamente desde coordenadas
            string expertTimezone = "UTC";
            string? expertCountry = null;
            
            try
            {
                expertTimezone = await _timezoneService.GetTimezoneFromCoordinatesAsync(latitude, longitude);
                expertCountry = await _timezoneService.GetCountryFromCoordinatesAsync(latitude, longitude);
            }
            catch (Exception ex)
            {
                // Si falla la detección, usar UTC como fallback y continuar
                await _loggingService.LogWarningAsync(
                    message: "Failed to detect timezone/country from coordinates",
                    details: $"Could not detect timezone/country for coordinates ({latitude}, {longitude}): {ex.Message}. Using UTC as fallback.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: null,
                    additionalData: new { 
                        Action = "DetectTimezoneCountry",
                        Latitude = latitude,
                        Longitude = longitude,
                        Exception = ex.Message
                    }
                );
            }

            var expertProfile = new ExpertProfile
            {
                UserId = user.Id,
                ProfilePictureUrl = imageUrl,
                ProfilePictureObjectName = objectName,
                Description = request.Description,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                Timezone = expertTimezone,
                Country = expertCountry,
                StripeAccountId = null, // No guardar StripeAccountId, se genera en el onboarding
                CreatedAt = DateTime.UtcNow
            };

            _context.ExpertProfiles.Add(expertProfile);
            await _context.SaveChangesAsync();

            // ✅ VALIDACIÓN: La disponibilidad horaria es OBLIGATORIA al crear un perfil de experto
            if (request.AvailabilityDaysOfWeek == null || request.AvailabilityDaysOfWeek.Count == 0 ||
                string.IsNullOrEmpty(request.AvailabilityStartTime) || string.IsNullOrEmpty(request.AvailabilityEndTime))
            {
                // Eliminar el perfil creado si falta la disponibilidad
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                return (false, null, null, null);
            }

            // Parsear y validar tiempos
            if (!TimeSpan.TryParse(request.AvailabilityStartTime, out var startTime) ||
                !TimeSpan.TryParse(request.AvailabilityEndTime, out var endTime))
            {
                // Eliminar el perfil creado si los tiempos son inválidos
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
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                return (false, null, null, null);
            }

            if (startTime >= endTime)
            {
                // Eliminar el perfil creado si el rango de tiempo es inválido
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

                _context.ExpertAvailabilities.Add(availability);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Si falla la creación de disponibilidad, eliminar el perfil
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                return (false, null, null, null);
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

        public async Task<ExpertProfileDto?> GetExpertProfile(int userId)
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
                ProfilePictureUrl = expertProfile.ProfilePictureUrl,
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


        public async Task<(bool Success, ExpertProfileDto? UpdatedProfile)> UpdateExpertProfile(int userId, UpdateExpertProfileRequestDto request)
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
                
                // ✅ DETECTAR TIMEZONE Y COUNTRY si cambian las coordenadas
                var coordinatesChanged = expertProfile.Latitude != request.Latitude || 
                                         expertProfile.Longitude != request.Longitude;
                
                expertProfile.Latitude = request.Latitude;
                expertProfile.Longitude = request.Longitude;
                
                // Si cambian las coordenadas, detectar nuevo timezone y country
                if (coordinatesChanged)
                {
                    try
                    {
                        var detectedTimezone = await _timezoneService.GetTimezoneFromCoordinatesAsync(latitude, longitude);
                        var detectedCountry = await _timezoneService.GetCountryFromCoordinatesAsync(latitude, longitude);
                        
                        expertProfile.Timezone = detectedTimezone;
                        expertProfile.Country = detectedCountry;
                        
                        await _loggingService.LogInfoAsync(
                            message: "Timezone and country updated from coordinates",
                            details: $"Updated timezone to {detectedTimezone} and country to {detectedCountry} for coordinates ({latitude}, {longitude})",
                            userId: userId,
                            source: "UserService.UpdateExpertProfile",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: expertProfile.Id,
                            additionalData: new { 
                                Action = "UpdateTimezoneCountry",
                                Latitude = latitude,
                                Longitude = longitude,
                                Timezone = detectedTimezone,
                                Country = detectedCountry
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        // Si falla la detección, mantener los valores actuales y loguear el error
                        await _loggingService.LogWarningAsync(
                            message: "Failed to detect timezone/country from new coordinates",
                            details: $"Could not detect timezone/country for new coordinates ({latitude}, {longitude}): {ex.Message}. Keeping existing values.",
                            userId: userId,
                            source: "UserService.UpdateExpertProfile",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: expertProfile.Id,
                            additionalData: new { 
                                Action = "DetectTimezoneCountry",
                                Latitude = latitude,
                                Longitude = longitude,
                                Exception = ex.Message
                            }
                        );
                    }
                }

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
                expires: DateTime.UtcNow.AddHours(1), // ✅ BEST PRACTICE 2024: 1 hora (estándar Microsoft/Google/Auth0)
                notBefore: DateTime.UtcNow, // ✅ SEGURIDAD: Token válido desde ahora
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// ✅ OPTIMIZACIÓN: Generar Refresh Token criptográficamente seguro (sin verificación de BD)
        /// La probabilidad de colisión es extremadamente baja (64 bytes = 512 bits de entropía)
        /// </summary>
        private string GenerateSecureRefreshToken()
        {
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            var randomBytes = new byte[64];
            rng.GetBytes(randomBytes);
            return Convert.ToBase64String(randomBytes);
        }

        /// <summary>
        /// ✅ SEGURIDAD 2025: Generar Refresh Token criptográficamente seguro
        /// </summary>
        public async Task<string> GenerateRefreshTokenAsync(int userId, string ipAddress)
        {
            var token = GenerateSecureRefreshToken();

            // ✅ OPTIMIZACIÓN: Verificar unicidad solo una vez (colisión extremadamente improbable)
            // Si hay colisión, regenerar una vez más
            if (await _context.RefreshTokens.AnyAsync(rt => rt.Token == token))
            {
                token = GenerateSecureRefreshToken();
            }

            var refreshToken = new RefreshToken
            {
                Token = token,
                UserId = userId,
                ExpiresAt = DateTime.UtcNow.AddDays(30), // ✅ BEST PRACTICE 2024: 30 días (estándar industria - balance seguridad/UX)
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
    }
}

