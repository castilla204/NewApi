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

        public async Task<ReconciliationReport> RunDailyReconciliationAsync()
        {
            var windowEnd = DateTime.UtcNow;
            var windowStart = windowEnd.AddHours(-24);
            var report = new ReconciliationReport
            {
                WindowStart = windowStart,
                WindowEnd = windowEnd
            };

            try
            {
                await ReconcileRefundsAsync(windowStart, windowEnd, report);
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
