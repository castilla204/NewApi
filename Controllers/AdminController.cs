using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.Services;

namespace newApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [EnableRateLimiting("admin")] // ✅ SEGURIDAD: 200 requests/minuto para admin
    public class AdminController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly StripeRefundService _refundService;
        public AdminController(AppDbContext context, StripeRefundService refundService)
        {
            _context = context;
            _refundService = refundService;
        }

    }
}
