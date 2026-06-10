using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace NewApi.Tests.Simulators;

/// <summary>
/// Simula el comportamiento end-to-end del marketplace contra una BD real
/// del testcontainer, sin necesidad de arrancar el host completo
/// (Program.cs · 3052 líneas + Google Secret Manager + Stripe API · imposible
/// en tests rápidos).
///
/// El simulador implementa el contrato observable de los servicios reales,
/// verificado contra el código fuente del backend:
///
///   - SubscriptionController · checkout.session.completed handler
///     (líneas 4290–5500): crea SearchHire(pending) + FinancialTransaction
///     ServicePayment con StripePaymentIntentId; idempotente por
///     (StripePaymentIntentId, TransactionType="ServicePayment").
///
///   - SearchHireController.CompleteService (líneas 1072-1300): admite hires
///     en estado pending|awaiting_client_decision, valida ClientApproved=true
///     (false rebota a /api/Dispute/dispute-service), llama
///     ProcessMoneyDistributionAsync("completed"), cancela timer client_decision,
///     marca Appointment → appointment_completed, hire → completed.
///
///   - StripeRefundService.ProcessMoneyDistributionAsync (líneas 51–2800):
///     pg_advisory_xact_lock(searchHireId::bigint), reusa tx exterior si la
///     hay (MUD-AP), lee StatusConfiguration por (StatusId, Category=null,
///     ServiceTypeCategory=null), aplica mapping AppointmentStatus →
///     SearchHireStatus si el statusValue es AppointmentStatus, crea
///     FinancialTransaction Refund (si ClientPercentage > 0) + Payout (si
///     ExpertPercentage > 0). El 5% (o el resto) de la plataforma queda
///     implícito como diferencia — NO se crea FT tipo PlatformFee.
///
///   - AppointmentService cancellations (líneas 2095, 2796, 3552, 3704, 3886):
///     1ª cancelación NO finaliza el hire (StatusMapping inactivo, permite
///     reagendar); 2ª cancelación dispara ProcessMoneyDistributionAsync con
///     el statusValue "*_second" (penalización).
///
/// Lo que el simulador NO hace (deliberado):
///   - Llamar a Stripe API: usa identificadores sintéticos `re_sim_*`, `tr_sim_*`.
///   - Schedulear Hangfire jobs: el campo HangfireJobId queda informativo.
///   - Validar firmas de webhook: los tests confían en que la firma es válida.
/// </summary>
public static partial class MarketplaceFlowSimulator
{
    // ─────────────────────────────────────────────────────────────────────────
    // STEP 1 · Webhook checkout.session.completed crea hire(pending) + FT ServicePayment
    // ─────────────────────────────────────────────────────────────────────────
    public static async Task<SearchHire> SimulateCheckoutCompletedAsync(
        AppDbContext db,
        int clientUserId,
        int expertUserId,
        int searchServiceId,
        decimal amount,
        string paymentIntentId,
        string currency = "EUR",
        decimal? baseAmount = null,
        decimal? taxAmount = null)
    {
        // Idempotencia: el handler real busca FT ServicePayment con este PaymentIntentId
        var alreadyProcessed = await db.FinancialTransactions.AnyAsync(ft =>
            ft.StripePaymentIntentId == paymentIntentId &&
            ft.TransactionType == "ServicePayment");
        if (alreadyProcessed)
            throw new InvalidOperationException(
                $"Webhook con PaymentIntent {paymentIntentId} ya procesado (idempotencia)");

        var pending = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "pending");

        var hire = new SearchHire
        {
            ClientId = clientUserId,
            ExpertId = expertUserId,
            SearchServiceId = searchServiceId,
            StatusId = pending.Id,
            Amount = amount,
            Currency = currency,
            BaseAmount = baseAmount,
            TaxAmount = taxAmount,
            CreatedAt = DateTime.UtcNow,
        };
        db.SearchHires.Add(hire);
        await db.SaveChangesAsync();

        // ServicePayment FT
        db.FinancialTransactions.Add(new FinancialTransaction
        {
            UserId = clientUserId,
            Amount = amount,
            AmountCents = checked((long)Math.Round(amount * 100)),
            Currency = currency,
            TransactionType = "ServicePayment",
            RelatedEntityType = "SearchHire",
            RelatedEntityId = hire.Id,
            StripePaymentIntentId = paymentIntentId,
            CreatedAt = DateTime.UtcNow,
        });

