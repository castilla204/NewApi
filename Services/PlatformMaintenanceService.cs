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
        Task CleanupExpiredAppointmentTimersAsync(); // 🔧 FIX [H1]: evita crecimiento sin límite de AppointmentTimers
        Task CleanupOldLogsAndNotificationsAsync(); // 🔧 FIX [LOG-GROWTH]: Logs y Notifications no tenían retención
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
        Task RetryRefundFailedHiresAsync(); // 🛡️ FIX [M5]: reintenta hires con RefundFailedAt (recuperables) antes de que solo queden en el digest
        Task SendDueAppointmentRemindersAsync(); // NOTIF-FIX [G3]: recordatorio ~24h antes de la cita confirmada (cliente + experto)
    }

    public class PlatformMaintenanceService : IPlatformMaintenanceService
    {
        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;
        // NOTIF-FIX [G1]: el watchdog de confirmación del experto necesita avisar a las partes del
        // desenlace (cliente/experto/vendedor) reutilizando el MISMO helper que usa el timer en-proceso
        // (AppointmentService.NotifyExpertConfirmationResolvedAsync). Sin ciclo de DI: AppointmentService
        // NO depende de IPlatformMaintenanceService (verificado).
        private readonly IAppointmentService _appointmentService;
        // NOTIF-FIX [G3]: el recordatorio previo a la cita refuerza al EXPERTO con un SMS (móvil
        // siempre verificado vía Stripe KYC). SendImportantSmsAsync auto-gatea por móvil verificado,
        // así que el cliente (normalmente sin móvil verificado) se descarta solo. Sin ciclo de DI:
        // InAppNotificationService NO depende de IPlatformMaintenanceService (verificado, mismo
        // razonamiento que IAppointmentService para G1).
        private readonly IInAppNotificationService _inAppNotificationService;
        // 🔔 NOTIF-FIX [S3]: el VENDEDOR (tercero SIN cuenta) no pasa por _inAppNotificationService
        // (gateado por usuario/móvil verificado) → email+SMS directos a SellerEmail/SellerPhone.
        private readonly INotificationService _notificationService;
        private readonly ISmsService? _smsService;

        public PlatformMaintenanceService(AppDbContext context, ILoggingService loggingService, IAppointmentService appointmentService, IInAppNotificationService inAppNotificationService, INotificationService notificationService, ISmsService? smsService = null)
        {
            _context = context;
            _loggingService = loggingService;
            _appointmentService = appointmentService; // NOTIF-FIX [G1]
            _inAppNotificationService = inAppNotificationService; // NOTIF-FIX [G3]
            _notificationService = notificationService; // NOTIF-FIX [S3]
            _smsService = smsService; // NOTIF-FIX [S3]
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
                    // 🛡️ H1 FIX (2026-07-06): limpiar el tracker — un fallo de concurrencia en este
                    // hire no debe envenenar el SaveChanges de las iteraciones siguientes del lote.
                    try { _context.ChangeTracker.Clear(); } catch { /* best-effort */ }
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

        /// <summary>
        /// NOTIF-FIX [G3] — Recordatorio previo a la cita confirmada (hueco G3). Hoy NADA avisa al
        /// cliente ni al experto la víspera de una inspección agendada. Este barrido HORARIO por
        /// ventana selecciona las citas confirmadas cuya hora (StartsAtUtc) cae en [now+23h, now+24h]
        /// y emite, por cada una:
        ///   - cliente: in-app + email (LogInfoAsync notifyUser:true) — "tu inspección es mañana...";
        ///   - experto: in-app + email (mismo helper) + SMS de refuerzo (SendImportantSmsAsync).
        ///
        /// ENFOQUE Y POR QUÉ NO HACE FALTA COLUMNA/MIGRACIÓN
        /// ------------------------------------------------
        /// Es un barrido por ventana (no scheduling por cita), así que cubre AUTOMÁTICAMENTE los dos
        /// puntos de confirmación (flujo Calendly y flujo viejo) sin enganchar en cada uno. La NO
        /// duplicación la dan dos capas y NINGUNA requiere esquema nuevo (el proyecto tiene drift de
        /// migraciones EF, así que se evitan a propósito):
        ///   (a) throttle de 24h del LoggingService: deduplica por UserId+Title+Message, de modo que
        ///       si el job corre dos veces dentro de la misma ventana de la cita, el segundo aviso
        ///       in-app/email no se reemite;
        ///   (b) re-validación de estado en la propia query (Status == "appointment_confirmed"): si la
        ///       cita se cancela/avanza, deja de ser candidata.
        /// El SMS al experto se apoya en (a) implícitamente porque solo se manda junto al aviso del
        /// experto; un doble SMS en el peor caso es inocuo (mismo texto). NO se persiste marca alguna.
        ///
        /// Ventana de 1h (23h↔24h) con cron horario: cada cita cae en exactamente una ejecución del
        /// barrido (salvo solape del job, mitigado por DisableConcurrentExecution + dedup del logger).
        /// try/catch POR cita: un fallo no aborta el lote.
        /// </summary>
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
        // G3-SMS-FIX: Attempts=0. El SMS de refuerzo al experto NO está deduplicado, así que un
        // AutomaticRetry (que reejecuta TODO el job casi al instante) reenviaría el SMS de las citas ya
        // avisadas en la misma ventana. El try/catch por-cita ya aísla fallos puntuales, y la pasada
        // horaria siguiente reintenta lo que quedara pendiente. No reintentar el lote completo.
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task SendDueAppointmentRemindersAsync()
        {
            List<int> dueAppointmentIds;
            var nowUtc = DateTime.UtcNow;
            var windowStart = nowUtc.AddHours(23);
            var windowEnd = nowUtc.AddHours(24);
            try
            {
                dueAppointmentIds = await _context.Appointments
                    .AsNoTracking()
                    .Where(a => a.Status.StatusValue == "appointment_confirmed"
                             && a.StartsAtUtc.HasValue
                             && a.StartsAtUtc.Value >= windowStart
                             && a.StartsAtUtc.Value < windowEnd)
                    .OrderBy(a => a.StartsAtUtc)
                    .Select(a => a.Id)
                    .Take(200)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                await _loggingService.LogWarningAsync(
                    message: "NOTIF-FIX [G3]: fallo al consultar citas próximas para recordatorio",
                    details: $"Ventana UTC [{windowStart:yyyy-MM-dd HH:mm}, {windowEnd:yyyy-MM-dd HH:mm}). Error: {ex.Message}. La próxima ejecución horaria reintentará.",
                    source: "PlatformMaintenanceService.SendDueAppointmentRemindersAsync");
                return;
            }

            if (dueAppointmentIds.Count == 0) return;

            int reminded = 0, errors = 0;
            foreach (var appointmentId in dueAppointmentIds)
            {
                try
                {
                    var appt = await _context.Appointments
                        .AsNoTracking()
                        .Include(a => a.SearchHire)
                        .Include(a => a.Status)
                        .FirstOrDefaultAsync(a => a.Id == appointmentId);
                    // Re-validar estado (la cita pudo cancelarse/avanzar entre la query y aquí).
                    if (appt == null
                        || appt.Status?.StatusValue != "appointment_confirmed"
                        || !appt.StartsAtUtc.HasValue
                        || appt.SearchHire == null)
                        continue;

                    // Hora local para mostrar: convertimos StartsAtUtc al huso del experto si lo
                    // conocemos (SearchHire.ExpertTimezone, o Appointment.ProposerTimezone como
                    // respaldo); si no, mostramos en UTC. La hora exacta local es secundaria — lo
                    // prioritario es que el aviso salga.
                    var startsUtc = DateTime.SpecifyKind(appt.StartsAtUtc.Value, DateTimeKind.Utc);
                    var tzId = !string.IsNullOrWhiteSpace(appt.SearchHire.ExpertTimezone)
                        ? appt.SearchHire.ExpertTimezone
                        : appt.ProposerTimezone;
                    DateTime localTime;
                    string tzSuffix;
                    if (!string.IsNullOrWhiteSpace(tzId))
                    {
                        try
                        {
                            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
                            localTime = TimeZoneInfo.ConvertTimeFromUtc(startsUtc, tz);
                            tzSuffix = "";
                        }
                        catch { localTime = startsUtc; tzSuffix = " UTC"; }
                    }
                    else { localTime = startsUtc; tzSuffix = " UTC"; }

                    var dateStr = localTime.ToString("dd/MM/yyyy");
                    var timeStr = localTime.ToString("HH:mm") + tzSuffix;
                    var clientId = appt.SearchHire.ClientId;
                    var expertId = appt.SearchHire.ExpertId;

                    // Aviso al CLIENTE (in-app + email). Throttle 24h del logger evita el doble envío.
                    if (clientId.HasValue)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Recordatorio: tu inspección es mañana",
                            details: $"Appointment {appt.Id} (hire {appt.SearchHireId}): recordatorio enviado al cliente. Cita {dateStr} {timeStr}.",
                            userId: clientId,
                            source: "PlatformMaintenanceService.SendDueAppointmentRemindersAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: appt.Id,
                            notifyUser: true,
                            userNotificationMessage: $"Recordatorio: tu inspección es mañana, {dateStr} a las {timeStr}. Asegúrate de tener todo listo para el encuentro con el experto.");

                        // 🔔 NOTIF-FIX [SMS-cliente]: refuerzo SMS también al cliente. Auto-gateado por
                        // móvil verificado → no-op para la mayoría; llega a quien lo tenga. Best-effort.
                        try
                        {
                            await _inAppNotificationService.SendImportantSmsAsync(
                                clientId.Value,
                                $"Inspecciono: recordatorio - tu inspeccion es manana {dateStr} a las {timeStr}. Revisa los detalles en la app.");
                        }
                        catch { /* best-effort: el SMS nunca rompe el lote */ }
                    }

                    // Aviso al EXPERTO (in-app + email).
                    if (expertId.HasValue)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Recordatorio: tienes una inspección mañana",
                            details: $"Appointment {appt.Id} (hire {appt.SearchHireId}): recordatorio enviado al experto. Cita {dateStr} {timeStr}.",
                            userId: expertId,
                            source: "PlatformMaintenanceService.SendDueAppointmentRemindersAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: appt.Id,
                            notifyUser: true,
                            userNotificationMessage: $"Recordatorio: tienes una inspección mañana, {dateStr} a las {timeStr}. Revisa la ubicación y los datos de la cita en tu panel.");

                        // SMS de refuerzo al experto. SendImportantSmsAsync auto-gatea por móvil
                        // verificado (el experto siempre lo está vía Stripe KYC). Best-effort.
                        try
                        {
                            await _inAppNotificationService.SendImportantSmsAsync(
                                expertId.Value,
                                $"Inspecciono: recordatorio - tienes una inspeccion manana {dateStr} a las {timeStr}. Revisa los detalles en tu panel.");
                        }
                        catch { /* best-effort: el SMS nunca rompe el lote */ }
                    }

                    // 🔔 NOTIF-FIX [S5]: recordatorio también al VENDEDOR en modo seller (tercero SIN
                    // cuenta; email+SMS directos). La inspección es EN SU COCHE: si él lo olvida, el
                    // técnico se planta allí y no hay vehículo — cliente y experto estaban avisados
                    // pero el actor imprescindible no. Sin dedup del logger (email directo), pero cada
                    // cita cae en exactamente una ventana horaria y el job no reintenta el lote
                    // (Attempts=0), mismo razonamiento que el SMS del experto. Best-effort.
                    if (string.Equals(appt.SearchHire.CoordinationMode, "seller", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(appt.SearchHire.SellerEmail))
                        {
                            try
                            {
                                await _notificationService.SendGeneralNotificationEmailAsync(
                                    toEmail: appt.SearchHire.SellerEmail,
                                    userName: "",
                                    title: "Recordatorio: la inspección de tu vehículo es mañana",
                                    message: $"El técnico irá a inspeccionar tu vehículo mañana, {dateStr} a las {timeStr}, en el lugar acordado. Ten el vehículo disponible a esa hora.");
                            }
                            catch { /* email best-effort */ }
                        }
                        if (_smsService != null && !string.IsNullOrWhiteSpace(appt.SearchHire.SellerPhone))
                        {
                            try
                            {
                                await _smsService.SendSmsAsync(appt.SearchHire.SellerPhone,
                                    $"Inspecciono: recordatorio - la inspeccion de tu vehiculo es manana {dateStr} a las {timeStr}. Ten el vehiculo disponible.");
                            }
                            catch { /* SMS best-effort */ }
                        }
                    }

                    reminded++;
                }
                catch (Exception exAppt)
                {
                    errors++;
                    await _loggingService.LogWarningAsync(
                        message: "NOTIF-FIX [G3]: fallo al enviar recordatorio de cita",
                        details: $"Appointment {appointmentId}: {exAppt.Message}. Se reintentará en la próxima ejecución horaria si sigue en ventana (dedup del logger evita duplicar lo ya enviado).",
                        source: "PlatformMaintenanceService.SendDueAppointmentRemindersAsync");
                }
            }

            await _loggingService.LogInfoAsync(
                message: $"NOTIF-FIX [G3]: recordatorios de cita procesados ({reminded} avisados, {errors} errores)",
                details: $"Ventana UTC [{windowStart:yyyy-MM-dd HH:mm}, {windowEnd:yyyy-MM-dd HH:mm}). Candidatas: {dueAppointmentIds.Count}. Dedup 24h del logger + re-validación de estado garantizan no duplicar sin columna de control.",
                userId: null,
                source: "PlatformMaintenanceService.SendDueAppointmentRemindersAsync",
                relatedEntityType: "Appointment",
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

        // 🔧 FIX [H1]: las filas de AppointmentTimers NUNCA se borraban (solo se marcan IsExpired=true),
        // a diferencia de las otras tablas con cleanup recurrente (webhooks/refresh-tokens/OTP) → crecía
        // sin límite. Borramos SOLO timers ya expirados cuya ventana terminó hace >90 días: el watchdog
        // solo consulta `!IsExpired` y el índice parcial filtra `IsExpired=false`, así que eliminar
        // expirados antiguos no afecta a ninguna consulta ni a ningún timer vivo. 90 días deja margen
        // sobrado de auditoría (los plazos máximos del flujo son de días, no meses).
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task CleanupExpiredAppointmentTimersAsync()
        {
            const int retentionDays = 90;
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            try
            {
                var deleted = await _context.AppointmentTimers
                    .Where(t => t.IsExpired && t.EndTime < cutoff)
                    .ExecuteDeleteAsync();

                if (deleted > 0)
                {
                    await _loggingService.LogInfoAsync(
                        message: "H1: AppointmentTimers cleanup",
                        details: $"Borrados {deleted} timers expirados con EndTime > {retentionDays} días (cutoff {cutoff:yyyy-MM-dd HH:mm} UTC). Solo filas IsExpired=true; sin efecto sobre timers vivos.",
                        userId: null,
                        source: "PlatformMaintenanceService.CleanupExpiredAppointmentTimersAsync",
                        relatedEntityType: "AppointmentTimer",
                        relatedEntityId: null);
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL H1: AppointmentTimers cleanup failed",
                    details: $"Cutoff {cutoff:yyyy-MM-dd HH:mm} UTC. Error: {ex.Message}. La tabla seguirá creciendo hasta el próximo ciclo.",
                    userId: null,
                    source: "PlatformMaintenanceService.CleanupExpiredAppointmentTimersAsync",
                    relatedEntityType: "AppointmentTimer",
                    relatedEntityId: null);
            }
        }

        // 🔧 FIX [LOG-GROWTH]: ni Logs ni Notifications tenían retención → crecían sin límite (a diferencia de
        // ProcessedWebhookEvents/AppointmentTimers/RefreshTokens/OTP, que sí la tienen; la doc EVENTOS_HANGFIRE
        // prometía un CleanupOldLogs que nunca se implementó). Retención 90 días. Borrado POR LOTES (LIMIT por
        // ejecución) para no provocar un DELETE gigante que bloquee la tabla en la primera pasada (Logs es la
        // tabla más caliente del sistema); las pasadas diarias drenan el backlog acumulado.
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 1800)]
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task CleanupOldLogsAndNotificationsAsync()
        {
            const int retentionDays = 90;
            const int batchLimit = 50000; // tope de filas por tabla y ejecución
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            try
            {
                var logsDeleted = await _context.Database.ExecuteSqlInterpolatedAsync(
                    $@"DELETE FROM ""Logs"" WHERE ""Id"" IN (SELECT ""Id"" FROM ""Logs"" WHERE ""CreatedAt"" < {cutoff} LIMIT {batchLimit})");
                var notifsDeleted = await _context.Database.ExecuteSqlInterpolatedAsync(
                    $@"DELETE FROM ""Notifications"" WHERE ""Id"" IN (SELECT ""Id"" FROM ""Notifications"" WHERE ""CreatedAt"" < {cutoff} LIMIT {batchLimit})");

                if (logsDeleted > 0 || notifsDeleted > 0)
                {
                    await _loggingService.LogInfoAsync(
                        message: "LOG-GROWTH: cleanup Logs/Notifications",
                        details: $"Borradas {logsDeleted} filas de Logs y {notifsDeleted} de Notifications anteriores a {retentionDays} días (cutoff {cutoff:yyyy-MM-dd HH:mm} UTC, lote máx {batchLimit}/tabla). Antes ninguna de las dos tenía retención.",
                        userId: null,
                        source: "PlatformMaintenanceService.CleanupOldLogsAndNotificationsAsync",
                        relatedEntityType: "Log",
                        relatedEntityId: null);
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL LOG-GROWTH: cleanup Logs/Notifications failed",
                    details: $"Cutoff {cutoff:yyyy-MM-dd HH:mm} UTC. Error: {ex.Message}. Las tablas seguirán creciendo hasta el próximo ciclo.",
                    userId: null,
                    source: "PlatformMaintenanceService.CleanupOldLogsAndNotificationsAsync",
                    relatedEntityType: "Log",
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
                                // 🛡️ BUG #1 FIX: excluir hires YA finalizados. La rama N10 cancela el PI a 0€
                                // pero NO crea FT 'Refund', así que la condición de abajo (NOT EXISTS Refund) los
                                // re-seleccionaba para SIEMPRE → reproceso cada 15 min → RequiresManualReview +
                                // CRITICAL falsos. Filtrar por estado finalizado corta el bucle SIN romper el
                                // self-healing: el hire que crasheó ANTES de N10 sigue en estado NO finalizado
                                // (token=null pero no cancelado), así que el OR de abajo lo sigue recogiendo.
                                && (h.Status == null || !h.Status.IsFinalizationStatus)
                                && (h.SellerBookingToken != null
                                    || !_context.FinancialTransactions.Any(ft =>
                                            ft.RelatedEntityType == "SearchHire"
                                            && ft.RelatedEntityId == h.Id
                                            && ft.TransactionType == "Refund")))
                    .OrderBy(h => h.Id) // progreso FIFO estable para el Take(200) (hallazgo hermano)
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
                    // firstClaim = esta es la PRIMERA vez que procesamos este hire (token aún vivo). En los
                    // reintentos self-healing (token ya null, refund pendiente) NO volvemos a notificar al
                    // comprador para no duplicar el aviso; el reembolso sí se reencola (es idempotente).
                    var firstClaim = hire.SellerBookingToken != null;
                    if (firstClaim)
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

                    // Avisar al COMPRADOR de la devolución. En captura diferida el PI se CANCELA (no se
                    // refunda), así que la notificación "Reembolso procesado" del RefundService no se
                    // dispara → sin esto el comprador no sabría que recuperó el dinero. Best-effort + solo
                    // en el primer claim (evita duplicado en reintentos).
                    if (firstClaim && hire.ClientId.HasValue)
                    {
                        try
                        {
                            await _loggingService.LogInfoAsync(
                                message: "Plazo del vendedor vencido: reembolso 100% al comprador",
                                details: $"Hire {hireId}: el vendedor no reservó en el plazo de 48h; se devuelve el 100% (0€).",
                                userId: hire.ClientId,
                                source: "PlatformMaintenanceService.ProcessExpiredSellerBookingsAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: hireId,
                                notifyUser: true,
                                userNotificationMessage: "El vendedor no reservó la inspección dentro del plazo, así que hemos cancelado tu reserva y te devolvemos el 100% (no se ha cobrado nada). Puedes contratar de nuevo cuando quieras.");
                        }
                        catch { /* best-effort: la notificación nunca rompe el lote */ }
                    }

                    // NOTIF-FIX [G6a]: avisar también al EXPERTO. Estaba a la espera de que el vendedor
                    // reservara la cita de SU coche; al vencer el plazo (vendedor no reservó) la contratación
                    // se cancela y se reembolsa al comprador, pero al experto nunca se le avisaba de que ya
                    // no tiene nada que coordinar. In-app + email vía LogInfoAsync(notifyUser:true), mismo
                    // mecanismo que el aviso al comprador. Best-effort + solo en el primer claim (no duplica).
                    if (firstClaim && hire.ExpertId.HasValue)
                    {
                        try
                        {
                            await _loggingService.LogInfoAsync(
                                message: "Plazo del vendedor vencido: contratación cancelada (aviso al experto)",
                                details: $"Hire {hireId}: el vendedor no reservó la cita en el plazo; la contratación se cancela y se reembolsa al comprador. No hay nada que coordinar.",
                                userId: hire.ExpertId,
                                source: "PlatformMaintenanceService.ProcessExpiredSellerBookingsAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: hireId,
                                notifyUser: true,
                                userNotificationMessage: "Una de tus contrataciones se ha cancelado: el vendedor no reservó la inspección dentro del plazo. No tienes que hacer nada; se ha reembolsado al comprador.");
                        }
                        catch { /* best-effort: la notificación nunca rompe el lote */ }
                    }

                    // 🔔 NOTIF-FIX [S3]: avisar también al VENDEDOR (email+SMS directos; no tiene cuenta).
                    // Cliente y experto ya se avisaban; el vendedor se quedaba con un enlace muerto sin
                    // saber que el plazo venció y la inspección se canceló. Best-effort + solo primer claim.
                    if (firstClaim && (!string.IsNullOrWhiteSpace(hire.SellerEmail) || !string.IsNullOrWhiteSpace(hire.SellerPhone)))
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(hire.SellerEmail))
                            {
                                await _notificationService.SendGeneralNotificationEmailAsync(
                                    toEmail: hire.SellerEmail,
                                    userName: "",
                                    title: "El plazo para coordinar la inspección ha vencido",
                                    message: "No se eligió día y hora para la inspección del vehículo dentro del plazo, así que la contratación se ha cancelado y el enlace ya no funciona. Se ha devuelto el importe al comprador. Si sigue interesado, puede contratar de nuevo.");
                            }
                            if (_smsService != null && !string.IsNullOrWhiteSpace(hire.SellerPhone))
                            {
                                await _smsService.SendSmsAsync(hire.SellerPhone,
                                    "Inspecciono: el plazo para coordinar la inspeccion de tu vehiculo ha vencido y la contratacion se ha cancelado. El enlace ya no funciona.");
                            }
                        }
                        catch { /* best-effort: la notificación nunca rompe el lote */ }
                    }
                }
                catch (Exception exHire)
                {
                    // 🛡️ H1 FIX (2026-07-06): descartar entidades a medio trackear del hire fallido.
                    // Un DbUpdateConcurrencyException (xmin: el vendedor confirmó en paralelo) dejaba el
                    // hire en estado Modified con xmin obsoleto en el _context COMPARTIDO → el SaveChanges
                    // de TODAS las iteraciones siguientes reintentaba ese UPDATE y fallaba en cascada
                    // (lote entero a CRITICAL, reembolsos retrasados al siguiente ciclo). Seguro aquí:
                    // cada iteración re-consulta su hire desde cero. (Mismo mal que F2/N22 en el gemelo
                    // ProcessOverdueTimersAsync.)
                    try { _context.ChangeTracker.Clear(); } catch { /* best-effort */ }
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
                // ya venció.
                // 🛡️ FIX [T-2] Self-healing (simetría con ProcessExpiredSellerBookingsAsync): incluimos
                // también hires con token==null que SIGUEN en pending_expert_confirmation y SIN reembolso.
                // Eso recupera el crash entre SaveChanges(token=null) y Enqueue: un approve exitoso habría
                // cambiado el estado fuera de pending_expert_confirmation, así que el filtro de estado prueba
                // que es un huérfano genuino (equivalente al "Appointment == null" del gemelo seller).
                // ProcessMoneyDistributionAsync es idempotente → reencolar un hire ya reembolsado es inocuo.
                expiredIds = await _context.SearchHires
                    .Where(h => h.ExpertConfirmationDeadline != null
                                && h.ExpertConfirmationDeadline < now
                                && h.Appointment != null
                                && h.Appointment.Status != null
                                && h.Appointment.Status.StatusValue == "appointment_pending_expert_confirmation"
                                && (h.ExpertConfirmationToken != null
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
                    // 🛡️ FIX [T-2]: re-verificamos que la cita SIGUE en pending_expert_confirmation. Un
                    // approve exitoso habría cambiado el estado → hire==null → no tocamos (es el guard que
                    // cierra el TOCTOU, equivalente al "Appointment == null" del gemelo seller). Esto hace
                    // SEGURO proceder aunque el token ya sea null (huérfano de un crash previo), porque
                    // token==null + estado==pending durable solo ocurre si approve NO llegó a confirmar.
                    var hire = await _context.SearchHires
                        .Include(h => h.Appointment)
                            .ThenInclude(a => a.Status)
                        .FirstOrDefaultAsync(h => h.Id == hireId
                            && h.Appointment != null
                            && h.Appointment.Status != null
                            && h.Appointment.Status.StatusValue == "appointment_pending_expert_confirmation");
                    if (hire == null) continue;

                    // Si el token sigue activo lo anulamos (mutex frente a approve/reject). Si ya es null
                    // (huérfano de un crash entre SaveChanges y Enqueue) proseguimos igualmente: el reparto
                    // es idempotente y el guard de estado de arriba garantiza que no hubo confirmación.
                    // NOTIF-FIX [G1]: capturamos si ESTE es el primer claim (token aún vivo) ANTES de anularlo.
                    // El reparto cambia el estado de forma asíncrona (Enqueue), así que un hire huérfano podría
                    // re-seleccionarse en una pasada posterior del watchdog mientras el estado no haya cambiado;
                    // como NotifyExpertConfirmationResolvedAsync NO es idempotente (siempre reemite avisos),
                    // gateamos la notificación a firstClaim para no repetir el aviso (mismo patrón que el gemelo seller).
                    bool firstClaim = hire.ExpertConfirmationToken != null;
                    if (hire.ExpertConfirmationToken != null)
                    {
                        hire.ExpertConfirmationToken = null;
                        await _context.SaveChangesAsync();
                    }

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

                    // NOTIF-FIX [G1]: avisar a las partes del desenlace, IGUAL que el timer en-proceso
                    // (AppointmentService case "expert_confirmation" → NotifyExpertConfirmationResolvedAsync(id,"timeout")).
                    // Antes, cuando la confirmación expiraba por ESTA red de seguridad, nadie se enteraba:
                    // ni cliente (in-app+email "reembolso 100%"), ni experto (in-app "cita caducada"), ni
                    // vendedor (email+SMS). Además, en authorize-only el PI se CANCELA sin crear Refund, así
                    // que la notif de RefundService tampoco salta. El helper es idempotente en efecto (solo
                    // emite avisos) y best-effort: nunca rompe el lote.
                    if (firstClaim)
                    {
                        try { await _appointmentService.NotifyExpertConfirmationResolvedAsync(hireId, "timeout"); }
                        catch { /* best-effort: la notificación nunca rompe el watchdog */ }
                    }
                }
                catch (Exception exHire)
                {
                    // 🛡️ H1 FIX (2026-07-06): igual que el gemelo seller — limpiar el tracker para que
                    // un fallo de concurrencia en este hire no envenene el SaveChanges del resto del lote.
                    try { _context.ChangeTracker.Clear(); } catch { /* best-effort */ }
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
            // más ventana de los 7 días de autorización de Stripe (seller booking 48h + confirmación del
            // experto 36h = hasta CreatedAt+84h = ~3,5 días antes incluso de poder capturar; margen real
            // 4,5d−3,5d = 1 día). Un cutoff de -6.5d dejaba muy
            // poco margen para reaccionar antes de la expiración automática del PI; -4.5d adelanta la red
            // de seguridad para liberar la autorización del cliente con holgura.
            var cutoff = DateTime.UtcNow.AddDays(-4.5);
            List<DataLayer.Models.PostGresModels.SearchHire> nearExpiry;
            try
            {
                // 🛡️ Captura diferida (modo vendedor): incluimos también "Authorized" como red de
                // seguridad. Si el job de 48h (ProcessExpiredSellerBookingsAsync) fallara, un PI
                // autorizado-sin-capturar >6.5d se cancela aquí antes de que Stripe lo expire a 7d.
                // 🛡️ A2 FIX (defensa en profundidad): NO cancelar la autorización mientras el experto siga
                // dentro de su plazo de confirmación. Hoy el plazo máximo (~CreatedAt+3.5d) cae por debajo
                // del cutoff de -4.5d, así que la carrera approve-vs-watchdog no se materializa; pero esa
                // garantía dependía de 3 constantes dispersas (48h + 36h vs 4.5d). Hacer explícita la
                // invariante evita que un futuro cambio de esos plazos reintroduzca la carrera (watchdog
                // cancela el PI de una cita que el experto aún podía aprobar legítimamente → confirmación caída).
                var nowR5 = DateTime.UtcNow;
                nearExpiry = await _context.SearchHires
                    .Where(sh => (sh.CaptureStatus == "Pending" || sh.CaptureStatus == "Authorized") && sh.CreatedAt < cutoff
                                 && (sh.ExpertConfirmationDeadline == null || sh.ExpertConfirmationDeadline < nowR5))
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
                        // FIX F1: persistir el CaptureStatus ANTES del `continue`. El SaveChanges
                        // por iteración está al final del foreach y `continue` lo salta; si este hire
                        // (ya liquidado) es el último del lote, su reflejo se perdería hasta que otra
                        // iteración flushease el DbContext compartido. Best-effort: si falla, el
                        // siguiente run lo re-detecta y re-marca de forma idempotente (PI ya liquidado).
                        // 🛡️ H1 FIX (2026-07-06): detachar el hire fallido — si no, queda Modified con
                        // xmin obsoleto en el _context compartido y hace fallar el SaveChanges de TODOS
                        // los hires siguientes del lote (aquí NO vale ChangeTracker.Clear(): el lote
                        // itera entidades pre-cargadas tracked y Clear() silenciaría sus updates).
                        try { await _context.SaveChangesAsync(); }
                        catch { try { _context.Entry(hire).State = EntityState.Detached; } catch { } /* no-op: idempotente en el próximo run */ }
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

                    // 🔔 NOTIF-FIX [cliente-PI-expira]: avisar al CLIENTE de que su reserva no pudo
                    // completarse y NO se le ha cobrado (la autorización se ha liberado). Antes solo había
                    // LogCritical a admin → el cliente se quedaba sin saber qué pasó con su reserva/tarjeta.
                    // Va aparte del Critical (el NOTIF-GUARD bloquea los Critical al usuario). Best-effort.
                    if (hire.ClientId.HasValue)
                    {
                        try
                        {
                            await _loggingService.LogInfoAsync(
                                message: "Reserva no completada · sin cargo",
                                details: $"SearchHire {hire.Id}: PI {pi.Id} cancelado por el watchdog de expiración; autorización liberada.",
                                userId: hire.ClientId,
                                source: "PlatformMaintenanceService.ProcessExpiringPaymentIntentsAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: hire.Id,
                                notifyUser: true,
                                userNotificationMessage: "Tu reserva no ha podido completarse a tiempo, así que la hemos cancelado. No se te ha cobrado nada: la autorización de tu tarjeta se ha liberado. Puedes volver a contratar cuando quieras.");
                        }
                        catch { /* best-effort: nunca rompe el lote */ }
                    }
                }
                catch (StripeException stripeEx) when (stripeEx.StripeError?.Code == "payment_intent_unexpected_state")
                {
                    // Race: alguien lo canceló/capturó entre el Get y el Cancel.
                    // 🛡️ H3 FIX (2026-07-06): releer el PI para reflejar el desenlace REAL de la carrera.
                    // Antes se marcaba "Failed" a ciegas: si la carrera la ganó una CAPTURA (approve del
                    // experto → PI succeeded), el hire quedaba etiquetado Failed para siempre con el
                    // dinero realmente cobrado (salía de la query del job y nadie lo re-marcaba).
                    // Mismo patrón que la recuperación BUG#7 de ExpertConfirmationController.Approve.
                    try
                    {
                        var piAfterRace = await piService.GetAsync(ft.StripePaymentIntentId);
                        hire.CaptureStatus = piAfterRace.Status == "succeeded" ? "Captured" : "Failed";
                    }
                    catch { hire.CaptureStatus = "Failed"; /* sin red: comportamiento previo */ }
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
                    // 🛡️ H1 FIX (2026-07-06): detachar el hire fallido para no envenenar el SaveChanges
                    // del resto del lote (el reflejo de ESTE hire se recupera en el próximo run).
                    try { _context.Entry(hire).State = EntityState.Detached; } catch { }
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
                              // 🛡️ T4 FIX (auditoría 2026-07-06): el retry era un no-op eterno para transfers
                              // realmente fallidos (el guard de RefundService ve la fila Payout del transfer
                              // FALLIDO → "ya procesado" → nunca re-paga) y F18 re-encolaba en bucle. Ahora el
                              // primer retry detecta el marcador TransferFailed, marca RequiresManualReview y
                              // este filtro corta el bucle; el hire queda visible en el digest diario del admin.
                              && !sh.RequiresManualReview
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
        /// 🛡️ FIX [M5] — Reintenta automáticamente los hires con RefundFailedAt (cobrados pero ni
        /// transferidos ni reembolsados). Antes de este job, al agotar los 5 reintentos de
        /// RetryMoneyDistributionJobAsync el hire quedaba SOLO en el digest diario (alerta), esperando
        /// acción humana → era el "casi-atasco" más cercano. RetryMoneyDistributionJobAsync es
        /// idempotente (advisory lock por hire + idempotency keys + guard FinancialTransactions +
        /// hasChargeback), así que reintentar no puede doble-pagar.
        ///
        /// Guardas que lo hacen seguro y NO indefinido:
        ///  - RefundFailedAt &lt; -6h: no pisa el flujo inline ni el AutomaticRetry de 5 intentos (último delay 2h).
        ///  - RefundFailedAt &gt; -7d: pasados 7 días se asume caso ESTRUCTURAL (cuenta experto 404,
        ///    currency-mismatch, transfers disabled) que NO se arregla solo → deja de reintentar y
        ///    queda solo en el digest, evitando un bucle diario de llamadas Stripe inútiles.
        ///  - Take(25): ritmo bajo. El estado del hire (StatusValue) determina el reparto correcto.
        /// </summary>
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 1200)]
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task RetryRefundFailedHiresAsync()
        {
            try
            {
                var now = DateTime.UtcNow;
                var minAge = now.AddHours(-6);   // deja respirar al flujo inline + los 5 reintentos
                var maxAge = now.AddDays(-7);    // >7d = estructural, no recuperable solo → solo digest
                var stuck = await _context.SearchHires
                    .AsNoTracking()
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Appointment)
                        .ThenInclude(a => a.Status)
                    .Where(sh => sh.RefundFailedAt != null
                              && sh.RefundFailedAt < minAge
                              && sh.RefundFailedAt > maxAge
                              && sh.Status != null)
                    .OrderBy(sh => sh.RefundFailedAt)
                    .Take(25)
                    .ToListAsync();

                if (stuck.Count == 0) return;

                foreach (var hire in stuck)
                {
                    try
                    {
                        // 🛡️ M5b FIX (2026-07-06): usar el tramo REAL de la cita (appointment_cancelled_by_*,
                        // etc.) cuando exista, no el estado genérico del hire. El hire mapeado queda en
                        // "cancelled", que NO empieza por "appointment_cancelled": re-encolar con él hacía
                        // que ProcessMoneyDistributionAsync aplicara el reparto equivocado (con snapshot V8,
                        // 0/95/5 → transfer del 95% al experto de un hire cancelado; sin snapshot, 100/0/0
                        // aunque el tramo real fuera 50/50). Mismo criterio que R16b en RefundService.
                        var appointmentStatus = hire.Appointment?.Status;
                        var statusValue = (appointmentStatus != null && appointmentStatus.IsFinalizationStatus)
                            ? appointmentStatus.StatusValue
                            : hire.Status!.StatusValue; // estado terminal real del hire (sin cita o cita no finalizada)
                        Hangfire.BackgroundJob.Schedule<StripeRefundService>(
                            s => s.RetryMoneyDistributionJobAsync(
                                hire.Id,
                                statusValue,
                                "Retry refund/transfer after RefundFailedAt (watchdog M5)",
                                null),
                            TimeSpan.FromSeconds(2));

                        var ageHours = (now - hire.RefundFailedAt!.Value).TotalHours;
                        await _loggingService.LogWarningAsync(
                            message: "M5: SearchHire con RefundFailedAt reencolado por watchdog",
                            details: $"SearchHire {hire.Id} lleva {ageHours:F1}h con RefundFailedAt. Reencolado RetryMoneyDistributionJobAsync (idempotente). Si vuelve a agotar los reintentos, se re-marca y queda en el digest; pasados 7 días deja de reintentarse (caso estructural).",
                            userId: hire.ClientId,
                            source: "PlatformMaintenanceService.RetryRefundFailedHiresAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: hire.Id);
                    }
                    catch (Exception ex)
                    {
                        await _loggingService.LogWarningAsync(
                            message: "M5: error reencolando hire con RefundFailedAt",
                            details: $"SearchHire {hire.Id}: {ex.Message}. Reintentará en el próximo ciclo.",
                            userId: hire.ClientId,
                            source: "PlatformMaintenanceService.RetryRefundFailedHiresAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: hire.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL M5: RetryRefundFailedHiresAsync failed",
                    details: ex.Message,
                    userId: null,
                    source: "PlatformMaintenanceService.RetryRefundFailedHiresAsync",
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
        ///    (margen para que el flow normal/outbox cierre) y dentro de los últimos 90 días, que
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
                // 🔧 FIX [M2-resid]: ventana ampliada de 7→90 días. Con 7 días, si este job (o Hangfire
                // entero) estuvo caído >7d, el candidato salía de la ventana y solo lo DETECTABA el job
                // diario (que alerta pero NO auto-repara) → la auto-reconciliación caducaba. La ventana
                // Stripe (ListAsync) es relativa a cada UpdatedAt, así que ampliar aquí no la rompe.
                var windowCutoff = DateTime.UtcNow.AddDays(-90);  // no escanear toda la historia
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
                    // 🛡️ H2 FIX (2026-07-06): paginar TODA la ventana, no solo la primera página.
                    // ListAsync(Limit=100) devolvía una única página; sin Destination (perfil sin
                    // StripeAccountId) son los transfers de TODA la plataforma en la ventana, y si el
                    // transfer real no caía en esos 100 la RAMA B re-encolaba la distribución → con la
                    // clave de idempotencia de Stripe caducada (~24h) se creaba un SEGUNDO transfer real.
                    await foreach (var t in transferSvc.ListAutoPagingAsync(listOptions))
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

                        // 🛡️ T1 FIX (auditoría 2026-07-06): hire bajo revisión manual (transfer bloqueado
                        // por refund externo total, chargeback, cuenta 404, etc.) → re-encolar la
                        // distribución cada 30 min solo produce un CRITICAL repetido; no puede
                        // auto-resolverse hasta que el admin decida. El hire ya está en el digest diario.
                        if (hire.RequiresManualReview)
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
                details: $"Total candidatos (finalizados sin Payout, últimos 90d, >1h): {candidates.Count}.",
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

                        // 🔔 NOTIF-FIX [SMS-stripe]: SMS SOLO en el tramo urgente (≤24h) — si Stripe
                        // pausa las transferencias, el experto deja de cobrar. Un único SMS de última
                        // llamada, no en cada peldaño de la escalera (evita fatiga). Best-effort.
                        if (hoursLeft <= 24)
                        {
                            try
                            {
                                await _inAppNotificationService.SendImportantSmsAsync(ep.UserId,
                                    $"Inspecciono: te quedan menos de 24h para completar los requisitos de Stripe o pausaran tus cobros. Entra en tu panel.");
                            }
                            catch { /* SMS best-effort */ }
                        }
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
