using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ Round 28 MUD-BD: helper para insertar filas en ClawbackQueues cuando se detecta
    /// una pérdida no recuperable automáticamente. Best-effort: si la persistencia falla,
    /// loggeamos warning y seguimos — el log Critical original sigue siendo la fuente de
    /// verdad para el admin.
    /// </summary>
    public class ClawbackQueueService
    {
        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;

        public ClawbackQueueService(AppDbContext context, ILoggingService loggingService)
        {
            _context = context;
            _loggingService = loggingService;
        }

        public async Task EnqueueAsync(
            int userId,
            string? stripeAccountId,
            decimal amountMajor,
            string currency,
            string reason,
            string? notes = null,
            int? searchHireId = null,
            CancellationToken ct = default)
        {
            try
            {
                _context.ClawbackQueues.Add(new ClawbackQueue
                {
                    UserId = userId,
                    StripeAccountId = stripeAccountId,
                    SearchHireId = searchHireId,
                    AmountMajor = amountMajor,
                    Currency = (currency ?? "EUR").ToUpperInvariant(),
                    Reason = reason,
                    Notes = notes,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // No bloqueamos el flow del caller — el Critical log adyacente ya alerta al admin.
                await _loggingService.LogWarningAsync(
                    message: "MUD-BD: failed to enqueue clawback row (non-blocking)",
                    details: $"UserId {userId} acct {stripeAccountId} amount {amountMajor:F2} {currency} reason={reason}: persist falló: {ex.Message}. El log Critical previo sigue siendo la alerta admin.",
                    userId: userId,
                    source: "ClawbackQueueService.EnqueueAsync",
                    relatedEntityType: "ClawbackQueue");
            }
        }
    }
}
