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
        Task RescueOrphanedAppointmentTimersAsync(); // 🛡️ R5-F1
        Task DetectUnreconciledFinalizedHiresAsync(); // 🛡️ R5-F5
        Task EscalateStaleDisputesAsync(); // 🛡️ T4
        Task NotifyUpcomingStripeDeadlinesAsync(); // 🛡️ Round 12 — D3
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

        /// <summary>
        /// 🛡️ T4 FIX: auto-escalar disputas en estado Pending cuyo deadline de respuesta del
        /// experto (48h) ya pasó SIN respuesta. Antes: disputa quedaba indefinida en Pending
        /// hasta intervención manual del admin → cliente sin refund, hire bloqueado.
        ///
        /// Estrategia: flip atómico Pending→Resolving + encolar RescueStuckResolvingDisputeAsync
        /// (que ya existe y mueve dinero a favor del cliente al 100% según el reparto configurado
        /// para el estado de hire correspondiente). Idempotente: si el flip falla porque otro
        /// proceso ya lo hizo (admin manual o reintento previo), se salta sin error.
        ///
        /// Batch de 50 por ejecución para no saturar Hangfire en caso de backlog.
        /// </summary>
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task EscalateStaleDisputesAsync()
        {
            List<int> staleDisputeIds;
            try
            {
                staleDisputeIds = await _context.Disputes
                    .AsNoTracking()
                    .Where(d => d.Status == "Pending"
                             && d.ExpertResponseDeadline.HasValue
                             && d.ExpertResponseDeadline.Value < DateTime.UtcNow
                             && d.ExpertResponseAt == null)
                    .OrderBy(d => d.ExpertResponseDeadline)
                    .Take(50)
                    .Select(d => d.Id)
                    .ToListAsync();
            }
            catch (Exception readEx)
            {
                await _loggingService.LogCriticalAsync(
                    message: "T4: query de stale disputes falló",
                    details: $"No se pudo leer disputas pendientes con deadline expirado. Error: {readEx.Message}. Próxima ejecución reintentará.",
                    userId: null,
                    source: "PlatformMaintenanceService.EscalateStaleDisputesAsync.T4",
                    relatedEntityType: "Dispute",
                    relatedEntityId: null);
                return;
            }

            if (staleDisputeIds.Count == 0) return;

            int escalated = 0, alreadyClaimed = 0, errors = 0;
            foreach (var disputeId in staleDisputeIds)
            {
                try
                {
                    // Claim atómico (mismo patrón B3/P1 que usa ResolveDispute admin)
                    var claimed = await _context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"Disputes\" SET \"Status\" = 'Resolving' WHERE \"Id\" = {disputeId} AND \"Status\" = 'Pending'");

                    if (claimed == 0)
                    {
                        alreadyClaimed++;
                        continue; // otro proceso (admin o retry) ya la tomó
                    }

                    await _loggingService.LogCriticalAsync(
                        message: "T4: Dispute auto-escalada por timeout 48h del experto",
                        details: $"Dispute {disputeId} pasó deadline ExpertResponseDeadline sin respuesta del experto → flip atómico a 'Resolving'. Encolado RescueStuckResolvingDisputeAsync para distribuir dinero a favor del cliente (mapeo por SearchHireStatus configurado). ACCIÓN ADMIN: monitorizar resolución; el experto perderá su parte por no responder en plazo.",
                        userId: null,
                        source: "PlatformMaintenanceService.EscalateStaleDisputesAsync.T4",
                        relatedEntityType: "Dispute",
                        relatedEntityId: disputeId);

                    // Encolar rescate inmediato (el método ya existe y es idempotente).
                    Hangfire.BackgroundJob.Schedule<StripeRefundService>(
                        s => s.RescueStuckResolvingDisputeAsync(disputeId),
                        TimeSpan.FromSeconds(2));

                    escalated++;
                }
                catch (Exception ex)
                {
                    errors++;
                    // Rollback del claim para que el siguiente ciclo lo reintente
                    try
                    {
                        await _context.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE \"Disputes\" SET \"Status\" = 'Pending' WHERE \"Id\" = {disputeId} AND \"Status\" = 'Resolving'");
                    }
                    catch { /* swallow — el siguiente ciclo o el rescate la limpiará */ }

                    await _loggingService.LogCriticalAsync(
                        message: "T4: error escalando dispute (rollback intentado)",
                        details: $"Dispute {disputeId}: {ex.Message}. Status revertido a Pending para reintento.",
                        userId: null,
                        source: "PlatformMaintenanceService.EscalateStaleDisputesAsync.T4",
                        relatedEntityType: "Dispute",
                        relatedEntityId: disputeId);
                }
            }

            await _loggingService.LogInfoAsync(
                message: $"T4: batch de stale disputes procesado ({escalated} escaladas, {alreadyClaimed} ya tomadas, {errors} errores)",
                details: $"Total candidatas: {staleDisputeIds.Count}.",
                userId: null,
                source: "PlatformMaintenanceService.EscalateStaleDisputesAsync.T4",
                relatedEntityType: "Dispute",
                relatedEntityId: null);
        }

        // 🛡️ W3 FIX (Round 8 A8): DisableConcurrentExecution para evitar duplicación en HPA multi-replica Render
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
        [Hangfire.AutomaticRetry(Attempts = 0)]
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

        // 🛡️ W3 FIX (Round 8 A8): DisableConcurrentExecution para evitar duplicación en HPA multi-replica
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 1800)]
        [Hangfire.AutomaticRetry(Attempts = 0)]
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
                    // 🛡️ R29: incluimos requires_action (3DS abandonado por cliente). Aunque Stripe
                    // tiene timeout propio para 3DS, dejar un PI en este estado >6.5 días bloquea la
                    // autorización del cliente sin que el flujo normal lo capture nunca.
                    if (pi.Status != "requires_capture" && pi.Status != "requires_action" && pi.Status != "requires_confirmation" && pi.Status != "requires_payment_method")
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

        /// <summary>
        /// 🛡️ R5-F1: rescata AppointmentTimers que quedaron con HangfireJobId=NULL.
        /// Causa: el proceso muere entre commit del timer y BackgroundJob.Schedule (R6 partial dejó
        /// 5/6 sitios pre-commit; este watchdog cubre cualquier caso donde el Schedule falla o se pierde).
        /// Solo considera timers NO expirados y NO procesados (EndTime futuro o pasado reciente).
        /// </summary>
        // 🛡️ W3 FIX (Round 8 A8): DisableConcurrentExecution para evitar duplicación en HPA multi-replica
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task RescueOrphanedAppointmentTimersAsync()
        {
            try
            {
                // Buscar timers sin HangfireJobId que aún no han vencido (o vencieron en última hora).
                var cutoff = DateTime.UtcNow.AddHours(-1);
                var orphaned = await _context.AppointmentTimers
                    .Where(t => !t.IsExpired
                             && string.IsNullOrEmpty(t.HangfireJobId)
                             && t.EndTime >= cutoff)
                    .Take(200) // batch limit
                    .ToListAsync();

                foreach (var timer in orphaned)
                {
                    try
                    {
                        var delay = timer.EndTime - DateTime.UtcNow;
                        if (delay < TimeSpan.Zero) delay = TimeSpan.FromSeconds(5); // ya venció → encolar inmediato

                        var jobId = Hangfire.BackgroundJob.Schedule<IAppointmentService>(
                            s => s.ProcessAppointmentTimerAsync(timer.Id),
                            delay);
                        timer.HangfireJobId = jobId;
                        await _loggingService.LogWarningAsync(
                            message: "R5-F1: orphaned AppointmentTimer rescued",
                            details: $"Timer {timer.Id} (AppointmentId={timer.AppointmentId}, TimerType={timer.TimerType}) tenía HangfireJobId NULL — re-encolado con jobId={jobId}, delay={delay.TotalMinutes:F1}min.",
                            userId: null,
                            source: "PlatformMaintenanceService.RescueOrphanedAppointmentTimersAsync",
                            relatedEntityType: "AppointmentTimer",
                            relatedEntityId: timer.Id);
                    }
                    catch (Exception scheduleEx)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL R5-F1: failed to rescue orphaned timer",
                            details: $"Timer {timer.Id}: error encolando job: {scheduleEx.Message}",
                            userId: null,
                            source: "PlatformMaintenanceService.RescueOrphanedAppointmentTimersAsync",
                            relatedEntityType: "AppointmentTimer",
                            relatedEntityId: timer.Id);
                    }
                }

                if (orphaned.Count > 0)
                {
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL R5-F1: RescueOrphanedAppointmentTimersAsync failed",
                    details: ex.Message,
                    userId: null,
                    source: "PlatformMaintenanceService.RescueOrphanedAppointmentTimersAsync",
                    relatedEntityType: "AppointmentTimer",
                    relatedEntityId: null);
            }
        }

        /// <summary>
        /// 🛡️ R5-F5: detecta SearchHires en estado terminal de finalización (Completed/DisputeResolved*)
        /// hace más de 24h SIN ninguna FT Refund/Payout asociada — síntoma de que ProcessMoneyDistribution
        /// nunca corrió o falló silenciosamente (Hangfire perdido, etc.). Solo log Critical: requiere
        /// reconciliación manual por admin (puede involucrar reembolso o transfer adicional via Stripe Dashboard).
        /// </summary>
        // 🛡️ W3 FIX (Round 8 A8): DisableConcurrentExecution para evitar duplicación en HPA multi-replica
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 1200)]
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task DetectUnreconciledFinalizedHiresAsync()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-24);
                // Status values que indican finalización con distribución de dinero esperada
                var finalizationStatusValues = new[]
                {
                    "completed",
                    "dispute_resolved_client",
                    "dispute_resolved_expert"
                };

                var unreconciled = await _context.SearchHires
                    .AsNoTracking()
                    .Include(sh => sh.Status)
                    .Where(sh => sh.Status != null
                              && finalizationStatusValues.Contains(sh.Status.StatusValue)
                              && sh.UpdatedAt < cutoff
                              && !_context.FinancialTransactions.Any(ft =>
                                    ft.RelatedEntityType == "SearchHire"
                                    && ft.RelatedEntityId == sh.Id
                                    && (ft.TransactionType == "Refund" || ft.TransactionType == "Payout")))
                    .Take(50)
                    .ToListAsync();

                foreach (var hire in unreconciled)
                {
                    var ageHours = (DateTime.UtcNow - (hire.UpdatedAt ?? hire.CreatedAt)).TotalHours;
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL R5-F5: SearchHire finalizado sin FT Refund/Payout",
                        details: $"SearchHire {hire.Id} en estado '{hire.Status?.StatusValue}' hace {ageHours:F1}h pero sin FinancialTransaction Refund/Payout. Posible falla silenciosa de ProcessMoneyDistribution (job Hangfire perdido, etc.). ACCIÓN ADMIN: revisar Stripe balance de cliente/experto y reconciliar manualmente.",
                        userId: hire.ClientId,
                        source: "PlatformMaintenanceService.DetectUnreconciledFinalizedHiresAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hire.Id,
                        additionalData: new { hire.Id, Status = hire.Status?.StatusValue, AgeHours = ageHours, hire.ExpertId, hire.ClientId, hire.Amount });
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL R5-F5: DetectUnreconciledFinalizedHiresAsync failed",
                    details: ex.Message,
                    userId: null,
                    source: "PlatformMaintenanceService.DetectUnreconciledFinalizedHiresAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: null);
            }
        }

        /// <summary>
        /// 🛡️ Round 12 — D3 FIX: notificación PROACTIVA al experto antes de que un requirement
        /// futuro de Stripe entre en `past_due` y le bloqueen los transfers.
        ///
        /// MOTIVACIÓN
        /// ----------
        /// Stripe envía emails de compliance a expertos Express, pero SIN SLA garantizado. Casos:
        ///  - El email cae en spam o el experto desactivó las preferencias en Express Dashboard.
        ///  - Cambió su email tras onboarding y no actualizó en Stripe.
        ///  - La cadencia varía por país y tipo de requisito (docs no garantizan tiempos).
        /// Si no notificamos nosotros: el deadline pasa → StripeStatus → RequirementsPastDue →
        /// transfers bloqueados → hires Pending zombi.
        ///
        /// ESTRATEGIA
        /// ----------
        /// Escanear diariamente ExpertProfiles donde StripeFutureDueAt está entre HOY y HOY+3d
        /// y aún no past_due. Para cada uno, emitir LogWarningAsync(notifyUser=true) — esto crea
        /// notificación in-app + envía email con el template estándar.
        ///
        /// DEDUP: para evitar spamear el mismo experto cada día, sólo notificamos a expertos cuyo
        /// StripeStatus actualmente es Approved (es decir, todavía no se les ha avisado por la
        /// transición a RequirementsDue/RestrictedSoon). Cuando el status cambie, el aviso lo
        /// hará NotifyStripeStatusTransitionAsync. Si el status sigue Approved pero el deadline
        /// está próximo, esta job es la única notificación.
        /// </summary>
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task NotifyUpcomingStripeDeadlinesAsync()
        {
            try
            {
                var now = DateTime.UtcNow;
                // 🛡️ Round 28 MUD-BO: ampliada la ventana 3d → 14d. Stripe puede poner
                // deadlines de 30-90 días (NIF caducando, IBAN a renovar). Con 3d el
                // experto que viaja, está de baja o no revisa el panel se entera 3 días
                // antes — insuficiente para conseguir cita en SEPE/banco/notaría. 14d da
                // 2 semanas de margen real. La dedup de 23h sigue evitando spam diario.
                var deadline = now.AddDays(14);

                // 🛡️ Round 27 — R27-T27-2 FIX: dedup vía Notifications existentes.
                // ANTES: el job corría diario (Cron.Daily 09:00 UTC) y para cada experto en la
                // ventana de 3 días emitía LogWarningAsync(notifyUser=true) → nueva Notification
                // + email. SIN throttle. Un experto con deadline a 70h recibía la misma alerta
                // 3 días seguidos antes de que el deadline pasara → banner blindness + waste
                // 🛡️ Round 28 MUD-CC: dedup ESCALONADA según proximidad del deadline.
                //
                // ANTES: dedup uniforme de 23h. Con ventana 14d (MUD-BO), un experto recibía
                // hasta 14 emails idénticos en 2 semanas → banner-blindness severo.
                //
                // AHORA: notificar solo en hitos significativos — 14, 7, 3, 1 días antes
                // del deadline. Implementación: dedup escalonada por "ventana hasta el
                // anterior hito":
                //   - Si deadline > 7d (hito día 14): dedup últimos 6d antes de re-avisar
                //   - Si 3d < deadline ≤ 7d (hito día 7): dedup últimos 3d
                //   - Si 1d < deadline ≤ 3d (hito día 3): dedup últimos 1d
                //   - Si deadline ≤ 1d (hito día 1, urgente): dedup últimas 23h (como antes)
                // Resultado: max 4 emails durante toda la ventana de 14d en lugar de 14.
                //
                // El job sigue corriendo diario. Cada experto se filtra por una de 4 ventanas
                // de dedup según su deadline. Si el deadline pasa de 8d → 6d, se mete en el
                // bucket "≤7d" y la dedup correspondiente expira limpiamente → nueva notif.
                var hoursLeftPerCutoff = new {
                    UrgentDeadlineCutoff = now.AddHours(-23),    // ≤ 1d
                    ShortDeadlineCutoff = now.AddDays(-1),        // 1-3d
                    MediumDeadlineCutoff = now.AddDays(-3),       // 3-7d
                    LongDeadlineCutoff = now.AddDays(-6),         // 7-14d
                };

                // Pre-cargar notificaciones de hasta 14d (el peor caso de dedup que aplicará).
                // Filtramos en memoria luego por ventana específica.
                var oldestRelevant = now.AddDays(-7);
                var recentDeadlineNotifications = await _context.Notifications
                    .AsNoTracking()
                    .Where(n => n.UserId != null
                                && n.CreatedAt >= oldestRelevant
                                && n.Title.StartsWith("⏰ Plazo Stripe próximo"))
                    .Select(n => new { UserId = n.UserId!.Value, n.CreatedAt })
                    .ToListAsync();
                var lastNotifByUser = recentDeadlineNotifications
                    .GroupBy(n => n.UserId)
                    .ToDictionary(g => g.Key, g => g.Max(n => n.CreatedAt));

                // Filtro: deadline en próximos 14 días (MUD-BO), todavía no past_due, experto
                // aprobado. La dedup escalonada se aplica DESPUÉS de cargar candidatos.
                var candidates = await _context.ExpertProfiles
                    .Include(ep => ep.User)
                    .Where(ep => ep.StripeFutureDueAt != null
                              && ep.StripeFutureDueAt > now
                              && ep.StripeFutureDueAt <= deadline
                              && ep.StripeStatus == DataLayer.Models.PostGresModels.StripeStatus.Approved
                              && ep.User != null
                              && !ep.User.IsDeleted)
                    .OrderBy(ep => ep.StripeFutureDueAt) // 🛡️ prioridad: deadlines más cercanos primero
                    .Take(200) // ampliado de 100 con ventana 14d
                    .ToListAsync();

                // Aplicar dedup escalonada en memoria.
                var experts = candidates.Where(ep =>
                {
                    if (!lastNotifByUser.TryGetValue(ep.UserId, out var lastNotif))
                    {
                        return true; // nunca notificado → enviar
                    }
                    var hoursLeftForExpert = (ep.StripeFutureDueAt!.Value - now).TotalHours;
                    DateTime applicableCutoff;
                    if (hoursLeftForExpert <= 24) applicableCutoff = hoursLeftPerCutoff.UrgentDeadlineCutoff;
                    else if (hoursLeftForExpert <= 72) applicableCutoff = hoursLeftPerCutoff.ShortDeadlineCutoff;
                    else if (hoursLeftForExpert <= 168) applicableCutoff = hoursLeftPerCutoff.MediumDeadlineCutoff;
                    else applicableCutoff = hoursLeftPerCutoff.LongDeadlineCutoff;
                    return lastNotif < applicableCutoff;
                }).ToList();

                foreach (var ep in experts)
                {
                    var hoursLeft = (ep.StripeFutureDueAt!.Value - now).TotalHours;
                    var detailsText = string.IsNullOrEmpty(ep.StripeFutureRequirements)
                        ? "Tienes documentación pendiente con Stripe."
                        : $"Documentos/datos pendientes: {ep.StripeFutureRequirements}.";

                    try
                    {
                        // 🛡️ LOTE D · D-19 — Dedup vs el email automático de Stripe Compliance.
                        // Stripe envía emails directos a los Express experts cuando hay future
                        // requirements → el experto recibía DOS emails con la misma info en
                        // pocas horas → fatiga + soporte ("¿quién me está pidiendo qué?").
                        // Fix: diferenciar explícitamente texto y subject ("Inspecciono" en
                        // la subject line y "recordatorio" en el cuerpo) para que el experto
                        // entienda que NO sustituye al email técnico de Stripe; lo complementa.
                        await _loggingService.LogWarningAsync(
                            message: $"[Inspecciono · recordatorio] Plazo Stripe próximo ({hoursLeft:F0}h)",
                            details: $"Stripe ya te ha enviado los detalles técnicos por su cuenta. Este es un recordatorio de Inspecciono: tu cuenta de pagos tiene un plazo a las {ep.StripeFutureDueAt:dd/MM HH:mm} UTC. {detailsText} Accede a tu panel y completa los requisitos para evitar que Stripe pause tus transferencias.",
                            userId: ep.UserId,
                            source: "PlatformMaintenanceService.NotifyUpcomingStripeDeadlinesAsync",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: ep.Id,
                            notifyUser: true);
                    }
                    catch (Exception notifyEx)
                    {
                        // No abortar el batch por fallo de notificación a un experto puntual.
                        try
                        {
                            await _loggingService.LogWarningAsync(
                                message: "D3: Notificación pre-deadline falló para un experto",
                                details: $"ExpertProfileId {ep.Id}, UserId {ep.UserId}: {notifyEx.Message}",
                                source: "PlatformMaintenanceService.NotifyUpcomingStripeDeadlinesAsync");
                        }
                        catch { /* swallow */ }
                    }
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL D3: NotifyUpcomingStripeDeadlinesAsync failed",
                    details: ex.Message,
                    userId: null,
                    source: "PlatformMaintenanceService.NotifyUpcomingStripeDeadlinesAsync",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: null);
            }
        }
    }
}
