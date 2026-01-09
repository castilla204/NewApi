using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using newApi.DataLayer.Models.DTOs;
using newApi.Services;
using System.Security.Claims;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize] // Todos los endpoints requieren autenticación
    public class FavoritesController : ControllerBase
    {
        private readonly IFavoriteService _favoriteService;
        private readonly ILogger<FavoritesController> _logger;

        public FavoritesController(
            IFavoriteService favoriteService,
            ILogger<FavoritesController> logger)
        {
            _favoriteService = favoriteService;
            _logger = logger;
        }

        /// <summary>
        /// Obtiene el ID del usuario autenticado desde el token JWT
        /// </summary>
        private int GetAuthenticatedUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                throw new UnauthorizedAccessException("Usuario no autenticado");
            }
            return userId;
        }

        /// <summary>
        /// Toggle favorito: agregar si no existe, eliminar si existe
        /// POST /api/Favorites/toggle
        /// </summary>
        [HttpPost("toggle")]
        public async Task<IActionResult> ToggleFavorite([FromBody] AddFavoriteDto dto)
        {
            try
            {
                var userId = GetAuthenticatedUserId();
                var (success, message, isNowFavorite) = await _favoriteService.ToggleFavoriteAsync(userId, dto.SearchServiceId);

                if (!success)
                {
                    return BadRequest(new { success = false, message });
                }

                return Ok(new
                {
                    success = true,
                    message,
                    isFavorite = isNowFavorite,
                    searchServiceId = dto.SearchServiceId
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { success = false, message = "Usuario no autenticado" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FAVORITOS] Error en ToggleFavorite");
                return StatusCode(500, new { success = false, message = "Error al actualizar favorito" });
            }
        }

        /// <summary>
        /// Agrega un servicio a favoritos
        /// POST /api/Favorites
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> AddFavorite([FromBody] AddFavoriteDto dto)
        {
            try
            {
                var userId = GetAuthenticatedUserId();
                var (success, message, favorite) = await _favoriteService.AddFavoriteAsync(userId, dto.SearchServiceId);

                if (!success)
                {
                    return BadRequest(new { success = false, message });
                }

                return CreatedAtAction(
                    nameof(GetUserFavorites),
                    new { userId },
                    new { success = true, message, data = favorite }
                );
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { success = false, message = "Usuario no autenticado" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FAVORITOS] Error en AddFavorite");
                return StatusCode(500, new { success = false, message = "Error al agregar favorito" });
            }
        }

        /// <summary>
        /// Elimina un servicio de favoritos
        /// DELETE /api/Favorites/{searchServiceId}
        /// </summary>
        [HttpDelete("{searchServiceId}")]
        public async Task<IActionResult> RemoveFavorite(int searchServiceId)
        {
            try
            {
                var userId = GetAuthenticatedUserId();
                var (success, message) = await _favoriteService.RemoveFavoriteAsync(userId, searchServiceId);

                if (!success)
                {
                    return NotFound(new { success = false, message });
                }

                return Ok(new { success = true, message });
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { success = false, message = "Usuario no autenticado" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FAVORITOS] Error en RemoveFavorite");
                return StatusCode(500, new { success = false, message = "Error al eliminar favorito" });
            }
        }

        /// <summary>
        /// Obtiene todos los favoritos del usuario autenticado con paginación
        /// GET /api/Favorites?page=1&pageSize=20
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetUserFavorites([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                var userId = GetAuthenticatedUserId();
                var (favorites, totalCount) = await _favoriteService.GetUserFavoritesAsync(userId, page, pageSize);

                return Ok(new
                {
                    success = true,
                    data = favorites,
                    pagination = new
                    {
                        currentPage = page,
                        pageSize,
                        totalCount,
                        totalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
                    }
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { success = false, message = "Usuario no autenticado" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FAVORITOS] Error en GetUserFavorites");
                return StatusCode(500, new { success = false, message = "Error al obtener favoritos" });
            }
        }

        /// <summary>
        /// Verifica si un servicio es favorito del usuario autenticado
        /// GET /api/Favorites/check/{searchServiceId}
        /// </summary>
        [HttpGet("check/{searchServiceId}")]
        public async Task<IActionResult> IsFavorite(int searchServiceId)
        {
            try
            {
                var userId = GetAuthenticatedUserId();
                var result = await _favoriteService.IsFavoriteAsync(userId, searchServiceId);

                return Ok(new
                {
                    success = true,
                    data = result
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { success = false, message = "Usuario no autenticado" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FAVORITOS] Error en IsFavorite");
                return StatusCode(500, new { success = false, message = "Error al verificar favorito" });
            }
        }

        /// <summary>
        /// Verifica múltiples servicios si son favoritos del usuario autenticado (para listas de servicios)
        /// POST /api/Favorites/check-multiple
        /// </summary>
        [HttpPost("check-multiple")]
        public async Task<IActionResult> CheckMultipleFavorites([FromBody] int[] searchServiceIds)
        {
            try
            {
                var userId = GetAuthenticatedUserId();
                var tasks = searchServiceIds.Select(id => _favoriteService.IsFavoriteAsync(userId, id));
                var results = await Task.WhenAll(tasks);

                var favoritesMap = searchServiceIds
                    .Select((id, index) => new { id, result = results[index] })
                    .ToDictionary(x => x.id, x => x.result.IsFavorite);

                return Ok(new
                {
                    success = true,
                    data = favoritesMap
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { success = false, message = "Usuario no autenticado" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FAVORITOS] Error en CheckMultipleFavorites");
                return StatusCode(500, new { success = false, message = "Error al verificar favoritos" });
            }
        }

        /// <summary>
        /// Obtiene la cantidad de favoritos de un servicio (público)
        /// GET /api/Favorites/service/{searchServiceId}/count
        /// </summary>
        [HttpGet("service/{searchServiceId}/count")]
        [AllowAnonymous] // Público
        public async Task<IActionResult> GetServiceFavoritesCount(int searchServiceId)
        {
            try
            {
                var count = await _favoriteService.GetServiceFavoritesCountAsync(searchServiceId);

                return Ok(new
                {
                    success = true,
                    searchServiceId,
                    favoritesCount = count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FAVORITOS] Error en GetServiceFavoritesCount");
                return StatusCode(500, new { success = false, message = "Error al obtener cantidad de favoritos" });
            }
        }
    }
}
