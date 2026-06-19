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
        Task RetryTransferFailedHiresAsync(); // 🛡️ F18
        Task ReconcileCompletedWithoutPayoutAsync(); // ✅ FIX AUDITORÍA [M2]
        Task EscalateStaleDisputesAsync(); // 🛡️ T4
        Task NotifyUpcomingStripeDeadlinesAsync(); // 🛡️ Round 12 — D3
        Task NotifyStalledOnboardingExpertsAsync(); // 🛡️ A2 — onboarding abandonado
        Task CheckAppointmentWatchdogHealthAsync(); // 🛡️ HEALTH — vigila que el watchdog de citas siga vivo
        Task ProcessExpiredSellerBookingsAsync(); // 🤝 Magic link vendedor: si no reserva en plazo → reembolso 100%
        Task ReconcileCapturedSellerHiresWithoutAppointmentAsync(); // 🛡️ HUECO 1b: pago capturado sin cita persistida → reembolso 100%
        Task ProcessExpiredExpertConfirmationsAsync(); // ⚓ Magic link experto: si no confirma en plazo → reembolso 100% (red de seguridad del timer)
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
        /// 🛡️ HEALTH: vigila que el RecurringJob "appointment-timers-watchdog" (ProcessOverdueTimersAsync,
        /// cada 10 min) siga vivo. Ese watchdog es el ÚNICO motor del ciclo de vida posterior a la
        /// contratación en el flujo nuevo cita+pago (appointment_confirmed → awaiting_report → report →
        /// decisión del cliente → payout). Si se para, TODAS las citas confirmadas quedan atascadas en
        /// silencio: el HangfireFailedJobNotificationFilter NO lo detecta porque no hay job "Failed",
        /// simplemente deja de ejecutarse. Dos comprobaciones complementarias:
        ///   (a) HEARTBEAT (best-effort, independiente de tráfico): lee la última ejecución del recurring
        ///       job desde el storage de Hangfire; si no consta o supera el umbral, alerta.
        ///   (b) SÍNTOMA (fiable, robusto a timezone gracias al margen de 2 días que absorbe cualquier
        ///       offset ±14h): si hay citas aún en "appointment_confirmed" con fecha de hace > 2 días, el
        ///       watchdog NO está cumpliendo su función → alerta.
        /// LogCriticalAsync ya enruta a email + Slack/Telegram (AlertChannelService).
        /// ⚠️ Este job también corre EN Hangfire: si Hangfire está caído por completo, tampoco se ejecuta.
        /// Para ese caso catastrófico hace falta un monitor EXTERNO (uptime check); ver INFORME.
        /// </summary>
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 120)]
        [Hangfire.AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 0, 60 })]
        public async Task CheckAppointmentWatchdogHealthAsync()
        {
            const string watchdogId = "appointment-timers-watchdog";
            const int heartbeatStaleMinutes = 30; // el watchdog corre cada 10 min → 3 ejecuciones perdidas
            var nowUtc = DateTime.UtcNow;

            // (a) HEARTBEAT — best-effort. Cualquier problema de storage/parseo degrada a Warning, nunca lanza.
            try
            {
                using var connection = Hangfire.JobStorage.Current.GetConnection();
                var hash = connection.GetAllEntriesFromHash($"recurring-job:{watchdogId}");

                if (hash == null || !hash.TryGetValue("LastExecution", out var lastExecRaw) || string.IsNullOrWhiteSpace(lastExecRaw))
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: appointment-timers-watchdog sin registro de última ejecución",
                        details: $"El RecurringJob '{watchdogId}' no tiene 'LastExecution' en el storage de Hangfire (¿no registrado, nunca ejecutado o storage inaccesible?). El ciclo de vida de las citas confirmadas no avanzará. ACCIÓN: verificar que el servidor Hangfire está activo y el recurring job registrado en Program.cs.",
                        userId: null,
                        source: "PlatformMaintenanceService.CheckAppointmentWatchdogHealthAsync",
                        relatedEntityType: "HangfireJob");
                }
                else
                {
                    var lastExec = TryParseHangfireDate(lastExecRaw);
                    if (lastExec == null)
                    {
                        await _loggingService.LogWarningAsync(
                            message: "No se pudo parsear LastExecution del watchdog de citas",
                            details: $"'{watchdogId}' LastExecution='{lastExecRaw}' no se pudo interpretar como fecha. El heartbeat queda no disponible esta vez; la comprobación por síntoma (citas atascadas) sigue activa.",
                            userId: null,
                            source: "PlatformMaintenanceService.CheckAppointmentWatchdogHealthAsync",
                            relatedEntityType: "HangfireJob");
                    }
                    else
                    {
                        var age = nowUtc - lastExec.Value;
                        if (age > TimeSpan.FromMinutes(heartbeatStaleMinutes))
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: appointment-timers-watchdog no se ejecuta (heartbeat obsoleto)",
                                details: $"Última ejecución de '{watchdogId}' hace {age.TotalMinutes:F0} min (umbral {heartbeatStaleMinutes} min; corre cada 10 min). El watchdog que avanza las citas confirmadas (y dispara los payouts) parece parado → ciclo de vida atascado en silencio. ACCIÓN: revisar el servidor Hangfire y sus logs.",
                                userId: null,
                                source: "PlatformMaintenanceService.CheckAppointmentWatchdogHealthAsync",
                                relatedEntityType: "HangfireJob");
                        }
                    }
                }
            }
            catch (Exception hbEx)
            {
                await _loggingService.LogWarningAsync(
                    message: "Heartbeat del watchdog de citas no disponible",
                    details: $"No se pudo leer la última ejecución de '{watchdogId}' del storage de Hangfire: {hbEx.Message}. La comprobación por síntoma (citas atascadas) sigue cubriendo el caso.",
                    userId: null,
                    source: "PlatformMaintenanceService.CheckAppointmentWatchdogHealthAsync",
                    relatedEntityType: "HangfireJob");
            }

            // (b) SÍNTOMA — fiable. Margen de 2 días: absorbe cualquier offset de timezone (±14h) y la
            // ventana normal de transición (hora de cita + 3h), de modo que CERO falsos positivos en
            // operación sana. Si aparecen, el watchdog no está avanzando las citas.
            try
            {
                var staleCutoff = nowUtc.AddDays(-2);
                var stalled = await _context.Appointments
                    .Where(a => a.Status.StatusValue == "appointment_confirmed"
                             && a.ProposedDate.HasValue
                             && a.ProposedDate.Value < staleCutoff)
                    .OrderBy(a => a.Id)
                    .Select(a => a.Id)
                    .Take(50)
                    .ToListAsync();

                if (stalled.Count > 0)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: citas confirmadas atascadas — el watchdog no las está avanzando",
                        details: $"{stalled.Count} cita(s) siguen en 'appointment_confirmed' con fecha de hace > 2 días (IDs muestra: {string.Join(", ", stalled.Take(20))}). El barrido por estado de ProcessOverdueTimersAsync debería haberlas pasado a 'awaiting_report'. Indica que el watchdog no se ejecuta o falla de forma sistemática → payouts y ciclo de vida bloqueados. ACCIÓN: revisar Hangfire y ejecutar manualmente ProcessOverdueTimersAsync.",
                        userId: null,
                        source: "PlatformMaintenanceService.CheckAppointmentWatchdogHealthAsync",
                        relatedEntityType: "Appointment");
                }
            }
            catch (Exception symEx)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: el health-check del watchdog de citas falló al consultar citas atascadas",
                    details: $"CheckAppointmentWatchdogHealthAsync no pudo ejecutar la comprobación por síntoma: {symEx.Message}. ACCIÓN: revisar BD/Hangfire.",
                    userId: null,
                    source: "PlatformMaintenanceService.CheckAppointmentWatchdogHealthAsync",
                    relatedEntityType: "Appointment");
            }
        }

        /// <summary>
        /// Parsea el valor 'LastExecution' que Hangfire guarda en el hash del recurring job. Hangfire
        /// lo serializa como ISO 8601 round-trip (formato actual) o como timestamp Unix en milisegundos
        /// (formato legacy). Probamos ambos en UTC; si ninguno cuadra, devolvemos null (no lanza).
        /// </summary>
        private static DateTime? TryParseHangfireDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var iso))
                return DateTime.SpecifyKind(iso, DateTimeKind.Utc);
            if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var unixMs))
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
            return null;
        }

        /// <summary>
        /// 🛡️ T4: detectar disputas 'Pending' cuyo plazo de respuesta del experto (48h) ya pasó SIN
        /// respuesta. La respuesta del experto es OPCIONAL (solo para aportar pruebas si quiere); que
        /// NO responda NO resuelve la disputa ni mueve dinero. La resolución es SIEMPRE MANUAL del
        /// admin (DisputeController.ResolveDispute → refund_client / pay_expert).
        ///
        /// Por tanto este job NO auto-resuelve nada y NO mueve dinero: solo (a) cierra disputas
        /// huérfanas cuyo hire ya salió de 'disputed' (otro flow lo gestionó) y (b) avisa al admin UNA
        /// sola vez de que la ventana del experto venció y la disputa está lista para su resolución
        /// manual (dedup vía Dispute.AdminAlertedAt). NO hace flip a 'Resolving', NO encola
        /// distribución de dinero → no hay bucle Pending↔Resolving ni reembolsos automáticos.
        ///
        /// Batch de 50 por ejecución para no saturar en caso de backlog.
        /// </summary>
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
        [Hangfire.AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 0, 60, 300 })]
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

            int alerted = 0, orphansClosed = 0, alreadyAlerted = 0, errors = 0;
            foreach (var disputeId in staleDisputeIds)
            {
                try
                {
                    var info = await _context.Disputes
                        .AsNoTracking()
                        .Where(d => d.Id == disputeId)
                        .Select(d => new
                        {
                            HireStatus = d.SearchHire.Status != null ? d.SearchHire.Status.StatusValue : null,
                            d.AdminAlertedAt
                        })
                        .FirstOrDefaultAsync();
                    if (info == null) continue;

                    // 🛡️ F27: si el hire asociado ya NO está en 'disputed' (un webhook lo movió fuera:
                    // chargeback, resolución por otro flow, etc.), la disputa quedó huérfana. La cerramos
                    // (Pending→Resolved) para que no se re-evalúe indefinidamente. No mueve dinero — el flow
                    // que cambió el estado del hire ya lo gestionó.
                    if (info.HireStatus != null && info.HireStatus != "disputed")
                    {
                        await _context.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE \"Disputes\" SET \"Status\" = 'Resolved' WHERE \"Id\" = {disputeId} AND \"Status\" = 'Pending'");

                        await _loggingService.LogWarningAsync(
                            message: "T4/F27: Dispute huérfana cerrada (hire ya no está 'disputed')",
                            details: $"Dispute {disputeId}: su SearchHire está ahora en '{info.HireStatus}' (no 'disputed'), seguramente movido por un webhook u otro flow. Cerrada Pending→Resolved. No mueve dinero — el flow que cambió el estado del hire ya lo gestionó.",
                            userId: null,
                            source: "PlatformMaintenanceService.EscalateStaleDisputesAsync.T4",
                            relatedEntityType: "Dispute",
                            relatedEntityId: disputeId);

                        orphansClosed++;
                        continue;
                    }

                    // Ya avisado: no repetir. La disputa permanece 'Pending' esperando la resolución
                    // MANUAL del admin (la respuesta del experto era opcional y su ausencia no la cambia).
                    if (info.AdminAlertedAt != null)
                    {
                        alreadyAlerted++;
                        continue;
                    }

                    // Aviso ÚNICO al admin: la ventana de respuesta del experto venció; la disputa está
                    // lista para su resolución MANUAL. NO se mueve dinero ni se cambia el estado de la
                    // disputa: sigue 'Pending' hasta que el admin la resuelva. Marcamos AdminAlertedAt de
                    // forma atómica (solo si sigue sin avisar y 'Pending') para no volver a avisar.
                    var marked = await _context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"Disputes\" SET \"AdminAlertedAt\" = (CURRENT_TIMESTAMP AT TIME ZONE 'UTC') WHERE \"Id\" = {disputeId} AND \"AdminAlertedAt\" IS NULL AND \"Status\" = 'Pending'");
                    if (marked == 0)
                    {
                        alreadyAlerted++;
                        continue; // otra réplica avisó primero (o ya no está Pending)
                    }

                    await _loggingService.LogWarningAsync(
                        message: "T4: disputa lista para resolución MANUAL (venció la ventana de respuesta del experto)",
                        details: $"Dispute {disputeId}: pasó ExpertResponseDeadline sin respuesta del experto (la respuesta es OPCIONAL). La disputa NO se resuelve automáticamente: permanece 'Pending' esperando tu resolución manual (refund_client / pay_expert) en el panel de disputas. No se ha movido dinero.",
                        userId: null,
                        source: "PlatformMaintenanceService.EscalateStaleDisputesAsync.T4",
                        relatedEntityType: "Dispute",
                        relatedEntityId: disputeId);

                    alerted++;
                }
                catch (Exception ex)
                {
                    errors++;
                    await _loggingService.LogCriticalAsync(
                        message: "T4: error procesando stale dispute",
                        details: $"Dispute {disputeId}: {ex.Message}. Se reintentará en el próximo ciclo (no se ha movido dinero ni cambiado el estado de la disputa).",
                        userId: null,
                        source: "PlatformMaintenanceService.EscalateStaleDisputesAsync.T4",
                        relatedEntityType: "Dispute",
                        relatedEntityId: disputeId);
                }
            }

            await _loggingService.LogInfoAsync(
                message: $"T4: stale disputes procesadas ({alerted} avisadas al admin, {orphansClosed} huérfanas cerradas, {alreadyAlerted} ya avisadas, {errors} errores)",
                details: $"Total candidatas: {staleDisputeIds.Count}. Recordatorio: las disputas se resuelven SOLO manualmente por el admin; el timeout del experto no mueve dinero.",
                userId: null,
                source: "PlatformMaintenanceService.EscalateStaleDisputesAsync.T4",
                relatedEntityType: "Dispute",
                relatedEntityId: null);
        }

        /// <summary>
        /// 🛡️ HUECO 1b — red de seguridad (defensa en profundidad sobre ProcessExpiredSellerBookingsAsync).
        ///
        /// PROBLEMA
        /// --------
        /// En la captura diferida modo vendedor (SellerBookingController.Confirm), EnsureCapturedAsync
        /// captura con éxito (dinero COBRADO, el PI pasa a `succeeded`) PERO el SaveChanges/Commit
        /// POSTERIOR falla con una excepción no transitoria → rollback: la cita NO se persiste y el
        /// hire queda con CaptureStatus="Authorized" SIN Appointment. Los datos del hueco venían en el
        /// request y no se guardan → NO se puede recrear la cita. La resolución correcta es REEMBOLSAR
        /// 100% al comprador.
        ///
        /// CRÍTICO — no romper el auto-rescate
        /// ----------------------------------
        /// Mientras el SellerBookingDeadline (48h) NO haya vencido, el vendedor puede reintentar el
        /// confirm (el token sigue válido tras el rollback) y el sistema se auto-cura (PI ya succeeded
        /// → EnsureCaptured no-op → crea la cita → commit). Por eso este job SOLO actúa DESPUÉS de que
        /// venza la ventana de 48h (SellerBookingDeadline &lt; now): es defensa en profundidad respecto
        /// a ProcessExpiredSellerBookingsAsync.
        ///
        /// DISTINCIÓN vs ProcessExpiredSellerBookingsAsync
        /// -----------------------------------------------
        /// Aquel job cubre "vendedor no reservó" (PI normalmente en requires_capture → N10 lo CANCELA,
        /// 0 € de fee). Este cubre el caso patológico en el que el PI YA está `succeeded` (dinero
        /// cobrado) pero sin cita: solo actúa si el PI está succeeded; si está requires_capture/etc.
        /// hace SKIP (no pisa al otro job); si está canceled hace SKIP.
        ///
        /// IDEMPOTENCIA
        /// ------------
        /// (a) WHERE descarta hires que ya tienen una FT "Refund"; (b) ProcessMoneyDistributionAsync
        /// tiene su propio guard existingRefund + idempotency key de Stripe. Doble garantía → ejecutar
        /// el job dos veces NO genera doble reembolso.
        ///
        /// Batch de 100; try/catch POR hire para no abortar el lote.
        /// </summary>
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
        [Hangfire.AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 0, 60, 300 })]
        public async Task ReconcileCapturedSellerHiresWithoutAppointmentAsync()
        {
            List<int> candidateIds;
            try
            {
                var now = DateTime.UtcNow;
                // Candidatos: modo vendedor (SellerBookingDeadline != null), ventana de 48h ya
                // vencida (deadline < now → ya no hay auto-rescate posible), SIN cita, hire NO
                // finalizado, y SIN FT "Refund" previa (idempotencia: no re-reembolsar).
                candidateIds = await _context.SearchHires
                    .Where(h => h.SellerBookingDeadline != null
                                && h.SellerBookingDeadline < now
                                && h.Appointment == null
                                && (h.Status == null || !h.Status.IsFinalizationStatus)
                                && !_context.FinancialTransactions.Any(ft =>
                                        ft.RelatedEntityType == "SearchHire"
                                        && ft.RelatedEntityId == h.Id
                                        && ft.TransactionType == "Refund"))
                    .OrderBy(h => h.SellerBookingDeadline)
                    .Select(h => h.Id)
                    .Take(100)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                await _loggingService.LogWarningAsync(
                    message: "HUECO 1b: fallo al consultar hires seller capturados sin cita",
                    details: ex.Message,
                    source: "PlatformMaintenanceService.ReconcileCapturedSellerHiresWithoutAppointmentAsync");
                return;
            }

            if (candidateIds.Count == 0) return;

            var piService = new PaymentIntentService();
            int reconciled = 0, skippedNotCaptured = 0, errors = 0;

            // try/catch POR hire: un fallo en uno no aborta el lote.
            foreach (var hireId in candidateIds)
            {
                try
                {
                    // Localizar el PaymentIntentId vía la FT ServicePayment (patrón del archivo).
                    var ft = await _context.FinancialTransactions
                        .AsNoTracking()
                        .Where(t => t.RelatedEntityType == "SearchHire" && t.RelatedEntityId == hireId
                                    && t.TransactionType == "ServicePayment")
                        .OrderByDescending(t => t.Id)
                        .FirstOrDefaultAsync();
                    if (ft == null || string.IsNullOrEmpty(ft.StripePaymentIntentId)) continue;

                    var pi = await piService.GetAsync(ft.StripePaymentIntentId);

                    // SOLO actuar si el PI está `succeeded` (= HUECO 1b real: dinero capturado pero
                    // sin cita). requires_capture/requires_action/etc → SKIP (lo maneja
                    // ProcessExpiredSellerBookingsAsync; no pisar). canceled → SKIP (nada que hacer).
                    if (pi.Status != "succeeded")
                    {
                        skippedNotCaptured++;
                        continue;
                    }

                    // Reclamar: re-verificar que sigue sin cita y anular el token (mutex) si aún no
                    // lo está, para que un confirm tardío no pueda crear la cita en paralelo.
                    var hire = await _context.SearchHires
                        .FirstOrDefaultAsync(h => h.Id == hireId && h.Appointment == null);
                    if (hire == null) continue; // se creó la cita entre la query y aquí → no tocar
                    if (hire.SellerBookingToken != null)
                    {
                        hire.SellerBookingToken = null;
                        await _context.SaveChangesAsync();
                    }

                    // MISMA distribución que el job de expiración. Con el PI succeeded, la rama N10
                    // NO cancela (no puede) → reembolsa el PI al 100% y finaliza el hire a 'cancelled'.
                    // El statusValue DEBE empezar por "appointment_cancelled" para que se aplique el
                    // reparto de cancelación (cliente 100, experto 0) y NO el de completado.
                    // Idempotente por el guard existingRefund de ProcessMoneyDistributionAsync.
                    Hangfire.BackgroundJob.Enqueue<newApi.Services.StripeRefundService>(svc =>
                        svc.ProcessMoneyDistributionAsync(
                            hireId,
                            "appointment_cancelled_by_client_gt24h",
                            "Reconciliación HUECO 1b: pago capturado sin cita persistida; reembolso 100% al comprador.",
                            null,
                            true));

                    await _loggingService.LogCriticalAsync(
                        message: "HUECO 1b auto-reconciliado: pago capturado sin cita → reembolso 100% al comprador",
                        details: $"SearchHire {hireId}: PI {pi.Id} está 'succeeded' (dinero cobrado) pero el hire no tiene Appointment y la ventana de 48h del vendedor ya venció (sin auto-rescate posible). Síntoma del crash entre la captura y el commit en SellerBookingController.Confirm. Token anulado (mutex) y distribución de reembolso 100% encolada (idempotente por existingRefund).",
                        userId: hire.ClientId,
                        source: "PlatformMaintenanceService.ReconcileCapturedSellerHiresWithoutAppointmentAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hireId,
                        additionalData: new { PaymentIntentId = pi.Id, hire.ClientId, hire.ExpertId });
                    reconciled++;
                }
                catch (Exception exHire)
                {
                    errors++;
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL HUECO 1b: fallo reconciliando hire seller capturado sin cita (posible reembolso pendiente manual)",
                        details: $"SearchHire {hireId}: {exHire.Message}. ACCIÓN: verificar si el PI está 'succeeded' y, si no hay reembolso, reembolsar 100% al comprador a mano. Se reintentará en el próximo ciclo.",
                        userId: null,
                        source: "PlatformMaintenanceService.ReconcileCapturedSellerHiresWithoutAppointmentAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hireId);
                }
            }

            await _loggingService.LogInfoAsync(
                message: $"HUECO 1b: reconciliación completada ({reconciled} reembolsos encolados, {skippedNotCaptured} omitidos (PI no capturado), {errors} errores)",
                details: $"Total candidatos (seller, post-48h, sin cita, sin Refund, no finalizados): {candidateIds.Count}.",
                userId: null,
                source: "PlatformMaintenanceService.ReconcileCapturedSellerHiresWithoutAppointmentAsync",
                relatedEntityType: "SearchHire",
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

        // 🤝 Magic link del vendedor: hires en modo "seller" cuyo plazo para reservar ha vencido
        // SIN cita → el vendedor no colaboró → reembolso 100% al comprador. "Reclama" el hire
        // anulando el token (re-check token!=null && sin cita) y encola la distribución de dinero.
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
        [Hangfire.AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 0, 60, 300 })]
        public async Task ProcessExpiredSellerBookingsAsync()
        {
            List<int> expiredIds;
            try
            {
                var now = DateTime.UtcNow;
                // 🛡️ Self-healing: incluimos también hires YA reclamados (token==null) que
                // quedaron sin reembolso — recupera el crash entre SaveChanges(token=null) y
                // Enqueue. ProcessMoneyDistributionAsync es idempotente (advisory lock +
                // re-verificación + refunds Stripe idempotentes), así que reencolar un hire
                // que SÍ llegó a reembolsarse es inocuo (no hay doble refund).
                expiredIds = await _context.SearchHires
                    .Where(h => h.SellerBookingDeadline != null
                                && h.SellerBookingDeadline < now
                                && h.Appointment == null
                                && (h.SellerBookingToken != null
                                    || !_context.FinancialTransactions.Any(ft =>
                                            ft.RelatedEntityType == "SearchHire"
                                            && ft.RelatedEntityId == h.Id
                                            && ft.TransactionType == "Refund")))
                    .Select(h => h.Id)
                    .Take(200)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                await _loggingService.LogWarningAsync(
                    message: "ProcessExpiredSellerBookingsAsync: fallo al consultar hires vencidos",
                    details: ex.Message,
                    source: "PlatformMaintenanceService.ProcessExpiredSellerBookingsAsync");
                return;
            }

            // try/catch POR hire: un fallo en uno no aborta el lote.
            foreach (var hireId in expiredIds)
            {
                try
                {
                    // Reclamar: solo procede si sigue sin reservar. Anular el token = mutex: a partir
                    // de aquí el confirm del vendedor (busca por token) ya no podrá crear la cita,
                    // así que no puede coexistir "cita confirmada + reembolso".
                    var hire = await _context.SearchHires
                        .FirstOrDefaultAsync(h => h.Id == hireId && h.Appointment == null);
                    if (hire == null) continue;
                    // Anular el token actúa de mutex frente al confirm del vendedor. Si ya estaba
                    // null (hire huérfano de un crash previo) seguimos: ProcessMoneyDistributionAsync
                    // re-verifica e idempotentemente no duplica el reembolso.
                    if (hire.SellerBookingToken != null)
                    {
                        hire.SellerBookingToken = null;
                        await _context.SaveChangesAsync();
                    }

                    // Reembolso 100% al comprador + cancelar el hire.
                    // ⚠️ CRÍTICO: el estado DEBE empezar por "appointment_cancelled" para que
                    // ProcessMoneyDistributionAsync IGNORE el snapshot V8 de % (reparto de COMPLETADO:
                    // experto 95%) y use el de cancelación. Con "cancelled" pagaría al EXPERTO.
                    // "appointment_cancelled_by_client_gt24h" → (cliente 100, experto 0, plataforma 0),
                    // es finalización, mapea el hire a 'cancelled' y NO penaliza al experto.
                    Hangfire.BackgroundJob.Enqueue<newApi.Services.StripeRefundService>(svc =>
                        svc.ProcessMoneyDistributionAsync(
                            hireId,
                            "appointment_cancelled_by_client_gt24h",
                            "Vendedor no reservó la cita en el plazo: reembolso 100% al comprador.",
                            null,
                            true));
                }
                catch (Exception exHire)
                {
                    // Si el token se anuló pero el encolado del reembolso falló, el comprador quedaría
                    // sin servicio y sin dinero → CRÍTICO para que el admin lo reembolse a mano.
                    await _loggingService.LogCriticalAsync(
                        message: "Fallo al procesar expiración de reserva del vendedor (posible reembolso pendiente manual)",
                        details: $"SearchHire {hireId}: {exHire.Message}. ACCIÓN: verificar si el token se anuló y, si no hay reembolso, reembolsar 100% al comprador a mano.",
                        userId: 0,
                        source: "PlatformMaintenanceService.ProcessExpiredSellerBookingsAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hireId);
                }
            }
        }

        // ⚓ Magic link del EXPERTO: red de seguridad del timer "expert_confirmation". El timer
        // Hangfire (ProcessAppointmentTimerAsync) auto-cancela la cita si el experto no responde en
        // plazo, pero si ESE job se perdió (crash entre Schedule y Save, storage purgado, etc.) la
        // cita quedaría pending_expert_confirmation para siempre con el PI autorizado bloqueando la
        // tarjeta del cliente. Este watchdog (cada 15 min) detecta esos hires y los reembolsa al 100%
        // anulando el token (mutex frente a approve/reject) y encolando la distribución de dinero.
        // CLON estructural de ProcessExpiredSellerBookingsAsync.
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
        [Hangfire.AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 0, 60, 300 })]
        public async Task ProcessExpiredExpertConfirmationsAsync()
        {
            List<int> expiredIds;
            try
            {
                var now = DateTime.UtcNow;
                // Citas aún en pending_expert_confirmation cuyo plazo (ExpertConfirmationDeadline)
                // ya venció y siguen con token activo (sin reclamar por approve/reject ni por el timer).
                expiredIds = await _context.SearchHires
                    .Where(h => h.ExpertConfirmationDeadline != null
                                && h.ExpertConfirmationDeadline < now
                                && h.ExpertConfirmationToken != null
                                && h.Appointment != null
                                && h.Appointment.Status != null
                                && h.Appointment.Status.StatusValue == "appointment_pending_expert_confirmation")
                    .Select(h => h.Id)
                    .Take(200)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                await _loggingService.LogWarningAsync(
                    message: "ProcessExpiredExpertConfirmationsAsync: fallo al consultar confirmaciones vencidas",
                    details: ex.Message,
                    source: "PlatformMaintenanceService.ProcessExpiredExpertConfirmationsAsync");
                return;
            }

            // try/catch POR hire: un fallo en uno no aborta el lote.
            foreach (var hireId in expiredIds)
            {
                try
                {
                    // Recargar y reclamar: anular el token = mutex. A partir de aquí el approve/reject
                    // del experto (buscan por token) ya no podrán confirmar la cita, así que no puede
                    // coexistir "cita confirmada/capturada + reembolso".
                    var hire = await _context.SearchHires
                        .FirstOrDefaultAsync(h => h.Id == hireId);
                    if (hire == null) continue;
                    // Si el token ya es null, otro actor (approve/reject/timer) lo reclamó entre la
                    // query y aquí → no procedemos (no duplicar la distribución).
                    if (hire.ExpertConfirmationToken == null) continue;

                    hire.ExpertConfirmationToken = null;
                    await _context.SaveChangesAsync();

                    // Reembolso 100% al comprador + cancelar el hire. Mismo motivo/razón que usa el
                    // timer principal (case "expert_confirmation"): "appointment_cancelled_by_expert_no_response"
                    // empieza por "appointment_cancelled" → ProcessMoneyDistributionAsync usa el reparto de
                    // cancelación (cliente 100, experto 0) y la rama N10 cancela el PI no capturado (0 € fee).
                    Hangfire.BackgroundJob.Enqueue<newApi.Services.StripeRefundService>(svc =>
                        svc.ProcessMoneyDistributionAsync(
                            hireId,
                            "appointment_cancelled_by_expert_no_response",
                            "Watchdog: expert confirmation deadline passed (no response).",
                            null,
                            true));
                }
                catch (Exception exHire)
                {
                    // Si el token se anuló pero el encolado del reembolso falló, el comprador quedaría
                    // sin servicio y sin liberar la autorización → CRÍTICO para que el admin lo resuelva.
                    await _loggingService.LogCriticalAsync(
                        message: "Fallo al procesar expiración de confirmación del experto (posible reembolso pendiente manual)",
                        details: $"SearchHire {hireId}: {exHire.Message}. ACCIÓN: verificar si el token se anuló y, si no hay reembolso, cancelar el PI / reembolsar 100% al comprador a mano.",
                        userId: 0,
                        source: "PlatformMaintenanceService.ProcessExpiredExpertConfirmationsAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hireId);
                }
            }
        }

        // 🛡️ W3 FIX (Round 8 A8): DisableConcurrentExecution para evitar duplicación en HPA multi-replica
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 1800)]
        [Hangfire.AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 0, 60, 300 })]
        public async Task ProcessExpiringPaymentIntentsAsync()
        {
            // SearchHires con CaptureStatus="Pending" y CreatedAt > 6.5 días: el PI autorizado está
            // a punto de expirar en Stripe (7 días). Intentamos cancelar para liberar la autorización
            // del cliente (no podemos capturar a estas alturas — si pudiéramos, el flow normal o el
            // outbox ya lo habría hecho). Marcamos CaptureStatus="Failed" para que admin investigue.
            // 🛡️ R5 (bajado de -6.5d a -4.5d): la cadena de captura diferida en modo vendedor consume
            // más ventana de los 7 días de autorización de Stripe (seller booking 36h + confirmación del
            // experto 36h = hasta ~3 días antes incluso de poder capturar). Un cutoff de -6.5d dejaba muy
            // poco margen para reaccionar antes de la expiración automática del PI; -4.5d adelanta la red
            // de seguridad para liberar la autorización del cliente con holgura.
            var cutoff = DateTime.UtcNow.AddDays(-4.5);
            List<DataLayer.Models.PostGresModels.SearchHire> nearExpiry;
            try
            {
                // 🛡️ Captura diferida (modo vendedor): incluimos también "Authorized" como red de
                // seguridad. Si el job de 48h (ProcessExpiredSellerBookingsAsync) fallara, un PI
                // autorizado-sin-capturar >6.5d se cancela aquí antes de que Stripe lo expire a 7d.
                nearExpiry = await _context.SearchHires
                    .Where(sh => (sh.CaptureStatus == "Pending" || sh.CaptureStatus == "Authorized") && sh.CreatedAt < cutoff)
                    .OrderBy(sh => sh.CreatedAt) // los más próximos a expirar primero
                    .Take(100)
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

                // Persistir por iteración: si el SaveChanges falla, solo se pierde el reflejo de
                // ESTE hire (no de todos). La IdempotencyKey de CancelAsync evita doble cancelación
                // en el reintento del job.
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (Exception saveEx)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL R5: SaveChanges failed after canceling near-expiry PI",
                        details: $"Cancelación en Stripe ya ocurrió pero el CaptureStatus local no se persistió. SearchHire {hire.Id}. Error: {saveEx.Message}",
                        userId: hire.ClientId,
                        source: "PlatformMaintenanceService.ProcessExpiringPaymentIntentsAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hire.Id);
                }
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
                    "dispute_resolved_expert",
                    // 🛡️ F01/F18: hires atascados en 'transfer_failed' también deben entrar en el
                    // digest/alerta de reconciliación — su dinero quedó sin distribuir tras un fallo
                    // de transfer y requieren reintento (watchdog RetryTransferFailedHiresAsync) o
                    // reconciliación manual.
                    "transfer_failed"
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
        /// 🛡️ F18 FIX: watchdog que reintenta SearchHires atascados en 'transfer_failed'. Cuando un
        /// transfer al experto falla (capability perdida, evento Hangfire perdido, etc.) el hire queda
        /// en 'transfer_failed' con el dinero sin distribuir y, si nadie lo reencola, se queda zombi.
        ///
        /// Estrategia: para cada hire en 'transfer_failed' con UpdatedAt &gt; 5h (margen para que el
        /// reintento inline/outbox del flow normal haya tenido su oportunidad), encolar
        /// RetryMoneyDistributionJobAsync — que YA es idempotente y re-verifica el estado del hire
        /// antes de mover dinero. Batch de 50 por ejecución. Cada iteración va en su try/catch para
        /// no abortar el batch por un fallo puntual de encolado.
        /// </summary>
        // 🛡️ W3 FIX (Round 8 A8): DisableConcurrentExecution para evitar duplicación en HPA multi-replica
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 1200)]
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task RetryTransferFailedHiresAsync()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-5);
                var transferFailed = await _context.SearchHires
                    .AsNoTracking()
                    .Include(sh => sh.Status)
                    .Where(sh => sh.Status != null
                              // ✅ FIX AUDITORÍA [M2] Medium: este watchdog SOLO recupera hires en "transfer_failed". Un crash entre el transfer en Stripe (RefundService ~1495) y el commit del Payout (~2318) deja el hire en "completed" con transfer ya ejecutado y sin fila Payout → es INVISIBLE para este filtro y nunca se reencola.
                              // Disparo/ataque: crash/redeploy durante la distribución de dinero; el estado "transfer_failed" casi nunca se alcanza (ni siquiera los catch de RefundService lo setean tras un transfer creado).
                              // Fix APLICADO: ReconcileCompletedWithoutPayoutAsync (abajo) detecta "hire finalizado SIN fila Payout pero CON transfer en Stripe" (cotejo por metadata["searchHireId"] + StripeTransferId idempotente) e inserta el Payout faltante o reencola la distribución.
                              && sh.Status.StatusValue == "transfer_failed"
                              && sh.UpdatedAt < cutoff)
                    .OrderBy(sh => sh.UpdatedAt)
                    .Take(50)
                    .ToListAsync();

                if (transferFailed.Count == 0) return;

                foreach (var hire in transferFailed)
                {
                    try
                    {
                        Hangfire.BackgroundJob.Schedule<StripeRefundService>(
                            s => s.RetryMoneyDistributionJobAsync(
                                hire.Id,
                                "transfer_failed",
                                "Retry transfer after failure (watchdog F18)",
                                null),
                            TimeSpan.FromSeconds(2));

                        var ageHours = (DateTime.UtcNow - (hire.UpdatedAt ?? hire.CreatedAt)).TotalHours;
                        await _loggingService.LogWarningAsync(
                            message: "F18: SearchHire en 'transfer_failed' reencolado por watchdog",
                            details: $"SearchHire {hire.Id} lleva {ageHours:F1}h en 'transfer_failed'. Encolado RetryMoneyDistributionJobAsync (idempotente, re-verifica estado del hire antes de mover dinero) para reintentar la distribución/transfer al experto.",
                            userId: hire.ClientId,
                            source: "PlatformMaintenanceService.RetryTransferFailedHiresAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: hire.Id);
                    }
                    catch (Exception ex)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL F18: failed to re-enqueue transfer_failed hire",
                            details: $"SearchHire {hire.Id}: error encolando RetryMoneyDistributionJobAsync: {ex.Message}. Quedará en 'transfer_failed' hasta el próximo ciclo del watchdog.",
                            userId: hire.ClientId,
                            source: "PlatformMaintenanceService.RetryTransferFailedHiresAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: hire.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL F18: RetryTransferFailedHiresAsync failed",
                    details: ex.Message,
                    userId: null,
                    source: "PlatformMaintenanceService.RetryTransferFailedHiresAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: null);
            }
        }

        /// <summary>
        /// ✅ FIX AUDITORÍA [M2] Medium — reconciliación del hueco no atómico transfer↔Payout.
        ///
        /// PROBLEMA
        /// --------
        /// En RefundService.ProcessMoneyDistributionAsync el dinero SALE de Stripe en
        /// transferSvc.CreateAsync (~1495) PERO la fila FinancialTransaction "Payout" no se
        /// commitea hasta ~2318. El estado del hire ya quedó commiteado como "completed" en
        /// FASE 2. Si el proceso muere en esa ventana: el transfer queda real en Stripe, SIN
        /// fila Payout, y el hire sigue "completed" (NO "transfer_failed") → es INVISIBLE para
        /// RetryTransferFailedHiresAsync (que solo filtra "transfer_failed"). Además habilita
        /// doble pago si alguien reintenta la distribución con otro statusValue.
        ///
        /// ESTRATEGIA (idempotente y conservadora)
        /// ---------------------------------------
        /// 1. Seleccionar SearchHires en estados de finalización, con experto, UpdatedAt &gt; 1h
        ///    (margen para que el flow normal/outbox cierre) y dentro de los últimos 7 días, que
        ///    NO tengan ninguna FT "Payout". Batch de 50.
        /// 2. Por cada candidato consultar Stripe (TransferService.ListAsync filtrado por la
        ///    cuenta del experto + ventana temporal) y casar por metadata["searchHireId"] (la
        ///    clave la escribe RefundService ~1464).
        ///    - Si existe transfer real y NO hay fila Payout → INSERTAR la fila Payout faltante
        ///      replicando EXACTAMENTE el patrón de RefundService ~2296-2308. Re-chequeo por
        ///      StripeTransferId antes de insertar (idempotente ante carreras/replays).
        ///    - Si NO existe transfer y se esperaba pago al experto (&gt; 0) → reencolar
        ///      RetryMoneyDistributionJobAsync (que ya es idempotente y re-verifica estado).
        /// 3. Cada candidato en su try/catch para no abortar el batch; resumen al final.
        /// </summary>
        // 🛡️ W3 FIX (Round 8 A8): DisableConcurrentExecution para evitar duplicación en HPA multi-replica
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 1200)]
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task ReconcileCompletedWithoutPayoutAsync()
        {
            List<DataLayer.Models.PostGresModels.SearchHire> candidates;
            try
            {
                var staleCutoff = DateTime.UtcNow.AddHours(-1);   // margen para que el flow normal/outbox cierre
                var windowCutoff = DateTime.UtcNow.AddDays(-7);   // no escanear toda la historia
                // Mismos estados de finalización que DetectUnreconciledFinalizedHiresAsync (con
                // distribución de dinero al experto esperada).
                var finalizationStatusValues = new[]
                {
                    "completed",
                    "dispute_resolved_expert"
                };

                candidates = await _context.SearchHires
                    .AsNoTracking()
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Expert)
                        .ThenInclude(u => u.ExpertProfile)
                    .Where(sh => sh.Status != null
                              && sh.ExpertId != null
                              && finalizationStatusValues.Contains(sh.Status.StatusValue)
                              && sh.UpdatedAt < staleCutoff
                              && sh.UpdatedAt >= windowCutoff
                              && !_context.FinancialTransactions.Any(ft =>
                                    ft.RelatedEntityType == "SearchHire"
                                    && ft.RelatedEntityId == sh.Id
                                    && ft.TransactionType == "Payout"))
                    .OrderBy(sh => sh.UpdatedAt)
                    .Take(50)
                    .ToListAsync();
            }
            catch (Exception readEx)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL M2: query de hires finalizados sin Payout falló",
                    details: $"No se pudo leer candidatos a reconciliación. Error: {readEx.Message}. Próxima ejecución reintentará.",
                    userId: null,
                    source: "PlatformMaintenanceService.ReconcileCompletedWithoutPayoutAsync.M2",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: null);
                return;
            }

            if (candidates.Count == 0) return;

            int reconciled = 0, reEnqueued = 0, alreadyOk = 0, errors = 0;
            var transferSvc = new TransferService();

            foreach (var hire in candidates)
            {
                try
                {
                    var statusValue = hire.Status?.StatusValue ?? "completed";

                    // Localizar el transfer real en Stripe para este hire. Stripe no permite
                    // filtrar ListAsync por metadata arbitraria → acotamos por la cuenta destino
                    // del experto (si la conocemos) + ventana temporal, y casamos en memoria por
                    // metadata["searchHireId"] (clave escrita en RefundService ~1464).
                    var expertStripeAccountId = hire.Expert?.ExpertProfile?.StripeAccountId;
                    var listOptions = new TransferListOptions
                    {
                        Limit = 100,
                        // Ventana generosa alrededor de UpdatedAt para capturar el transfer creado
                        // justo antes del crash (incluye reintentos del flow normal).
                        Created = new DateRangeOptions
                        {
                            GreaterThanOrEqual = (hire.UpdatedAt ?? hire.CreatedAt).AddHours(-24),
                            LessThanOrEqual = DateTime.UtcNow
                        }
                    };
                    if (!string.IsNullOrEmpty(expertStripeAccountId))
                    {
                        listOptions.Destination = expertStripeAccountId;
                    }

                    Transfer matchedTransfer = null;
                    var hireIdStr = hire.Id.ToString();
                    var transfers = await transferSvc.ListAsync(listOptions);
                    foreach (var t in transfers)
                    {
                        if (t.Metadata != null
                            && t.Metadata.TryGetValue("searchHireId", out var metaHireId)
                            && metaHireId == hireIdStr)
                        {
                            matchedTransfer = t;
                            break;
                        }
                    }

                    if (matchedTransfer != null)
                    {
                        // RAMA A: transfer real existe pero no hay fila Payout → insertar la
                        // fila faltante. Idempotente: re-chequear por StripeTransferId antes
                        // de insertar (otra réplica/replay pudo insertarla ya).
                        var existsByTransfer = await _context.FinancialTransactions
                            .AsNoTracking()
                            .AnyAsync(ft => ft.StripeTransferId == matchedTransfer.Id
                                         && ft.TransactionType == "Payout");
                        if (existsByTransfer)
                        {
                            alreadyOk++;
                            continue;
                        }

                        // Recuperar el StripePaymentIntentId del ServicePayment para trazabilidad
                        // (igual que RefundService ~2306). Best-effort: si no está, queda null.
                        var servicePaymentPi = await _context.FinancialTransactions
                            .AsNoTracking()
                            .Where(ft => ft.RelatedEntityType == "SearchHire"
                                      && ft.RelatedEntityId == hire.Id
                                      && ft.TransactionType == "ServicePayment")
                            .OrderByDescending(ft => ft.Id)
                            .Select(ft => ft.StripePaymentIntentId)
                            .FirstOrDefaultAsync();

                        // Importe/divisa tomados del transfer REAL de Stripe (fuente de verdad
                        // del dinero ya movido), no de cálculos locales.
                        var amountDecimal = matchedTransfer.Amount / 100m;
                        var transferCurrency = string.IsNullOrEmpty(matchedTransfer.Currency)
                            ? (hire.Currency ?? "EUR")
                            : matchedTransfer.Currency.ToUpperInvariant();

                        // Patrón EXACTO de RefundService ~2296-2308.
                        var expertTx = new DataLayer.Models.PostGresModels.FinancialTransaction
                        {
                            UserId = hire.ExpertId.Value,
                            Amount = Math.Round(amountDecimal, 2),
                            AmountCents = matchedTransfer.Amount,
                            Currency = transferCurrency,
                            TransactionType = "Payout",
                            RelatedEntityType = "SearchHire",
                            RelatedEntityId = hire.Id,
                            StripeTransferId = matchedTransfer.Id,
                            StripePaymentIntentId = servicePaymentPi,
                            CreatedAt = matchedTransfer.Created
                        };
                        _context.FinancialTransactions.Add(expertTx);

                        try
                        {
                            await _context.SaveChangesAsync();
                        }
                        catch (DbUpdateException)
                        {
                            // Carrera con otra réplica/replay que insertó el mismo Payout: la
                            // restricción única absorbe el conflicto → idempotente, no es error.
                            _context.Entry(expertTx).State = EntityState.Detached;
                            alreadyOk++;
                            continue;
                        }

                        await _loggingService.LogWarningAsync(
                            message: "M2: fila Payout faltante reconciliada desde transfer real de Stripe",
                            details: $"SearchHire {hire.Id} ('{statusValue}') tenía transfer {matchedTransfer.Id} ejecutado en Stripe ({amountDecimal:F2} {transferCurrency}) pero SIN fila FinancialTransaction Payout — síntoma del crash en la ventana CreateAsync↔commit (RefundService ~1495-2318). Fila Payout insertada idempotentemente. No se ha movido dinero: solo se ha reflejado en el ledger el transfer ya existente.",
                            userId: hire.ExpertId,
                            source: "PlatformMaintenanceService.ReconcileCompletedWithoutPayoutAsync.M2",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: hire.Id);
                        reconciled++;
                    }
                    else
                    {
                        // RAMA B: no hay transfer en Stripe. Si se esperaba pago al experto (>0),
                        // la distribución nunca llegó a mover dinero → reencolar el job idempotente
                        // (re-verifica el estado del hire antes de mover dinero). Si el split
                        // esperado al experto es 0 (todo refund/plataforma), no hay nada que pagar.
                        var expertPct = hire.ExpertPercentageSnapshot;
                        var expectedExpertAmount = expertPct.HasValue
                            ? hire.Amount * (expertPct.Value / 100m)
                            : hire.Amount; // sin snapshot, usar Amount como proxy de "se esperaba pago"
                        if (expectedExpertAmount <= 0)
                        {
                            alreadyOk++;
                            continue;
                        }

                        Hangfire.BackgroundJob.Enqueue<StripeRefundService>(
                            s => s.RetryMoneyDistributionJobAsync(
                                hire.Id,
                                statusValue,
                                "M2-reconcile",
                                null));

                        var ageHours = (DateTime.UtcNow - (hire.UpdatedAt ?? hire.CreatedAt)).TotalHours;
                        await _loggingService.LogWarningAsync(
                            message: "M2: hire finalizado sin Payout y SIN transfer en Stripe — distribución reencolada",
                            details: $"SearchHire {hire.Id} ('{statusValue}') lleva {ageHours:F1}h finalizado sin fila Payout y NO se encontró transfer en Stripe (metadata searchHireId={hire.Id}). El dinero al experto nunca llegó a moverse. Encolado RetryMoneyDistributionJobAsync (idempotente, re-verifica estado del hire antes de mover dinero) para completar la distribución.",
                            userId: hire.ExpertId,
                            source: "PlatformMaintenanceService.ReconcileCompletedWithoutPayoutAsync.M2",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: hire.Id);
                        reEnqueued++;
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL M2: error reconciliando hire finalizado sin Payout",
                        details: $"SearchHire {hire.Id}: {ex.Message}. Se reintentará en el próximo ciclo (no se ha insertado fila ni reencolado para este candidato).",
                        userId: hire.ExpertId,
                        source: "PlatformMaintenanceService.ReconcileCompletedWithoutPayoutAsync.M2",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hire.Id);
                }
            }

            await _loggingService.LogInfoAsync(
                message: $"M2: reconciliación transfer↔Payout completada ({reconciled} filas Payout insertadas, {reEnqueued} distribuciones reencoladas, {alreadyOk} ya correctos, {errors} errores)",
                details: $"Total candidatos (finalizados sin Payout, últimos 7d, >1h): {candidates.Count}.",
                userId: null,
                source: "PlatformMaintenanceService.ReconcileCompletedWithoutPayoutAsync.M2",
                relatedEntityType: "SearchHire",
                relatedEntityId: null);
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
                                // 🛡️ A1: la dedup buscaba por Title "⏰ Plazo Stripe próximo", pero
                                // LogWarningAsync (sin userNotificationMessage) persiste Title genérico
                                // "⚠️ Advertencia" → el filtro nunca matcheaba → recordatorio diario
                                // repetido toda la ventana de 14d. El Message SÍ se persiste con el
                                // prefijo estable de abajo (fullMessage = message + " - " + details).
                                && n.Message.StartsWith("[Inspecciono · recordatorio] Plazo Stripe próximo"))
                    .Select(n => new { UserId = n.UserId!.Value, n.CreatedAt })
                    .ToListAsync();
                var lastNotifByUser = recentDeadlineNotifications
                    .GroupBy(n => n.UserId)
                    .ToDictionary(g => g.Key, g => g.Max(n => n.CreatedAt));

                // Filtro: deadline en próximos 14 días (MUD-BO), todavía no past_due, experto
                // aprobado. La dedup escalonada se aplica DESPUÉS de cargar candidatos.
                var candidates = await _context.ExpertProfiles
                    .Include(ep => ep.User)
                    // 🛡️ MUD-DN — Antes filtrábamos solo Approved → expertos que ya cayeron a
                    // ActionRequired/RequirementsDue/etc se quedaban sin recordatorios (ya tenían
                    // un banner genérico pero el job de deadlines escalonada 14/7/3/1d nunca
                    // disparaba para ellos). Resultado: pasaban de warning a PastDue sin alerta
                    // urgente "te quedan 24h". Ampliamos a todos los estados donde el experto
                    // PUEDE resolver el problema y donde la cuenta sigue gestionable.
                    .Where(ep => ep.StripeFutureDueAt != null
                              && ep.StripeFutureDueAt > now
                              && ep.StripeFutureDueAt <= deadline
                              && (ep.StripeStatus == DataLayer.Models.PostGresModels.StripeStatus.Approved
                                  || ep.StripeStatus == DataLayer.Models.PostGresModels.StripeStatus.ActionRequired
                                  || ep.StripeStatus == DataLayer.Models.PostGresModels.StripeStatus.RequirementsDue
                                  || ep.StripeStatus == DataLayer.Models.PostGresModels.StripeStatus.RestrictedSoon
                                  || ep.StripeStatus == DataLayer.Models.PostGresModels.StripeStatus.PendingVerification
                                  || ep.StripeStatus == DataLayer.Models.PostGresModels.StripeStatus.UnderReview)
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

        /// <summary>
        /// 🛡️ A2: recordatorio proactivo a expertos que EMPEZARON el alta pero NO la terminaron y
        /// llevan ≥ stalledDays sin completarla. El banner del panel ya los avisa de forma pasiva,
        /// pero quien no vuelve al panel nunca lo ve → este job empuja un email + notificación in-app.
        /// Dedup vía Notifications.Message (no Title; ver A1: el Title persistido es genérico) para no
        /// repetir el aviso dentro de reNotifyDays.
        /// NOTA: la antigüedad se mide con ExpertProfile.CreatedAt (no existe UpdatedAt ni timestamp de
        /// inicio de onboarding). En reseteos (Rejected→NotRequested, mudanza) CreatedAt NO se refresca
        /// → la antigüedad puede sobreestimarse; aceptable (siguen siendo onboarding incompleto).
        /// </summary>
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task NotifyStalledOnboardingExpertsAsync()
        {
            try
            {
                const int stalledDays = 3;       // antigüedad mínima para avisar
                const int reNotifyDays = 7;      // no repetir el aviso dentro de 7 días
                const string notifMessagePrefix = "[Inspecciono · recordatorio] Completa tu alta";
                var now = DateTime.UtcNow;
                var stalledCutoff = now.AddDays(-stalledDays);
                var dedupCutoff = now.AddDays(-reNotifyDays);

                // Dedup: expertos ya avisados en los últimos reNotifyDays (match por Message, ver A1).
                var recentNotifs = await _context.Notifications
                    .AsNoTracking()
                    .Where(n => n.UserId != null
                                && n.CreatedAt >= dedupCutoff
                                && n.Message.StartsWith(notifMessagePrefix))
                    .Select(n => new { UserId = n.UserId!.Value, n.CreatedAt })
                    .ToListAsync();
                var lastNotifByUser = recentNotifs
                    .GroupBy(n => n.UserId)
                    .ToDictionary(g => g.Key, g => g.Max(n => n.CreatedAt));

                // Onboarding empezado y NO terminado, estancado ≥ stalledDays.
                var candidates = await _context.ExpertProfiles
                    .Include(ep => ep.User)
                    .Where(ep => !ep.OnboardingCompleted
                              && ep.CreatedAt <= stalledCutoff
                              && (ep.StripeStatus == DataLayer.Models.PostGresModels.StripeStatus.Pending
                                  || ep.PendingStripeAccountId != null
                                  || ep.StripeAccountId != null)
                              && ep.StripeStatus != DataLayer.Models.PostGresModels.StripeStatus.Rejected
                              && ep.StripeStatus != DataLayer.Models.PostGresModels.StripeStatus.Approved
                              && ep.User != null
                              && !ep.User.IsDeleted)
                    .OrderBy(ep => ep.CreatedAt)
                    .Take(200)
                    .ToListAsync();

                var experts = candidates
                    .Where(ep => !lastNotifByUser.TryGetValue(ep.UserId, out var last) || last < dedupCutoff)
                    .ToList();

                foreach (var ep in experts)
                {
                    var ageDays = (now - ep.CreatedAt).TotalDays;
                    try
                    {
                        await _loggingService.LogWarningAsync(
                            message: $"{notifMessagePrefix}: tu cuenta de pagos sigue sin terminar",
                            details: $"Empezaste tu alta como experto hace {ageDays:F0} días pero aún no has completado el registro de tu cuenta de pagos. Mientras tanto NO apareces en búsquedas ni puedes recibir contrataciones. Entra a tu panel y termina el onboarding — solo te llevará unos minutos.",
                            userId: ep.UserId,
                            source: "PlatformMaintenanceService.NotifyStalledOnboardingExpertsAsync",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: ep.Id,
                            notifyUser: true);
                    }
                    catch (Exception notifyEx)
                    {
                        try
                        {
                            await _loggingService.LogWarningAsync(
                                message: "A2: recordatorio onboarding falló para un experto",
                                details: $"ExpertProfileId {ep.Id}, UserId {ep.UserId}: {notifyEx.Message}",
                                source: "PlatformMaintenanceService.NotifyStalledOnboardingExpertsAsync");
                        }
                        catch { /* swallow */ }
                    }
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL A2: NotifyStalledOnboardingExpertsAsync failed",
                    details: ex.Message,
                    userId: null,
                    source: "PlatformMaintenanceService.NotifyStalledOnboardingExpertsAsync",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: null);
            }
        }
    }
}
