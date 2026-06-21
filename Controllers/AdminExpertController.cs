using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.ScrapperGateway.DataLayer.Models.DTOs;
using newApi.Services;
using System.Security.Claims;

namespace newApi.Controllers
{
    /// <summary>
    /// 🧑‍🔧 Edición de expertos POR PARTE DEL ADMIN ("en su nombre", sin impersonación).
    /// Reutiliza la misma lógica de servicio que usa el experto (parametrizada por userId),
    /// de modo que las validaciones son idénticas. Cada escritura queda auditada.
    /// Stripe (KYC/onboarding) queda FUERA: lo gestiona el propio experto.
    /// </summary>
    [ApiController]
    [Route("api/admin/expert")]
    [EnableRateLimiting("admin")]
    [Authorize(Roles = "Admin")]
    public class AdminExpertController : ControllerBase
    {
        private readonly UserService _userService;
        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;
        private readonly ILogger<AdminExpertController> _logger;

        public AdminExpertController(
            UserService userService,
            AppDbContext context,
            ILoggingService loggingService,
            ILogger<AdminExpertController> logger)
        {
            _userService = userService;
            _context = context;
            _loggingService = loggingService;
            _logger = logger;
        }

        private int? AdminId()
            => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : (int?)null;

        /// <summary>Datos agregados del experto para la pantalla de edición del panel admin.</summary>
        [HttpGet("{userId:int}")]
        public async Task<IActionResult> GetExpert(int userId)
        {
            var profile = await _context.ExpertProfiles
                .Include(ep => ep.User)
                .FirstOrDefaultAsync(ep => ep.UserId == userId);

            if (profile == null)
                return NotFound(new { message = "Expert profile not found" });

            return Ok(new
            {
                userId,
                name = profile.User != null ? profile.User.Name : null,
                email = profile.User != null ? profile.User.Email : null,
                profilePictureUrl = profile.ProfilePictureUrl,
                description = profile.Description,
                formacion = profile.Formacion,
                latitude = profile.Latitude,
                longitude = profile.Longitude,
                workRadiusKm = profile.WorkRadiusKm,
                workLocationDoor = profile.WorkLocationDoor,
                workLocationFloor = profile.WorkLocationFloor,
                workLocationDetails = profile.WorkLocationDetails,
                isOnVacation = profile.IsOnVacation,
                // 🔒 Solo lectura — gestionado por Stripe / el experto.
                stripeStatus = profile.StripeStatus.ToString(),
                onboardingCompleted = profile.OnboardingCompleted,
                country = profile.Country,
                createdAt = profile.CreatedAt
            });
        }

        /// <summary>Edita el perfil del experto en su nombre (mismas validaciones que el experto).</summary>
        [HttpPut("{userId:int}/profile")]
        public async Task<IActionResult> UpdateProfile(int userId, [FromForm] UpdateExpertProfileRequestDto request)
        {
            var exists = await _context.ExpertProfiles.AnyAsync(ep => ep.UserId == userId);
            if (!exists)
                return NotFound(new { message = "Expert profile not found" });

            var (success, updatedProfile, errorCode, errorMessage, detectedCountry)
                = await _userService.UpdateExpertProfile(userId, request);

            if (!success)
                return BadRequest(new { errorCode, message = errorMessage });

            await _loggingService.LogCriticalAsync(
                message: "CRITICAL: Admin edited expert profile",
                details: $"Admin {AdminId()} updated profile of expert {userId}",
                userId: AdminId(),
                source: "AdminExpertController.UpdateProfile",
                relatedEntityType: "ExpertProfile",
                relatedEntityId: userId,
                additionalData: new { Action = "AdminUpdateExpertProfile", TargetUserId = userId, AdminUserId = AdminId() }
            );

            return Ok(new { success = true, profile = updatedProfile, detectedCountry });
        }

        /// <summary>Activa/desactiva el modo vacaciones del experto en su nombre.</summary>
        [HttpPost("{userId:int}/toggle-vacation")]
        public async Task<IActionResult> ToggleVacation(int userId)
        {
            var exists = await _context.ExpertProfiles.AnyAsync(ep => ep.UserId == userId);
            if (!exists)
                return NotFound(new { message = "Expert profile not found" });

            var (success, isOnVacation) = await _userService.ToggleVacationMode(userId);
            if (!success)
                return BadRequest(new { message = "No se pudo cambiar el modo vacaciones (¿hires activos?)" });

            await _loggingService.LogCriticalAsync(
                message: "CRITICAL: Admin toggled expert vacation",
                details: $"Admin {AdminId()} set vacation={isOnVacation} for expert {userId}",
                userId: AdminId(),
                source: "AdminExpertController.ToggleVacation",
                relatedEntityType: "ExpertProfile",
                relatedEntityId: userId,
                additionalData: new { Action = "AdminToggleVacation", TargetUserId = userId, IsOnVacation = isOnVacation }
            );

            return Ok(new { success = true, isOnVacation });
        }
    }
}