        // Idempotencia de webhooks: el ProcessedWebhookEvent también se registra
        db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
        {
            EventId = "evt_sim_" + Guid.NewGuid().ToString("N"),
            EventType = "checkout.session.completed",
            Status = "Success",
            ProcessedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return hire;
    }

    /// <summary>
    /// Crea un Appointment vacío vinculado al hire. Algunos flujos lo necesitan
    /// (timers, cancelaciones); otros pueden saltárselo si el ServiceType.RequiresAppointment=false.
    /// </summary>
    public static async Task<Appointment> AttachAppointmentAsync(
        AppDbContext db,
        int hireId,
        string initialStatusValue = "awaiting_appointment")
    {
        var status = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == initialStatusValue);
        var appt = new Appointment
        {
            SearchHireId = hireId,
            StatusId = status.Id,
        };
        db.Appointments.Add(appt);
        await db.SaveChangesAsync();
        return appt;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP 2 · CompleteService(ClientApproved=true)
    // ─────────────────────────────────────────────────────────────────────────
    public static async Task ClientApprovesServiceAsync(
        AppDbContext db, int hireId, int clientUserId)
    {
        var hire = await LoadHireAsync(db, hireId);

        if (hire.ClientId != clientUserId)
            throw new UnauthorizedAccessException(
                $"Usuario {clientUserId} no es el ClientId del hire {hireId}");

        var allowedStatuses = new[] { "pending", "awaiting_client_decision" };
        if (!allowedStatuses.Contains(hire.Status.StatusValue))
            throw new InvalidOperationException(
                $"Hire en estado '{hire.Status.StatusValue}' no admite CompleteService (debe ser pending o awaiting_client_decision)");

        hire.ClientApproved = true;

        await ProcessMoneyDistributionAsync(db, hire, "completed");

        var completed = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "completed");
        hire.StatusId = completed.Id;
        hire.UpdatedAt = DateTime.UtcNow;

        // FRENTE 8 (c): si hay Appointment, marcarlo también completado
        var appt = await db.Appointments.SingleOrDefaultAsync(a => a.SearchHireId == hire.Id);
        if (appt is not null)
        {
            var apptCompleted = await db.SystemStatuses.SingleAsync(s =>
                s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_completed");
            appt.StatusId = apptCompleted.Id;
            appt.UpdatedAt = DateTime.UtcNow;
        }

        // Cancelar timers client_decision si quedan
        await ExpireOpenTimersAsync(db, appt, "client_decision");

        await db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CANCELACIONES · cliente y experto, 1ª y 2ª
    // ─────────────────────────────────────────────────────────────────────────
    public static async Task<string> CancelByClientAsync(
        AppDbContext db, int hireId, int clientUserId)
    {
        var hire = await LoadHireAsync(db, hireId);
        if (hire.ClientId != clientUserId)
            throw new UnauthorizedAccessException();

        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id)
            ?? throw new InvalidOperationException("Cancel requiere Appointment");

        appt.ClientCancellationCount++;

        if (appt.ClientCancellationCount == 1)
        {
            // 1ª cancelación · NO finaliza
            var firstStatus = await db.SystemStatuses.SingleAsync(s =>
                s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_cancelled_by_client");
            appt.StatusId = firstStatus.Id;
            await db.SaveChangesAsync();
            return "appointment_cancelled_by_client";
        }

        // 2ª cancelación · finaliza con penalización (0/95/5)
        await ProcessMoneyDistributionAsync(db, hire, "appointment_cancelled_by_client_second");
        var secondStatus = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_cancelled_by_client_second");
        appt.StatusId = secondStatus.Id;
        appt.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return "appointment_cancelled_by_client_second";
    }

    public static async Task<string> CancelByExpertAsync(
        AppDbContext db, int hireId, int expertUserId)
    {
        var hire = await LoadHireAsync(db, hireId);
        if (hire.ExpertId != expertUserId)
            throw new UnauthorizedAccessException();

        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);
        appt.ExpertCancellationCount++;

        if (appt.ExpertCancellationCount == 1)
        {
            var firstStatus = await db.SystemStatuses.SingleAsync(s =>
                s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_cancelled_by_expert");
            appt.StatusId = firstStatus.Id;
            await db.SaveChangesAsync();
            return "appointment_cancelled_by_expert";
        }

        // 2ª cancelación experto · 100/0/0
        await ProcessMoneyDistributionAsync(db, hire, "appointment_cancelled_by_expert_second");
        var secondStatus = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_cancelled_by_expert_second");
        appt.StatusId = secondStatus.Id;
        appt.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return "appointment_cancelled_by_expert_second";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FLUJO de cita con reporte (alternativo al CompleteService directo)
    // ─────────────────────────────────────────────────────────────────────────
    public static async Task ProposeAndConfirmAppointmentAsync(
        AppDbContext db, int hireId)
    {
        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hireId);
        var confirmed = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_confirmed");
        appt.StatusId = confirmed.Id;
        appt.ProposedDate = DateTime.UtcNow.AddDays(3);
        await db.SaveChangesAsync();
    }

    public static async Task SubmitExpertReportAsync(
        AppDbContext db, int hireId, int expertUserId)
    {
        var hire = await LoadHireAsync(db, hireId);
        if (hire.ExpertId != expertUserId) throw new UnauthorizedAccessException();

        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);
        var reportSent = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_report_sent");
        appt.StatusId = reportSent.Id;

        // El hire pasa a awaiting_client_decision (esperando respuesta del cliente 24h)
        var awaiting = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "awaiting_client_decision");
        hire.StatusId = awaiting.Id;
        hire.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Simula que el timer client_decision (24h) vence sin que el cliente apruebe ni
    /// dispute. El job de Hangfire transiciona Appointment a
    /// appointment_completed_without_client_approval y aplica el split 0/95/5.
    /// </summary>
    public static async Task ExpireClientDecisionTimerAsync(AppDbContext db, int hireId)
    {
        var hire = await LoadHireAsync(db, hireId);
        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);

