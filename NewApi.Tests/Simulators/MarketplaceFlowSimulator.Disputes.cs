using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace NewApi.Tests.Simulators;

/// <summary>
/// Extensión del simulador para disputas avanzadas (Agente 3).
///
/// Verifica los flujos reales documentados en:
///   - DisputeController.cs:1327 (POST dispute-service · createWithFiles + T3 rate-limit + R8 14d/N14 guards)
///   - DisputeController.cs:515  (PUT  {id}/resolve · P1 atomic claim Pending→Resolving→Resolved)
///   - DisputeController.cs:2171 (POST {id}/expert-response · CanExpertRespond 48h)
///   - StripeRefundService.cs:395-427 (multi-currency con tax: refund = Amount × % , transfer = BaseAmount × %)
///
/// HALLAZGOS DEL AUDIT (divergencias frente al catálogo):
///   D1. No existe ResolveDispute en SubscriptionController — el único endpoint de resolución
///       admin es PUT api/Dispute/{id}/resolve (DisputeController.cs:515).
///   D2. El endpoint correcto para crear disputa con files es POST /api/Dispute/dispute-service
///       (no “dispute-service” es el único; CreateDispute en :101 es el JSON-only sin files).
///   D3. El guard R8.a de ventana 14d sólo se evalúa cuando el hire está en 'completed';
///       cuando está en 'awaiting_client_decision' NO se valida ventana (porque
///       AwaitingClientDecision no tiene UpdatedAt fiable). Reportado.
///   D4. El guard N14 (DisputeController.cs:1413) bloquea por *cualquier* FT con StripeRefundId
///       o StripeTransferId no vacíos. Esto incluye Chargeback (TransactionType == "Chargeback"),
///       protección extra contra carrera dispute↔chargeback.
///   D5. El simulador base usa fallback baseAmount = hire.BaseAmount ?? hire.Amount (línea 409)
///       y aplica el split sobre baseAmount para AMBOS lados (refund+payout). El código real
///       (RefundService.cs:411-415) calcula el REFUND sobre hire.Amount × % cuando hay TaxAmount
///       (mantiene proporción de tax), y el PAYOUT sobre baseAmount × % (sin tax).
///       → cuando un test pasa BaseAmount+TaxAmount, el simulador base produce refund INCORRECTO
///         (calcula 100×0.90=90, debería ser 121×0.90=108.90). Por eso J-D3 usa una versión
///         tax-aware del cálculo (ResolveDisputeAtomicallyAsync más abajo) para verificar el
///         contrato real del servicio.
/// </summary>
public static partial class MarketplaceFlowSimulator
{
    /// <summary>
    /// Crea Dispute en Pending + DisputeFiles por cada filename. Cancela timers activos
    /// del Appointment (replica DisputeController.cs:1458-1505) y pone hire en 'disputed'.
    /// </summary>
    public static async Task<Dispute> OpenDisputeWithFilesAsync(
        AppDbContext db,
        int hireId,
        int reporterUserId,
        string reason,
        string[] fileNames)
    {
        var hire = await LoadHireAsync(db, hireId);

        var disputed = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "disputed");
        hire.StatusId = disputed.Id;
        hire.UpdatedAt = DateTime.UtcNow;

        var dispute = new Dispute
        {
            SearchHireId = hire.Id,
            ReporterId = reporterUserId,
            Reason = reason,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
            ExpertResponseDeadline = DateTime.UtcNow.AddHours(48),
        };
        db.Disputes.Add(dispute);
        await db.SaveChangesAsync();

