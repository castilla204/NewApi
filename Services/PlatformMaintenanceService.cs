using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using Stripe;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ R5 + R7: Servicio de mantenimiento de plataforma. Aloja jobs Hangfire periódicos que no
    /// pertenecen a ningún dominio específico:
    ///   - CleanupOldProcessedWebhookEventsAsync (R7): borra ProcessedWebhookEvent &gt; 30 días para
    ///     evitar que la tabla crezca infinitamente. Stripe reintenta máx 3 días → 30 días deja
    ///     ventana cómoda + auditoría.
    ///   - ProcessExpiringPaymentIntentsAsync (R5): cancela proactivamente PaymentIntents en
    ///     `requires_capture` con CreatedAt &gt; 6.5 días para evitar que Stripe los expire a 7d
    ///     dejando dinero atascado (TODO P3-9 documentado en SubscriptionController:3152).
    /// </summary>
    public interface IPlatformMaintenanceService
    {
        Task CleanupOldProcessedWebhookEventsAsync();
        Task ProcessExpiringPaymentIntentsAsync();
    }

    public class PlatformMaintenanceService : IPlatformMaintenanceService
    {
        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;

        public PlatformMaintenanceService(AppDbContext context, ILoggingService loggingService)
        {
            _context = context;
            _loggingService = loggingService;
        }

        public async Task CleanupOldProcessedWebhookEventsAsync()
        {
            // Retención 30 días: Stripe reintenta máx ~3 días entre eventos, mantenemos 10x ese
            // margen para auditoría y debugging.
            const int retentionDays = 30;
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            try
            {
                var deleted = await _context.ProcessedWebhookEvents
                    .Where(e => e.ProcessedAt < cutoff)
                    .ExecuteDeleteAsync();

                if (deleted > 0)
                {
                    await _loggingService.LogInfoAsync(
                        message: "R7: ProcessedWebhookEvent cleanup",
                        details: $"Deleted {deleted} rows older than {retentionDays} days (cutoff {cutoff:yyyy-MM-dd HH:mm} UTC). Tabla mantenida en tamaño operable.",
                        userId: null,
                        source: "PlatformMaintenanceService.CleanupOldProcessedWebhookEventsAsync",
                        relatedEntityType: "ProcessedWebhookEvent",
                        relatedEntityId: null);
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL R7: ProcessedWebhookEvent cleanup failed",
                    details: $"Cutoff {cutoff:yyyy-MM-dd HH:mm} UTC. Error: {ex.Message}. Tabla crecerá hasta que el job se reejecute mañana.",
                    userId: null,
                    source: "PlatformMaintenanceService.CleanupOldProcessedWebhookEventsAsync",
                    relatedEntityType: "ProcessedWebhookEvent",
                    relatedEntityId: null);
            }
        }

        public async Task ProcessExpiringPaymentIntentsAsync()
        {
            // SearchHires con CaptureStatus="Pending" y CreatedAt > 6.5 días: el PI autorizado está
            // a punto de expirar en Stripe (7 días). Intentamos cancelar para liberar la autorización
            // del cliente (no podemos capturar a estas alturas — si pudiéramos, el flow normal o el
            // outbox ya lo habría hecho). Marcamos CaptureStatus="Failed" para que admin investigue.
            var cutoff = DateTime.UtcNow.AddDays(-6.5);
            List<DataLayer.Models.PostGresModels.SearchHire> nearExpiry;
            try
            {
                nearExpiry = await _context.SearchHires
                    .Where(sh => sh.CaptureStatus == "Pending" && sh.CreatedAt < cutoff)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL R5: failed to query expiring PIs",
                    details: $"Error: {ex.Message}",
                    userId: null,
                    source: "PlatformMaintenanceService.ProcessExpiringPaymentIntentsAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: null);
                return;
            }

            if (nearExpiry.Count == 0) return;

            var piService = new PaymentIntentService();
            foreach (var hire in nearExpiry)
            {
                // Buscar el PaymentIntent del hire via FinancialTransaction ServicePayment.
                var ft = await _context.FinancialTransactions
                    .AsNoTracking()
                    .Where(t => t.RelatedEntityType == "SearchHire" && t.RelatedEntityId == hire.Id
                                && t.TransactionType == "ServicePayment")
                    .OrderByDescending(t => t.Id)
                    .FirstOrDefaultAsync();
                if (ft == null || string.IsNullOrEmpty(ft.StripePaymentIntentId)) continue;

                try
                {
                    var pi = await piService.GetAsync(ft.StripePaymentIntentId);
                    if (pi.Status != "requires_capture" && pi.Status != "requires_action" && pi.Status != "requires_confirmation")
                    {
                        // Estado distinto: ya fue capturado o cancelado por otro flow. Marcar y seguir.
                        if (pi.Status == "succeeded")
                        {
                            hire.CaptureStatus = "Captured";
                        }
                        else if (pi.Status == "canceled")
                        {
                            hire.CaptureStatus = "Failed";
                        }
                        continue;
                    }

                    // Cancelar PI antes de que Stripe lo expire automático (7 días).
                    await piService.CancelAsync(pi.Id,
                        new PaymentIntentCancelOptions { CancellationReason = "abandoned" },
                        new RequestOptions { IdempotencyKey = $"r5-expiry-cancel-{pi.Id}" });

                    hire.CaptureStatus = "Failed";
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL R5: PaymentIntent cancelado por watchdog 7-day expiry",
                        details: $"SearchHire {hire.Id}: PI {pi.Id} llevaba {(DateTime.UtcNow - hire.CreatedAt).TotalDays:F1} días en '{pi.Status}'. Cancelado para liberar autorización del cliente antes de la expiración automática de Stripe. ACCIÓN ADMIN: investigar por qué no se capturó (capability del experto, evento perdido, etc).",
                        userId: hire.ClientId,
                        source: "PlatformMaintenanceService.ProcessExpiringPaymentIntentsAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hire.Id,
                        additionalData: new { PaymentIntentId = pi.Id, AgeDays = (DateTime.UtcNow - hire.CreatedAt).TotalDays });
                }
                catch (StripeException stripeEx) when (stripeEx.StripeError?.Code == "payment_intent_unexpected_state")
                {
                    // Race: alguien lo canceló/capturó entre el Get y el Cancel. No-op.
                    hire.CaptureStatus = "Failed";
                }
                catch (Exception ex)
                {
                    await _loggingService.LogWarningAsync(
                        message: "R5: failed to cancel near-expiry PI",
                        details: $"SearchHire {hire.Id} PI {ft.StripePaymentIntentId}: {ex.Message}",
                        userId: hire.ClientId,
                        source: "PlatformMaintenanceService.ProcessExpiringPaymentIntentsAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hire.Id);
                }
            }

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception saveEx)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL R5: SaveChanges failed after canceling near-expiry PIs",
                    details: $"Cancelaciones en Stripe ya ocurrieron pero los CaptureStatus locales no se persistieron. {nearExpiry.Count} hires afectados. Error: {saveEx.Message}",
                    userId: null,
                    source: "PlatformMaintenanceService.ProcessExpiringPaymentIntentsAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: null);
            }
        }
    }
}
