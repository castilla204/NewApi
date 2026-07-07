using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;

namespace newApi.Controllers
{
    /// <summary>
    /// Magic link del vendedor (modo "seller"). SIN login: el token secreto de la URL ES la
    /// credencial. Solo expone lo imprescindible para reservar; NUNCA datos personales del cliente.
    /// El vendedor elige día/hora + lugar dentro del horario y el rango del experto.
    ///
    /// ⚠️ El POST /confirm crea una cita confirmada con la MISMA reserva atómica que el webhook
    /// (exclusion constraint GiST anti-doble-booking). Verificar con pruebas de concurrencia.
    /// </summary>
    [ApiController]
    [Route("api/seller-booking")]
    [AllowAnonymous]
    public class SellerBookingController : ControllerBase
    {
        private readonly IAvailabilityService _availability;
        private readonly AppDbContext _context;
        private readonly IPaymentCaptureService _paymentCapture;
        private readonly ILoggingService _logging;

        public SellerBookingController(
            IAvailabilityService availability,
            AppDbContext context,
            IPaymentCaptureService paymentCapture,
            ILoggingService logging)
        {
            _availability = availability;
            _context = context;
            _paymentCapture = paymentCapture;
            _logging = logging;
        }

        private static bool TokenLooksValid(string? token) =>
            !string.IsNullOrWhiteSpace(token) && token.Length >= 32 && token.Length <= 128;

        private Task<SearchHire?> FindByTokenAsync(string token, bool tracking) =>
            (tracking ? _context.SearchHires : _context.SearchHires.AsNoTracking())
                .Include(h => h.Appointment)
                .FirstOrDefaultAsync(h => h.SellerBookingToken == token);

