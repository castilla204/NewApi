using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;

namespace newApi.Controllers
{
    /// <summary>
    /// Magic link del EXPERTO (modo "seller"). SIN login: el token secreto de la URL ES la
    /// credencial. La cita nace en `appointment_pending_expert_confirmation` con el pago
    /// AUTORIZADO (PI en requires_capture, CaptureStatus="Authorized"). El experto decide:
    ///
    ///   • APPROVE → confirma la cita y CAPTURA el pago (cobro real al comprador).
    ///   • REJECT  → cancela la AUTORIZACIÓN del pago SIN coste (0 €, ni fee), devuelve el 100%
    ///               al comprador y añade un strike de cancelación al experto.
    ///
    /// Clon estructural de SellerBookingController: token single-use como mutex + xmin
    /// (DbUpdateConcurrencyException) para idempotencia, ExecutionStrategy + transacción por
    /// EnableRetryOnFailure, y nunca expone datos personales del cliente.
    /// </summary>
    [ApiController]
    [Route("api/expert-confirmation")]
    [AllowAnonymous]
    public class ExpertConfirmationController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IPaymentCaptureService _paymentCapture;
        private readonly ILoggingService _logging;

        public ExpertConfirmationController(
            AppDbContext context,
            IPaymentCaptureService paymentCapture,
            ILoggingService logging)
        {
            _context = context;
            _paymentCapture = paymentCapture;
            _logging = logging;
        }

        private static bool TokenLooksValid(string? token) =>
            !string.IsNullOrWhiteSpace(token) && token.Length >= 32 && token.Length <= 128;

        private Task<SearchHire?> FindByTokenAsync(string token, bool tracking) =>
            (tracking ? _context.SearchHires : _context.SearchHires.AsNoTracking())
                .Include(h => h.Appointment)
                .Include(h => h.SearchService)
                .FirstOrDefaultAsync(h => h.ExpertConfirmationToken == token);

