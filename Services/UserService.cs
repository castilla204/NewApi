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
using newApi.Common;

namespace newApi.Services
{
    public class UserService : IUserService
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly StorageClient _storageClient;
        private readonly ISupabaseStorageService _supabaseStorage;
        private readonly ILoggingService _loggingService;
        private readonly ITimezoneService _timezoneService;
        private readonly INotificationService _notificationService;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        // 🛡️ SEC-MAJ-7 FIX: admin email check delegado a registry (env var ADMIN_EMAILS).
        private readonly IAdminEmailRegistry _adminEmails;
        private readonly string _twilioVerificationServiceSid;
        private readonly string _twilioauthToken;

        public UserService(
     AppDbContext context,
     IConfiguration configuration,

     StorageClient storageClient,
     ISupabaseStorageService supabaseStorage,
     ILoggingService loggingService,
     ITimezoneService timezoneService,
     INotificationService notificationService,
     IServiceScopeFactory serviceScopeFactory,
     IAdminEmailRegistry adminEmails)
        {
            _context = context;
            _configuration = configuration;
            _storageClient = storageClient;
            _supabaseStorage = supabaseStorage;
            _loggingService = loggingService;
            _timezoneService = timezoneService;
            _notificationService = notificationService;
            _serviceScopeFactory = serviceScopeFactory;
            _adminEmails = adminEmails;
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
            // 🛡️ SEC-MAJ-7 FIX: admin check vía registry; literal antes era "dcastillaa@gmail.com".
            if (user == null || _adminEmails.IsAdmin(user.Email))
                return false;

            // 🛡️ Round 15 — R4 FIX: cuando se bloquea (transición false→true), revocar
            // INMEDIATAMENTE todos los refresh tokens activos del usuario. Antes solo togglea
            // el flag → AccessTokens siguen vivos hasta 1h y RefreshTokens hasta 90d (sólo se
            // revocaban perezosamente en el siguiente intento de refresh, AuthController:71).
            // Ahora la transición a Banned cierra refresh tokens; AccessTokens siguen vivos
            // hasta exp (max 1h con R2 fix) — pendiente OnTokenValidated lookup para cierre
            // instantáneo (TIER 2 deferred).
            var wasBlocked = user.IsBlocked;
            user.IsBlocked = !user.IsBlocked;
            await _context.SaveChangesAsync();

            if (!wasBlocked && user.IsBlocked)
            {
                try
                {
                    var activeTokens = await _context.RefreshTokens
                        .Where(rt => rt.UserId == userId && !rt.IsRevoked && rt.ExpiresAt > DateTime.UtcNow)
                        .ToListAsync();
                    foreach (var t in activeTokens)
                    {
                        t.IsRevoked = true;
                        t.RevokedAt = DateTime.UtcNow;
                        t.RevokedByIp = "Admin-Block";
                    }
                    if (activeTokens.Count > 0)
                    {
                        await _context.SaveChangesAsync();
                    }
                }
                catch
                {
                    // Best-effort: si el bloqueo principal ya commiteó, no abortamos por fallo de revocación.
                    // El AuthController.RefreshToken (línea 71) seguirá rechazando refresh por IsBlocked.
                }
            }
            return true;
        }

        public async Task<bool> DeleteUser(int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            // 🛡️ SEC-MAJ-7 FIX: admin check vía registry; literal antes era "dcastillaa@gmail.com".
            if (user == null || _adminEmails.IsAdmin(user.Email))
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

        public async Task<(bool success, string? token, User? user, string? errorReason)> GoogleAuth(GoogleAuthDto request)
        {
            // Leer Client IDs
            string[]? clientIds = null;
            var clientIdsJson = _configuration["Google:ClientIds"];
            if (!string.IsNullOrEmpty(clientIdsJson) && clientIdsJson.TrimStart().StartsWith("["))
            {
                try
                {
                    clientIds = System.Text.Json.JsonSerializer.Deserialize<string[]>(clientIdsJson);
                }
                catch { }
            }
            
            if (clientIds == null || clientIds.Length == 0)
            {
                clientIds = _configuration.GetSection("Google:ClientIds").Get<string[]>();
            }
            
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
            
            if (clientIds == null || clientIds.Length == 0)
            {
                throw new InvalidOperationException("Google Client IDs not configured");
            }
            
            // Validar token de Google
            var settings = new GoogleJsonWebSignature.ValidationSettings { Audience = clientIds };
            var payload = await GoogleJsonWebSignature.ValidateAsync(request.AccessToken, settings);

            // 🛡️ SEGURIDAD CRÍTICA: Rechazar si Google no ha verificado el email.
            // Sin esta comprobación, un atacante con un ID token de email no verificado
            // podría enlazar a cuentas existentes vía fallback por email.
            if (!payload.EmailVerified)
            {
                await _loggingService.LogWarningAsync(
                    message: "Google auth rejected: email not verified",
                    details: $"Google sub {payload.Subject} attempted login with unverified email {payload.Email}",
                    userId: null,
                    source: "UserService.GoogleAuth",
                    relatedEntityType: "User",
                    relatedEntityId: null
                );
                return (false, null, null, "email_not_verified");
            }

            // Query user (sin AsNoTracking para poder actualizar si es necesario)
            // ✅ FIX: Usar IgnoreQueryFilters para encontrar usuarios incluso si están eliminados
            // pero luego verificar IsDeleted para rechazar el login
            var user = await _context.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.GoogleId == payload.Subject);

            // ✅ SEGURIDAD: Rechazar login si el usuario está bloqueado
            if (user != null && user.IsBlocked)
            {
                return (false, null, null, "account_blocked");
            }

            // ✅ SEGURIDAD CRÍTICA: Rechazar login si el usuario está eliminado
            // Los usuarios eliminados NO pueden acceder al sistema
            if (user != null && user.IsDeleted)
            {
                return (false, null, null, "account_deleted");
            }

            // SIN TRANSACCIÓN - Usar SaveChanges directamente
            // Las transacciones con BeginTransactionAsync causan problemas con multiplexing
            if (user == null)
            {
                var emailToCheck = payload.Email?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(emailToCheck))
                {
                    user = await _context.Users
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(u => u.Email.ToLower() == emailToCheck);

                    if (user != null)
                    {
                        if (user.IsBlocked)
                            return (false, null, null, "account_blocked");
                        if (user.IsDeleted)
                            return (false, null, null, "account_deleted");

                        user.GoogleId = payload.Subject;
                        user.Name = string.IsNullOrWhiteSpace(user.Name) ? payload.Name?.Trim() : user.Name;
                        // 🛡️ SEC-MAJ-7 FIX: admin promotion vía registry. Google ya validó EmailVerified
                        // arriba, así que sabemos que `emailToCheck` lo posee realmente el usuario.
                        if (_adminEmails.IsAdmin(emailToCheck))
                            user.Role = UserRole.Admin;
                    }
                }
            }

