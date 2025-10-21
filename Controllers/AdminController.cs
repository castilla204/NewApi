using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.Services;

namespace newApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AdminController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly StripeRefundService _refundService;
        private readonly ILogger<AdminController> _logger;

        public AdminController(AppDbContext context, StripeRefundService refundService, ILogger<AdminController> logger)
        {
            _context = context;
            _refundService = refundService;
            _logger = logger;
        }

    }
}
