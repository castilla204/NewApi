using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using newApi.Common;
using newApi.Services;
using newApi.ScrapperGateway.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using Microsoft.EntityFrameworkCore;
using Google.Apis.Auth;

[Route("api/[controller]")]
[ApiController]
[EnableRateLimiting("api")] // ✅ SEGURIDAD: 100 requests/minuto por IP
// TODO P3-8: envolver con ConcurrencyRetryHelper los SaveChangesAsync de UpdateProfile y
// endpoints UPDATE-heavy. Con xmin token activo (P2-4) un conflicto produce 500 directo.
public class UserController : ControllerBase
{
        private readonly UserService _userService;
        private readonly IAuthorizationServices _authService;
        private readonly ILoggingService _loggingService;
        private readonly AppDbContext _context;
        private readonly ISignedUrlService _signedUrlService;
        private readonly IServiceScopeFactory _serviceScopeFactory;

    public UserController(
        UserService userService,
        IAuthorizationServices authService,
        ILoggingService loggingService,
        AppDbContext context,
        ISignedUrlService signedUrlService,
        IServiceScopeFactory serviceScopeFactory)
    {
            _userService = userService;
            _authService = authService;
            _loggingService = loggingService;
            _context = context;
            _signedUrlService = signedUrlService;
            _serviceScopeFactory = serviceScopeFactory;
    }

    [Authorize]
    [HttpGet("all")]
    public async Task<IActionResult> GetAllUsers([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        try
        {
            // 🔐 SEGURIDAD: Verificar rol usando AuthorizationService
            if (!_authService.IsAdmin(User))
            {
                return Unauthorized(new { message = "Admin access required" });
            }

            // Validar parámetros
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 50) pageSize = 20;

            var (users, totalCount) = await _userService.GetAllUsers(page, pageSize);
            
            return Ok(new
            {
                users,
                pagination = new
                {
                    page,
                    pageSize,
                    totalCount,
                    totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                    hasNextPage = page * pageSize < totalCount,
                    hasPreviousPage = page > 1
                }
            });
        }
        catch (Exception ex)
        {
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

    // ✅ COMENTADO: Verificación de teléfono ya no es necesaria
    /*
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
            return StatusCode(500, new { message = "Failed to send verification code" });
        }
    }
    */

    // ✅ COMENTADO: Verificación de teléfono ya no es necesaria
    /*
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
            return StatusCode(500, new { message = "Failed to verify code" });
        }
    }
    */

    [HttpPost("google-auth")]
    [EnableRateLimiting("auth")] // ✅ SEGURIDAD: 30 intentos cada 5 minutos por IP
    public async Task<IActionResult> GoogleAuth([FromBody] GoogleAuthDto request)
    {
        var requestId = Guid.NewGuid().ToString();
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        try
        {
            // ✅ VALIDACIÓN: Verificar que el request no sea null
            if (request == null)
            {
                // ✅ OPTIMIZACIÓN: Logging en background con scope propio para evitar disposed objects
                BackgroundLogger.FireAndForget(async () =>
                /* 🛡️ MUD-BA: helper captura excepciones en ContinueWith para no perderlas silenciosamente */
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                    await loggingService.LogWarningAsync(
                        message: "Google Auth request is null",
                        details: $"Google Auth request is null. RequestId: {requestId}, IP: {remoteIp}",
                        userId: null,
                        source: "UserController.GoogleAuth",
                        relatedEntityType: "Auth",
                        additionalData: new { RequestId = requestId, RemoteIp = remoteIp }
                    );
                });
                return BadRequest(new { 
                    message = "Invalid request", 
                    error = "Request body is required",
                    requestId = requestId
                });
            }

            // ✅ VALIDACIÓN: Verificar campos requeridos
            if (string.IsNullOrWhiteSpace(request.AccessToken))
            {
                // ✅ OPTIMIZACIÓN: Logging en background con scope propio
                var email = request.Email; // Capturar antes de Task.Run
                BackgroundLogger.FireAndForget(async () =>
                /* 🛡️ MUD-BA: helper captura excepciones en ContinueWith para no perderlas silenciosamente */
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                    await loggingService.LogWarningAsync(
                        message: "Google Auth request missing AccessToken",
                        details: $"Google Auth request missing AccessToken. RequestId: {requestId}, IP: {remoteIp}, Email: {email}",
                        userId: null,
                        source: "UserController.GoogleAuth",
                        relatedEntityType: "Auth",
                        additionalData: new { RequestId = requestId, RemoteIp = remoteIp, RequestEmail = email }
                    );
                });
                return BadRequest(new { 
                    message = "Invalid request", 
                    error = "AccessToken is required",
                    requestId = requestId
                });
            }

            // ✅ OPTIMIZACIÓN: Autenticación sin logging síncrono
            var (success, token, user, errorReason) = await _userService.GoogleAuth(request);
            
            if (!success)
            {
                // ✅ OPTIMIZACIÓN: Logging en background con scope propio
                var email = request.Email; // Capturar antes de Task.Run
                var googleId = request.GoogleId; // Capturar antes de Task.Run
                BackgroundLogger.FireAndForget(async () =>
                /* 🛡️ MUD-BA: helper captura excepciones en ContinueWith para no perderlas silenciosamente */
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                    await loggingService.LogWarningAsync(
                        message: "Google Auth failed",
                        details: $"Google Auth failed. RequestId: {requestId}, IP: {remoteIp}, Email: {email}, GoogleId: {googleId}, Reason: {errorReason ?? "unknown"}",
                        userId: null,
                        source: "UserController.GoogleAuth",
                        relatedEntityType: "Auth",
                        additionalData: new { RequestId = requestId, RemoteIp = remoteIp, RequestEmail = email, RequestGoogleId = googleId, ErrorReason = errorReason }
                    );
                });
                
                // ✅ Mensaje específico según el motivo del error
                string message;
                string error;
                
                if (errorReason == "account_deleted")
                {
                    message = "No puedes acceder a tu cuenta";
                    error = "Tu cuenta fue eliminada. Si eliminaste tu cuenta, no puedes volver a acceder con este correo electrónico. Si crees que esto es un error, contacta con soporte.";
                }
                else if (errorReason == "account_blocked")
                {
                    message = "Cuenta bloqueada";
                    error = "Tu cuenta ha sido bloqueada. Por favor, contacta con soporte para más información.";
                }
                else
                {
                    message = "Authentication failed";
                    error = "Invalid Google token or authentication error";
                }
                
                return BadRequest(new { 
                    message = message, 
                    error = error,
                    requestId = requestId
                });
            }

            if (user == null)
            {
                // ✅ OPTIMIZACIÓN: Logging en background con scope propio
                BackgroundLogger.FireAndForget(async () =>
                /* 🛡️ MUD-BA: helper captura excepciones en ContinueWith para no perderlas silenciosamente */
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                    await loggingService.LogErrorAsync(
                        message: "Google Auth returned success but user is null",
                        details: $"Google Auth returned success but user is null. RequestId: {requestId}, IP: {remoteIp}",
                        userId: null,
                        source: "UserController.GoogleAuth",
                        relatedEntityType: "Auth",
                        additionalData: new { RequestId = requestId, RemoteIp = remoteIp }
                    );
                });
                return StatusCode(500, new { 
                    message = "Internal server error", 
                    error = "User object is null after successful authentication",
                    requestId = requestId
                });
            }

