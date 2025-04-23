using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using newApi.ScrapperGateway.DataLayer.Models;
using newApi.ScrapperGateway.DataLayer.Models.DTOs;
using System.Security.Claims;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class PlatformController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IMapper _mapper;

        public PlatformController(AppDbContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        [HttpGet("GetPlatforms")]
        public async Task<IActionResult> GetPlatforms()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Obtener plataformas activas
                var platforms = _context.Platforms.Where(z => z.IsActive).ToList();

                // Mapear a DTO
                var platformDtos = _mapper.Map<List<PlatformDTO>>(platforms);

                return Ok(platformDtos);
            }catch(Exception ex)
            {
                throw ex;
            }
        }
    }
}
