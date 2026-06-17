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

        /// <summary>Día de la semana (0=domingo … 6=sábado) → nombre inglés usado por la tabla legacy.</summary>
        private static readonly string[] DayIntToName =
            { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

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

        // ─────────────────────────────────────────────────────────────────────────
        // 🗓️ Fase E1: CRUD de ExpertAvailabilityRule (horas por día + turnos partidos).
        // Es la tabla que consume el slot API (modelo Calendly). NO toca la disponibilidad
        // legacy (ExpertAvailability), que se conserva para hires en curso del flujo antiguo.
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>Reglas de disponibilidad activas del experto autenticado (horas por día).</summary>
        [HttpGet("rules")]
        public async Task<IActionResult> GetRules()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                return Unauthorized(new { message = "Invalid user identification" });

            var expert = await _context.ExpertProfiles.FirstOrDefaultAsync(ep => ep.UserId == userId);
            if (expert == null)
                return NotFound(new { message = "Expert profile not found" });

            var rules = await _context.ExpertAvailabilityRules
                .Where(r => r.ExpertId == expert.Id && r.IsActive && r.EffectiveTo == null)
                .OrderBy(r => r.DayOfWeek).ThenBy(r => r.StartLocal)
                .Select(r => new AvailabilityRuleDto
                {
                    Id = r.Id,
                    DayOfWeek = r.DayOfWeek,
                    StartLocal = r.StartLocal.ToString(@"hh\:mm"),
                    EndLocal = r.EndLocal.ToString(@"hh\:mm"),
                    Timezone = r.Timezone,
                })
                .ToListAsync();

            return Ok(rules);
        }

        /// <summary>
        /// Reemplaza TODA la disponibilidad por reglas del experto (set semanal completo).
        /// Desactiva las reglas activas (effective-dating) e inserta las nuevas con snapshot del timezone.
        /// </summary>
        [HttpPut("rules")]
        public async Task<IActionResult> SetRules([FromBody] SetAvailabilityRulesDto dto)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                return Unauthorized(new { message = "Invalid user identification" });

            var expert = await _context.ExpertProfiles.FirstOrDefaultAsync(ep => ep.UserId == userId);
            if (expert == null)
                return NotFound(new { message = "Expert profile not found. You must be an expert to manage availability." });

            var rules = dto?.Rules ?? new List<AvailabilityRuleInputDto>();

            // Validar + parsear antes de tocar la BD.
            var parsed = new List<(int Day, TimeSpan Start, TimeSpan End)>();
            foreach (var r in rules)
            {
                if (r.DayOfWeek < 0 || r.DayOfWeek > 6)
                    return BadRequest(new { message = $"Día inválido: {r.DayOfWeek} (debe ser 0=domingo … 6=sábado)." });
                if (!TimeSpan.TryParse(r.StartLocal, out var start) || !TimeSpan.TryParse(r.EndLocal, out var end))
                    return BadRequest(new { message = $"Hora inválida en una franja del día {r.DayOfWeek} (usa HH:mm)." });
                if (end <= start)
                    return BadRequest(new { message = $"La hora de fin debe ser posterior a la de inicio (día {r.DayOfWeek})." });
                // P3: la franja debe caer dentro de [00:00, 24:00] (TimeSpan.TryParse acepta días/>24h).
                if (start < TimeSpan.Zero || end > TimeSpan.FromHours(24))
                    return BadRequest(new { message = $"Franja fuera de rango en el día {r.DayOfWeek} (debe estar entre 00:00 y 24:00)." });
                parsed.Add((r.DayOfWeek, start, end));
            }

            // P2: rechazar franjas que se solapan dentro del MISMO día (turnos partidos deben ser disjuntos).
            foreach (var dayGroup in parsed.GroupBy(p => p.Day))
            {
                var ordered = dayGroup.OrderBy(p => p.Start).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    if (ordered[i].Start < ordered[i - 1].End)
                        return BadRequest(new { message = $"Franjas solapadas en el día {dayGroup.Key}; revísalas." });
                }
            }

            var now = DateTime.UtcNow;
            var tz = string.IsNullOrWhiteSpace(expert.Timezone) ? "UTC" : expert.Timezone;

            var existing = await _context.ExpertAvailabilityRules
                .Where(r => r.ExpertId == expert.Id && r.IsActive && r.EffectiveTo == null)
                .ToListAsync();
            foreach (var old in existing)
            {
                old.IsActive = false;
                old.EffectiveTo = now;
                old.UpdatedAt = now;
            }

            foreach (var (day, start, end) in parsed)
            {
                _context.ExpertAvailabilityRules.Add(new ExpertAvailabilityRule
                {
                    ExpertId = expert.Id,
                    DayOfWeek = day,
                    StartLocal = start,
                    EndLocal = end,
                    Timezone = tz,
                    EffectiveFrom = now,
                    EffectiveTo = null,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            // 🔄 Mantener coherente la disponibilidad legacy (la que muestran los widgets públicos del
            // perfil) con el horario por-día recién guardado: resumen = unión de días + [min inicio, max fin].
            // Así el perfil no muestra un horario distinto del que se usa para reservar.
            var legacyActive = await _context.ExpertAvailabilities
                .Where(ea => ea.ExpertId == expert.Id && ea.IsActive && ea.EffectiveTo == null)
                .ToListAsync();
            foreach (var oldLegacy in legacyActive)
            {
                oldLegacy.IsActive = false;
                oldLegacy.EffectiveTo = now;
                oldLegacy.UpdatedAt = now;
            }
            if (parsed.Count > 0)
            {
                var dayNames = parsed.Select(p => p.Day).Distinct().OrderBy(d => d)
                    .Select(d => DayIntToName[d]).ToList();
                _context.ExpertAvailabilities.Add(new ExpertAvailability
                {
                    ExpertId = expert.Id,
                    DaysOfWeek = JsonSerializer.Serialize(dayNames),
                    StartTime = parsed.Min(p => p.Start),
                    EndTime = parsed.Max(p => p.End),
                    Timezone = tz,
                    EffectiveFrom = now,
                    EffectiveTo = null,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            await _context.SaveChangesAsync();
            return Ok(new { count = parsed.Count });
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 🗓️ Excepciones por FECHA concreta (sobreescriben el horario semanal ese día).
        // Cerrado = IsWorking=false sin franjas. Abierto/horas especiales = IsWorking=true + N franjas.
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>Excepciones del experto autenticado en [from, to] (inclusive), agrupadas por fecha.</summary>
        [HttpGet("exceptions")]
        public async Task<IActionResult> GetExceptions([FromQuery] DateOnly from, [FromQuery] DateOnly to)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                return Unauthorized(new { message = "Invalid user identification" });

            var expert = await _context.ExpertProfiles.FirstOrDefaultAsync(ep => ep.UserId == userId);
            if (expert == null)
                return NotFound(new { message = "Expert profile not found" });

            var rows = await _context.ExpertAvailabilityExceptions
                .Where(e => e.ExpertId == expert.Id && e.Date >= from && e.Date <= to)
                .OrderBy(e => e.Date).ThenBy(e => e.StartLocal)
                .ToListAsync();

            var result = rows.GroupBy(e => e.Date).Select(g => new AvailabilityExceptionDto
            {
                Date = g.Key.ToString("yyyy-MM-dd"),
                IsWorking = g.Any(r => r.IsWorking),
                Ranges = g.Where(r => r.IsWorking && r.StartLocal != null && r.EndLocal != null)
                    .Select(r => new ExceptionRangeDto
                    {
                        Start = r.StartLocal!.Value.ToString(@"hh\:mm"),
                        End = r.EndLocal!.Value.ToString(@"hh\:mm"),
                    }).ToList(),
            }).ToList();

            return Ok(result);
        }

        /// <summary>Upsert de la excepción de UNA fecha (reemplaza las filas de esa fecha).</summary>
        [HttpPut("exceptions")]
        public async Task<IActionResult> SetException([FromBody] SetAvailabilityExceptionDto dto)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                return Unauthorized(new { message = "Invalid user identification" });

            var expert = await _context.ExpertProfiles.FirstOrDefaultAsync(ep => ep.UserId == userId);
            if (expert == null)
                return NotFound(new { message = "Expert profile not found. You must be an expert to manage availability." });

            if (dto == null || !DateOnly.TryParse(dto.Date, out var date))
                return BadRequest(new { message = "Fecha inválida (usa YYYY-MM-DD)." });
            if (date < DateOnly.FromDateTime(DateTime.UtcNow.Date))
                return BadRequest(new { message = "No puedes configurar una fecha en el pasado." });

            var parsed = new List<(TimeSpan Start, TimeSpan End)>();
            if (dto.IsWorking)
            {
                foreach (var r in dto.Ranges ?? new List<ExceptionRangeInputDto>())
                {
                    if (!TimeSpan.TryParse(r.Start, out var start) || !TimeSpan.TryParse(r.End, out var end))
                        return BadRequest(new { message = "Hora inválida en una franja (usa HH:mm)." });
                    if (end <= start)
                        return BadRequest(new { message = "La hora de fin debe ser posterior a la de inicio." });
                    if (start < TimeSpan.Zero || end > TimeSpan.FromHours(24))
                        return BadRequest(new { message = "Franja fuera de rango (debe estar entre 00:00 y 24:00)." });
                    parsed.Add((start, end));
                }
                // Sin solapes entre franjas del mismo día.
                var ordered = parsed.OrderBy(p => p.Start).ToList();
                for (var i = 1; i < ordered.Count; i++)
                    if (ordered[i].Start < ordered[i - 1].End)
                        return BadRequest(new { message = "Franjas solapadas; revísalas." });
                if (parsed.Count == 0)
                    return BadRequest(new { message = "Si el día está abierto, indica al menos una franja horaria." });
            }

            var now = DateTime.UtcNow;
            var tz = string.IsNullOrWhiteSpace(expert.Timezone) ? "UTC" : expert.Timezone;

            // Reemplazar las filas de esa fecha.
            var existing = await _context.ExpertAvailabilityExceptions
                .Where(e => e.ExpertId == expert.Id && e.Date == date).ToListAsync();
            _context.ExpertAvailabilityExceptions.RemoveRange(existing);

            if (!dto.IsWorking)
            {
                _context.ExpertAvailabilityExceptions.Add(new ExpertAvailabilityException
                {
                    ExpertId = expert.Id, Date = date, IsWorking = false,
                    Timezone = tz, CreatedAt = now, UpdatedAt = now,
                });
            }
            else
            {
                foreach (var (start, end) in parsed)
                {
                    _context.ExpertAvailabilityExceptions.Add(new ExpertAvailabilityException
                    {
                        ExpertId = expert.Id, Date = date, IsWorking = true,
                        StartLocal = start, EndLocal = end,
                        Timezone = tz, CreatedAt = now, UpdatedAt = now,
                    });
                }
            }

            await _context.SaveChangesAsync();
            return Ok(new { date = dto.Date, ranges = parsed.Count });
        }

        /// <summary>Elimina la excepción de una fecha (vuelve a regir el horario semanal).</summary>
        [HttpDelete("exceptions/{date}")]
        public async Task<IActionResult> DeleteException(string date)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                return Unauthorized(new { message = "Invalid user identification" });
            if (!DateOnly.TryParse(date, out var d))
                return BadRequest(new { message = "Fecha inválida (usa YYYY-MM-DD)." });

            var expert = await _context.ExpertProfiles.FirstOrDefaultAsync(ep => ep.UserId == userId);
            if (expert == null)
                return NotFound(new { message = "Expert profile not found" });

            var rows = await _context.ExpertAvailabilityExceptions
                .Where(e => e.ExpertId == expert.Id && e.Date == d).ToListAsync();
            _context.ExpertAvailabilityExceptions.RemoveRange(rows);
            await _context.SaveChangesAsync();
            return Ok(new { removed = rows.Count });
        }
    }

    /// <summary>Regla de disponibilidad para lectura (Fase E1).</summary>
    public class AvailabilityRuleDto
    {
        public int Id { get; set; }
        public int DayOfWeek { get; set; }
        public string StartLocal { get; set; } = "";
        public string EndLocal { get; set; } = "";
        public string? Timezone { get; set; }
    }

    /// <summary>Una franja del editor (Fase E1).</summary>
    public class AvailabilityRuleInputDto
    {
        public int DayOfWeek { get; set; }
        public string StartLocal { get; set; } = "";
        public string EndLocal { get; set; } = "";
    }

    /// <summary>Set semanal completo a guardar (Fase E1).</summary>
    public class SetAvailabilityRulesDto
    {
        public List<AvailabilityRuleInputDto> Rules { get; set; } = new();
    }

    /// <summary>Excepción de fecha para lectura (agrupada por día).</summary>
    public class AvailabilityExceptionDto
    {
        public string Date { get; set; } = "";
        public bool IsWorking { get; set; }
        public List<ExceptionRangeDto> Ranges { get; set; } = new();
    }

    public class ExceptionRangeDto
    {
        public string Start { get; set; } = "";
        public string End { get; set; } = "";
    }

    /// <summary>Upsert de la excepción de una fecha.</summary>
    public class SetAvailabilityExceptionDto
    {
        public string Date { get; set; } = "";
        public bool IsWorking { get; set; }
        public List<ExceptionRangeInputDto> Ranges { get; set; } = new();
    }

    public class ExceptionRangeInputDto
    {
        public string Start { get; set; } = "";
        public string End { get; set; } = "";
    }
}

