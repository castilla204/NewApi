using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DataLayer.Models.PostGresModels;
using DataLayer.Models.DTOs;
using DataLayer.Models;
using System.Security.Claims;
using newApi.Services;
using newApi.Services;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SearchController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<SearchController> _logger;
        private readonly IAuthorizationServices _authService;
        private readonly IUserService _userService;
        private readonly ISubscriptionService _subscriptionService;

        public SearchController(
            AppDbContext context,
            ILogger<SearchController> logger,
            IAuthorizationServices authService,
            IUserService userService,
            ISubscriptionService subscriptionService)
        {
            _context = context;
            _logger = logger;
            _authService = authService;
            _userService = userService;
            _subscriptionService = subscriptionService;
        }

        [HttpGet("all")]
        public async Task<IActionResult> GetAllSearches()
        {
            try
            {
                if (!_authService.IsAdmin(User))
                {
                    return Unauthorized(new { message = "Admin access required" });
                }

                var searches = await _context.Searches
                    .Include(s => s.User)
                    .Include(s => s.SearchParameters)
                    .Select(s => new SearchListDto
                    {
                        Id = s.Id,
                        UserId = s.UserId,
                        Title = s.Title,
                        Description = s.Description,
                        Frequency = s.Frequency,
                        IsActive = s.IsActive,
                        IsRevised = s.isRevised,
                        LastExecution = s.LastExecution,
                        CreatedAt = s.CreatedAt,
                        StartDate = s.StartDate,
                        Category = s.SearchParameters.FirstOrDefault().Category ?? 0,
                        User = new UserDto
                        {
                            Email = s.User.Email,
                            Name = s.User.Name
                        }
                    })
                    .OrderBy(s => s.IsRevised)
                    .ThenBy(s => s.LastExecution)
                    .ToListAsync();

                return Ok(searches);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving all searches");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("{searchId}/revise")]
        public async Task<IActionResult> MarkAsRevised(int searchId)
        {
            try
            {
                if (!_authService.IsAdmin(User))
                {
                    return Unauthorized(new { message = "Admin access required" });
                }

                var search = await _context.Searches.FindAsync(searchId);
                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                search.isRevised = true;
                await _context.SaveChangesAsync();

                return Ok(new { message = "Search marked as revised" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking search as revised");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CreateSearch([FromBody] CreateSearchDto searchDto)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Check subscription limits
                var subscriptionLimits = await _subscriptionService.GetUserSubscriptionLimits(userId);
                var activeSearchCount = await _context.Searches.CountAsync(s => s.UserId == userId && s.IsActive);

                if (activeSearchCount >= subscriptionLimits.MaxSearches)
                {
                    return StatusCode(403, new { message = $"You've reached your plan's limit of {subscriptionLimits.MaxSearches} active searches" });
                }

                if (searchDto.Frequency < subscriptionLimits.MinSearchInterval)
                {
                    return StatusCode(403, new { message = $"Minimum search interval for your plan is {subscriptionLimits.MinSearchInterval} hours" });
                }

                // Verificar si el teléfono está verificado
                var user = await _userService.GetUserAsync(userId);
                if (user == null)
                {
                    return NotFound(new { message = "User not found" });
                }

                if (!user.PhoneVerified)
                {
                    return StatusCode(403, new { message = "Phone verification required to create searches" });
                }
                var search = new Search
                {
                    UserId = int.Parse(userIdClaim),
                    Frequency = searchDto.Frequency,
                    Title = searchDto.Title,
                    Description = searchDto.Description,
                    IsActive = searchDto.IsActive,
                    NextExecution = DateTime.UtcNow,
                    StartDate = searchDto.StartDate,
                    CreatedAt = DateTime.UtcNow

                };

                await _context.Searches.AddAsync(search);
                await _context.SaveChangesAsync();

                return Ok(new { search.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating search");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetUserSearches()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var searchDtos = await _context.Searches
                    .Where(s => s.UserId == userId)
                    .Include(s => s.SearchParameters)
                    .Select(s => new SearchDto
                    {
                        Id = s.Id,
                        UserId = s.UserId,
                        Title = s.Title,
                        Description = s.Description,
                        Frequency = s.Frequency,
                        IsActive = s.IsActive,
                        IsRevised = s.isRevised,
                        LastExecution = s.LastExecution,
                        CreatedAt = s.CreatedAt,
                        StartDate = s.StartDate,
                        Category = s.SearchParameters.FirstOrDefault().Category ?? 0
                    })
                    .ToListAsync();

                return Ok(searchDtos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving user searches");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("{searchId}/toggle-active")]
        public async Task<IActionResult> ToggleSearchActive(int searchId)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var search = await _context.Searches.FirstOrDefaultAsync(s =>
                    s.Id == searchId && (s.UserId == userId || _authService.IsAdmin(User))
                );

                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                search.IsActive = !search.IsActive;
                await _context.SaveChangesAsync();

                return Ok(new { isActive = search.IsActive });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling search active status");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpDelete("{searchId}")]
        public async Task<IActionResult> DeleteSearch(int searchId)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var search = await _context.Searches.FirstOrDefaultAsync(s => s.Id == searchId);
                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                _context.Searches.Remove(search);
                await _context.SaveChangesAsync();

                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting search");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("{searchId}")]
        public async Task<IActionResult> UpdateSearch(int searchId, [FromBody] UpdateSearchDto updateDto)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var search = await _context.Searches.FirstOrDefaultAsync(s =>
                    s.Id == searchId && (s.UserId == userId || _authService.IsAdmin(User))
                );

                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                search.Title = updateDto.Title;
                search.Description = updateDto.Description;
                search.StartDate = updateDto.StartDate;

                // Validate frequency against subscription limits
                var subscriptionLimits = await _subscriptionService.GetUserSubscriptionLimits(userId);
                if (updateDto.Frequency < subscriptionLimits.MinSearchInterval)
                {
                    return StatusCode(403, new { message = $"Minimum search interval for your plan is {subscriptionLimits.MinSearchInterval} hours" });
                }
                search.Frequency = updateDto.Frequency;

                await _context.SaveChangesAsync();

                return Ok(new { message = "Search updated successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating search");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpGet("{searchId}")]
        public async Task<IActionResult> GetSearch(int searchId)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var search = await _context.Searches
                    .Include(s => s.SearchParameters)
                    .FirstOrDefaultAsync(s => s.Id == searchId &&
                        (s.UserId == userId || _authService.IsAdmin(User)));

                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                var searchDto = new SearchDto
                {
                    Id = search.Id,
                    UserId = search.UserId,
                    Title = search.Title,
                    Description = search.Description,
                    Frequency = search.Frequency,
                    IsActive = search.IsActive,
                    IsRevised = search.isRevised,
                    LastExecution = search.LastExecution,
                    CreatedAt = search.CreatedAt,
                    StartDate = search.StartDate,
                    Category = search.SearchParameters.FirstOrDefault()?.Category ?? 0
                };

                return Ok(searchDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving search");
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}