            if (user == null)
            {
                var emailToCheck = payload.Email?.Trim().ToLowerInvariant();
                // 🛡️ SEC-MAJ-7 FIX: admin promotion vía registry; literal antes era "dcastillaa@gmail.com".
                var isAdminEmail = _adminEmails.IsAdmin(emailToCheck);
                
                user = new User
                {
                    Name = payload.Name?.Trim(),
                    Email = payload.Email?.Trim(),
                    GoogleId = payload.Subject,
                    CreatedAt = DateTime.UtcNow,
                    Role = isAdminEmail ? UserRole.Admin : UserRole.Client,
                    // 🛡️ Round 16: Google ya verificó payload.EmailVerified arriba antes de llegar aquí,
                    // así que podemos confiar en marcar el email como verificado.
                    EmailVerified = true,
                    EmailVerifiedAt = DateTime.UtcNow,
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
            }
            // ✅ NOTA: El código de restauración de usuarios eliminados fue removido
            // Los usuarios eliminados NO pueden restaurarse automáticamente al intentar loguearse
            // Esto es por seguridad y para mantener la integridad de los datos

            var refreshToken = GenerateSecureRefreshToken();
            var refreshTokenEntity = new RefreshToken
            {
                Token = refreshToken,
                UserId = user.Id,
                // 🛡️ Round 15 — R2 FIX: 90 días uniforme con AuthController.RefreshToken.
                ExpiresAt = DateTime.UtcNow.AddDays(90),
                CreatedByIp = "GoogleAuth",
                DeviceInfo = null
            };
            _context.RefreshTokens.Add(refreshTokenEntity);

            await _context.SaveChangesAsync();

            var accessToken = GenerateJwtToken(user);
            var combinedToken = $"{accessToken}|{refreshToken}";

            return (true, combinedToken, user, null);
        }

