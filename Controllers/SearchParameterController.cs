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
        private readonly ILogger<SearchParameterController> _logger;

        public SearchParameterController(AppDbContext context, ILogger<SearchParameterController> logger)
        {
            _context = context;
            _logger = logger;
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
                _logger.LogError(ex, "Error retrieving search parameter");
                return StatusCode(500, new { message = ex.Message });
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
                _logger.LogError(ex, "Error creating search parameter");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetAllSearchParameters()
        {
            try
            {
                var parameterDtos = await _context.SearchParameters
                    .Include(sp => sp.ServiceType) // Added: Include ServiceType
                    .Select(p => new SearchParameterDto
                    {
                        SearchParameterId = p.SearchParameterId,
                        Keywords = p.Keywords,
                        UserSearch = p.UserSearch,
                        Latitude = p.Latitude,
                        Longitude = p.Longitude,
                        ShippingAvailable = p.ShippingAvailable,
                        StrictMatchOnly = p.StrictMatchOnly,
                        Category = p.Category,
                        LocationRange = p.LocationRange,
                        MinPrice = p.MinPrice,
                        MaxPrice = p.MaxPrice,
                        BrandId = p.BrandId,
                        ModelId = p.ModelId,
                        ServiceTypeId = p.ServiceTypeId, // Added: ServiceTypeId
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
                _logger.LogError(ex, "Error retrieving search parameters");
                return StatusCode(500, new { message = ex.Message });
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
                _logger.LogError(ex, "Error updating search parameter");
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}