        // ── Contexto (solo lectura) ────────────────────────────────────────────
        // Solo lo imprescindible para que el experto decida: cuándo, dónde y hasta cuándo
        // puede responder. NUNCA nombre/email/teléfono del cliente.
        [HttpGet("{token}")]
        public async Task<IActionResult> GetContext(string token)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });
            var hire = await FindByTokenAsync(token, tracking: false);
            if (hire == null || hire.Appointment == null)
                return NotFound(new { message = "Enlace no válido o caducado." });

            var appt = hire.Appointment;
            return Ok(new
            {
                serviceId = hire.SearchServiceId,
                startsAtUtc = appt.StartsAtUtc,
                endsAtUtc = appt.EndsAtUtc,
                location = appt.Location,
                doorNumber = appt.DoorNumber,
                siteDetails = appt.SiteDetails,
                latitude = appt.Latitude,
                longitude = appt.Longitude,
                deadline = hire.ExpertConfirmationDeadline,
                expired = hire.ExpertConfirmationDeadline.HasValue
                          && DateTime.UtcNow > hire.ExpertConfirmationDeadline.Value,
            });
        }

        // ── Aprobar la cita → confirma + CAPTURA el pago ──────────────────────────
        // Orden: el hueco ya está reservado desde el checkout, así que cambiamos estado →
        // capturamos. Si la captura falla, el rollback deshace el cambio de estado y deja
        // el token vivo (reintentable). Estado EXACTO pending + token single-use + xmin =
        // idempotencia (un segundo approve no recaptura ni reconfirma).
        [HttpPost("{token}/approve")]
        public async Task<IActionResult> Approve(string token)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });

            var hire = await FindByTokenAsync(token, tracking: true);
            if (hire == null || hire.Appointment == null)
                return NotFound(new { message = "Enlace no válido o caducado." });

            // El estado debe ser EXACTAMENTE pending_expert_confirmation. Si ya se confirmó,
            // canceló o expiró → 409 (esto + token single-use + xmin dan idempotencia).
            var pendingStatusId = await _context.SystemStatuses
                .Where(s => s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_pending_expert_confirmation")
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync();
            if (pendingStatusId == null)
                return StatusCode(500, new { message = "Configuración de estados incompleta." });

            if (hire.Appointment.StatusId != pendingStatusId.Value)
                return Conflict(new { message = "La cita ya no está pendiente de confirmación." });

            // 🛡️ FIX [SELF-T2]: revalidar el plazo de confirmación antes de capturar. Sin esto, el experto
            // podía aprobar (y CAPTURAR el pago) segundos/minutos después de vencer ExpertConfirmationDeadline,
            // mientras el watchdog de expiración (cada 15 min) aún no había reembolsado → cobro fuera de plazo.
            // Tolerancia de 2 min para no rechazar un approve legítimo en el borde por desfase de reloj.
            if (hire.ExpertConfirmationDeadline.HasValue
                && DateTime.UtcNow > hire.ExpertConfirmationDeadline.Value.AddMinutes(2))
                return Conflict(new { message = "El plazo para confirmar la cita ha vencido." });

            var confirmedStatusId = await _context.SystemStatuses
                .Where(s => s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_confirmed")
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync();
            if (confirmedStatusId == null)
                return StatusCode(500, new { message = "Configuración de estados incompleta." });

            // Localizar el PaymentIntent del cobro del servicio (ServicePayment del hire).
            var paymentIntentId = await _context.FinancialTransactions.AsNoTracking()
                .Where(t => t.RelatedEntityType == "SearchHire"
                            && t.RelatedEntityId == hire.Id
                            && t.TransactionType == "ServicePayment")
                .OrderByDescending(t => t.Id)
                .Select(t => t.StripePaymentIntentId)
                .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(paymentIntentId))
            {
                await _logging.LogCriticalAsync(
                    message: "CRITICAL: approve del experto sin PaymentIntent localizable",
                    details: $"No hay FinancialTransaction ServicePayment con StripePaymentIntentId para el hire {hire.Id}. " +
                             "No se puede capturar el pago al aprobar la cita. ACCIÓN ADMIN: revisar el vínculo PI↔hire.",
                    userId: hire.ClientId,
                    source: "ExpertConfirmationController.Approve",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: hire.Id,
                    additionalData: new { SearchHireId = hire.Id });
                return StatusCode(500, new { message = "No se pudo localizar el pago de la cita." });
            }

            // La connection string usa EnableRetryOnFailure (NpgsqlRetryingExecutionStrategy), que
            // prohíbe transacciones iniciadas por el usuario salvo a través de la propia execution
            // strategy → envolvemos el cambio de estado + captura en ella. Los fallos no transitorios
            // se capturan y devuelven IActionResult (no relanzan) para que la strategy no los reintente.
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
                await using var tx = await _context.Database.BeginTransactionAsync();

                // (1) Confirmar la cita + anular el token (single-use). SaveChanges con xmin: si otro
                //     proceso tocó el hire/cita a la vez → concurrency → rollback + 409.
                hire.Appointment!.StatusId = confirmedStatusId.Value;
                hire.Appointment.UpdatedAt = DateTime.UtcNow;
                hire.ExpertConfirmationToken = null;
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    await tx.RollbackAsync();
                    return Conflict(new { message = "La cita ya no está pendiente de confirmación." });
                }

                // (2) Capturar el pago (idempotente, IdempotencyKey capture-{hireId}). Si falla, el
                //     rollback deshace la confirmación y deja el token vivo (reintentable). Marcamos
                //     CaptureStatus="Failed" en un contexto LIMPIO (la tx del _context se va a revertir)
                //     para que admin/watchdog lo vea sin contaminar el change-tracker que vamos a rollback.
                try
                {
                    await _paymentCapture.EnsureCapturedAsync(paymentIntentId, hire.ExpertId ?? 0, hire.SearchServiceId, hire.Id);
                }
                catch (Exception captureEx)
                {
                    try { await tx.RollbackAsync(); } catch { /* la tx puede estar ya inutilizable */ }

                    // 🛡️ BUG #7 FIX: la StripeException pudo lanzarse por timeout/red DESPUÉS de que Stripe
                    // procesara la captura (PI quedó 'succeeded'); solo se perdió la respuesta. Re-consultar el PI:
                    // si está 'succeeded', la captura SÍ ocurrió → re-aplicar la confirmación (Captured) sobre un
                    // contexto limpio, en vez de marcar 'Failed'. Sin esto, 'Failed' excluye el hire de R5 y deja que
                    // el watchdog no_response REEMBOLSE el 100% de un pago YA cobrado (se come la fee de Stripe).
                    try
                    {
                        var verifyPi = await new Stripe.PaymentIntentService().GetAsync(paymentIntentId);
                        if (verifyPi.Status == "succeeded")
                        {
                            var recOpts = new DbContextOptionsBuilder<AppDbContext>()
                                .UseNpgsql(_context.Database.GetConnectionString())
                                .Options;
                            await using var recoverCtx = new AppDbContext(recOpts);
                            // Anti-TOCTOU: solo re-confirmar si la cita SIGUE pending (si un reject/watchdog ya
                            // la resolvió en paralelo, no la pisamos).
                            var apptRows = await recoverCtx.Appointments
                                .Where(a => a.SearchHireId == hire.Id && a.StatusId == pendingStatusId.Value)
                                .ExecuteUpdateAsync(s => s
                                    .SetProperty(a => a.StatusId, confirmedStatusId.Value)
                                    .SetProperty(a => a.UpdatedAt, DateTime.UtcNow));
                            if (apptRows > 0)
                            {
                                await recoverCtx.SearchHires
                                    .Where(h => h.Id == hire.Id)
                                    .ExecuteUpdateAsync(s => s
                                        .SetProperty(h => h.CaptureStatus, "Captured")
                                        .SetProperty(h => h.ExpertConfirmationToken, (string?)null));
                                await _logging.LogCriticalAsync(
                                    message: "Captura RECUPERADA: la respuesta de Stripe se perdió pero el PI quedó 'succeeded'",
                                    details: $"Hire {hire.Id} (PI {paymentIntentId}): EnsureCapturedAsync lanzó ({captureEx.Message}) pero el PI está 'succeeded' → la captura SÍ ocurrió. Cita re-confirmada y CaptureStatus='Captured'. Sin pérdida de dinero. (Aviso: en esta rama de recuperación NO se reenvían factura/notificaciones; el watchdog de awaiting_report cubre la transición.)",
                                    userId: hire.ClientId,
                                    source: "ExpertConfirmationController.Approve.CaptureRecovered",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: hire.Id,
                                    additionalData: new { SearchHireId = hire.Id, PaymentIntentId = paymentIntentId });
                                return Ok(new { ok = true });
                            }
                        }
                    }
                    catch { /* best-effort: si el GET o el update falla, caemos al comportamiento 'Failed' de abajo */ }

                    try
                    {
                        var opts = new DbContextOptionsBuilder<AppDbContext>()
                            .UseNpgsql(_context.Database.GetConnectionString())
                            .Options;
                        await using var cleanCtx = new AppDbContext(opts);
                        await cleanCtx.SearchHires
                            .Where(h => h.Id == hire.Id)
                            .ExecuteUpdateAsync(s => s.SetProperty(h => h.CaptureStatus, "Failed"));
                    }
                    catch { /* best-effort: el watchdog cubrirá el PI si esto falla */ }

                    await _logging.LogCriticalAsync(
                        message: "CRITICAL: la captura falló al aprobar el experto",
                        details: $"EnsureCapturedAsync falló para el hire {hire.Id} (PI {paymentIntentId}) al aprobar la cita. " +
                                 "La confirmación se revirtió (rollback) y el token sigue vivo (reintentable). " +
                                 $"CaptureStatus marcado 'Failed'. Error: {captureEx.Message}",
                        userId: hire.ClientId,
                        source: "ExpertConfirmationController.Approve",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hire.Id,
                        additionalData: new { SearchHireId = hire.Id, PaymentIntentId = paymentIntentId, Exception = captureEx.Message });

                    return StatusCode(402, new { message = "No se pudo confirmar el pago. Inténtalo de nuevo." });
                }

                // (3) Captura OK → marcar Captured y confirmar la tx.
                hire.CaptureStatus = "Captured";
                try
                {
                    await _context.SaveChangesAsync();
                    await tx.CommitAsync();
                }
                catch (Exception commitEx)
                {
                    // HUECO 1b: el dinero YA se cobró en Stripe pero el commit local falló. Rollback
                    // best-effort (el cambio de estado se revierte) y log crítico para derivar a soporte:
                    // hay un PI succeeded sin cita confirmada que requiere reconciliación manual.
                    try { await tx.RollbackAsync(); } catch { /* la tx puede estar ya inutilizable */ }

                    await _logging.LogCriticalAsync(
                        message: "CRITICAL: commit falló tras capturar el pago al aprobar el experto",
                        details: $"PI {paymentIntentId} YA capturado en Stripe pero el commit local falló (hire {hire.Id}). " +
                                 "El cambio de estado se revirtió: hay dinero cobrado sin cita confirmada. " +
                                 $"ACCIÓN SOPORTE: reconciliar manualmente. Error: {commitEx.Message}",
                        userId: hire.ClientId,
                        source: "ExpertConfirmationController.Approve",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hire.Id,
                        additionalData: new { SearchHireId = hire.Id, PaymentIntentId = paymentIntentId, Exception = commitEx.Message });

                    return StatusCode(500, new { message = "El pago se procesó pero hubo un problema al confirmar. Contacta con soporte." });
                }

                // 📄 OBS-1: emitir la FACTURA al CAPTURAR. Con captura diferida (modo vendedor) el
                // webhook ya NO factura (la rama !deferCapture quedó muerta para este flujo): la captura
                // real del pago ocurre AQUÍ, al aprobar el experto, así que la factura debe encolarse aquí.
                // Mismo job que usaba el webhook: IInvoiceService.SendInvoiceByEmailBackgroundJob(hireId, email).
                // Best-effort en try/catch: si el enqueue falla NO rompemos la confirmación ya commiteada
                // (el pago está cobrado y la cita confirmada; la factura es secundaria y reintentable).
                try
                {
                    var clientEmail = await _context.SearchHires.AsNoTracking()
                        .Where(h => h.Id == hire.Id)
                        .Select(h => h.Client != null ? h.Client.Email : null)
                        .FirstOrDefaultAsync();
                    if (!string.IsNullOrWhiteSpace(clientEmail))
                    {
                        Hangfire.BackgroundJob.Enqueue<IInvoiceService>(svc =>
                            svc.SendInvoiceByEmailBackgroundJob(hire.Id, clientEmail));
                    }
                    else
                    {
                        await _logging.LogWarningAsync(
                            message: "Factura no encolada al aprobar: cliente sin email",
                            details: $"Hire {hire.Id} aprobado y capturado, pero el cliente no tiene email localizable → no se envía la factura automáticamente.",
                            source: "ExpertConfirmationController.Approve");
                    }
                }
                catch (Exception invoiceEx)
                {
                    // No crítico: la confirmación + captura ya están commiteadas. Solo la factura no se encoló.
                    await _logging.LogWarningAsync(
                        message: "Failed to enqueue invoice email job on expert approve",
                        details: $"Hire {hire.Id} confirmado y capturado correctamente, pero el enqueue de la factura falló: {invoiceEx.Message}. La factura no se enviará automáticamente.",
                        source: "ExpertConfirmationController.Approve");
                }

                // ⚓ Tarea 5: cancelar el timer de confirmación (el experto ya respondió → no auto-cancelar).
                // IAppointmentService no está inyectado en este controlador → lo resolvemos por scope.
                // Best-effort: si falla, el guard de estado del handler lo vuelve no-op (la cita ya no está
                // en appointment_pending_expert_confirmation).
                if (HttpContext?.RequestServices?.GetService(typeof(IAppointmentService)) is IAppointmentService apptSvc
                    && hire.Appointment != null)
                {
                    try { await apptSvc.CancelExpertConfirmationTimerAsync(hire.Appointment.Id); }
                    catch { /* best-effort: el guard de estado del handler evita el auto-cancel */ }

                    // ⚓ Timer PRIMARIO de transición confirmed→awaiting_report (cita+3h). Antes esta transición
                    // dependía solo del watchdog A-iii (hasta 10 min de latencia + barrido de 500 citas); ahora se
                    // programa puntual al confirmar. Idempotente con el watchdog (índice único + el handler
                    // re-valida estado). Best-effort: si falla, el watchdog A-iii rescata la cita confirmada.
                    try { await apptSvc.CreateAwaitingReportTransitionTimerAsync(hire.Appointment.Id); }
                    catch { /* best-effort: el watchdog A-iii es la red de seguridad */ }
                }

                // ⚓ Tarea 6: notificar al cliente (cita confirmada). Best-effort.
                if (HttpContext?.RequestServices?.GetService(typeof(IAppointmentService)) is IAppointmentService notifySvc)
                {
                    try { await notifySvc.NotifyExpertConfirmationResolvedAsync(hire.Id, "approved"); }
                    catch { /* best-effort: la notificación nunca rompe el approve ya commiteado */ }
                }

                return Ok(new { ok = true });
            });
        }

        // ── Rechazar la cita → cancela autorización (0 €) + reembolso 100% + strike ──
        // El experto pulsa "no puedo". Cancelamos la AUTORIZACIÓN del pago SIN coste (PI en
        // requires_capture → cancel = 0 €) y devolvemos el 100% al comprador finalizando el
        // hire. Penaliza al experto con un strike. Anular el token actúa de mutex frente al
        // approve y al job de expiración (ambos buscan por token).
        [HttpPost("{token}/reject")]
        public async Task<IActionResult> Reject(string token)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });

            var hire = await FindByTokenAsync(token, tracking: true);
            if (hire == null || hire.Appointment == null)
                return NotFound(new { message = "Enlace no válido o caducado." });

            // Anular el token = mutex PRIMERO: a partir de aquí ni el approve ni el job de
            // expiración (ambos buscan por token) podrán confirmar la cita ni coexistir.
            hire.ExpertConfirmationToken = null;
            // 🛡️ B9 FIX: liberar el hueco del calendario INLINE, en el mismo SaveChanges que el mutex del
            // token. Antes BlocksCalendar=false solo se ponía dentro del job de dinero encolado más abajo;
            // si el Enqueue de Hangfire fallaba, el hueco quedaba bloqueado por la GiST indefinidamente
            // (degradación silenciosa de la disponibilidad del experto, sin watchdog que lo rescatara).
            // Si approve gana la carrera, el xmin aborta AMBOS cambios → 409 abajo (no se libera el hueco
            // de una cita confirmada). El job (RefundService) ya es idempotente (if BlocksCalendar) → backup.
            if (hire.Appointment != null) hire.Appointment.BlocksCalendar = false;
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Carrera approve-vs-reject: el approve ganó (token xmin). La cita ya quedó
                // confirmada y capturada → NO rechazamos, NO reembolsamos y NO aplicamos strike
                // (sería injusto: la cita SÍ se atendió). 409 limpio.
                return Conflict(new { message = "La cita acaba de confirmarse. No se puede rechazar." });
            }

            // Strike SÍNCRONO al experto — SOLO tras ganar el mutex del token (si la carrera la
            // gana approve, arriba devolvemos 409 sin penalizar). ExecuteUpdate sobre ExpertProfiles.
            // 🛡️ REJECT-LATE FIX: si el plazo de confirmación YA venció, NO penalizamos con strike. El
            // watchdog de expiración (ProcessExpiredExpertConfirmationsAsync) habría cancelado la cita SIN
            // strike (no es culpa imputable: AppointmentService "auto-cancelamos SIN strike"); un reject
            // tardío en la ventana de minutos antes de que corra el watchdog no debe castigar al experto
            // más que la propia inacción. Mismo desenlace de dinero (100% cliente, PI a 0€) en ambos casos.
            // Tolerancia de 2 min por desfase de reloj (idéntica a Approve [SELF-T2]).
            bool deadlinePassed = hire.ExpertConfirmationDeadline.HasValue
                && DateTime.UtcNow > hire.ExpertConfirmationDeadline.Value.AddMinutes(2);
            if (hire.ExpertId.HasValue && !deadlinePassed)
            {
                await _context.ExpertProfiles
                    .Where(p => p.UserId == hire.ExpertId.Value)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.CancellationStrikes, p => p.CancellationStrikes + 1));
            }

            // Reembolso 100% al comprador + cancelar el hire + cancelar el PI no capturado (0 €).
            // La rama N10 del RefundService CANCELA el PI si sigue en requires_capture (0 €, sin fee).
            // El estado "appointment_cancelled_by_expert_strike" empieza por "appointment_cancelled"
            // → finalización del hire.
            // 🛡️ Encolado defensivo (simetría con ProcessExpiredExpertConfirmationsAsync): si el storage de
            // Hangfire está caído, el Enqueue lanza. Como el token ya se anuló (mutex) y el strike ya se aplicó,
            // un fallo silencioso dejaría al comprador sin reembolso encolado → CRÍTICO para resolución manual.
            // (El PI sigue en requires_capture; el watchdog R5 de expiración de PIs lo cancelaría a 0€ igualmente,
            // así que no hay pérdida de principal, pero el hire quedaría sin finalizar.)
            try
            {
                Hangfire.BackgroundJob.Enqueue<global::newApi.Services.StripeRefundService>(svc =>
                    svc.ProcessMoneyDistributionAsync(
                        hire.Id,
                        "appointment_cancelled_by_expert_strike",
                        "El experto rechazó la cita: devolución íntegra al cliente (0€) + strike.",
                        null,
                        true));
            }
            catch (Exception enqueueEx)
            {
                await _logging.LogCriticalAsync(
                    message: "CRITICAL: no se pudo encolar el reembolso tras el rechazo del experto",
                    details: $"Hire {hire.Id}: el experto rechazó la cita (token anulado + strike aplicado) pero el " +
                             $"Enqueue de ProcessMoneyDistributionAsync falló: {enqueueEx.Message}. ACCIÓN: finalizar el hire " +
                             "y cancelar/reembolsar el PI 100% al comprador a mano (el watchdog R5 cancelaría el PI no capturado).",
                    userId: hire.ClientId,
                    source: "ExpertConfirmationController.Reject",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: hire.Id,
                    additionalData: new { SearchHireId = hire.Id, Exception = enqueueEx.Message });
            }

            // ⚓ Tarea 5: cancelar el timer de confirmación (el experto rechazó → no auto-cancelar también).
            // Best-effort: si falla, el guard de estado del handler lo vuelve no-op (la distribución de dinero
            // encolada finaliza el hire, así que la cita ya no estará en pending_expert_confirmation).
            if (HttpContext?.RequestServices?.GetService(typeof(IAppointmentService)) is IAppointmentService apptSvc
                && hire.Appointment != null)
            {
                try { await apptSvc.CancelExpertConfirmationTimerAsync(hire.Appointment.Id); }
                catch { /* best-effort: el guard de estado del handler evita el auto-cancel */ }
            }

            // ⚓ Tarea 6: notificar al cliente (cita rechazada, reembolso 100%) + al experto (rechazada). Best-effort.
            if (HttpContext?.RequestServices?.GetService(typeof(IAppointmentService)) is IAppointmentService notifySvc)
            {
                try { await notifySvc.NotifyExpertConfirmationResolvedAsync(hire.Id, "rejected"); }
                catch { /* best-effort: la notificación nunca rompe el reject ya procesado */ }
            }

            return Ok(new { ok = true });
        }
    }
}