        // 🛡️ Round 28: extendido con errorCode/errorMessage/detectedCountry para que el controller
        // mapee a HTTP status + mensaje específico al cliente. Patrón consistente con GoogleAuth.
        public async Task<(bool success, string? token, User? user, ExpertProfile? expertProfile,
                          string? errorCode, string? errorMessage, string? detectedCountry)> BecomeExpert(
            int userId,
            BecomeExpertRequestDto request)
        {
            var user = await _context.Users
                .Include(u => u.ExpertProfile)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
            {
                return (false, null, null, null, null, null, null);
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
                return (false, null, null, null, null, null, null);
            }

            // 🛡️ Round 28 MUD-K (GAP-1 fix): permitir re-onboarding tras mudanza.
            // Si el experto pasó por ExpertRelocationService.ExecuteAsync, su perfil queda con
            //   RelocatedFromCountry != null && OnboardingCompleted == false && StripeAccountId == null
            // y NO debemos rechazar — al contrario, el BecomeExpert UPDATEA el ExpertProfile
            // existente con el nuevo país (no inserta otra fila). Sin este check, el experto
            // queda bloqueado para siempre.
            var isRelocating = user.ExpertProfile != null
                            && user.ExpertProfile.RelocatedFromCountry != null
                            && !user.ExpertProfile.OnboardingCompleted
                            && string.IsNullOrEmpty(user.ExpertProfile.StripeAccountId);

            // Validar radio de trabajo (0 = solo en su taller, máx 200 km)
            if (request.WorkRadiusKm.HasValue &&
                (request.WorkRadiusKm.Value < 0 || request.WorkRadiusKm.Value > 200))
            {
                return (false, null, null, null, null, null, null);
            }

            if (user.Role == UserRole.Expert && !isRelocating)
            {
                return (false, null, null, null, null, null, null);
            }

            if (user.ExpertProfile != null && !isRelocating)
            {
                return (false, null, null, null, null, null, null);
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
                
                return (false, null, null, null, null, null, null);
            }

            // Validar Latitude y Longitude
            if (string.IsNullOrEmpty(request.Latitude) || string.IsNullOrEmpty(request.Longitude))
            {
                await _loggingService.LogWarningAsync(
                    message: "BecomeExpert failed: Missing latitude or longitude",
                    details: $"User {userId} failed to become expert: Latitude or Longitude is missing. Latitude: '{request.Latitude}', Longitude: '{request.Longitude}'",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { Action = "BecomeExpert", UserId = userId, Reason = "MissingLatitudeOrLongitude" }
                );
                return (false, null, null, null, null, null, null);
            }

            if (!decimal.TryParse(request.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var latitude))
            {
                await _loggingService.LogWarningAsync(
                    message: "BecomeExpert failed: Invalid latitude format",
                    details: $"User {userId} failed to become expert: Invalid latitude format '{request.Latitude}'",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { Action = "BecomeExpert", UserId = userId, Reason = "InvalidLatitudeFormat", Latitude = request.Latitude }
                );
                return (false, null, null, null, null, null, null);
            }

            if (!decimal.TryParse(request.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var longitude))
            {
                await _loggingService.LogWarningAsync(
                    message: "BecomeExpert failed: Invalid longitude format",
                    details: $"User {userId} failed to become expert: Invalid longitude format '{request.Longitude}'",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { Action = "BecomeExpert", UserId = userId, Reason = "InvalidLongitudeFormat", Longitude = request.Longitude }
                );
                return (false, null, null, null, null, null, null);
            }

            if (latitude < -90m || latitude > 90m)
            {
                await _loggingService.LogWarningAsync(
                    message: "BecomeExpert failed: Latitude out of range",
                    details: $"User {userId} failed to become expert: Latitude {latitude} is out of valid range (-90 to 90)",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { Action = "BecomeExpert", UserId = userId, Reason = "LatitudeOutOfRange", Latitude = latitude }
                );
                return (false, null, null, null, null, null, null);
            }

            if (longitude < -180m || longitude > 180m)
            {
                await _loggingService.LogWarningAsync(
                    message: "BecomeExpert failed: Longitude out of range",
                    details: $"User {userId} failed to become expert: Longitude {longitude} is out of valid range (-180 to 180)",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { Action = "BecomeExpert", UserId = userId, Reason = "LongitudeOutOfRange", Longitude = longitude }
                );
                return (false, null, null, null, null, null, null);
            }

            // 🛡️ Round 28 MUD-AG: para un experto mudado, ProfilePicture puede ser null
            // (preserva la foto del país anterior). Salteamos todo el bloque de upload
            // y reusamos los valores del ExpertProfile existente al final.
            string imageUrl;
            string objectName;
            if (request.ProfilePicture == null)
            {
                // Solo válido para mudanza (controller ya gateó eso). Reusar valores existentes.
                imageUrl = user.ExpertProfile?.ProfilePictureUrl ?? string.Empty;
                objectName = user.ExpertProfile?.ProfilePictureObjectName ?? string.Empty;
                goto skipUpload;
            }

            // Validar tamaño del archivo (5MB límite para imágenes de perfil)
            if (request.ProfilePicture.Length > 5 * 1024 * 1024)
            {
                await _loggingService.LogWarningAsync(
                    message: "BecomeExpert failed: Profile picture too large",
                    details: $"User {userId} failed to become expert: Profile picture size {request.ProfilePicture.Length} bytes exceeds 5MB limit",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { Action = "BecomeExpert", UserId = userId, Reason = "ProfilePictureTooLarge", Size = request.ProfilePicture.Length }
                );
                return (false, null, null, null, null, null, null);
            }

            // Validar tipo de archivo
            var extension = Path.GetExtension(request.ProfilePicture.FileName).ToLowerInvariant();
            if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(extension))
            {
                await _loggingService.LogWarningAsync(
                    message: "BecomeExpert failed: Invalid file extension",
                    details: $"User {userId} failed to become expert: Invalid file extension '{extension}'. File: {request.ProfilePicture.FileName}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { Action = "BecomeExpert", UserId = userId, Reason = "InvalidFileExtension", Extension = extension, FileName = request.ProfilePicture.FileName }
                );
                return (false, null, null, null, null, null, null);
            }
            var bucketName = _configuration["GoogleCloud:BucketName"];
            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            // ✅ MIGRACIÓN: prefijo 'profiles/' para que las lecturas se enruten al bucket PÚBLICO de imágenes.
            objectName = $"profiles/{uniqueFileName}";

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
                        // ✅ MIGRACIÓN: subir a Supabase Storage (bucket público de imágenes) en vez de GCS.
                        await _supabaseStorage.UploadAsync(
                            _supabaseStorage.ImagesBucket,
                            objectName,
                            outputStream,
                            "image/jpeg"
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
                var exceptionDetails = new
                {
                    ExceptionType = ex.GetType().FullName,
                    Message = ex.Message,
                    StackTrace = ex.StackTrace,
                    InnerException = ex.InnerException != null ? new
                    {
                        Type = ex.InnerException.GetType().FullName,
                        Message = ex.InnerException.Message,
                        StackTrace = ex.InnerException.StackTrace
                    } : null,
                    Source = ex.Source,
                    TargetSite = ex.TargetSite?.ToString()
                };
                
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Profile picture upload failed",
                    details: $"Profile picture upload failed for user {userId}. " +
                             $"Exception Type: {ex.GetType().FullName}. " +
                             $"Message: {ex.Message}. " +
                             $"Source: {ex.Source}. " +
                             $"Inner Exception: {(ex.InnerException != null ? ex.InnerException.Message : "None")}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        Action = "ProfilePictureUpload",
                        UserId = userId,
                        ExceptionDetails = exceptionDetails,
                        BucketName = bucketName,
                        ObjectName = objectName,
                        FileName = request.ProfilePicture?.FileName,
                        FileSize = request.ProfilePicture?.Length ?? 0,
                        Success = false
                    }
                );
                
                return (false, null, null, null, null, null, null);
            }

            imageUrl = _supabaseStorage.GetPublicUrl(_supabaseStorage.ImagesBucket, objectName);

            // 🛡️ MUD-AG: punto de aterrizaje cuando un experto mudado conserva foto.
            skipUpload:

            // ✅ CRÍTICO: NO cambiar el rol hasta que el ExpertProfile se guarde exitosamente
            // Si falla después, el usuario quedará con rol Expert pero sin perfil
            // El rol se cambiará DESPUÉS de guardar el ExpertProfile
            
            // ✅ DETECTAR TIMEZONE, COUNTRY Y CITY automáticamente desde coordenadas
            string expertTimezone = "UTC";
            string? expertCountry = null;
            string? expertCity = null;
            
            // 🛡️ Round 28: reintentos con backoff exponencial (0s, 1s, 2s) ANTES de declarar fallo.
            // Antes una sola excepción de Mapbox (timeout, 5xx, DNS) bloqueaba el registro completo.
            // NO añadimos IP-country fallback intencionalmente: Stripe Connect account.country es
            // INMUTABLE tras activación; usar IP-country (VPN/roaming/datacenter) crearía cuentas
            // con jurisdicción equivocada → KYC fallaría irreversiblemente. Conservar el comportamiento
            // de bloquear con error claro + admin notification (vía LogCritical en catch final).
            const int MAX_GEOCODE_ATTEMPTS = 3;
            var geocodeDelays = new[] { 0, 1000, 2000 }; // ms
            Exception? lastGeocodeError = null;

            for (int attempt = 0; attempt < MAX_GEOCODE_ATTEMPTS; attempt++)
            {
                if (geocodeDelays[attempt] > 0) await Task.Delay(geocodeDelays[attempt]);
                try
                {
                    expertTimezone = await _timezoneService.GetTimezoneFromCoordinatesAsync(latitude, longitude);
                    expertCountry = await _timezoneService.GetCountryFromCoordinatesAsync(latitude, longitude);
                    expertCity = await _timezoneService.GetCityFromCoordinatesAsync(latitude, longitude);
                    lastGeocodeError = null;
                    if (attempt > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Mapbox geocoding succeeded on retry",
                            details: $"User {userId}: Mapbox respondió OK en intento {attempt + 1}/{MAX_GEOCODE_ATTEMPTS} para ({latitude}, {longitude}) → {expertCountry}.",
                            userId: userId,
                            source: "UserService.BecomeExpert",
                            relatedEntityType: "User",
                            relatedEntityId: userId,
                            additionalData: new { Attempt = attempt + 1, Country = expertCountry });
                    }
                    break;
                }
                catch (Exception ex) when (attempt < MAX_GEOCODE_ATTEMPTS - 1)
                {
                    lastGeocodeError = ex;
                    // Intermedio: no Critical para no spamear admin con fallos transitorios.
                    await _loggingService.LogInfoAsync(
                        message: "Mapbox geocoding failed, retrying",
                        details: $"User {userId}: intento {attempt + 1}/{MAX_GEOCODE_ATTEMPTS} falló: {ex.Message}. Reintentando en {(geocodeDelays.Length > attempt + 1 ? geocodeDelays[attempt + 1] : 0)}ms.",
                        userId: userId,
                        source: "UserService.BecomeExpert",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new { Attempt = attempt + 1, Error = ex.Message });
                }
                catch (Exception ex)
                {
                    lastGeocodeError = ex;
                }
            }

            if (lastGeocodeError != null)
            {
                // 🛡️ Round 28: SUBIDO a Critical (notifica admin) y solo tras agotar reintentos.
                await _loggingService.LogCriticalAsync(
                    message: "Failed to detect timezone/country/city from coordinates (exhausted retries)",
                    details: $"User {userId}: Mapbox falló {MAX_GEOCODE_ATTEMPTS} intentos consecutivos para ({latitude}, {longitude}). Último error: {lastGeocodeError.Message}. ACCION ADMIN: revisar token Mapbox, rate-limit (100k/mes free), o conectividad outbound desde Render.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: null,
                    additionalData: new {
                        Action = "DetectTimezoneCountryCity",
                        Latitude = latitude,
                        Longitude = longitude,
                        Exception = lastGeocodeError.Message,
                        AttemptsExhausted = MAX_GEOCODE_ATTEMPTS
                    }
                );
            }

            // 🔧 GATE DE PAÍS (P3, paso 1): NO promover a Expert si el país detectado no puede recibir pagos.
            // Sin esto se crea un "experto zombie": perfil publicado y contratable que NUNCA puede cobrar
            // (el paso 2, onboarding Stripe, devolvería BadRequest). Usamos la MISMA whitelist que el paso 2.
            //
            // 🛡️ Round 28: SEPARADO en 2 ramas para distinguir causa al admin:
            //   - country=null (Mapbox no devolvió) → Critical (infra down, notify admin via email)
            //   - country no soportado → Information (negocio normal, no satura admin)
            // Antes ambas causas mezcladas en un solo Warning silencioso que no avisaba a nadie.
            if (string.IsNullOrWhiteSpace(expertCountry))
            {
                await _loggingService.LogCriticalAsync(
                    message: "BecomeExpert ABORTADO: no se pudo detectar pais (Mapbox)",
                    details: $"User {userId} ({user.Email}): geocoding devolvió country=null para coords ({latitude}, {longitude}). " +
                             "Posible causa: Mapbox token mal configurado, timeout, 5xx, coords en zona sin coverage (océano/Antártida), o respuesta sin context.country. " +
                             "Registro abortado para no crear experto-zombie. ACCION ADMIN: revisar logs de TimezoneService y estado de api.mapbox.com.",
                    userId: userId,
                    source: "UserService.BecomeExpert.CountryGate",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new
                    {
                        Action = "CountryGate",
                        UserId = userId,
                        UserEmail = user.Email,
                        Latitude = latitude,
                        Longitude = longitude,
                        Cause = "GeocodingReturnedNull"
                    }
                );
                // 🛡️ Round 28: limpiar foto huérfana en Supabase (se subió antes del gate de país).
                // Sin esto, cada rechazo deja una imagen pagando storage hasta intervención manual.
                try
                {
                    await _supabaseStorage.DeleteAsync(_supabaseStorage.ImagesBucket, objectName);
                }
                catch (Exception cleanupEx)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Could not cleanup profile picture after CountryGate (null) rejection",
                        details: $"User {userId}: foto {objectName} quedó huérfana en Supabase. Error: {cleanupEx.Message}. Limpiar manualmente.",
                        userId: userId,
                        source: "UserService.BecomeExpert.OrphanPhotoCleanup");
                }
                return (false, null, null, null,
                        BecomeExpertErrorCodes.CountryDetectionFailed,
                        "No pudimos verificar tu ubicación. Inténtalo en unos minutos o contacta con soporte.",
                        null);
            }

            if (!SupportedConnectCountries.IsSupported(expertCountry))
            {
                await _loggingService.LogInfoAsync(
                    message: "BecomeExpert rechazado: pais fuera de whitelist Stripe Connect",
                    details: $"User {userId}: country={expertCountry} no soportado para separate charges & transfers desde plataforma EEA. " +
                             "Se evita crear un experto que no podria cobrar. Esto es negocio normal (no fallo de sistema).",
                    userId: userId,
                    source: "UserService.BecomeExpert.CountryGate",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new
                    {
                        Action = "CountryGate",
                        UserId = userId,
                        DetectedCountry = expertCountry,
                        Latitude = latitude,
                        Longitude = longitude,
                        Cause = "CountryNotInWhitelist"
                    }
                );

                // 🛡️ Round 28: limpiar foto huérfana en Supabase (rechazo por país no soportado).
                try
                {
                    await _supabaseStorage.DeleteAsync(_supabaseStorage.ImagesBucket, objectName);
                }
                catch (Exception cleanupEx)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Could not cleanup profile picture after CountryGate (not-supported) rejection",
                        details: $"User {userId}: foto {objectName} quedó huérfana en Supabase tras rechazo por país {expertCountry}. Error: {cleanupEx.Message}. Limpiar manualmente.",
                        userId: userId,
                        source: "UserService.BecomeExpert.OrphanPhotoCleanup");
                }

                return (false, null, null, null,
                        BecomeExpertErrorCodes.CountryNotSupported,
                        $"Tu país ({expertCountry}) aún no está disponible para recibir pagos en la plataforma. Elige una ubicación en un país soportado o contacta con soporte.",
                        expertCountry);
            }

            // 🛡️ Round 28 MUD-K (GAP-1 fix): si el experto se está re-onboardeando tras una
            // mudanza (ExpertProfile ya existe con RelocatedFromCountry != null), actualizamos
            // la fila existente en lugar de crear una nueva — preservando Id (FKs) y reviews.
            ExpertProfile expertProfile;
            if (user.ExpertProfile != null && user.ExpertProfile.RelocatedFromCountry != null)
            {
                expertProfile = user.ExpertProfile;
                // 🛡️ MUD-AG: solo sobrescribir si hubo nuevo upload (imageUrl !=
                // existingProfile.ProfilePictureUrl). Si el experto reutilizó su foto
                // anterior, no tocamos nada para no leak el blob de Supabase.
                if (request.ProfilePicture != null)
                {
                    expertProfile.ProfilePictureUrl = imageUrl;
                    expertProfile.ProfilePictureObjectName = objectName;
                }
                expertProfile.Description = request.Description;
                expertProfile.Latitude = request.Latitude;
                expertProfile.Longitude = request.Longitude;
                expertProfile.Timezone = expertTimezone;
                expertProfile.Country = expertCountry;
                expertProfile.City = expertCity;
                expertProfile.StripeAccountId = null;
                expertProfile.PendingStripeAccountId = null;
                expertProfile.StripeStatus = StripeStatus.NotRequested;
                expertProfile.StripeStatusDetails = null;
                expertProfile.OnboardingCompleted = false;
                expertProfile.IsOnVacation = false; // ya hay nuevo país; activable de nuevo
                if (request.WorkRadiusKm.HasValue)
                {
                    expertProfile.WorkRadiusKm = request.WorkRadiusKm.Value;
                }
                // RelocatedFromCountry/RelocatedAt SE PRESERVAN — son histórico de auditoría.
            }
            else
            {
                expertProfile = new ExpertProfile
                {
                    UserId = user.Id,
                    ProfilePictureUrl = imageUrl,
                    ProfilePictureObjectName = objectName,
                    Description = request.Description,
                    Latitude = request.Latitude,
                    Longitude = request.Longitude,
                    Timezone = expertTimezone,
                    Country = expertCountry,
                    City = expertCity,
                    WorkRadiusKm = request.WorkRadiusKm ?? 100,
                    StripeAccountId = null,
                    CreatedAt = DateTime.UtcNow
                };
                _context.ExpertProfiles.Add(expertProfile);
            }

            // 🛡️ V7 FIX: atomicidad ExpertProfile + Role en un solo SaveChanges. Antes
            // eran 2 SaveChanges separados — si el segundo fallaba, ExpertProfile quedaba en BD
            // pero User con Role=Client (inconsistente). Combinar cambios y persistir UNA vez:
            // si falla, ChangeTracker descarta AMBOS.
            user.Role = UserRole.Expert;

            // ✅ FIX: Manejar ObjectDisposedException y DbUpdateException específicamente
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
            {
                // Si la conexión está disposed dentro de DbUpdateException
                var ex = dbEx.InnerException as ObjectDisposedException;
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Connection disposed during ExpertProfile save (DbUpdateException)",
                    details: $"User {userId} failed to become expert: Connection was disposed during SaveChangesAsync. " +
                             $"Exception: {ex?.Message ?? dbEx.Message}. Attempting recovery with new context.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: null,
                    additionalData: new { 
                        Action = "SaveExpertProfile", 
                        UserId = userId,
                        ExceptionType = dbEx.GetType().FullName,
                        InnerExceptionType = ex?.GetType().FullName,
                        ExceptionMessage = dbEx.Message,
                        InnerExceptionMessage = ex?.Message
                    }
                );
                
                // Intentar con un nuevo contexto usando scope
                using var scope = _serviceScopeFactory.CreateScope();
                var scopedContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                
                // Obtener el usuario en el nuevo contexto
                var scopedUser = await scopedContext.Users.FindAsync(userId);
                if (scopedUser == null)
                {
                    return (false, null, null, null, null, null, null);
                }
                
                // Re-attach el expertProfile al nuevo contexto
                scopedContext.ExpertProfiles.Add(new ExpertProfile
                {
                    UserId = expertProfile.UserId,
                    ProfilePictureUrl = expertProfile.ProfilePictureUrl,
                    ProfilePictureObjectName = expertProfile.ProfilePictureObjectName,
                    Description = expertProfile.Description,
                    Latitude = expertProfile.Latitude,
                    Longitude = expertProfile.Longitude,
                    Timezone = expertProfile.Timezone,
                    Country = expertProfile.Country,
                    City = expertProfile.City,
                    WorkRadiusKm = expertProfile.WorkRadiusKm,
                    StripeAccountId = expertProfile.StripeAccountId,
                    CreatedAt = expertProfile.CreatedAt
                });
                
                await scopedContext.SaveChangesAsync();
                
                // Cambiar el rol DESPUÉS de guardar el perfil
                scopedUser.Role = UserRole.Expert;
                await scopedContext.SaveChangesAsync();
                
                // Obtener el ID del perfil guardado
                var savedProfile = await scopedContext.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);
                
                if (savedProfile != null)
                {
                    expertProfile = savedProfile;
                    user.Role = UserRole.Expert; // Actualizar también en el contexto original
                }
                else
                {
                    // Si aún falla, retornar error
                    return (false, null, null, null, null, null, null);
                }
            }
            catch (ObjectDisposedException ex)
            {
                // Si la conexión está disposed, intentar crear un nuevo contexto y reintentar
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Connection disposed during ExpertProfile save",
                    details: $"User {userId} failed to become expert: Connection was disposed during SaveChangesAsync. " +
                             $"Exception: {ex.Message}. Attempting recovery with new context.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: null,
                    additionalData: new { 
                        Action = "SaveExpertProfile", 
                        UserId = userId,
                        ExceptionType = ex.GetType().FullName,
                        ExceptionMessage = ex.Message
                    }
                );
                
                // Intentar con un nuevo contexto usando scope
                using var scope = _serviceScopeFactory.CreateScope();
                var scopedContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                
                // Re-attach el expertProfile al nuevo contexto
                scopedContext.ExpertProfiles.Add(new ExpertProfile
                {
                    UserId = expertProfile.UserId,
                    ProfilePictureUrl = expertProfile.ProfilePictureUrl,
                    ProfilePictureObjectName = expertProfile.ProfilePictureObjectName,
                    Description = expertProfile.Description,
                    Latitude = expertProfile.Latitude,
                    Longitude = expertProfile.Longitude,
                    Timezone = expertProfile.Timezone,
                    Country = expertProfile.Country,
                    City = expertProfile.City,
                    WorkRadiusKm = expertProfile.WorkRadiusKm,
                    StripeAccountId = expertProfile.StripeAccountId,
                    CreatedAt = expertProfile.CreatedAt
                });
                
                await scopedContext.SaveChangesAsync();
                
                // Obtener el ID del perfil guardado
                var savedProfile = await scopedContext.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);
                
                if (savedProfile != null)
                {
                    expertProfile = savedProfile;
                }
                else
                {
                    // Si aún falla, retornar error
                    return (false, null, null, null, null, null, null);
                }
            }
            catch (DbUpdateException dbEx)
            {
                // Si es DbUpdateException, verificar si el inner exception es ObjectDisposedException
                if (dbEx.InnerException is ObjectDisposedException)
                {
                    // Ya se manejó arriba, pero por si acaso reintentar aquí también
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: DbUpdateException with ObjectDisposedException during ExpertProfile save",
                        details: $"User {userId} failed to become expert: DbUpdateException with ObjectDisposedException. " +
                                 $"Exception: {dbEx.Message}. Inner: {dbEx.InnerException?.Message}",
                        userId: userId,
                        source: "UserService.BecomeExpert",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: null,
                        additionalData: new { 
                            Action = "SaveExpertProfile", 
                            UserId = userId,
                            ExceptionType = dbEx.GetType().FullName,
                            InnerExceptionType = dbEx.InnerException?.GetType().FullName,
                            ExceptionMessage = dbEx.Message,
                            InnerExceptionMessage = dbEx.InnerException?.Message,
                            StackTrace = dbEx.StackTrace
                        }
                    );
                    return (false, null, null, null, null, null, null);
                }
                else
                {
                    // Otro tipo de DbUpdateException
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: DbUpdateException during ExpertProfile save",
                        details: $"User {userId} failed to become expert: DbUpdateException. Exception: {dbEx.Message}",
                        userId: userId,
                        source: "UserService.BecomeExpert",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: null,
                        additionalData: new { 
                            Action = "SaveExpertProfile", 
                            UserId = userId,
                            ExceptionType = dbEx.GetType().FullName,
                            ExceptionMessage = dbEx.Message,
                            InnerExceptionMessage = dbEx.InnerException?.Message,
                            StackTrace = dbEx.StackTrace
                        }
                    );
                    return (false, null, null, null, null, null, null);
                }
            }
            catch (Exception ex)
            {
                // Cualquier otro error
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Failed to save ExpertProfile",
                    details: $"User {userId} failed to become expert: Error saving ExpertProfile. Exception: {ex.Message}",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: null,
                    additionalData: new { 
                        Action = "SaveExpertProfile", 
                        UserId = userId,
                        ExceptionType = ex.GetType().FullName,
                        ExceptionMessage = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                return (false, null, null, null, null, null, null);
            }

            // ✅ VALIDACIÓN: La disponibilidad horaria es OBLIGATORIA al crear un perfil de experto
            if (request.AvailabilityDaysOfWeek == null || request.AvailabilityDaysOfWeek.Count == 0 ||
                string.IsNullOrEmpty(request.AvailabilityStartTime) || string.IsNullOrEmpty(request.AvailabilityEndTime))
            {
                // Eliminar el perfil creado si falta la disponibilidad
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                
                await _loggingService.LogWarningAsync(
                    message: "BecomeExpert failed: Missing availability data",
                    details: $"User {userId} failed to become expert: Missing availability data. " +
                             $"DaysOfWeek: {(request.AvailabilityDaysOfWeek == null ? "null" : request.AvailabilityDaysOfWeek.Count.ToString())}, " +
                             $"StartTime: '{request.AvailabilityStartTime}', EndTime: '{request.AvailabilityEndTime}'. " +
                             $"ExpertProfile {expertProfile.Id} was created but removed due to missing availability.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: expertProfile.Id,
                    additionalData: new { 
                        Action = "BecomeExpert", 
                        UserId = userId, 
                        ExpertProfileId = expertProfile.Id,
                        Reason = "MissingAvailabilityData",
                        AvailabilityDaysOfWeek = request.AvailabilityDaysOfWeek?.Count ?? 0,
                        AvailabilityStartTime = request.AvailabilityStartTime,
                        AvailabilityEndTime = request.AvailabilityEndTime
                    }
                );
                return (false, null, null, null, null, null, null);
            }

            // Parsear y validar tiempos
            if (!TimeSpan.TryParse(request.AvailabilityStartTime, out var startTime) ||
                !TimeSpan.TryParse(request.AvailabilityEndTime, out var endTime))
            {
                // Eliminar el perfil creado si los tiempos son inválidos
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                
                await _loggingService.LogWarningAsync(
                    message: "BecomeExpert failed: Invalid time format",
                    details: $"User {userId} failed to become expert: Invalid time format. " +
                             $"StartTime: '{request.AvailabilityStartTime}', EndTime: '{request.AvailabilityEndTime}'. " +
                             $"ExpertProfile {expertProfile.Id} was created but removed due to invalid time format.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: expertProfile.Id,
                    additionalData: new { 
                        Action = "BecomeExpert", 
                        UserId = userId, 
                        ExpertProfileId = expertProfile.Id,
                        Reason = "InvalidTimeFormat",
                        AvailabilityStartTime = request.AvailabilityStartTime,
                        AvailabilityEndTime = request.AvailabilityEndTime
                    }
                );
                return (false, null, null, null, null, null, null);
            }

            // Validar días válidos
            var validDays = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
            var invalidDays = request.AvailabilityDaysOfWeek.Except(validDays, StringComparer.OrdinalIgnoreCase).ToList();
            
            if (invalidDays.Any())
            {
                // Eliminar el perfil creado si los días son inválidos
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                
                await _loggingService.LogWarningAsync(
                    message: "BecomeExpert failed: Invalid days of week",
                    details: $"User {userId} failed to become expert: Invalid days of week: {string.Join(", ", invalidDays)}. " +
                             $"ExpertProfile {expertProfile.Id} was created but removed due to invalid days.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: expertProfile.Id,
                    additionalData: new { 
                        Action = "BecomeExpert", 
                        UserId = userId, 
                        ExpertProfileId = expertProfile.Id,
                        Reason = "InvalidDaysOfWeek",
                        InvalidDays = invalidDays,
                        AllDays = request.AvailabilityDaysOfWeek
                    }
                );
                return (false, null, null, null, null, null, null);
            }

            if (startTime >= endTime)
            {
                // Eliminar el perfil creado si el rango de tiempo es inválido
                _context.ExpertProfiles.Remove(expertProfile);
                await _context.SaveChangesAsync();
                
                await _loggingService.LogWarningAsync(
                    message: "BecomeExpert failed: Start time must be before end time",
                    details: $"User {userId} failed to become expert: Start time {startTime} must be before end time {endTime}. " +
                             $"ExpertProfile {expertProfile.Id} was created but removed due to invalid time range.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: expertProfile.Id,
                    additionalData: new { 
                        Action = "BecomeExpert", 
                        UserId = userId, 
                        ExpertProfileId = expertProfile.Id,
                        Reason = "InvalidTimeRange",
                        StartTime = startTime.ToString(),
                        EndTime = endTime.ToString()
                    }
                );
                return (false, null, null, null, null, null, null);
            }

            // Crear disponibilidad horaria inicial (obligatoria)
            try
            {
                var now = DateTime.UtcNow;
                // 🛡️ Round 28 MUD-AG: para mudanza, desactivar la availability row anterior
                // antes de insertar la nueva. Sin esto, ExpertProfile 3 quedaría con DOS rows
                // activas (IsActive=true, EffectiveTo=null) → search filters/admin reports
                // doble-cuentan. Mismo patrón que UpdateExpertProfile:1725-1731.
                if (user.ExpertProfile != null && user.ExpertProfile.RelocatedFromCountry != null)
                {
                    var staleAvailabilities = await _context.ExpertAvailabilities
                        .Where(ea => ea.ExpertId == expertProfile.Id && ea.IsActive && ea.EffectiveTo == null)
                        .ToListAsync();
                    foreach (var stale in staleAvailabilities)
                    {
                        stale.IsActive = false;
                        stale.EffectiveTo = now;
                        stale.UpdatedAt = now;
                    }
                }
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
                
                var exceptionDetails = new
                {
                    ExceptionType = ex.GetType().FullName,
                    Message = ex.Message,
                    StackTrace = ex.StackTrace,
                    InnerException = ex.InnerException != null ? new
                    {
                        Type = ex.InnerException.GetType().FullName,
                        Message = ex.InnerException.Message,
                        StackTrace = ex.InnerException.StackTrace
                    } : null,
                    Source = ex.Source,
                    TargetSite = ex.TargetSite?.ToString()
                };
                
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Failed to create expert availability",
                    details: $"User {userId} failed to become expert: Exception while creating availability. " +
                             $"Exception Type: {ex.GetType().FullName}. " +
                             $"Message: {ex.Message}. " +
                             $"ExpertProfile {expertProfile.Id} was created but removed due to availability creation failure.",
                    userId: userId,
                    source: "UserService.BecomeExpert",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: expertProfile.Id,
                    additionalData: new { 
                        Action = "CreateExpertAvailability", 
                        UserId = userId, 
                        ExpertProfileId = expertProfile.Id,
                        ExceptionDetails = exceptionDetails,
                        AvailabilityDaysOfWeek = request.AvailabilityDaysOfWeek,
                        AvailabilityStartTime = request.AvailabilityStartTime,
                        AvailabilityEndTime = request.AvailabilityEndTime,
                        StartTime = startTime.ToString(),
                        EndTime = endTime.ToString()
                    }
                );
                
                return (false, null, null, null, null, null, null);
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

            return (true, combinedToken, user, expertProfile, null, null, expertCountry);
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
                ProfilePictureUrl = expertProfile.ProfilePictureUrl ?? string.Empty,
                StripeAccountId = expertProfile.StripeAccountId,
                Description = expertProfile.Description,
                CreatedAt = expertProfile.CreatedAt,
                User = new UserDto
                {
                    Id = expertProfile.User.Id,
                    Name = expertProfile.User.Name,
                    Email = expertProfile.User.Email,
                    ProfilePictureUrl = null // ✅ FIX: User no tiene ProfilePictureUrl, está en ExpertProfile
                },
                Reviews = new List<ReviewDto>(), // Inicializar lista vacía para mantener compatibilidad
                Latitude = expertProfile.Latitude,
                Longitude = expertProfile.Longitude,
                WorkRadiusKm = expertProfile.WorkRadiusKm,
                StripeStatus = expertProfile.StripeStatus,
                StripeStatusDetails = expertProfile.StripeStatusDetails,
                OnboardingCompleted = expertProfile.OnboardingCompleted,
                IsOnVacation = expertProfile.IsOnVacation,
                CurrentAvailability = availabilityDto,
                // ✅ FUTURE REQUIREMENTS
                StripeFutureRequirements = expertProfile.StripeFutureRequirements,
                StripeFutureDueAt = expertProfile.StripeFutureDueAt,
                // ✅ INTERNACIONALIZACIÓN
                Timezone = expertProfile.Timezone,
                Country = expertProfile.Country,
                City = expertProfile.City,
                // 🛡️ Round 28 MUD-W: señaliza relocation al frontend.
                RelocatedFromCountry = expertProfile.RelocatedFromCountry,
                RelocatedAt = expertProfile.RelocatedAt,
            };
        }


        // 🛡️ Round 28: extendido con errorCode/errorMessage/detectedCountry como BecomeExpert.
        public async Task<(bool Success, ExpertProfileDto? UpdatedProfile,
                          string? errorCode, string? errorMessage, string? detectedCountry)> UpdateExpertProfile(int userId, UpdateExpertProfileRequestDto request)
        {
            try
            {
                // Buscar el perfil de experto existente
                var expertProfile = await _context.ExpertProfiles
                    .Include(ep => ep.User)
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return (false, null, null, null, null);
                }

                // Validar coordenadas
                if (!decimal.TryParse(request.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var latitude) ||
                    latitude < -90m || latitude > 90m)
                {
                    return (false, null, null, null, null);
                }

                if (!decimal.TryParse(request.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var longitude) ||
                    longitude < -180m || longitude > 180m)
                {
                    return (false, null, null, null, null);
                }

                // Validar radio de trabajo (0 = solo en su taller, máx 200 km)
                if (request.WorkRadiusKm.HasValue &&
                    (request.WorkRadiusKm.Value < 0 || request.WorkRadiusKm.Value > 200))
                {
                    return (false, null, null, null, null);
                }

                // Actualizar los campos básicos
                expertProfile.Description = request.Description;
                if (request.WorkRadiusKm.HasValue)
                {
                    expertProfile.WorkRadiusKm = request.WorkRadiusKm.Value;
                }
                
                // ✅ DETECTAR TIMEZONE Y COUNTRY si cambian las coordenadas
                var coordinatesChanged = expertProfile.Latitude != request.Latitude ||
                                         expertProfile.Longitude != request.Longitude;

                // 🛡️ E3 FIX: si cambian las coordenadas, detectar país NUEVO ANTES de tocar nada y
                // validar contra la whitelist de Stripe Connect. BecomeExpert (línea 670) ya valida
                // al registro, pero UpdateExpertProfile no lo hacía → un experto registrado en ES
                // podía mover sus coords a un país fuera de la whitelist (JP/AR/etc.) y todos los
                // payouts futuros fallarían. Rechazamos el update completo antes de persistir nada.
                string? detectedTimezone = null;
                string? detectedCountry = null;
                string? detectedCity = null;
                if (coordinatesChanged)
                {
                    try
                    {
                        detectedTimezone = await _timezoneService.GetTimezoneFromCoordinatesAsync(latitude, longitude);
                        detectedCountry = await _timezoneService.GetCountryFromCoordinatesAsync(latitude, longitude);
                        detectedCity = await _timezoneService.GetCityFromCoordinatesAsync(latitude, longitude);

                        if (!string.IsNullOrEmpty(detectedCountry)
                            && !SupportedConnectCountries.IsSupported(detectedCountry))
                        {
                            // 🛡️ Round 28: BAJADO a Information (negocio normal, no infra).
                            // País real fuera de whitelist no satura admin via email.
                            await _loggingService.LogInfoAsync(
                                message: "E3: Expert update rechazado — coords resuelven a país fuera de whitelist",
                                details: $"UserId {userId}: nuevas coords ({latitude},{longitude}) → {detectedCountry} no está en la whitelist de Stripe Connect. Update abortado. (Negocio normal, no fallo de sistema.)",
                                userId: userId,
                                source: "UserService.UpdateExpertProfile.E3",
                                relatedEntityType: "ExpertProfile",
                                relatedEntityId: expertProfile.Id,
                                additionalData: new { Latitude = latitude, Longitude = longitude, DetectedCountry = detectedCountry, CurrentCountry = expertProfile.Country, Cause = "CountryNotInWhitelist" });
                            return (false, null,
                                    BecomeExpertErrorCodes.CountryNotSupported,
                                    $"Tu nueva ubicación ({detectedCountry}) no está disponible para recibir pagos. Elige otra dentro de los países soportados.",
                                    detectedCountry);
                        }

                        // 🛡️ Round 28 — STRIPE_COUNTRY_LOCKED gate: si el experto tiene cuenta
                        // Stripe Connect creada (incluso si no completó onboarding), su Account.country
                        // YA está fijado en Stripe e es INMUTABLE. Permitir que expertProfile.Country
                        // diverja del Stripe Account.country crea currency_mismatch en transfers.
                        // 🛡️ MUD-3: condición original requería OnboardingCompleted=true, pero el acct
                        // ya tiene country fijado desde el momento de su creación (AccountCreateOptions).
                        // Un experto que creó acct US y abandonó el onboarding podía mover su pin a ES
                        // sin restricción → divergencia silenciosa. Ahora basta con que exista
                        // StripeAccountId para activar el gate.
                        if (!string.IsNullOrEmpty(detectedCountry)
                            && !string.IsNullOrEmpty(expertProfile.StripeAccountId))
                        {
                            try
                            {
                                var stripeAccountService = new Stripe.AccountService();
                                var stripeAccount = await stripeAccountService.GetAsync(expertProfile.StripeAccountId);
                                if (!string.IsNullOrEmpty(stripeAccount?.Country)
                                    && !string.Equals(stripeAccount.Country, detectedCountry, StringComparison.OrdinalIgnoreCase))
                                {
                                    await _loggingService.LogInfoAsync(
                                        message: "STRIPE_COUNTRY_LOCKED: rechazado cambio de país por inmutabilidad de Stripe Connect",
                                        details: $"UserId {userId}: nuevas coords ({latitude},{longitude}) detectan país {detectedCountry}, pero Stripe Account {expertProfile.StripeAccountId} tiene country={stripeAccount.Country} (inmutable). Update abortado. Para mudarse, el experto debe cerrar su cuenta y volver a registrarse en el nuevo país.",
                                        userId: userId,
                                        source: "UserService.UpdateExpertProfile.StripeCountryLocked",
                                        relatedEntityType: "ExpertProfile",
                                        relatedEntityId: expertProfile.Id,
                                        additionalData: new { Latitude = latitude, Longitude = longitude, DetectedCountry = detectedCountry, StripeAccountCountry = stripeAccount.Country, StripeAccountId = expertProfile.StripeAccountId });
                                    return (false, null,
                                            BecomeExpertErrorCodes.StripeCountryLocked,
                                            $"Tu cuenta Stripe está vinculada a {stripeAccount.Country.ToUpperInvariant()} y no se puede cambiar a {detectedCountry}. Stripe no permite mover una cuenta entre países. Para hacerlo, ve al panel de experto → 'Mudarme a otro país' y completa el asistente; cerraremos tu cuenta Stripe actual y podrás iniciar onboarding fresco en el nuevo país (tu User, historial de cliente y reseñas se preservan).",
                                            detectedCountry);
                                }
                            }
                            catch (Stripe.StripeException stripeReadEx) when (stripeReadEx.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                // Stripe Account ya no existe (404) — sin gate posible, permitir el update.
                                // El UserController/AccountDeletion debería haber limpiado StripeAccountId, pero
                                // si no, log defensivo y continuar.
                                await _loggingService.LogWarningAsync(
                                    message: "STRIPE_COUNTRY_LOCKED gate skipped: Stripe Account 404",
                                    details: $"UserId {userId} ExpertProfile {expertProfile.Id}: StripeAccountId={expertProfile.StripeAccountId} no existe en Stripe (404). Gate de country-locked omitido — update procederá.",
                                    userId: userId,
                                    source: "UserService.UpdateExpertProfile.StripeCountryLockedSkipped",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfile.Id);
                            }
                            catch (Exception stripeReadEx)
                            {
                                // Falla de red/Stripe — no bloquear update por esto, pero loguear para auditoría.
                                await _loggingService.LogWarningAsync(
                                    message: "STRIPE_COUNTRY_LOCKED gate skipped: Stripe Account read failed",
                                    details: $"UserId {userId} ExpertProfile {expertProfile.Id}: no se pudo leer Stripe Account {expertProfile.StripeAccountId} para validar país: {stripeReadEx.Message}. Gate omitido — update procederá; riesgo: si el país en Stripe difiere, transfers fallarán con currency_mismatch.",
                                    userId: userId,
                                    source: "UserService.UpdateExpertProfile.StripeCountryLockedSkipped",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfile.Id);
                            }
                        }
                    }
                    catch (Exception detectEx)
                    {
                        // 🛡️ Round 28: SUBIDO a Critical. Mapbox tiró excepción durante un update de
                        // coords (infra down). Admin debe enterarse via email — solo Critical notifica.
                        await _loggingService.LogCriticalAsync(
                            message: "Failed to detect country from new coordinates (E3 gate)",
                            details: $"Could not detect country for ({latitude},{longitude}): {detectEx.Message}. Coords ACEPTADAS pero Timezone/Country/City NO se actualizan. ACCION ADMIN: revisar Mapbox/token/rate-limit.",
                            userId: userId,
                            source: "UserService.UpdateExpertProfile",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: expertProfile.Id,
                            additionalData: new { Latitude = latitude, Longitude = longitude, Exception = detectEx.Message });
                        detectedCountry = null; // marcar como "no detectado"
                    }
                }

                // 🛡️ Round 28 — Sprint 3: solo persistir coords si el geocoding tuvo éxito o las
                // coords no cambiaron. Antes persistíamos lat/lng aunque Mapbox fallase, dejando
                // el perfil con coords en otro país pero Country/Timezone obsoletos.
                if (!coordinatesChanged)
                {
                    expertProfile.Latitude = request.Latitude;
                    expertProfile.Longitude = request.Longitude;
                }
                else if (detectedCountry != null)
                {
                    // Geocoding OK y país aceptado → persistir TODO el set (coords + tz + country + city) atómico.
                    expertProfile.Latitude = request.Latitude;
                    expertProfile.Longitude = request.Longitude;
                }
                // Si coordinatesChanged && detectedCountry == null → NO persistir nada (las coords
                // quedan con sus valores anteriores). El bloque siguiente solo se ejecuta si hubo éxito.

                // Si cambian las coordenadas, aplicar timezone/country/city (ya validados arriba)
                if (coordinatesChanged && detectedCountry != null)
                {
                    try
                    {
                        expertProfile.Timezone = detectedTimezone;
                        expertProfile.Country = detectedCountry;
                        expertProfile.City = detectedCity;
                        
                        await _loggingService.LogInfoAsync(
                            message: "Timezone, country and city updated from coordinates",
                            details: $"Updated timezone to {detectedTimezone}, country to {detectedCountry} and city to {detectedCity} for coordinates ({latitude}, {longitude})",
                            userId: userId,
                            source: "UserService.UpdateExpertProfile",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: expertProfile.Id,
                            additionalData: new { 
                                Action = "UpdateTimezoneCountryCity",
                                Latitude = latitude,
                                Longitude = longitude,
                                Timezone = detectedTimezone,
                                Country = detectedCountry,
                                City = detectedCity
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        // Si falla la detección, mantener los valores actuales y loguear el error
                        await _loggingService.LogWarningAsync(
                            message: "Failed to detect timezone/country/city from new coordinates",
                            details: $"Could not detect timezone/country/city for new coordinates ({latitude}, {longitude}): {ex.Message}. Keeping existing values.",
                            userId: userId,
                            source: "UserService.UpdateExpertProfile",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: expertProfile.Id,
                            additionalData: new { 
                                Action = "DetectTimezoneCountryCity",
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
                        return (false, null, null, null, null);
                    }

                    // Validar tipo de archivo
                    var extension = Path.GetExtension(request.ProfilePicture.FileName).ToLowerInvariant();
                    if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(extension))
                    {
                        return (false, null, null, null, null);
                    }

                    var bucketName = _configuration["GoogleCloud:BucketName"];
                    var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                    // ✅ MIGRACIÓN: prefijo 'profiles/' para que las lecturas se enruten al bucket PÚBLICO de imágenes.
                    var objectName = $"profiles/{uniqueFileName}";

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
                                // ✅ MIGRACIÓN: subir a Supabase Storage (bucket público de imágenes) en vez de GCS.
                                await _supabaseStorage.UploadAsync(
                                    _supabaseStorage.ImagesBucket,
                                    objectName,
                                    outputStream,
                                    "image/jpeg"
                                );
                            }
                        }

                        // Eliminar la imagen anterior si existe (ahora en Supabase Storage)
                        if (!string.IsNullOrEmpty(expertProfile.ProfilePictureObjectName))
                        {
                            try
                            {
                                await _supabaseStorage.DeleteAsync(_supabaseStorage.ImagesBucket, expertProfile.ProfilePictureObjectName);
                            }
                            catch (Exception ex)
                            {
                            }
                        }

                        // Actualizar URLs de la nueva imagen
                        var imageUrl = _supabaseStorage.GetPublicUrl(_supabaseStorage.ImagesBucket, objectName);
                        expertProfile.ProfilePictureUrl = imageUrl;
                        expertProfile.ProfilePictureObjectName = objectName;
                    }
                    catch (Exception ex)
                    {
                        return (false, null, null, null, null);
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
                    return (false, null, null, null, null);
                }

                if (!hasAvailabilityProvided)
                {
                    return (false, null, null, null, null);
                }

                // Parsear y validar tiempos
                if (!TimeSpan.TryParse(request.AvailabilityStartTime, out var startTime) ||
                    !TimeSpan.TryParse(request.AvailabilityEndTime, out var endTime))
                {
                    return (false, null, null, null, null);
                }

                // Validar días válidos
                var validDays = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
                var invalidDays = request.AvailabilityDaysOfWeek?.Except(validDays, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
                
                if (invalidDays.Any())
                {
                    return (false, null, null, null, null);
                }

                if (startTime >= endTime)
                {
                    return (false, null, null, null, null);
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
                    return (false, null, null, null, null);
                }

                await _context.SaveChangesAsync();

                // Devolver el perfil actualizado
                // ✅ Obtener la nueva disponibilidad creada
                var createdAvailability = await _context.ExpertAvailabilities
                    .Where(ea => ea.ExpertId == expertProfile.Id && ea.IsActive && ea.EffectiveTo == null)
                    .OrderByDescending(ea => ea.EffectiveFrom)
                    .FirstOrDefaultAsync();

                CurrentExpertAvailabilityDto? availabilityDto = null;
                if (createdAvailability != null)
                {
                    var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(createdAvailability.DaysOfWeek) ?? new List<string>();
                    availabilityDto = new CurrentExpertAvailabilityDto
                    {
                        Id = createdAvailability.Id,
                        DaysOfWeek = daysOfWeek,
                        StartTime = createdAvailability.StartTime,
                        EndTime = createdAvailability.EndTime,
                        EffectiveFrom = createdAvailability.EffectiveFrom
                    };
                }

                var updatedProfileDto = new ExpertProfileDto
                {
                    Id = expertProfile.Id,
                    ProfilePictureUrl = expertProfile.ProfilePictureUrl ?? string.Empty,
                    StripeAccountId = expertProfile.StripeAccountId,
                    Description = expertProfile.Description,
                    CreatedAt = expertProfile.CreatedAt,
                    User = new UserDto
                    {
                        Id = expertProfile.User.Id,
                        Name = expertProfile.User.Name,
                        Email = expertProfile.User.Email,
                        ProfilePictureUrl = null // ✅ FIX: User no tiene ProfilePictureUrl, está en ExpertProfile
                    },
                    Reviews = new List<ReviewDto>(), // Mantener compatibilidad
                    Latitude = expertProfile.Latitude,
                    Longitude = expertProfile.Longitude,
                    WorkRadiusKm = expertProfile.WorkRadiusKm,
                    StripeStatus = expertProfile.StripeStatus,
                    StripeStatusDetails = expertProfile.StripeStatusDetails,
                    OnboardingCompleted = expertProfile.OnboardingCompleted,
                    IsOnVacation = expertProfile.IsOnVacation,
                    CurrentAvailability = availabilityDto,
                    // ✅ FUTURE REQUIREMENTS
                    StripeFutureRequirements = expertProfile.StripeFutureRequirements,
                    StripeFutureDueAt = expertProfile.StripeFutureDueAt,
                    // ✅ INTERNACIONALIZACIÓN
                    Timezone = expertProfile.Timezone,
                    Country = expertProfile.Country,
                    City = expertProfile.City,
                    // 🛡️ MUD-W
                    RelocatedFromCountry = expertProfile.RelocatedFromCountry,
                    RelocatedAt = expertProfile.RelocatedAt,
                };
                return (true, updatedProfileDto, null, null, expertProfile.Country);
            }
            catch (Exception ex)
            {
                return (false, null, null, null, null);
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

                // 🛡️ Round 28 MUD-AI: si está TURNING ON vacation (false → true), bloquear si hay
                // hires activos. Sin esto: experto con 10 hires confirmados activa vacaciones,
                // no-shows masivos en sus appointments confirmados → 10× 95/0/5 refunds + Stripe
                // fees + reseñas negativas + soporte. El relocation preflight ya hace este check;
                // copiar el patrón aquí.
                if (!expertProfile.IsOnVacation)
                {
                    var activeHiresCount = await _context.SearchHires
                        .Include(h => h.Status)
                        .CountAsync(h => h.ExpertId == userId
                                      && h.Status != null
                                      && !h.Status.IsFinalizationStatus);
                    if (activeHiresCount > 0)
                    {
                        await _loggingService.LogWarningAsync(
                            message: "ToggleVacationMode blocked — active hires",
                            details: $"UserId {userId} intentó activar vacaciones con {activeHiresCount} hires activos. Bloqueado para evitar no-shows masivos. Debe finalizar/cancelar los hires primero.",
                            userId: userId,
                            source: "UserService.ToggleVacationMode.ActiveHiresGuard",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: expertProfile.Id,
                            additionalData: new { ActiveHires = activeHiresCount },
                            notifyUser: true);
                        return (false, expertProfile.IsOnVacation); // sigue OFF
                    }
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
                // 🛡️ Round 28 MUD-AF: 24h matches marketplace standard (eBay/Etsy). El refresh
                // token (90d, rotation + reuse detection) cubre la seguridad. Con 1h el usuario
                // perdía sesión mid-flow muy a menudo si el silent-refresh fallaba por timing
                // de Chrome (setTimeout throttled cuando tab background) o hop entre rutas.
                expires: DateTime.UtcNow.AddHours(24),
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
                // 🛡️ Round 15 — R2 FIX: 90 días (era 30d). Uniforme con AuthController.
                // Justificación: app es mobile-first, mejor UX que el usuario no re-loguee cada mes.
                // Rotación + detección de reuso siguen activas → seguridad real intacta.
                ExpiresAt = DateTime.UtcNow.AddDays(90),
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