            // ✅ OPTIMIZACIÓN: Respuesta inmediata, logging en background
            var response = Ok(new
            {
                token,
                user = new
                {
                    user.Id,
                    user.Name,
                    user.Email,
                    user.PhoneVerified,
                    // 🖼️ Avatar (sembrado desde Google en el primer login si estaba vacío).
                    user.ProfilePictureUrl,
                    Role = user.Role.ToString()
                },
                requestId = requestId
            });

            // ✅ OPTIMIZACIÓN: Logging exitoso en background con scope propio
            var userId = user.Id; // Capturar antes de Task.Run
            var userEmail = user.Email; // Capturar antes de Task.Run
            BackgroundLogger.FireAndForget(async () =>
            /* 🛡️ MUD-BA: helper captura excepciones para no perderlas silenciosamente */
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                await loggingService.LogInfoAsync(
                    message: "Google Auth successful",
                    details: $"Google Auth successful. RequestId: {requestId}, UserId: {userId}, Email: {userEmail}, IP: {remoteIp}",
                    userId: userId,
                    source: "UserController.GoogleAuth",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { RequestId = requestId, UserId = userId, Email = userEmail, RemoteIp = remoteIp }
                );
            });

            return response;
        }
        catch (InvalidJwtException jwtEx)
        {
            // ✅ OPTIMIZACIÓN: Logging en background con scope propio
            var requestEmail = request?.Email; // Capturar antes de Task.Run
            var errorMessage = jwtEx.Message; // Capturar antes de Task.Run
            var errorType = jwtEx.GetType().Name; // Capturar antes de Task.Run
            BackgroundLogger.FireAndForget(async () =>
            /* 🛡️ MUD-BA: helper captura excepciones para no perderlas silenciosamente */
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                await loggingService.LogErrorAsync(
                    message: "Invalid JWT token in Google Auth",
                    details: $"Invalid JWT token in Google Auth. RequestId: {requestId}, IP: {remoteIp}, Email: {requestEmail}, Error: {errorMessage}",
                    userId: null,
                    source: "UserController.GoogleAuth",
                    relatedEntityType: "Auth",
                    additionalData: new { 
                        RequestId = requestId,
                        RemoteIp = remoteIp,
                        RequestEmail = requestEmail,
                        Error = errorMessage,
                        ErrorType = errorType
                    }
                );
            });
            
            return BadRequest(new { 
                message = "Invalid Google token", 
                error = "The provided Google token is invalid or expired",
                details = jwtEx.Message,
                requestId = requestId
            });
        }
        catch (Exception ex)
        {
            // ✅ OPTIMIZACIÓN: Logging en background con scope propio
            var requestEmail = request?.Email; // Capturar antes de Task.Run
            var requestGoogleId = request?.GoogleId; // Capturar antes de Task.Run
            var errorMessage = ex.Message; // Capturar antes de Task.Run
            var errorType = ex.GetType().Name; // Capturar antes de Task.Run
            var innerExceptionMessage = ex.InnerException?.Message; // Capturar antes de Task.Run
            BackgroundLogger.FireAndForget(async () =>
            /* 🛡️ MUD-BA: helper captura excepciones para no perderlas silenciosamente */
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                await loggingService.LogErrorAsync(
                    message: "Unexpected error during Google authentication",
                    details: $"Unexpected error during Google authentication. RequestId: {requestId}, IP: {remoteIp}, Email: {requestEmail}, GoogleId: {requestGoogleId}, ErrorType: {errorType}, Error: {errorMessage}",
                    userId: null,
                    source: "UserController.GoogleAuth",
                    relatedEntityType: "Auth",
                    additionalData: new { 
                        RequestId = requestId,
                        RemoteIp = remoteIp,
                        RequestEmail = requestEmail,
                        RequestGoogleId = requestGoogleId,
                        Error = errorMessage,
                        ErrorType = errorType,
                        InnerException = innerExceptionMessage
                    }
                );
            });
            
            return StatusCode(500, new { 
                message = "An error occurred during authentication", 
                error = ex.Message,
                errorType = ex.GetType().Name,
                requestId = requestId
            });
        }
    }

    public class BecomeExpertMinimalDto { public string Country { get; set; } = string.Empty; }

    /// <summary>
    /// 🧩 STRIPE-FIRST: alta de experto mínima — solo el país. Crea el perfil con
    /// placeholders y Role=Expert para que el frontend lance el onboarding de Stripe
    /// de inmediato. El perfil se completa en el panel (invisible hasta entonces).
    /// </summary>
    [Authorize]
    [HttpPost("become-expert-minimal")]
    public async Task<IActionResult> BecomeExpertMinimal([FromBody] BecomeExpertMinimalDto request)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            return Unauthorized(new { message = "Invalid user identification" });

        if (string.IsNullOrWhiteSpace(request?.Country))
            return BadRequest(new { message = "El país es obligatorio (no se puede cambiar después en Stripe)." });

        var (success, token, errorCode, errorMessage) = await _userService.BecomeExpertMinimal(userId, request.Country);
        if (!success)
            return BadRequest(new { message = errorMessage ?? "No se pudo completar el alta.", errorCode });

        return Ok(new { message = "Alta de experto creada. Continúa con Stripe.", token });
    }

    /// <summary>
    /// 🖼️ Sube/cambia el avatar de la cuenta (foto de perfil). Para expertos, esta misma
    /// imagen es su foto pública del marketplace (avatar unificado). Multipart, campo
    /// 'profilePicture' (JPG/PNG ≤5MB). Devuelve la URL pública nueva.
    /// </summary>
    [Authorize]
    [HttpPost("avatar")]
    [RequestSizeLimit(6 * 1024 * 1024)] // 6MB: 5MB de imagen + margen multipart
    public async Task<IActionResult> SetAvatar([FromForm] IFormFile profilePicture)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            return Unauthorized(new { message = "Invalid user identification" });

        var (success, url, errorCode) = await _userService.SetAvatarAsync(userId, profilePicture);
        if (!success)
        {
            var message = errorCode switch
            {
                "file_required" => "Debes seleccionar una imagen.",
                "file_too_large" => "La imagen no puede superar los 5MB.",
                "invalid_extension" => "Solo se permiten imágenes JPG o PNG.",
                "user_not_found" => "Usuario no encontrado.",
                _ => "No se pudo actualizar la foto de perfil."
            };
            return BadRequest(new { message, errorCode });
        }

        return Ok(new { profilePictureUrl = url });
    }

    /// <summary>
    /// 🖼️ Quita el avatar de la cuenta. Bloqueado para expertos (su foto pública es
    /// obligatoria): un experto solo puede reemplazarla vía POST avatar.
    /// </summary>
    [Authorize]
    [HttpDelete("avatar")]
    public async Task<IActionResult> RemoveAvatar()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            return Unauthorized(new { message = "Invalid user identification" });

        var (success, errorCode) = await _userService.RemoveAvatarAsync(userId);
        if (!success)
        {
            var message = errorCode switch
            {
                "expert_photo_required" => "Como experto, tu foto es pública y obligatoria: solo puedes cambiarla, no quitarla.",
                "user_not_found" => "Usuario no encontrado.",
                _ => "No se pudo quitar la foto de perfil."
            };
            return BadRequest(new { message, errorCode });
        }

        return Ok(new { profilePictureUrl = (string?)null });
    }

    /// <summary>
    /// ✏️ Actualiza el nombre de la cuenta del usuario autenticado. El email NO es editable
    /// (es la identidad de login), solo el nombre.
    /// </summary>
    [Authorize]
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileDto dto)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            return Unauthorized(new { message = "Invalid user identification" });

        var (success, name, errorCode) = await _userService.UpdateNameAsync(userId, dto?.Name ?? string.Empty);
        if (!success)
        {
            var message = errorCode switch
            {
                "name_required" => "El nombre no puede estar vacío.",
                "name_too_long" => "El nombre no puede superar los 100 caracteres.",
                "user_not_found" => "Usuario no encontrado.",
                _ => "No se pudo actualizar el nombre."
            };
            return BadRequest(new { message, errorCode });
        }

        return Ok(new { name });
    }

    public class UpdateProfileDto { public string Name { get; set; } = string.Empty; }

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

            // 🛡️ Round 28 MUD-AG: cargar User+Profile PRIMERO para detectar isRelocating.
            // Para mudanza, ProfilePicture es opcional (se reusa la foto del perfil anterior).
            var user = await _context.Users
                .Include(u => u.ExpertProfile)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
            {
                return BadRequest(new { message = "User not found" });
            }

            // 🛡️ Round 28 MUD-AC + MUD-X (GAP-1 fix): permitir re-onboarding tras mudanza.
            // El check del UserService MUD-K nunca se alcanza porque este controller corta
            // primero. Replicamos el mismo isRelocating bypass aquí — sin esto el experto
            // mudado queda atascado en "You are already an expert" y NO puede completar
            // el onboarding del nuevo país. MUD-X: también aceptar StripeStatus=NotRequested
            // (por si el campo no se limpió pero el status indica no-active).
            var isRelocating = user.ExpertProfile != null
                            && user.ExpertProfile.RelocatedFromCountry != null
                            && !user.ExpertProfile.OnboardingCompleted
                            && (string.IsNullOrEmpty(user.ExpertProfile.StripeAccountId)
                                // (ya hay using de newApi.DataLayer.Models.PostGresModels — el
                                // cualificador relativo "DataLayer.…" no resuelve desde este namespace)
                                || user.ExpertProfile.StripeStatus == StripeStatus.NotRequested);

            if (user.Role == UserRole.Expert && !isRelocating)
            {
                return BadRequest(new { message = "You are already an expert" });
            }

            if (user.ExpertProfile != null && !isRelocating)
            {
                return BadRequest(new { message = "You already have an expert profile" });
            }

            // 🛡️ Round 28 MUD-AG: Photo validation MOVIDO AQUÍ desde arriba.
            // - First-time expert: ProfilePicture REQUERIDA.
            // - Relocating expert: opcional — si null, se preserva la foto del país anterior.
            // - Si se envía nueva foto: validar tamaño + extensión normalmente.
            if (request.ProfilePicture == null)
            {
                if (!isRelocating || string.IsNullOrEmpty(user.ExpertProfile?.ProfilePictureUrl))
                {
                    return BadRequest(new { message = "Profile picture is required" });
                }
                // isRelocating && existing URL exists → OK, se preserva en el service.
            }
            else
            {
                if (request.ProfilePicture.Length > 5 * 1024 * 1024)
                {
                    return BadRequest(new { message = "Profile picture must be smaller than 5MB" });
                }
                var extension = Path.GetExtension(request.ProfilePicture.FileName).ToLowerInvariant();
                if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(extension))
                {
                    return BadRequest(new { message = "Profile picture must be a JPG or PNG image" });
                }
            }

            // ✅ VALIDACIÓN: la descripción del experto debe tener entre 30 y 60 caracteres
            if (string.IsNullOrWhiteSpace(request.Description))
            {
                return BadRequest(new { message = "La descripción es requerida" });
            }

            var descriptionLength = request.Description.Trim().Length;
            if (descriptionLength < 30 || descriptionLength > 60)
            {
                return BadRequest(new { message = "La descripción del experto debe tener entre 30 y 60 caracteres" });
            }

            // Validar Latitude y Longitude
            if (string.IsNullOrEmpty(request.Latitude) || string.IsNullOrEmpty(request.Longitude))
            {
                return BadRequest(new { message = "Latitude and Longitude are required" });
            }

            if (!decimal.TryParse(request.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var latitude))
            {
                return BadRequest(new { message = "Invalid latitude format. Must be a valid number." });
            }

            if (!decimal.TryParse(request.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var longitude))
            {
                return BadRequest(new { message = "Invalid longitude format. Must be a valid number." });
            }

            if (latitude < -90m || latitude > 90m)
            {
                return BadRequest(new { message = "Latitude must be between -90 and 90 degrees" });
            }

            if (longitude < -180m || longitude > 180m)
            {
                return BadRequest(new { message = "Longitude must be between -180 and 180 degrees" });
            }

            // Verificar contrataciones activas
            var activeContractsAsClient = await _context.SearchHires
                .Include(sh => sh.Status)
                .Where(sh => sh.ClientId == userId && sh.Status != null && !sh.Status.IsFinalizationStatus)
                .ToListAsync();

            if (activeContractsAsClient.Any())
            {
                return BadRequest(new { 
                    message = $"No puedes convertirte en experto mientras tengas contrataciones activas como cliente. " +
                             $"Tienes {activeContractsAsClient.Count} contratación(es) activa(s) que deben estar finalizadas antes de convertirte en experto. " +
                             $"Debes usar una cuenta distinta (no registrada como experto) para contratar servicios."
                });
            }

            // ✅ Validar disponibilidad horaria (obligatoria)
            if (request.AvailabilityDaysOfWeek == null || request.AvailabilityDaysOfWeek.Count == 0)
            {
                return BadRequest(new { message = "Availability days of week are required. Please select at least one day." });
            }

            if (string.IsNullOrEmpty(request.AvailabilityStartTime))
            {
                return BadRequest(new { message = "Availability start time is required (format: HH:mm, e.g., 09:00)" });
            }

            if (string.IsNullOrEmpty(request.AvailabilityEndTime))
            {
                return BadRequest(new { message = "Availability end time is required (format: HH:mm, e.g., 18:00)" });
            }

            // Validar formato de tiempos
            if (!TimeSpan.TryParse(request.AvailabilityStartTime, out var startTime))
            {
                return BadRequest(new { message = "Invalid availability start time format. Must be HH:mm (e.g., 09:00)" });
            }

            if (!TimeSpan.TryParse(request.AvailabilityEndTime, out var endTime))
            {
                return BadRequest(new { message = "Invalid availability end time format. Must be HH:mm (e.g., 18:00)" });
            }

            if (startTime >= endTime)
            {
                return BadRequest(new { message = "Availability start time must be before end time" });
            }

            // Validar días válidos
            var validDays = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
            var invalidDays = request.AvailabilityDaysOfWeek.Except(validDays, StringComparer.OrdinalIgnoreCase).ToList();
            if (invalidDays.Any())
            {
                return BadRequest(new { 
                    message = $"Invalid days of week: {string.Join(", ", invalidDays)}. Valid days are: Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday" 
                });
            }

            var (success, token, userResult, expertProfile, errorCode, errorMessage, detectedCountry)
                = await _userService.BecomeExpert(userId, request);
            if (!success)
            {
                // 🛡️ Round 28: log conciso (el service ya logueó el detalle específico arriba).
                await _loggingService.LogWarningAsync(
                    message: $"BecomeExpert failed: {errorCode ?? "UNKNOWN"}",
                    details: $"User {userId} failed to become expert. ErrorCode={errorCode ?? "n/a"}. DetectedCountry={detectedCountry ?? "n/a"}. El service registró el detalle.",
                    userId: userId,
                    source: "UserController.BecomeExpert",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new {
                        Action = "BecomeExpert",
                        UserId = userId,
                        Success = false,
                        ErrorCode = errorCode,
                        DetectedCountry = detectedCountry
                    }
                );

                // 🛡️ Round 28: mapeo errorCode → HTTP status + mensaje específico.
                // Reemplaza al mensaje fósil "Google Cloud Storage configuration issue".
                var statusCode = errorCode switch
                {
                    BecomeExpertErrorCodes.CountryDetectionFailed => StatusCodes.Status503ServiceUnavailable,
                    BecomeExpertErrorCodes.ProfilePictureUploadFailed => StatusCodes.Status500InternalServerError,
                    BecomeExpertErrorCodes.AvailabilityCreationFailed => StatusCodes.Status500InternalServerError,
                    BecomeExpertErrorCodes.DatabaseError => StatusCodes.Status500InternalServerError,
                    BecomeExpertErrorCodes.InternalError => StatusCodes.Status500InternalServerError,
                    BecomeExpertErrorCodes.UserBlocked => StatusCodes.Status403Forbidden,
                    BecomeExpertErrorCodes.UserNotFound => StatusCodes.Status404NotFound,
                    _ => StatusCodes.Status400BadRequest,
                };

                return StatusCode(statusCode, new {
                    message = errorMessage ?? "No se pudo completar el registro como experto. Revisa los datos e inténtalo de nuevo.",
                    errorCode = errorCode ?? "UNKNOWN_ERROR",
                    detectedCountry = detectedCountry
                });
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
                          ProfilePictureUrl = ResolveProfilePictureUrl(expertProfile),
                          Description = expertProfile.Description,
                          StripeAccountId = expertProfile.StripeAccountId,
                          CreatedAt = expertProfile.CreatedAt,
                          Latitude = expertProfile.Latitude,
                          Longitude = expertProfile.Longitude,
                          StripeStatus = expertProfile.StripeStatus.ToString(), // 🛡️ C2: string, no entero
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
            // 🚨 LOG CRÍTICO: Error al convertirse en experto
            var userIdForLog = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int userIdValue) ? userIdValue : (int?)null;
            
            // Obtener información detallada de la excepción
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
                TargetSite = ex.TargetSite?.ToString(),
                Data = ex.Data?.Count > 0 ? ex.Data : null
            };
            
            await _loggingService.LogCriticalAsync(
                message: "CRITICAL: Exception during become expert",
                details: $"User {userIdForLog} encountered exception while becoming expert. " +
                        $"Exception Type: {ex.GetType().FullName}. " +
                        $"Message: {ex.Message}. " +
                        $"Source: {ex.Source}. " +
                        $"Inner Exception: {(ex.InnerException != null ? ex.InnerException.Message : "None")}",
                userId: userIdForLog,
                source: "UserController.BecomeExpert",
                relatedEntityType: "User",
                relatedEntityId: userIdForLog,
                additionalData: new { 
                    Action = "BecomeExpert",
                    UserId = userIdForLog,
                    ExceptionDetails = exceptionDetails,
                    RequestData = new {
                        HasProfilePicture = request?.ProfilePicture != null,
                        ProfilePictureSize = request?.ProfilePicture?.Length ?? 0,
                        ProfilePictureFileName = request?.ProfilePicture?.FileName,
                        Description = request?.Description,
                        Latitude = request?.Latitude,
                        Longitude = request?.Longitude,
                        AvailabilityDaysOfWeek = request?.AvailabilityDaysOfWeek?.Count ?? 0,
                        AvailabilityStartTime = request?.AvailabilityStartTime,
                        AvailabilityEndTime = request?.AvailabilityEndTime
                    }
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

            // ✅ VALIDACIÓN: la descripción del experto debe tener entre 30 y 60 caracteres
            var descriptionLength = request.Description.Trim().Length;
            if (descriptionLength < 30 || descriptionLength > 60)
            {
                return BadRequest(new { message = "La descripción del experto debe tener entre 30 y 60 caracteres" });
            }

            if (string.IsNullOrWhiteSpace(request.Latitude) || string.IsNullOrWhiteSpace(request.Longitude))
            {
                return BadRequest(new { message = "Latitud y Longitud son requeridas" });
            }
            var (success, updatedProfile, errorCode, errorMessage, detectedCountry)
                = await _userService.UpdateExpertProfile(userId, request);
            if (!success)
            {
                // 🛡️ Round 28: mapeo errorCode → HTTP status + mensaje específico.
                var statusCode = errorCode switch
                {
                    BecomeExpertErrorCodes.CountryDetectionFailed => StatusCodes.Status503ServiceUnavailable,
                    BecomeExpertErrorCodes.ProfilePictureUploadFailed => StatusCodes.Status500InternalServerError,
                    BecomeExpertErrorCodes.DatabaseError => StatusCodes.Status500InternalServerError,
                    BecomeExpertErrorCodes.ExpertProfileNotFound => StatusCodes.Status404NotFound,
                    _ => StatusCodes.Status400BadRequest,
                };
                return StatusCode(statusCode, new {
                    message = errorMessage ?? "No se pudo actualizar el perfil. Revisa los datos e inténtalo de nuevo.",
                    errorCode = errorCode ?? "UNKNOWN_ERROR",
                    detectedCountry = detectedCountry
                });
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
            return StatusCode(500, new { message = "Failed to update expert profile", detail = ex.Message });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 📱 SMS-CENTRAL: estado y verificación OTP del teléfono.
    // Un FIJO no recibe SMS → smsCapable=false y el panel pide cargar un móvil
    // y validar el código recibido (Twilio Verify).
    // ─────────────────────────────────────────────────────────────────────────

    [Authorize]
    [HttpGet("phone-status")]
    public async Task<IActionResult> GetPhoneStatus([FromServices] newApi.DataLayer.Models.AppDbContext db)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            return Unauthorized(new { message = "Invalid user identification" });

        var u = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.PhoneNumber, x.PhoneVerified, x.PhoneLineType, x.PhoneVerificationSource })
            .FirstOrDefaultAsync();
        if (u == null) return NotFound();

        var isLandline = string.Equals(u.PhoneLineType, "landline", StringComparison.OrdinalIgnoreCase);
        return Ok(new
        {
            phoneNumber = u.PhoneNumber,
            phoneVerified = u.PhoneVerified,
            phoneLineType = u.PhoneLineType,
            phoneVerificationSource = u.PhoneVerificationSource,
            // Válido para SMS = verificado, con número, y NO fijo.
            smsCapable = u.PhoneVerified && !string.IsNullOrWhiteSpace(u.PhoneNumber) && !isLandline,
        });
    }

    /// <summary>
    /// 🧩 FUENTE ÚNICA DE VERDAD de visibilidad del experto. Devuelve el desglose con
    /// EXACTAMENTE las mismas condiciones que el filtro SQL de los listados
    /// (SearchServiceService): Stripe operativo + sin vacaciones + foto + descripción +
    /// ubicación + móvil verificado (no fijo). El banner y el checklist del panel
    /// consumen esto — así front y back no pueden volver a descoordinarse.
    /// </summary>
    [Authorize(Roles = "Expert")]
    [HttpGet("expert-visibility")]
    public async Task<IActionResult> GetExpertVisibility([FromServices] newApi.DataLayer.Models.AppDbContext db)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            return Unauthorized(new { message = "Invalid user identification" });

        var data = await db.ExpertProfiles.AsNoTracking()
            .Where(ep => ep.UserId == userId)
            .Select(ep => new
            {
                ep.StripeStatus,
                ep.OnboardingCompleted,
                ep.IsOnVacation,
                ep.Description,
                ep.ProfilePictureUrl,
                ep.Latitude,
                ep.User.PhoneVerified,
                ep.User.PhoneNumber,
                ep.User.PhoneLineType,
            })
            .FirstOrDefaultAsync();
        if (data == null) return NotFound(new { message = "Expert profile not found" });

        // ⚠️ MISMAS condiciones que el Where de SearchServiceService — si cambias una,
        // cambia la otra.
        var stripeOk = (data.StripeStatus == StripeStatus.Approved && data.OnboardingCompleted)
                       || data.StripeStatus == StripeStatus.PendingVerification;
        var hasDescription = !string.IsNullOrEmpty(data.Description);
        var hasPhoto = !string.IsNullOrEmpty(data.ProfilePictureUrl);
        var hasLocation = !string.IsNullOrEmpty(data.Latitude);
        // NB: el gate SQL NO exige PhoneNumber no vacío (los expertos seed se backfillean
        // verificados SIN número, para que jamás se envíe un SMS a un número real ajeno).
        // En el flujo real el OTP siempre guarda el número: verified ⇒ número presente.
        var phoneOk = data.PhoneVerified
                      && !string.Equals(data.PhoneLineType, "landline", StringComparison.OrdinalIgnoreCase);
        var notOnVacation = !data.IsOnVacation;

        var missing = new List<string>();
        if (!stripeOk) missing.Add("stripe");
        if (!hasPhoto) missing.Add("photo");
        if (!hasDescription) missing.Add("description");
        if (!hasLocation) missing.Add("location");
        if (!phoneOk) missing.Add("phone");
        if (!notOnVacation) missing.Add("vacation");

        return Ok(new
        {
            isVisible = missing.Count == 0,
            stripeOk,
            hasPhoto,
            hasDescription,
            hasLocation,
            phoneOk,
            notOnVacation,
            missing,
        });
    }

    public class PhoneCodeRequestDto { public string PhoneNumber { get; set; } = string.Empty; }
    public class PhoneVerifyRequestDto { public string PhoneNumber { get; set; } = string.Empty; public string Code { get; set; } = string.Empty; }

    [Authorize]
    [HttpPost("phone/send-code")]
    public async Task<IActionResult> SendPhoneCode([FromBody] PhoneCodeRequestDto request, [FromServices] IPhoneLookupService phoneLookup)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            return Unauthorized(new { message = "Invalid user identification" });

        // 📱 Normalizar a E.164 — Twilio exige "+34681634037"; el usuario suele
        // escribir "681634037" o "681 634 037" y el envío fallaba en silencio.
        var phone = PhoneLookupService.NormalizeToE164(request?.PhoneNumber ?? "");
        if (phone == null)
            return BadRequest(new { message = "Introduce un número de móvil válido (ej. 681634037 o +34681634037)." });

        // 📱 Rechazar FIJOS antes de gastar un OTP: no recibirían el SMS.
        var lineType = await phoneLookup.GetLineTypeAsync(phone);
        if (string.Equals(lineType, "landline", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Ese número parece un teléfono FIJO y no puede recibir SMS. Introduce un número MÓVIL.", lineType });

        var sent = await _userService.SendVerification(userId, phone);
        if (!sent)
            return StatusCode(503, new { message = "No se pudo enviar el código de verificación. Inténtalo de nuevo en unos minutos." });

        return Ok(new { message = "Código enviado por SMS.", lineType });
    }

    [Authorize]
    [HttpPost("phone/verify-code")]
    public async Task<IActionResult> VerifyPhoneCode([FromBody] PhoneVerifyRequestDto request)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            return Unauthorized(new { message = "Invalid user identification" });

        if (string.IsNullOrWhiteSpace(request?.PhoneNumber) || string.IsNullOrWhiteSpace(request?.Code))
            return BadRequest(new { message = "Faltan el teléfono o el código." });

        // Misma normalización que send-code para que coincida con user.PhoneNumber.
        var normalizedPhone = PhoneLookupService.NormalizeToE164(request.PhoneNumber) ?? request.PhoneNumber.Trim();
        var (success, _, _) = await _userService.VerifyCode(userId, normalizedPhone, request.Code.Trim());
        if (!success)
            return BadRequest(new { message = "Código incorrecto o caducado. Vuelve a intentarlo." });

        return Ok(new { message = "Teléfono verificado correctamente.", phoneVerified = true, phoneLineType = "mobile" });
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

            // ✅ EMAIL DE PRUEBA: Enviar email cuando se activa/desactiva el modo vacaciones
            var message = isOnVacation ? "Modo vacaciones activado" : "Modo vacaciones desactivado";
            var details = isOnVacation 
                ? "Has activado el modo vacaciones. No recibirás nuevas contrataciones mientras esté activo."
                : "Has desactivado el modo vacaciones. Ya puedes recibir nuevas contrataciones.";
            
            await _loggingService.LogInfoAsync(
                message: message,
                details: details,
                userId: userId,
                source: "UserController.ToggleVacationMode",
                relatedEntityType: "ExpertProfile",
                relatedEntityId: null,
                notifyUser: true
            );

            return Ok(new { 
                message = message,
                isOnVacation = isOnVacation 
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Failed to toggle vacation mode" });
        }
    }

    /// <summary>
    /// 🌍 Round 22: Permite al usuario fijar la divisa que verá en la interfaz.
    /// Persistente entre sesiones. Null en BD = auto-detección por IP/país.
    /// Valida contra <see cref="SupportedCurrenciesList"/>.
    /// </summary>
    [HttpPost("preferred-currency")]
    [Authorize]
    public async Task<IActionResult> SetPreferredCurrency([FromBody] PreferredCurrencyDto dto)
    {
        try
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Currency))
            {
                return BadRequest(new { message = "Currency is required" });
            }

            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized(new { message = "Invalid user identification" });
            }

            // Validar contra la lista canónica (Normalize → null si no es soportada).
            var normalized = SupportedCurrenciesList.Normalize(dto.Currency);
            if (normalized == null)
            {
                return BadRequest(new
                {
                    message = "Unsupported currency",
                    supported = SupportedCurrenciesList.Codes
                });
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            user.PreferredCurrency = normalized;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Preferred currency updated",
                user = new
                {
                    user.Id,
                    user.Name,
                    user.Email,
                    user.PreferredCurrency,
                    Role = user.Role.ToString()
                }
            });
        }
        catch (Exception ex)
        {
            await _loggingService.LogErrorAsync(
                message: "Failed to set preferred currency",
                details: $"Exception while updating PreferredCurrency: {ex.Message}",
                userId: int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int uid) ? uid : (int?)null,
                source: "UserController.SetPreferredCurrency",
                relatedEntityType: "User",
                additionalData: new { Error = ex.Message, ErrorType = ex.GetType().Name }
            );
            return StatusCode(500, new { message = "Failed to update preferred currency" });
        }
    }

    /// <summary>
    /// 🛡️ Round 28 S2-P0-13: GDPR Art. 20 — Derecho a la Portabilidad de datos.
    /// Devuelve un JSON estructurado con todos los datos personales del usuario autenticado.
    /// Plazo legal de respuesta: 30 días desde la solicitud (Art. 12.3 RGPD).
    /// Hard-cap por ahora: 1000 mensajes más recientes para no explotar el payload.
    /// </summary>
    [HttpGet("me/export")]
    [Authorize]
    public async Task<IActionResult> ExportMyData([FromServices] newApi.Services.DataPortabilityService portabilityService, CancellationToken ct)
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized(new { message = "Invalid user identification" });
            }

            var export = await portabilityService.ExportUserDataAsync(userId, ct);
            if (export == null)
            {
                return NotFound(new { message = "User not found" });
            }

            // Cabecera para que el navegador sugiera descarga como JSON (Art. 20: lectura mecánica).
            Response.Headers.Append("Content-Disposition", $"attachment; filename=\"inspecciono-data-export-{userId}-{System.DateTime.UtcNow:yyyyMMdd}.json\"");
            return Ok(export);
        }
        catch (Exception ex)
        {
            await _loggingService.LogErrorAsync(
                message: "GDPR Art. 20 — data export failed",
                details: $"Export failed: {ex.Message}",
                userId: int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int uid) ? uid : (int?)null,
                source: "UserController.ExportMyData",
                relatedEntityType: "User",
                additionalData: new { Error = ex.Message, ErrorType = ex.GetType().Name });
            return StatusCode(500, new { message = "Failed to export user data — contacta soporte para gestionar la solicitud manualmente." });
        }
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

    // 🛡️ Round 28 MUD-R: ExpertRelocationController extraído a su propio archivo
    // Controllers/ExpertRelocationController.cs porque ASP.NET Core no descubre
    // controllers anidados (TypeInfo.IsPublic devuelve false para nested types).
}