        // Cancelar timers activos del appointment (replica DisputeController.cs:1466-1486).
        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.SearchHireId == hire.Id);
        if (appt is not null)
        {
            var timers = await db.AppointmentTimers
                .Where(t => t.AppointmentId == appt.Id && !t.IsExpired)
                .ToListAsync();
            foreach (var t in timers)
            {
                t.IsExpired = true;
                t.ExpiredAt = DateTime.UtcNow;
                t.HangfireJobId = null;
            }
        }

        // Persistir DisputeFiles si hay nombres.
        if (fileNames is { Length: > 0 })
        {
            foreach (var name in fileNames)
            {
                db.DisputeFiles.Add(new DisputeFile
                {
                    DisputeId = dispute.Id,
                    FileName = name,
                    FilePath = $"disputes/dispute-{dispute.Id}/client-files/{Guid.NewGuid():N}{Path.GetExtension(name)}",
                    FileType = Path.GetExtension(name),
                    FileSize = 1024,
                    UploadedByUserId = reporterUserId,
                    FileCategory = "client",
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }

        await db.SaveChangesAsync();
        return dispute;
    }

    /// <summary>
    /// Replica las precondiciones reales del POST /api/Dispute/dispute-service sin lanzar:
    ///   - hire existe y reporter ∈ {client, expert}
    ///   - status ∈ {completed, awaiting_client_decision} (DisputeController.cs:1390)
    ///   - R8.a si completed: hire.UpdatedAt ≥ -14d (línea 1401)
    ///   - R8.b N14: no FT con StripeRefundId / StripeTransferId / Chargeback (línea 1413)
    ///   - T3: &lt; 5 disputas del reporter en última 1h (línea 1349)
    /// </summary>
    public static async Task<(bool Ok, string Reason)> ValidateDisputePreconditionsAsync(
        AppDbContext db, int hireId, int reporterUserId)
    {
        var hire = await db.SearchHires
            .Include(h => h.Status)
            .FirstOrDefaultAsync(h => h.Id == hireId);

        if (hire is null) return (false, "hire_not_found");
        if (hire.ClientId != reporterUserId && hire.ExpertId != reporterUserId)
            return (false, "forbidden");

        var allowed = new[] { "completed", "awaiting_client_decision" };
        if (hire.Status is null || !allowed.Contains(hire.Status.StatusValue))
            return (false, "invalid_status");

        // Ventana 14d sólo aplica en completed (igual que el código real).
        if (hire.Status.StatusValue == "completed")
        {
            var completedAt = hire.UpdatedAt ?? hire.CreatedAt;
            if (completedAt < DateTime.UtcNow.AddDays(-14))
                return (false, "window_14d_expired");
        }

        // N14: dinero ya distribuido.
        var hasMoney = await db.FinancialTransactions.AnyAsync(ft =>
            ft.RelatedEntityType == "SearchHire" &&
            ft.RelatedEntityId == hireId &&
            (!string.IsNullOrEmpty(ft.StripeRefundId)
             || !string.IsNullOrEmpty(ft.StripeTransferId)
             || ft.TransactionType == "Chargeback"));
        if (hasMoney) return (false, "money_already_distributed");

        // T3 rate-limit: 5/h por reporter.
        var recent = await db.Disputes.CountAsync(d =>
            d.ReporterId == reporterUserId &&
            d.CreatedAt >= DateTime.UtcNow.AddHours(-1));
        if (recent >= 5) return (false, "rate_limit");

        // Disputa única por hire (no documentado como check Rxx pero está en :1381).
        var existing = await db.Disputes.AnyAsync(d => d.SearchHireId == hireId);
        if (existing) return (false, "already_exists");

        return (true, "ok");
    }

    /// <summary>
    /// Replica DisputeController.cs:2225-2229: guarda ExpertResponse + ExpertResponseAt y
    /// valida CanExpertRespond (UtcNow ≤ ExpertResponseDeadline AND ExpertResponseAt == null).
    /// </summary>
    public static async Task ExpertResponseToDisputeAsync(
        AppDbContext db, int disputeId, int expertUserId, string response)
    {
        var dispute = await db.Disputes
            .Include(d => d.SearchHire)
            .SingleAsync(d => d.Id == disputeId);

        if (dispute.SearchHire.ExpertId != expertUserId)
            throw new UnauthorizedAccessException("expert mismatch");
        if (dispute.Status != "Pending")
            throw new InvalidOperationException("Cannot respond to a resolved dispute");
        if (!dispute.CanExpertRespond)
            throw new InvalidOperationException("48h deadline expired");
        if (dispute.ExpertResponseAt.HasValue)
            throw new InvalidOperationException("already responded");

        dispute.ExpertResponse = response;
        dispute.ExpertResponseAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Replica el P1 FIX (DisputeController.cs:577-582 + 957): UPDATE atómico
    /// Pending→Resolving + ProcessMoneyDistribution + Resolving→Resolved.
    /// Idempotente: si la disputa ya está Resolving/Resolved devuelve false sin re-mover dinero.
    ///
    /// Para multi-currency con tax usa la fórmula REAL del RefundService:
    ///   refund (cliente) = hire.Amount × Client% / 100  ← incluye tax proporcional
    ///   payout (experto) = baseAmount  × Expert% / 100  ← base sin tax
    /// </summary>
    public static async Task<bool> ResolveDisputeAtomicallyAsync(
        AppDbContext db, int disputeId, int adminId, bool proClient)
    {
        // Claim atómico: única request que voltea Pending→Resolving gana.
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Disputes\" SET \"Status\" = 'Resolving' WHERE \"Id\" = {disputeId} AND \"Status\" = 'Pending'");
        if (claimed == 0)
        {
            // Otro request ya está resolviendo (idempotente).
            return false;
        }

        var dispute = await db.Disputes.SingleAsync(d => d.Id == disputeId);
        dispute.Status = "Resolving";
        var hire = await LoadHireAsync(db, dispute.SearchHireId);

        var statusValue = proClient ? "dispute_resolved_client" : "dispute_resolved_expert";

        await ProcessMoneyDistributionTaxAwareAsync(db, hire, statusValue);

        var resolved = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == statusValue);
        hire.StatusId = resolved.Id;
        hire.UpdatedAt = DateTime.UtcNow;
        dispute.Status = "Resolved";

        // Cancelar timers client_decision del appointment (DisputeController líneas
        // de createDispute cierran timers; al RESOLVE también deben quedar cerrados).
        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.SearchHireId == hire.Id);
        if (appt is not null)
        {
            var open = await db.AppointmentTimers
                .Where(t => t.AppointmentId == appt.Id && !t.IsExpired)
                .ToListAsync();
            foreach (var t in open)
            {
                t.IsExpired = true;
                t.ExpiredAt = DateTime.UtcNow;
                t.HangfireJobId = null;
            }
        }

        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Distribución de dinero tax-aware (RefundService.cs:395-427):
    ///   - refund = hire.Amount × Client% / 100  (proporción de tax preservada)
    ///   - transfer = baseAmount × Expert% / 100  (base, sin tax — el tax va a Hacienda)
    ///   - 100% refund → usa hire.Amount completo (no rounding drift)
    /// </summary>
    private static async Task ProcessMoneyDistributionTaxAwareAsync(
        AppDbContext db, SearchHire hire, string statusValue)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({(long)hire.Id})");

        var status = await db.SystemStatuses
            .Where(s => s.StatusValue == statusValue)
            .OrderBy(s => s.StatusType == "AppointmentStatus" ? 0 : 1)
            .FirstAsync();
        var config = await db.StatusConfigurations.SingleAsync(c =>
            c.StatusId == status.Id && c.CategoryId == null && c.ServiceTypeCategoryId == null);

        var baseAmount = hire.BaseAmount ?? hire.Amount;
        var expertAmountBase = Math.Round(baseAmount * (config.ExpertPercentage / 100m), 2);

        decimal clientRefundForStripe;
        if (config.ClientPercentage == 100m)
        {
            // Reembolso total exacto.
            clientRefundForStripe = hire.Amount;
        }
        else if (hire.TaxAmount.HasValue && hire.TaxAmount.Value > 0m && baseAmount > 0m)
        {
            // Reembolso parcial con tax: misma proporción que el pago original.
            clientRefundForStripe = Math.Round(hire.Amount * (config.ClientPercentage / 100m), 2);
        }
        else
        {
            // Legacy / sin tax separado.
            clientRefundForStripe = Math.Round(baseAmount * (config.ClientPercentage / 100m), 2);
        }

        var paymentIntentId = await db.FinancialTransactions
            .Where(ft => ft.RelatedEntityType == "SearchHire" &&
                         ft.RelatedEntityId == hire.Id &&
                         ft.TransactionType == "ServicePayment")
            .Select(ft => ft.StripePaymentIntentId)
            .FirstOrDefaultAsync();

        if (clientRefundForStripe > 0m && hire.ClientId.HasValue)
        {
            db.FinancialTransactions.Add(new FinancialTransaction
            {
                UserId = hire.ClientId,
                Amount = clientRefundForStripe,
                AmountCents = checked((long)Math.Round(clientRefundForStripe * 100m)),
                Currency = hire.Currency,
                TransactionType = "Refund",
                RelatedEntityType = "SearchHire",
                RelatedEntityId = hire.Id,
                StripePaymentIntentId = paymentIntentId,
                StripeRefundId = "re_sim_" + Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            });
        }

        if (expertAmountBase > 0m && hire.ExpertId.HasValue)
        {
            db.FinancialTransactions.Add(new FinancialTransaction
            {
                UserId = hire.ExpertId,
                Amount = expertAmountBase,
                AmountCents = checked((long)Math.Round(expertAmountBase * 100m)),
                Currency = hire.Currency,
                TransactionType = "Payout",
                RelatedEntityType = "SearchHire",
                RelatedEntityId = hire.Id,
                StripePaymentIntentId = paymentIntentId,
                StripeTransferId = "tr_sim_" + Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
