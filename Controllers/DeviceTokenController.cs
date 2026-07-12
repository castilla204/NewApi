using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class DeviceTokenController : ControllerBase
    {
        private readonly AppDbContext _context;
        public DeviceTokenController(AppDbContext context) { _context = context; }

        public record RegisterDeviceTokenDto(string Token, string Platform);

        /// <summary>Registra/actualiza (upsert por Token) el token FCM del usuario logueado.</summary>
        [HttpPost]
        public async Task<IActionResult> Register([FromBody] RegisterDeviceTokenDto dto)
        {
            if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                return Unauthorized();
            if (dto == null || string.IsNullOrWhiteSpace(dto.Token))
                return BadRequest(new { message = "Token requerido" });

            var platform = string.IsNullOrWhiteSpace(dto.Platform) ? "android" : dto.Platform.ToLowerInvariant();
            var now = DateTime.UtcNow;

            var existing = await _context.DeviceTokens.FirstOrDefaultAsync(d => d.Token == dto.Token);
            if (existing == null)
            {
                _context.DeviceTokens.Add(new DeviceToken
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Token = dto.Token,
                    Platform = platform,
                    CreatedAt = now,
                    LastSeenAt = now,
                });
            }
            else
            {
                existing.UserId = userId;   // reasignar dispositivo a la cuenta actual
                existing.Platform = platform;
                existing.LastSeenAt = now;
            }
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // 📲 Carrera: dos registros del MISMO token nuevo a la vez → el segundo
                // choca con el índice único de Token. El token queda igualmente registrado,
                // así que lo tratamos como éxito idempotente (registro es best-effort).
                return Ok(new { success = true });
            }
            return Ok(new { success = true });
        }

        /// <summary>Borra el token del usuario logueado (logout).</summary>
        [HttpDelete("{token}")]
        public async Task<IActionResult> Unregister(string token)
        {
            if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                return Unauthorized();
            await _context.DeviceTokens
                .Where(d => d.Token == token && d.UserId == userId)
                .ExecuteDeleteAsync();
            return Ok(new { success = true });
        }
    }
}
