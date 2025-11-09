using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoMapper;
using newApi.Services;
using AdModel = newApi.DataLayer.Models.AdModel;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SearchResultFilteredController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IMapper _mapper;
        private readonly IAuthorizationServices _authService;

        public SearchResultFilteredController(
            AppDbContext context,

            IMapper mapper,
            IAuthorizationServices authService)
        {
            _context = context;
            _mapper = mapper;
            _authService = authService;
        }

        [HttpPost("filter/{searchId}/{adId}")]
        public async Task<IActionResult> AddToFiltered(int searchId, string adId, [FromBody] FilterRequest request)
        {
            try
            {
                if (!await _authService.CanAccessSearchAsync(User, searchId))
                {
                    return Unauthorized(new { message = "You don't have access to this search" });
                }

                // Verify the search exists
                var search = await _context.Searches.FindAsync(searchId);
                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                // Verify the ad exists
                var ad = await _context.Ads.FindAsync(adId);
                if (ad == null)
                {
                    return NotFound(new { message = "Ad not found" });
                }

                // Check if already filtered
                var existingFiltered = await _context.SearchResultsFiltered
                    .FirstOrDefaultAsync(srf => srf.SearchId == searchId && srf.AdId == adId && srf.IsVisible);

                if (existingFiltered != null)
                {
                    return BadRequest(new { message = "Ad is already in filtered results" });
                }

                var filtered = new SearchResultFiltered
                {
                    SearchId = searchId,
                    AdId = adId,
                    FilterNotes = request.Notes,
                    IsVisible = true,
                    FilteredAt = DateTime.UtcNow
                };

                await _context.SearchResultsFiltered.AddAsync(filtered);
                await _context.SaveChangesAsync();
                return Ok(filtered);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "An error occurred while adding to filtered results" });
            }
        }

        [HttpGet("search/{searchId}")]
        public async Task<IActionResult> GetFilteredResults(int searchId)
        {
            try
            {
                if (!await _authService.CanAccessSearchAsync(User, searchId))
                {
                    return Unauthorized(new { message = "You don't have access to this search" });
                }

                var filteredResults = await _context.SearchResultsFiltered
                    .Where(srf => srf.SearchId == searchId && srf.IsVisible)
                    .Include(srf => srf.Ad)
                    .Select(srf => new
                    {
                        srf.Id,
                        Ad = _mapper.Map<AdModel>(srf.Ad),
                        srf.FilteredAt,
                        srf.FilterNotes
                    })
                    .ToListAsync();
                return Ok(filteredResults);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "An error occurred while retrieving filtered results" });
            }
        }

        [Authorize] // 🔐 SEGURIDAD: Requerir autenticación para eliminar resultados filtrados
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> RemoveFromFiltered(int id)
        {
            try
            {
                var filtered = await _context.SearchResultsFiltered
                    .Include(srf => srf.Search)
                    .FirstOrDefaultAsync(srf => srf.Id == id);

                if (filtered == null)
                {
                    return NotFound(new { message = "Filtered result not found" });
                }

                if (!await _authService.CanAccessSearchAsync(User, filtered.SearchId))
                {
                    return Unauthorized(new { message = "You don't have access to this filtered result" });
                }

                // Soft delete
                filtered.IsVisible = false;
                await _context.SaveChangesAsync();
                return NoContent();
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "An error occurred while removing the filtered result" });
            }
        }
    }

    public class FilterRequest
    {
        public string? Notes { get; set; }
    }
}