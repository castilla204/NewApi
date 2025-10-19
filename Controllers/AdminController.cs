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

        [HttpPost("process-missing-refunds")]
        public async Task<IActionResult> ProcessMissingRefunds()
        {
            try
            {
                _logger.LogInformation("Starting to process missing refunds for second cancellations...");

                // Buscar servicios que fueron cancelados por segunda vez pero no se procesó el refund
                var missingRefunds = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .Where(sh => sh.Status.StatusValue == "cancelled")
                    .ToListAsync();

                // Filtrar por appointments con RejectionCount >= 2 y CancellationCount == 0
                var appointmentsToCheck = await _context.Appointments
                    .Where(a => a.RejectionCount >= 2 && a.CancellationCount == 0)
                    .Select(a => a.SearchHireId)
                    .ToListAsync();

                missingRefunds = missingRefunds.Where(sh => appointmentsToCheck.Contains(sh.Id)).ToList();

                _logger.LogInformation("Found {Count} services that need refund processing", missingRefunds.Count);

                var results = new List<object>();

                foreach (var searchHire in missingRefunds)
                {
                    try
                    {
                        _logger.LogInformation("Processing refund for SearchHire {SearchHireId}, Amount: {Amount}, ClientId: {ClientId}", 
                            searchHire.Id, searchHire.Amount, searchHire.ClientId);

                        // Procesar el refund usando el servicio existente
                        var refundSuccess = await _refundService.ProcessAutomaticClientRefundAsync(
                            searchHire.Id, 
                            "Refund procesado manualmente para segunda cancelación no procesada");

                        results.Add(new
                        {
                            SearchHireId = searchHire.Id,
                            Amount = searchHire.Amount,
                            ClientId = searchHire.ClientId,
                            ExpertId = searchHire.ExpertId,
                            Success = refundSuccess,
                            Message = refundSuccess ? "Refund processed successfully" : "Failed to process refund"
                        });

                        if (refundSuccess)
                        {
                            _logger.LogInformation("✅ Refund processed successfully for SearchHire {SearchHireId}", searchHire.Id);
                        }
                        else
                        {
                            _logger.LogError("❌ Failed to process refund for SearchHire {SearchHireId}", searchHire.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing refund for SearchHire {SearchHireId}", searchHire.Id);
                        results.Add(new
                        {
                            SearchHireId = searchHire.Id,
                            Amount = searchHire.Amount,
                            ClientId = searchHire.ClientId,
                            ExpertId = searchHire.ExpertId,
                            Success = false,
                            Message = $"Error: {ex.Message}"
                        });
                    }
                }

                _logger.LogInformation("Completed processing missing refunds");

                return Ok(new
                {
                    Message = "Missing refunds processing completed",
                    ProcessedCount = results.Count,
                    Results = results
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProcessMissingRefunds");
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}
