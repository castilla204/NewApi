using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using Stripe;

namespace newApi.Services
{
    public interface IStripeReconciliationService
    {
        Task<ReconciliationReport> RunDailyReconciliationAsync();
    }

    public class ReconciliationReport
    {
        public DateTime WindowStart { get; set; }
        public DateTime WindowEnd { get; set; }
        public int RefundsInStripeMissingInDb { get; set; }
        public int RefundsInDbMissingInStripe { get; set; }
        public int TransfersInStripeMissingInDb { get; set; }
        public int ChargesUnreconciled { get; set; }
        public List<string> CriticalIssues { get; set; } = new();
    }

    // P2-5: Job diario de conciliación BD vs Stripe.
    // Compara los movimientos visibles en Stripe (Refund/Transfer) en una
    // ventana de 24h con su contraparte en FinancialTransactions. Cualquier
    // mismatch se eleva como log Critical (e-mail al admin vía el filtro
    // existente) y se devuelve un reporte resumen. NO muta nada en BD: solo
    // observa y notifica.
    public class StripeReconciliationService : IStripeReconciliationService
    {
        private const int StripePageSize = 100;

        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;

        public StripeReconciliationService(AppDbContext context, ILoggingService loggingService)
        {
            _context = context;
            _loggingService = loggingService;
        }

