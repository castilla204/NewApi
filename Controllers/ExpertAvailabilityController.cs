using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Controllers
{
    /// <summary>
    /// Controlador para gestionar la disponibilidad horaria de expertos (solo lectura)
    /// La creación y actualización de disponibilidad se realiza a través del perfil de experto
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ExpertAvailabilityController : ControllerBase
    {
        private readonly AppDbContext _context;
        /// <summary>
        /// Constructor del controlador de disponibilidad de expertos
        /// </summary>
        public ExpertAvailabilityController(AppDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Obtener la disponibilidad actual activa del experto autenticado
        /// </summary>
        [HttpGet("current")]
        public async Task<IActionResult> GetCurrentAvailability()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Verificar que el usuario es experto
                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return NotFound(new { message = "Expert profile not found. You must be an expert to manage availability." });
                }

                // Obtener la disponibilidad actual activa
                var currentAvailability = await _context.ExpertAvailabilities
                    .Where(ea => ea.ExpertId == expertProfile.Id && ea.IsActive && ea.EffectiveTo == null)
                    .OrderByDescending(ea => ea.EffectiveFrom)
                    .FirstOrDefaultAsync();

                if (currentAvailability == null)
                {
                    return Ok(new CurrentExpertAvailabilityDto
                    {
                        DaysOfWeek = new List<string>(),
                        StartTime = TimeSpan.Zero,
                        EndTime = TimeSpan.Zero,
                        EffectiveFrom = DateTime.UtcNow
                    });
                }

                var daysOfWeek = JsonSerializer.Deserialize<List<string>>(currentAvailability.DaysOfWeek) ?? new List<string>();

                return Ok(new CurrentExpertAvailabilityDto
                {
                    Id = currentAvailability.Id,
                    DaysOfWeek = daysOfWeek,
                    StartTime = currentAvailability.StartTime,
                    EndTime = currentAvailability.EndTime,
                    EffectiveFrom = currentAvailability.EffectiveFrom
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to retrieve availability" });
            }
        }

        /// <summary>
        /// Obtener el historial completo de disponibilidades del experto
        /// </summary>
        [HttpGet("history")]
        public async Task<IActionResult> GetAvailabilityHistory()
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

                var availabilities = await _context.ExpertAvailabilities
                    .Where(ea => ea.ExpertId == expertProfile.Id)
                    .OrderByDescending(ea => ea.EffectiveFrom)
                    .ToListAsync();

                var result = availabilities.Select(ea => new ExpertAvailabilityDto
                {
                    Id = ea.Id,
                    ExpertId = ea.ExpertId,
                    DaysOfWeek = JsonSerializer.Deserialize<List<string>>(ea.DaysOfWeek) ?? new List<string>(),
                    StartTime = ea.StartTime,
                    EndTime = ea.EndTime,
                    EffectiveFrom = ea.EffectiveFrom,
                    EffectiveTo = ea.EffectiveTo,
                    IsActive = ea.IsActive,
                    CreatedAt = ea.CreatedAt,
                    UpdatedAt = ea.UpdatedAt
                }).ToList();

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to retrieve availability history" });
            }
        }

    }
}