        // ── Contexto (solo lectura) ────────────────────────────────────────────
        [HttpGet("{token}")]
        public async Task<IActionResult> GetContext(string token)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });
            // Incluimos el perfil VIVO del experto para poder caer a sus coords/radio si el snapshot
            // del hire viniera vacío (mismo patrón ?? que SearchHireController/SearchController). Sin
            // este fallback, un snapshot nulo dejaba al vendedor SIN MAPA cuando el experto trabaja
            // por rango (radio > 0), porque el front sin coordenadas no monta el mapa.
            var hire = await _context.SearchHires.AsNoTracking()
                .Include(h => h.Appointment)
                .Include(h => h.SearchService).ThenInclude(s => s!.ExpertProfile)
                .FirstOrDefaultAsync(h => h.SellerBookingToken == token);
            if (hire == null) return NotFound(new { message = "Enlace no válido o caducado." });
            return Ok(new
            {
                serviceId = hire.SearchServiceId,
                alreadyBooked = hire.Appointment != null,
                // Plazo agotado sin reservar: el enlace ya no sirve (habrá reembolso al comprador).
                expired = hire.SellerBookingDeadline.HasValue && DateTime.UtcNow > hire.SellerBookingDeadline.Value && hire.Appointment == null,
                listingUrl = hire.SellerListingUrl,
                // Tope de días a futuro que fijó el cliente (para limitar el calendario).
                maxDays = hire.SellerBookingMaxDays ?? 14,
                // Ubicación/rango del experto para el mapa de selección de lugar: snapshot del hire
                // con FALLBACK al perfil vivo del experto (idéntico a SearchHireController:831).
                expertLatitude = hire.ExpertLatitudeSnapshot ?? hire.SearchService?.ExpertProfile?.Latitude,
                expertLongitude = hire.ExpertLongitudeSnapshot ?? hire.SearchService?.ExpertProfile?.Longitude,
                expertCountry = hire.ExpertCountry ?? hire.SearchService?.ExpertProfile?.Country,
                workRadiusKm = hire.ExpertWorkRadiusKmSnapshot ?? hire.SearchService?.ExpertProfile?.WorkRadiusKm,
            });
        }

        // ── Huecos (token-gated; reusa el mismo servicio que el flujo del cliente) ──
        [HttpGet("{token}/slots")]
        public async Task<IActionResult> GetSlots(string token, [FromQuery] DateTime date, CancellationToken ct)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });
            var hire = await FindByTokenAsync(token, tracking: false);
            if (hire == null) return NotFound(new { message = "Enlace no válido o caducado." });
            if (hire.Appointment != null) return Ok(Array.Empty<object>());
            // Plazo agotado: no servir la agenda del experto.
            if (hire.SellerBookingDeadline.HasValue && DateTime.UtcNow > hire.SellerBookingDeadline.Value)
                return Ok(Array.Empty<object>());
            // Defensa: no servir días fuera de la ventana [suelo, tope] anclada al pago.
            var reqDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
            if (reqDate < SellerBookingWindow.StartUtc(hire.CreatedAt).Date
                || reqDate >= SellerBookingWindow.HardEndExclusiveUtc(hire.CreatedAt).Date)
                return Ok(Array.Empty<object>());
            var slots = await _availability.GetAvailableSlotsAsync(hire.SearchServiceId, date, ct);
            return Ok(slots);
        }

        [HttpGet("{token}/summary")]
        public async Task<IActionResult> GetSummary(string token, [FromQuery] DateTime? from, [FromQuery] int days, CancellationToken ct)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });
            var hire = await FindByTokenAsync(token, tracking: false);
            if (hire == null) return NotFound(new { message = "Enlace no válido o caducado." });
            if (hire.Appointment != null
                || (hire.SellerBookingDeadline.HasValue && DateTime.UtcNow > hire.SellerBookingDeadline.Value))
                return Ok(Array.Empty<object>());
            // La ventana manda: ignoramos el from/days del cliente y anclamos al pago.
            var fromDate = SellerBookingWindow.StartUtc(hire.CreatedAt);
            var n = days <= 0 ? SellerBookingWindow.HardDays : Math.Min(days, SellerBookingWindow.HardDays);
            var summary = await _availability.GetAvailabilitySummaryAsync(hire.SearchServiceId, fromDate, n, ct);
            return Ok(summary);
        }

        // ── Ventana de fechas efectiva (expansión progresiva objetivo→tope) ───────
        [HttpGet("{token}/window")]
        public async Task<IActionResult> GetWindow(string token, CancellationToken ct)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });
            var hire = await FindByTokenAsync(token, tracking: false);
            if (hire == null) return NotFound(new { message = "Enlace no válido o caducado." });

            var fromUtc = SellerBookingWindow.StartUtc(hire.CreatedAt);
            var fromYmd = fromUtc.ToString("yyyy-MM-dd");

            // Enlace ya usado o plazo de reserva vencido: no hay ventana ofrecible.
            if (hire.Appointment != null
                || (hire.SellerBookingDeadline.HasValue && DateTime.UtcNow > hire.SellerBookingDeadline.Value))
                return Ok(new { fromYmd, days = SellerBookingWindow.TargetDays, windowExtended = false, hasAvailability = false });

            // 1) ¿Hay huecos dentro del objetivo (+3..+7)?
            var target = await _availability.GetAvailabilitySummaryAsync(
                hire.SearchServiceId, fromUtc, SellerBookingWindow.TargetDays, ct);
            if (target.Any(d => d.FreeSlots > 0))
                return Ok(new { fromYmd, days = SellerBookingWindow.TargetDays, windowExtended = false, hasAvailability = true });

            // 2) No los hay → ampliar al tope (+3..+14).
            var hard = await _availability.GetAvailabilitySummaryAsync(
                hire.SearchServiceId, fromUtc, SellerBookingWindow.HardDays, ct);
            var hasHard = hard.Any(d => d.FreeSlots > 0);
            return Ok(new
            {
                fromYmd,
                days = hasHard ? SellerBookingWindow.HardDays : SellerBookingWindow.TargetDays,
                windowExtended = hasHard,
                hasAvailability = hasHard,
            });
        }

        // ── Confirmar la cita (crea la cita CONFIRMADA con reserva atómica) ────────
        [HttpPost("{token}/confirm")]
        public async Task<IActionResult> Confirm(string token, [FromBody] SellerConfirmDto dto)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });

            // Validaciones del hueco (mismas que el flujo del cliente).
            if (dto == null || dto.StartsAtUtc == default || dto.EndsAtUtc == default)
                return BadRequest(new { message = "Falta el día y la hora de la cita." });
            var startUtc = DateTime.SpecifyKind(dto.StartsAtUtc, DateTimeKind.Utc);
            var endUtc = DateTime.SpecifyKind(dto.EndsAtUtc, DateTimeKind.Utc);
            if (endUtc <= startUtc) return BadRequest(new { message = "El fin de la cita debe ser posterior al inicio." });
            if (startUtc <= DateTime.UtcNow) return BadRequest(new { message = "Ese hueco ya ha pasado, elige otro." });

            var hire = await FindByTokenAsync(token, tracking: true);
            if (hire == null) return NotFound(new { message = "Enlace no válido o caducado." });
            if (hire.Appointment != null) return Conflict(new { message = "La cita ya estaba reservada." });

            // Plazo para reservar agotado: el enlace ya no sirve (se reembolsará al comprador).
            if (hire.SellerBookingDeadline.HasValue && DateTime.UtcNow > hire.SellerBookingDeadline.Value)
                return BadRequest(new { message = "El plazo para reservar la cita ha caducado." });

            // Ventana fija anclada al pago: suelo +3 días, tope +14 (la expansión a 7→14 la
            // resuelve el endpoint /window; aquí solo defendemos el rango duro server-side).
            if (startUtc < SellerBookingWindow.StartUtc(hire.CreatedAt))
                return BadRequest(new { message = $"La cita debe ser al menos {SellerBookingWindow.MinLeadDays} días después de la compra." });
            if (startUtc >= SellerBookingWindow.HardEndExclusiveUtc(hire.CreatedAt))
                return BadRequest(new { message = $"La cita no puede ser más de {SellerBookingWindow.HardMaxDays} días después de la compra." });

            // El hueco debe seguir ofreciéndose por el calendario del experto.
            if (!await _availability.IsSlotBookableAsync(hire.SearchServiceId, startUtc, endUtc))
                return BadRequest(new { message = "Ese hueco ya no está disponible, elige otro." });

            // La cita nace PENDIENTE DE CONFIRMACIÓN DEL EXPERTO: el pago sigue AUTORIZADO (no se
            // cobra al confirmar el vendedor); la captura se hará cuando el experto apruebe la cita.
            var pendingExpertStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_pending_expert_confirmation");
            if (pendingExpertStatus == null)
                return StatusCode(500, new { message = "Configuración de estados incompleta." });

            var expertTimezone = hire.ExpertTimezone ?? "Europe/Madrid";
            DateTime slotLocalStart;
            try
            {
                var tzi = TimeZoneInfo.FindSystemTimeZoneById(expertTimezone);
                slotLocalStart = TimeZoneInfo.ConvertTimeFromUtc(startUtc, tzi);
            }
            catch { slotLocalStart = startUtc; }

            decimal? lat = decimal.TryParse(dto.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var latVal) ? latVal : (decimal?)null;
            decimal? lng = decimal.TryParse(dto.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var lngVal) ? lngVal : (decimal?)null;

            // 🛡️ M3 FIX: validar rango de coordenadas (mismo patrón que ChatController.SendMessage).
            // Antes se parseaban y persistían sin validar [-90,90]/[-180,180]; un valor fuera de rango
            // ensuciaba la cita (pin imposible en el mapa). Solo se valida cuando hay valor.
            if ((lat.HasValue && (lat < -90 || lat > 90)) ||
                (lng.HasValue && (lng < -180 || lng > 180)))
            {
                return BadRequest(new { message = "Latitude must be between -90 and 90, and longitude between -180 and 180" });
            }

            // ⚓ Tarea 5: capturar la cita para programar su timer de confirmación tras el commit.
            var pendingExpertAppointment = new Appointment
            {
                SearchHireId = hire.Id,
                StatusId = pendingExpertStatus.Id,
                ExpertId = hire.ExpertId,
                StartsAtUtc = startUtc,
                EndsAtUtc = endUtc,
                BlocksCalendar = true,
                Location = string.IsNullOrWhiteSpace(dto.Location) ? null : dto.Location.Trim(),
                Latitude = lat,
                Longitude = lng,
                DoorNumber = string.IsNullOrWhiteSpace(dto.DoorNumber) ? null : dto.DoorNumber.Trim(),
                // El teléfono del propietario del coche = el del vendedor capturado en el checkout.
                OwnerPhone = hire.SellerPhone,
                SiteDetails = string.IsNullOrWhiteSpace(dto.SiteDetails) ? null : dto.SiteDetails.Trim(),
                ProposedDate = DateTime.SpecifyKind(slotLocalStart.Date, DateTimeKind.Utc),
                ProposedTime = slotLocalStart.TimeOfDay,
                ProposerTimezone = expertTimezone,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _context.Appointments.Add(pendingExpertAppointment);

            // Token de un solo uso: invalidar para que el enlace no sirva dos veces.
            hire.SellerBookingToken = null;

            // 🔐 Confirmación del experto (modo seller = ventana 36h): genera el token y el plazo para
            // que el experto apruebe/rechace la cita. El plazo nunca supera el inicio de la cita.
            hire.ExpertConfirmationToken = global::newApi.Common.SecureTokenHelper.GenerateMagicLinkToken(); // FIX [M1]: CSPRNG, no Guid
            var baseDl = DateTime.UtcNow.AddHours(36);
            hire.ExpertConfirmationDeadline = baseDl < startUtc ? baseDl : startUtc;

            // Plazo de entrega real = fin de cita + SLA del servicio (sobrescribe el provisional).
            hire.CompletionDeadline = endUtc.AddHours(hire.DurationInHoursSnapshot ?? 24);

            // ⚠️ El pago quedó AUTORIZADO en el checkout (PI en requires_capture, CaptureStatus=
            // "Authorized") y AQUÍ YA NO SE CAPTURA: la cita nace en pending_expert_confirmation y la
            // captura se hará cuando el EXPERTO apruebe la cita (otra tarea). Por tanto el vendedor solo
            // reserva el hueco: no hay localización de PI ni EnsureCapturedAsync en este camino.
            //
            // La connection string usa EnableRetryOnFailure (NpgsqlRetryingExecutionStrategy), que
            // prohíbe transacciones iniciadas por el usuario salvo a través de la propia execution
            // strategy → envolvemos la reserva en ella. Los fallos NO transitorios (23P01, concurrency)
            // se capturan y devuelven IActionResult (no relanzan) para que la strategy no los reintente.
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
                await using var tx = await _context.Database.BeginTransactionAsync();

                try
                {
                    // Reservar el hueco (Add appointment + anular token + token/deadline del experto
                    // en el change-tracker) y confirmar la tx. CaptureStatus sigue "Authorized".
                    await _context.SaveChangesAsync();
                    await tx.CommitAsync();
                }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23P01")
                {
                    // Otro reservó el mismo hueco en paralelo (exclusion constraint GiST).
                    // NOTA: nunca se captura aquí — no hay POST .../capture en este camino.
                    await tx.RollbackAsync();
                    return Conflict(new { message = "Ese hueco se acaba de ocupar, elige otro." });
                }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505")
                {
                    // 🛡️ FIX [SB-1]: doble-submit casi simultáneo que pasa el check en memoria y choca con
                    // el unique index IX_Appointments_SearchHireId (ya existe una cita para este hire).
                    // Antes caía al catch genérico → 500 + CRITICAL falso. Es una colisión benigna: 409.
                    await tx.RollbackAsync();
                    return Conflict(new { message = "Esta contratación ya tiene una cita reservada." });
                }
                catch (DbUpdateConcurrencyException)
                {
                    // El job de expiración (u otro proceso) tocó el mismo hire a la vez (token xmin):
                    // el plazo acaba de vencer mientras confirmabas. Devolver 409 en vez de 500.
                    await tx.RollbackAsync();
                    return Conflict(new { message = "El plazo para reservar acaba de vencer. Pide al comprador que te reenvíe el enlace." });
                }
                catch (Exception commitEx)
                {
                    // Fallo no transitorio al persistir la reserva: rollback + log. Como AQUÍ YA NO SE
                    // CAPTURA, no hay dinero cobrado que reconciliar; basta revertir y avisar al vendedor.
                    try { await tx.RollbackAsync(); } catch { /* la tx puede estar ya inutilizable */ }

                    await _logging.LogCriticalAsync(
                        message: "CRITICAL: la cita del vendedor NO se pudo persistir",
                        details: $"SaveChanges/Commit falló al reservar el hueco del hire {hire.Id} al confirmar el vendedor. " +
                                 $"NO se cobró nada (el pago sigue AUTORIZADO) y la reserva se revirtió. Error: {commitEx.Message}",
                        userId: hire.ClientId,
                        source: "SellerBookingController.Confirm",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hire.Id,
                        additionalData: new { SearchHireId = hire.Id, Exception = commitEx.Message });

                    return StatusCode(500, new { message = "La reserva se está procesando, contacta con soporte." });
                }

                // ⚓ Tarea 5: programar el timer de confirmación del experto (auto-cancela sin strike si no
                // aprueba/rechaza dentro de ExpertConfirmationDeadline). IAppointmentService no está inyectado
                // en este controlador → lo resolvemos por scope (mismo patrón que SubscriptionController con
                // IEmailService). Best-effort: si falla, el watchdog recoge la cita pendiente sin timer.
                if (pendingExpertAppointment.Id > 0
                    && HttpContext?.RequestServices?.GetService(typeof(IAppointmentService)) is IAppointmentService apptSvc)
                {
                    try
                    {
                        await apptSvc.CreateExpertConfirmationTimerAsync(pendingExpertAppointment.Id, hire.Id);
                    }
                    catch { /* best-effort: el watchdog recogerá la cita si quedó sin timer */ }

                    // ⚓ Tarea 6: notificar al experto (email magic-link + in-app + SMS). Best-effort.
                    try { await apptSvc.NotifyExpertConfirmationRequestedAsync(hire.Id); }
                    catch { /* best-effort: la notificación nunca tumba el confirm del vendedor */ }

                    // 🔔 BUG #6 FIX [SELLER-NOTIFY]: avisar al COMPRADOR de que el vendedor ya eligió día/hora/lugar
                    // y la cita quedó pendiente de que el experto la confirme. Simetría con el camino self
                    // (FIX [SELF-NOTIFY]) y con el Decline (que sí avisa al comprador). Sin esto el comprador pagó
                    // y se queda sin acuse hasta el desenlace. Best-effort: nunca rompe el confirm del vendedor.
                    if (hire.ClientId.HasValue)
                    {
                        try
                        {
                            await _logging.LogInfoAsync(
                                message: "El vendedor ya eligió la fecha: cita pendiente de confirmación del experto",
                                details: $"Hire {hire.Id}: el vendedor reservó el hueco por el enlace; la cita queda pendiente de que el experto la confirme.",
                                userId: hire.ClientId,
                                source: "SellerBookingController.Confirm",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: hire.Id,
                                notifyUser: true,
                                userNotificationMessage: "El vendedor ya ha elegido la fecha y el lugar de la inspección. Tu cita está pendiente de que el experto la confirme; aún no se ha cobrado nada. Te avisamos en cuanto responda.");
                        }
                        catch { /* best-effort: la notificación nunca rompe el confirm del vendedor */ }
                    }
                }

                // 🔔 NOTIF-FIX [S2]: acuse al VENDEDOR de su propia reserva (email con fecha/lugar por
                // escrito + SMS). Antes solo veía la pantalla de éxito: sin comprobante, y si cerraba
                // la pestaña no le quedaba registro de a qué hora viene el técnico. Best-effort.
                if (!string.IsNullOrWhiteSpace(hire.SellerEmail) || !string.IsNullOrWhiteSpace(hire.SellerPhone))
                {
                    var whenText = slotLocalStart.ToString("dddd d 'de' MMMM 'a las' HH:mm", CultureInfo.GetCultureInfo("es-ES"));
                    if (!string.IsNullOrWhiteSpace(hire.SellerEmail)
                        && HttpContext?.RequestServices?.GetService(typeof(INotificationService)) is INotificationService notifSvc)
                    {
                        try { await notifSvc.SendSellerBookingConfirmedEmailAsync(hire.SellerEmail, whenText, pendingExpertAppointment.Location); }
                        catch { /* email best-effort */ }
                    }
                    if (!string.IsNullOrWhiteSpace(hire.SellerPhone)
                        && HttpContext?.RequestServices?.GetService(typeof(ISmsService)) is ISmsService smsSvc)
                    {
                        try
                        {
                            await smsSvc.SendSmsAsync(hire.SellerPhone,
                                $"Inspecciono: reserva registrada para el {whenText}. El tecnico confirmara la cita; no tienes que hacer nada mas.");
                        }
                        catch { /* SMS best-effort */ }
                    }
                }

                return Ok(new { ok = true });
            });
        }

        // ── Declinar la cita ("no puedo coordinar") → devolución sin coste al comprador ──
        // El vendedor pulsa "no puedo quedar". Cancelamos la AUTORIZACIÓN del pago SIN coste
        // (PI en requires_capture → cancel = 0 €, ni la fee de Stripe) y finalizamos el hire
        // devolviendo el 100% al comprador. MODELADO sobre el job de expiración
        // (PlatformMaintenanceService.ProcessExpiredSellerBookingsAsync) para máxima
        // consistencia: anular el token actúa de mutex frente al confirm y al propio job
        // de expiración, y la finalización la hace la MISMA rama N10 de la distribución de
        // dinero (cancela el PI si está en requires_capture, reembolsa si estuviera succeeded).
        [HttpPost("{token}/decline")]
        public async Task<IActionResult> Decline(string token)
        {
            if (!TokenLooksValid(token)) return NotFound(new { message = "Enlace no válido." });

            var hire = await FindByTokenAsync(token, tracking: true);
            if (hire == null) return NotFound(new { message = "Enlace no válido o caducado." });
            if (hire.Appointment != null) return Conflict(new { message = "La cita ya estaba reservada." });

            // Anular el token = mutex: a partir de aquí ni el confirm ni el job de expiración
            // (ambos buscan por token) podrán crear la cita ni coexistir con esta devolución.
            hire.SellerBookingToken = null;
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Carrera confirm-vs-decline: el confirm ganó (token xmin). La cita ya quedó
                // reservada y capturada por el confirm → NO declinamos ni reembolsamos. 409 limpio
                // (en vez de un 500), igual que el catch equivalente en Confirm.
                return Conflict(new { message = "La cita acaba de reservarse. No se puede cancelar." });
            }

            // Reembolso 100% al comprador + cancelar el hire, reusando EXACTAMENTE el patrón del
            // job de expiración. "appointment_cancelled_by_client_gt24h" → (cliente 100, experto 0,
            // plataforma 0); es finalización, mapea el hire a 'cancelled' y NO penaliza al experto.
            // La rama N10 del RefundService CANCELA el PI si sigue en requires_capture (0 €, sin
            // fee) o reembolsa si estuviera succeeded (legacy). NO pre-cancelamos el PI aquí para
            // no romper esa lógica refund-vs-cancel.
            // 🛡️ B3 FIX (auditoría 2026-07-06): try/catch alrededor del enqueue — único de los 4
            // hermanos sin él. Si Hangfire está caído, el token ya se anuló (SaveChanges arriba) y
            // devolver 500 dejaría al vendedor sin poder reintentar; el self-healing de
            // ProcessExpiredSellerBookingsAsync recoge el hire al vencer el deadline (solo
            // autorización, 0€ en juego). CRITICAL para que el admin lo vea antes.
            try
            {
                Hangfire.BackgroundJob.Enqueue<global::newApi.Services.StripeRefundService>(svc =>
                    svc.ProcessMoneyDistributionAsync(
                        hire.Id,
                        "appointment_cancelled_by_client_gt24h",
                        "El vendedor indicó que no puede coordinar la cita: devolución sin coste al comprador.",
                        null,
                        true));
            }
            catch (Exception enqueueEx)
            {
                await _logging.LogCriticalAsync(
                    message: "CRITICAL: Decline del vendedor sin enqueue del reembolso - lo recogerá el job de expiración",
                    details: $"Hire {hire.Id}: el vendedor declinó (token ya consumido) pero Hangfire no aceptó el job de devolución: {enqueueEx.Message}. El PI sigue solo AUTORIZADO (0€); ProcessExpiredSellerBookingsAsync lo cancelará al vencer el deadline si nadie lo hace antes.",
                    userId: hire.ClientId,
                    source: "SellerBookingController.Decline",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: hire.Id);
            }

            // Avisar al COMPRADOR de la devolución. En captura diferida el PI se CANCELA (no se refunda),
            // así que la notificación "Reembolso procesado" del RefundService no se dispara → sin esto el
            // comprador no se enteraría de que su reserva se canceló y se le devolvió todo. Best-effort:
            // nunca rompe el decline (el reembolso ya está encolado).
            if (hire.ClientId.HasValue)
            {
                try
                {
                    await _logging.LogInfoAsync(
                        message: "Vendedor declinó la coordinación: reembolso 100% al comprador",
                        details: $"Hire {hire.Id}: el vendedor declinó por el enlace; se devuelve el 100% (0€).",
                        userId: hire.ClientId,
                        source: "SellerBookingController.Decline",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hire.Id,
                        notifyUser: true,
                        userNotificationMessage: "El vendedor no puede coordinar la inspección, así que hemos cancelado tu reserva y te devolvemos el 100% (no se ha cobrado nada). Puedes contratar de nuevo cuando quieras.");
                }
                catch { /* best-effort: la notificación nunca rompe el decline */ }
            }

            // NOTIF-FIX [G6b]: avisar al EXPERTO de que el vendedor declinó coordinar y la contratación se
            // cancela. En modo "seller" el experto YA fue contratado y notificado al crearse el hire ("tienes
            // una nueva contratación; el cliente/vendedor propondrá fecha") y queda a la espera de que el vendedor
            // elija hueco. Si el vendedor declina, el hire se finaliza/cancela → sin este aviso el experto seguiría
            // esperando una cita que nunca llegará y reservando disponibilidad en balde. Aquí Appointment == null
            // (no se llegó a pedir confirmación de una cita concreta), así que es solo aviso de cancelación, no de
            // "cita muerta". In-app+email por LogInfoAsync(notifyUser:true) + SMS de refuerzo. Best-effort: nunca
            // rompe el decline (el reembolso al comprador ya está encolado).
            if (hire.ExpertId.HasValue && hire.ExpertId.Value > 0)
            {
                try
                {
                    await _logging.LogInfoAsync(
                        message: "El vendedor declinó coordinar: contratación cancelada",
                        details: $"Hire {hire.Id}: el vendedor declinó por el enlace; la contratación se cancela y ya no tendrás que realizar esta inspección.",
                        userId: hire.ExpertId,
                        source: "SellerBookingController.Decline",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hire.Id,
                        notifyUser: true,
                        userNotificationMessage: $"El vendedor de la contratación #{hire.Id} ha indicado que no puede coordinar la inspección, así que la contratación se ha cancelado. Ya no tienes que realizarla; se ha devuelto el importe al cliente.");
                    try
                    {
                        await HttpContext.RequestServices.GetRequiredService<IInAppNotificationService>()
                            .SendImportantSmsAsync(hire.ExpertId.Value,
                                $"Inspecciono: el vendedor de la contratación #{hire.Id} no puede coordinar la cita, así que se ha cancelado. No tienes que hacer nada.");
                    }
                    catch { /* SMS best-effort */ }
                }
                catch { /* best-effort: el aviso al experto nunca rompe el decline */ }
            }

            return Ok(new { ok = true });
        }
    }

    public class SellerConfirmDto
    {
        public DateTime StartsAtUtc { get; set; }
        public DateTime EndsAtUtc { get; set; }
        public string? Location { get; set; }
        public string? Latitude { get; set; }
        public string? Longitude { get; set; }
        public string? DoorNumber { get; set; }
        public string? SiteDetails { get; set; }
    }
}
