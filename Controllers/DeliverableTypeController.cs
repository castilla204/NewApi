using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DeliverableTypeController : ControllerBase
    {
        private readonly AppDbContext _context;

        public DeliverableTypeController(AppDbContext context)
        {
            _context = context;
        }

        // GET: api/DeliverableType
        [HttpGet]
        public async Task<ActionResult<IEnumerable<DeliverableTypeDto>>> GetDeliverableTypes()
        {
            var deliverableTypes = await _context.DeliverableTypes
                .Where(dt => dt.IsActive)
                .OrderBy(dt => dt.SortOrder)
                .Select(dt => new DeliverableTypeDto
                {
                    Id = dt.Id,
                    Name = dt.Name,
                    DisplayName = dt.DisplayName,
                    Description = dt.Description,
                    IsRequired = dt.IsRequired,
                    IsActive = dt.IsActive,
                    SortOrder = dt.SortOrder
                })
                .ToListAsync();

            return Ok(deliverableTypes);
        }
    }
}

