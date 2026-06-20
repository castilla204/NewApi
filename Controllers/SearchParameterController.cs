using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SearchParameterController : ControllerBase
    {
        private readonly AppDbContext _context;
        public SearchParameterController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet("{searchId}")]
        public async Task<IActionResult> GetSearchParameter(int searchId)
        {
            try
            {
                var searchParameter = await _context.SearchParameters
                    .Include(sp => sp.SearchParameterPlatforms)
                    .Include(sp => sp.ServiceType) // Added: Include ServiceType
                    .FirstOrDefaultAsync(sp => sp.SearchId == searchId);

                if (searchParameter == null)
                {
                    return NotFound(new { message = "Search parameter not found" });
                }

                var parameterDto = new SearchParameterDto
                {
                    SearchParameterId = searchParameter.SearchParameterId,
                    Keywords = searchParameter.Keywords,
                    UserSearch = searchParameter.UserSearch,
                    Latitude = searchParameter.Latitude,
                    Longitude = searchParameter.Longitude,
                    LocationName = searchParameter.LocationName, // ✅ NUEVO: Incluir LocationName
                    ShippingAvailable = searchParameter.ShippingAvailable,
                    StrictMatchOnly = searchParameter.StrictMatchOnly,
                    Category = searchParameter.Category,
                    LocationRange = searchParameter.LocationRange,
                    MinPrice = searchParameter.MinPrice,
                    MaxPrice = searchParameter.MaxPrice,
                    BrandId = searchParameter.BrandId,
                    ModelId = searchParameter.ModelId,
                    ServiceTypeId = searchParameter.ServiceTypeId, // Added: ServiceTypeId
                    SearchId = searchParameter.SearchId,
                    PlatformIds = searchParameter.SearchParameterPlatforms
                        .Select(spp => spp.PlatformId)
                        .ToList()
                };

                return Ok(parameterDto);
            }
            catch (Exception ex)
            {
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "No se pudo obtener el parámetro de búsqueda", errorCode = "SP_GET" });
            }
        }

        [HttpPost("{searchId}")]
        public async Task<IActionResult> CreateSearchParameter(int searchId, [FromBody] CreateSearchParameterDto searchParameterDto)
        {
            try
            {
                var search = await _context.Searches.FindAsync(searchId);
                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                // Validate ServiceTypeId
                if (searchParameterDto.ServiceTypeId.HasValue)
                {
                    var serviceType = await _context.ServiceTypes.FindAsync(searchParameterDto.ServiceTypeId);
                    if (serviceType == null)
                    {
                        return BadRequest(new { message = "Invalid ServiceTypeId" });
                    }
                }

                // Create the main entity
                var searchParameter = new SearchParameter
                {
                    Keywords = searchParameterDto.Keywords,
                    UserSearch = searchParameterDto.UserSearch,
                    Latitude = searchParameterDto.Latitude,
                    Longitude = searchParameterDto.Longitude,
                    LocationName = searchParameterDto.LocationName, // ✅ NUEVO: Incluir LocationName
                    ShippingAvailable = searchParameterDto.ShippingAvailable,
                    StrictMatchOnly = searchParameterDto.StrictMatchOnly,
                    Category = searchParameterDto.Category,
                    LocationRange = searchParameterDto.LocationRange,
                    MinPrice = searchParameterDto.MinPrice,
                    MaxPrice = searchParameterDto.MaxPrice,
                    BrandId = searchParameterDto.BrandId,
                    ModelId = searchParameterDto.ModelId,
                    ServiceTypeId = searchParameterDto.ServiceTypeId, // Added: ServiceTypeId
                    SearchId = searchId
                };

                await _context.SearchParameters.AddAsync(searchParameter);
                await _context.SaveChangesAsync();

                // Process associated platforms
                if (searchParameterDto.PlatformIds != null && searchParameterDto.PlatformIds.Any())
                {
                    var platforms = await _context.Platforms
                        .Where(p => searchParameterDto.PlatformIds.Contains(p.Id))
                        .ToListAsync();

                    if (platforms.Count != searchParameterDto.PlatformIds.Count)
                    {
                        return BadRequest(new { message = "Some platform IDs are invalid" });
                    }

                    foreach (var platform in platforms)
                    {
                        var searchParameterPlatform = new SearchParameterPlatform
                        {
                            SearchParameterId = searchParameter.SearchParameterId,
                            PlatformId = platform.Id
                        };
                        _context.SearchParameterPlatforms.Add(searchParameterPlatform);
                    }

                    await _context.SaveChangesAsync();
                }

                return Ok(new { message = "Search parameter created successfully" });
            }
            catch (Exception ex)
            {
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "No se pudo crear el parámetro de búsqueda", errorCode = "SP_CREATE" });
            }
        }

        [HttpGet]
        [Authorize] // 🔐 SEGURIDAD: Requerir autenticación para acceder a parámetros de búsqueda
        public async Task<IActionResult> GetAllSearchParameters()
        {
            try
            {
                // 🔐 SEGURIDAD: Verificar autenticación del usuario
                var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // 🔐 SEGURIDAD: Solo devolver parámetros de búsqueda del usuario autenticado
                var parameterDtos = await _context.SearchParameters
                    .Include(sp => sp.ServiceType)
                    .Include(sp => sp.Search) // Incluir Search para filtrar por usuario
                    .Where(sp => sp.Search.UserId == userId) // 🔐 FILTRAR POR USUARIO AUTENTICADO
                    .Select(p => new SearchParameterDto
                    {
                        SearchParameterId = p.SearchParameterId,
                        Keywords = p.Keywords,
                        UserSearch = p.UserSearch,
                        Latitude = p.Latitude,
                        Longitude = p.Longitude,
                        LocationName = p.LocationName,
                        ShippingAvailable = p.ShippingAvailable,
                        StrictMatchOnly = p.StrictMatchOnly,
                        Category = p.Category,
                        LocationRange = p.LocationRange,
                        MinPrice = p.MinPrice,
                        MaxPrice = p.MaxPrice,
                        BrandId = p.BrandId,
                        ModelId = p.ModelId,
                        ServiceTypeId = p.ServiceTypeId,
                        SearchId = p.SearchId,
                        PlatformIds = p.SearchParameterPlatforms
                            .Select(spp => spp.PlatformId)
                            .ToList()
                    })
                    .ToListAsync();

                return Ok(parameterDtos);
            }
            catch (Exception ex)
            {
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "No se pudieron obtener los parámetros de búsqueda", errorCode = "SP_GET_ALL" });
            }
        }

        [HttpPut("{searchId}")]
        public async Task<IActionResult> UpdateSearchParameter(int searchId, [FromBody] UpdateSearchParameterDto updateDto)
        {
            try
            {
                var searchParameter = await _context.SearchParameters
                    .Include(sp => sp.SearchParameterPlatforms)
                    .FirstOrDefaultAsync(sp => sp.SearchId == searchId);

                if (searchParameter == null)
                {
                    return NotFound(new { message = "Search parameter not found" });
                }

                // Validate ServiceTypeId
                if (updateDto.ServiceTypeId.HasValue)
                {
                    var serviceType = await _context.ServiceTypes.FindAsync(updateDto.ServiceTypeId);
                    if (serviceType == null)
                    {
                        return BadRequest(new { message = "Invalid ServiceTypeId" });
                    }
                }

                // Update basic properties
                searchParameter.Keywords = updateDto.Keywords;
                searchParameter.UserSearch = updateDto.UserSearch;
                searchParameter.Latitude = updateDto.Latitude;
                searchParameter.Longitude = updateDto.Longitude;
                searchParameter.ShippingAvailable = updateDto.ShippingAvailable;
                searchParameter.StrictMatchOnly = updateDto.StrictMatchOnly;
                searchParameter.Category = updateDto.Category;
                searchParameter.LocationRange = updateDto.LocationRange;
                searchParameter.MinPrice = updateDto.MinPrice;
                searchParameter.MaxPrice = updateDto.MaxPrice;
                searchParameter.BrandId = updateDto.BrandId;
                searchParameter.ModelId = updateDto.ModelId;
                searchParameter.ServiceTypeId = updateDto.ServiceTypeId; // Added: ServiceTypeId

                // Update platform associations
                if (updateDto.PlatformIds != null)
                {
                    // Remove existing associations
                    _context.SearchParameterPlatforms.RemoveRange(searchParameter.SearchParameterPlatforms);

                    // Add new associations
                    foreach (var platformId in updateDto.PlatformIds)
                    {
                        var searchParameterPlatform = new SearchParameterPlatform
                        {
                            SearchParameterId = searchParameter.SearchParameterId,
                            PlatformId = platformId
                        };
                        _context.SearchParameterPlatforms.Add(searchParameterPlatform);
                    }
                }

                await _context.SaveChangesAsync();

                return Ok(new { message = "Search parameter updated successfully" });
            }
            catch (Exception ex)
            {
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "No se pudo actualizar el parámetro de búsqueda", errorCode = "SP_UPDATE" });
            }
        }
    }
}