        await ProcessMoneyDistributionAsync(db, hire, "appointment_completed_without_client_approval");

        var autoCompleted = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" &&
            s.StatusValue == "appointment_completed_without_client_approval");
        appt.StatusId = autoCompleted.Id;
        appt.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Timer expert_report (24h post-cita) vence sin reporte → 95/0/5.
    /// </summary>
    public static async Task ExpireExpertReportTimerAsync(AppDbContext db, int hireId)
    {
        var hire = await LoadHireAsync(db, hireId);
        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);

        await ProcessMoneyDistributionAsync(db, hire, "appointment_cancelled_by_no_report");

        var noReport = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_cancelled_by_no_report");
        appt.StatusId = noReport.Id;
        appt.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DISPUTAS
    // ─────────────────────────────────────────────────────────────────────────
    public static async Task<Dispute> OpenDisputeAsync(
        AppDbContext db, int hireId, int reporterUserId, string reason = "Test dispute")
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
        return dispute;
    }

    public static async Task ResolveDisputeProClientAsync(
        AppDbContext db, int disputeId, int adminUserId)
    {
        var dispute = await db.Disputes.SingleAsync(d => d.Id == disputeId);
        var hire = await LoadHireAsync(db, dispute.SearchHireId);

        // Claim atómico simulado (P1 FIX): Pending → Resolving
        dispute.Status = "Resolving";
        await db.SaveChangesAsync();

        await ProcessMoneyDistributionAsync(db, hire, "dispute_resolved_client");

        var resolved = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "dispute_resolved_client");
        hire.StatusId = resolved.Id;
        hire.UpdatedAt = DateTime.UtcNow;
        dispute.Status = "Resolved";
        await db.SaveChangesAsync();
    }

    public static async Task ResolveDisputeProExpertAsync(
        AppDbContext db, int disputeId, int adminUserId)
    {
        var dispute = await db.Disputes.SingleAsync(d => d.Id == disputeId);
        var hire = await LoadHireAsync(db, dispute.SearchHireId);

        dispute.Status = "Resolving";
        await db.SaveChangesAsync();

        await ProcessMoneyDistributionAsync(db, hire, "dispute_resolved_expert");

        var resolved = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "dispute_resolved_expert");
        hire.StatusId = resolved.Id;
        hire.UpdatedAt = DateTime.UtcNow;
        dispute.Status = "Resolved";
        await db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CORE · ProcessMoneyDistribution (fiel al código real)
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Implementa la lógica observable de StripeRefundService.ProcessMoneyDistributionAsync:
    /// advisory lock + lee StatusConfiguration + crea FT Refund (si client% > 0) +
    /// FT Payout (si expert% > 0). NO crea FT tipo PlatformFee — la plataforma se
    /// queda con el residuo implícito (verificado en RefundService.cs:2140-2300).
    /// </summary>
    public static async Task ProcessMoneyDistributionAsync(
        AppDbContext db, SearchHire hire, string statusValue)
    {
        // 🛡️ Aplicar pg_advisory_xact_lock real (bigint para signo)
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({(long)hire.Id})");

        // El statusValue puede ser SearchHireStatus o AppointmentStatus.
        // Buscamos por StatusValue; si hay ambiguedad preferimos AppointmentStatus
        // porque las penalizaciones (las que disparan dinero) suelen vivir allí.
        var status = await db.SystemStatuses
            .Where(s => s.StatusValue == statusValue)
            .OrderBy(s => s.StatusType == "AppointmentStatus" ? 0 : 1)
            .FirstAsync();

        var config = await db.StatusConfigurations.SingleAsync(c =>
            c.StatusId == status.Id &&
            c.CategoryId == null &&
            c.ServiceTypeCategoryId == null);

        // baseAmount = BaseAmount si existe (con tax separado) o Amount completo (legacy fallback)
        var baseAmount = hire.BaseAmount ?? hire.Amount;

        var clientRefundAmount = Math.Round(hire.Amount * (config.ClientPercentage / 100m), 2);
        var expertAmount = Math.Round(baseAmount * (config.ExpertPercentage / 100m), 2);

        var paymentIntentId = await db.FinancialTransactions
            .Where(ft => ft.RelatedEntityType == "SearchHire" &&
                         ft.RelatedEntityId == hire.Id &&
                         ft.TransactionType == "ServicePayment")
            .Select(ft => ft.StripePaymentIntentId)
            .FirstOrDefaultAsync();

        if (clientRefundAmount > 0 && hire.ClientId.HasValue)
        {
            db.FinancialTransactions.Add(new FinancialTransaction
            {
                UserId = hire.ClientId,
                Amount = clientRefundAmount,
                AmountCents = checked((long)Math.Round(clientRefundAmount * 100)),
                Currency = hire.Currency,
                TransactionType = "Refund",
                RelatedEntityType = "SearchHire",
                RelatedEntityId = hire.Id,
                StripePaymentIntentId = paymentIntentId,
                StripeRefundId = "re_sim_" + Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            });
        }

        if (expertAmount > 0 && hire.ExpertId.HasValue)
        {
            db.FinancialTransactions.Add(new FinancialTransaction
            {
                UserId = hire.ExpertId,
                Amount = expertAmount,
                AmountCents = checked((long)Math.Round(expertAmount * 100)),
                Currency = hire.Currency,
                TransactionType = "Payout",
                RelatedEntityType = "SearchHire",
                RelatedEntityId = hire.Id,
                StripePaymentIntentId = paymentIntentId,
                StripeTransferId = "tr_sim_" + Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            });
        }

        // El 5%/2% de plataforma queda implícito como residuo (verificado: RefundService.cs
        // NO crea FT tipo PlatformFee).

        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────
    private static async Task<SearchHire> LoadHireAsync(AppDbContext db, int hireId)
    {
        return await db.SearchHires
            .Include(h => h.Status)
            .Include(h => h.Client)
            .Include(h => h.Expert)
            .SingleAsync(h => h.Id == hireId);
    }

    private static async Task ExpireOpenTimersAsync(
        AppDbContext db, Appointment? appointment, string timerType)
    {
        if (appointment is null) return;
        var open = await db.AppointmentTimers
            .Where(t => t.AppointmentId == appointment.Id &&
                        t.TimerType == timerType &&
                        !t.IsExpired)
            .ToListAsync();
        foreach (var t in open)
        {
            t.IsExpired = true;
            t.ExpiredAt = DateTime.UtcNow;
            t.HangfireJobId = null;
        }
    }
}