        // 🛡️ R3-V2 FIX (consistencia con N13): evita ejecución concurrente del recurring job
        // en HPA multi-replica Render. Timeout 1800s (30 min) — reconciliación recorre 24h de
        // FT vs Stripe API; en peor caso con muchos hires + rate limits puede tardar varios min.
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 1800)]
        public async Task<ReconciliationReport> RunDailyReconciliationAsync()
        {
            var windowEnd = DateTime.UtcNow;
            // 🛡️ B5 FIX: ventana de 72h (no 24h) para SOLAPAR con corridas anteriores. El cron es
            // diario y la ventana era de 24h justa, sin solape: si una corrida se saltaba (Hangfire caído,
            // deploy a las 03:00, outage), el tramo entre la última corrida buena y el windowStart de la
            // siguiente NO volvía a entrar en ninguna ventana futura → cargo/refund/transfer huérfano sin
            // detectar para siempre. Este servicio SOLO observa+alerta (no muta BD) y la detección es
            // idempotente, así que reexaminar 72h cada día es inocuo (a lo sumo re-loguea), y tolera hasta
            // ~2 corridas consecutivas saltadas sin abrir hueco.
            var windowStart = windowEnd.AddHours(-72);
            var report = new ReconciliationReport
            {
                WindowStart = windowStart,
                WindowEnd = windowEnd
            };

            try
            {
                await ReconcileRefundsAsync(windowStart, windowEnd, report);
                // ✅ FIX AUDITORÍA [L6] Low: la reconciliación diaria AHORA escanea charges/PaymentIntents succeeded.
                // Disparo/ataque: tras un cargo huérfano (crash entre capture y commit en HandlePendingHireCompleted)
                //   y agotados los ~3 días de reintentos de webhook de Stripe, esta es la última red de seguridad y era
                //   ciega: report.ChargesUnreconciled se declaraba, sumaba y logueaba pero NUNCA se rellenaba (no había
                //   ReconcileChargesAsync). El cliente quedaba cobrado sin contratación, recuperación solo manual.
                // Fix aplicado: ReconcileChargesAsync(windowStart, windowEnd, report) lista charges succeeded/capturados
                //   en la ventana y marca ChargesUnreconciled += los que no tengan FinancialTransaction 'ServicePayment'.
                //   Solo DETECTA+ALERTA (cobro huérfano); NO inserta ServicePayment (falta contexto del hire).
                await ReconcileChargesAsync(windowStart, windowEnd, report);
                await ReconcileTransfersAsync(windowStart, windowEnd, report);

                var totalMismatches =
                    report.RefundsInStripeMissingInDb +
                    report.RefundsInDbMissingInStripe +
                    report.TransfersInStripeMissingInDb +
                    report.ChargesUnreconciled;

                if (totalMismatches > 0 || report.CriticalIssues.Count > 0)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Stripe reconciliation found mismatches",
                        details: $"Window {windowStart:O} → {windowEnd:O}. " +
                                 $"RefundsInStripeMissingInDb={report.RefundsInStripeMissingInDb}, " +
                                 $"RefundsInDbMissingInStripe={report.RefundsInDbMissingInStripe}, " +
                                 $"TransfersInStripeMissingInDb={report.TransfersInStripeMissingInDb}, " +
                                 $"ChargesUnreconciled={report.ChargesUnreconciled}. " +
                                 $"Issues: {string.Join(" | ", report.CriticalIssues)}",
                        userId: null,
                        source: "StripeReconciliationService.RunDailyReconciliationAsync",
                        relatedEntityType: "Reconciliation",
                        additionalData: new
                        {
                            report.WindowStart,
                            report.WindowEnd,
                            report.RefundsInStripeMissingInDb,
                            report.RefundsInDbMissingInStripe,
                            report.TransfersInStripeMissingInDb,
                            report.ChargesUnreconciled,
                            report.CriticalIssues
                        });
                }
                else
                {
                    await _loggingService.LogInfoAsync(
                        message: "Stripe reconciliation OK",
                        details: $"Window {windowStart:O} → {windowEnd:O}. No mismatches.",
                        userId: null,
                        source: "StripeReconciliationService.RunDailyReconciliationAsync",
                        relatedEntityType: "Reconciliation");
                }
            }
            catch (Exception ex)
            {
                report.CriticalIssues.Add($"Reconciliation aborted: {ex.Message}");
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Stripe reconciliation aborted",
                    details: $"Exception during reconciliation: {ex.Message}. StackTrace: {ex.StackTrace}",
                    userId: null,
                    source: "StripeReconciliationService.RunDailyReconciliationAsync",
                    relatedEntityType: "Reconciliation");
            }

            return report;
        }

        private async Task ReconcileRefundsAsync(DateTime windowStart, DateTime windowEnd, ReconciliationReport report)
        {
            var refundSvc = new Stripe.RefundService();
            var listOptions = new RefundListOptions
            {
                Limit = StripePageSize,
                Created = new DateRangeOptions
                {
                    GreaterThanOrEqual = windowStart,
                    LessThanOrEqual = windowEnd
                }
            };

            var seenStripeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? startingAfter = null;
            int loopGuard = 0;

            do
            {
                listOptions.StartingAfter = startingAfter;

                StripeList<Refund> page;
                try
                {
                    page = await refundSvc.ListAsync(listOptions);
                }
                catch (StripeException ex)
                {
                    report.CriticalIssues.Add($"Refund list page failed: {ex.Message}");
                    break;
                }

                foreach (var refund in page.Data)
                {
                    if (string.IsNullOrEmpty(refund.Id)) continue;
                    seenStripeIds.Add(refund.Id);

                    var existsInDb = await _context.FinancialTransactions
                        .AsNoTracking()
                        .AnyAsync(ft => ft.StripeRefundId == refund.Id);

                    if (!existsInDb)
                    {
                        report.RefundsInStripeMissingInDb++;
                        report.CriticalIssues.Add(
                            $"Refund {refund.Id} (PI {refund.PaymentIntentId}, amount {refund.Amount}) " +
                            $"present in Stripe but missing in FinancialTransactions.");

                        if (!string.IsNullOrEmpty(refund.PaymentIntentId))
                        {
                            var hasOriginal = await _context.FinancialTransactions
                                .AsNoTracking()
                                .AnyAsync(ft => ft.StripePaymentIntentId == refund.PaymentIntentId
                                                && ft.TransactionType == "ServicePayment");
                            if (!hasOriginal)
                            {
                                report.ChargesUnreconciled++;
                                report.CriticalIssues.Add(
                                    $"Refund {refund.Id} references PaymentIntent {refund.PaymentIntentId} " +
                                    $"with no original ServicePayment in BD.");
                            }
                        }
                    }
                }

                startingAfter = page.Data.Count > 0 ? page.Data[^1].Id : null;
                loopGuard++;
                if (!page.HasMore || loopGuard > 100)
                {
                    break;
                }
            } while (!string.IsNullOrEmpty(startingAfter));

            // DB → Stripe: refunds en BD del último día sin Refund en Stripe
            var dbRefunds = await _context.FinancialTransactions
                .AsNoTracking()
                .Where(ft => ft.StripeRefundId != null
                             && ft.CreatedAt >= windowStart
                             && ft.CreatedAt <= windowEnd)
                .Select(ft => new { ft.Id, ft.StripeRefundId })
                .ToListAsync();

            foreach (var row in dbRefunds)
            {
                if (row.StripeRefundId == null) continue;
                if (seenStripeIds.Contains(row.StripeRefundId)) continue;

                try
                {
                    var stripeRefund = await refundSvc.GetAsync(row.StripeRefundId);
                    if (stripeRefund == null)
                    {
                        report.RefundsInDbMissingInStripe++;
                        report.CriticalIssues.Add(
                            $"FinancialTransaction {row.Id} references Refund {row.StripeRefundId} " +
                            $"that does not exist in Stripe.");
                    }
                }
                catch (StripeException)
                {
                    report.RefundsInDbMissingInStripe++;
                    report.CriticalIssues.Add(
                        $"FinancialTransaction {row.Id} references Refund {row.StripeRefundId} " +
                        $"not retrievable in Stripe.");
                }
            }
        }

        // ✅ FIX AUDITORÍA [L6]: detecta cargos huérfanos (cliente cobrado sin contratación).
        // Lista los Charges succeeded/capturados de la ventana y verifica que cada uno tenga su
        // FinancialTransaction 'ServicePayment' con ese StripePaymentIntentId. Solo OBSERVA y
        // ALERTA — coherente con la doctrina del servicio (NO muta BD): no inserta ServicePayment
        // automáticamente porque falta el contexto del hire (RelatedEntityId, importes desglosados).
        private async Task ReconcileChargesAsync(DateTime windowStart, DateTime windowEnd, ReconciliationReport report)
        {
            var chargeSvc = new Stripe.ChargeService();
            var listOptions = new ChargeListOptions
            {
                Limit = StripePageSize,
                Created = new DateRangeOptions
                {
                    GreaterThanOrEqual = windowStart,
                    LessThanOrEqual = windowEnd
                }
            };

            string? startingAfter = null;
            int loopGuard = 0;

            do
            {
                listOptions.StartingAfter = startingAfter;

                StripeList<Charge> page;
                try
                {
                    page = await chargeSvc.ListAsync(listOptions);
                }
                catch (StripeException ex)
                {
                    report.CriticalIssues.Add($"Charge list page failed: {ex.Message}");
                    break;
                }

                foreach (var charge in page.Data)
                {
                    if (string.IsNullOrEmpty(charge.Id)) continue;

                    // Solo cobros realmente cobrados al cliente: succeeded y capturados (no holds).
                    if (!string.Equals(charge.Status, "succeeded", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!charge.Captured) continue;

                    // Sin PaymentIntent no podemos cruzar con ServicePayment; lo señalamos como huérfano.
                    if (string.IsNullOrEmpty(charge.PaymentIntentId))
                    {
                        report.ChargesUnreconciled++;
                        report.CriticalIssues.Add(
                            $"Charge {charge.Id} (amount {charge.Amount}) succeeded/capturado en Stripe " +
                            $"sin PaymentIntent asociado — posible cobro huérfano.");
                        continue;
                    }

                    var existsInDb = await _context.FinancialTransactions
                        .AsNoTracking()
                        .AnyAsync(ft => ft.StripePaymentIntentId == charge.PaymentIntentId
                                        && ft.TransactionType == "ServicePayment");

                    if (!existsInDb)
                    {
                        report.ChargesUnreconciled++;
                        report.CriticalIssues.Add(
                            $"Charge {charge.Id} (PI {charge.PaymentIntentId}, amount {charge.Amount}) " +
                            $"succeeded/capturado en Stripe pero SIN ServicePayment en FinancialTransactions — " +
                            $"posible cobro huérfano (cliente cobrado sin contratación).");
                    }
                }

                startingAfter = page.Data.Count > 0 ? page.Data[^1].Id : null;
                loopGuard++;
                if (!page.HasMore || loopGuard > 100)
                {
                    break;
                }
            } while (!string.IsNullOrEmpty(startingAfter));
        }

        private async Task ReconcileTransfersAsync(DateTime windowStart, DateTime windowEnd, ReconciliationReport report)
        {
            var transferSvc = new Stripe.TransferService();
            var listOptions = new TransferListOptions
            {
                Limit = StripePageSize,
                Created = new DateRangeOptions
                {
                    GreaterThanOrEqual = windowStart,
                    LessThanOrEqual = windowEnd
                }
            };

            string? startingAfter = null;
            int loopGuard = 0;

            do
            {
                listOptions.StartingAfter = startingAfter;

                StripeList<Transfer> page;
                try
                {
                    page = await transferSvc.ListAsync(listOptions);
                }
                catch (StripeException ex)
                {
                    report.CriticalIssues.Add($"Transfer list page failed: {ex.Message}");
                    break;
                }

                foreach (var transfer in page.Data)
                {
                    if (string.IsNullOrEmpty(transfer.Id)) continue;

                    var existsInDb = await _context.FinancialTransactions
                        .AsNoTracking()
                        .AnyAsync(ft => ft.StripeTransferId == transfer.Id);

                    if (!existsInDb)
                    {
                        report.TransfersInStripeMissingInDb++;
                        report.CriticalIssues.Add(
                            $"Transfer {transfer.Id} (amount {transfer.Amount}, dest {transfer.Destination}) " +
                            $"present in Stripe but missing in FinancialTransactions.");
                    }
                }

                startingAfter = page.Data.Count > 0 ? page.Data[^1].Id : null;
                loopGuard++;
                if (!page.HasMore || loopGuard > 100)
                {
                    break;
                }
            } while (!string.IsNullOrEmpty(startingAfter));
        }
    }
}
