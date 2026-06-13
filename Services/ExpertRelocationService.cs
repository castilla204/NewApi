using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using Stripe;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ Round 28 MUD-E: servicio de mudanza self-service del experto.
    ///
    /// Permite a un experto cerrar su cuenta Stripe Connect (cuyo `country` es inmutable)
    /// y empezar onboarding nuevo en su nuevo país, PRESERVANDO el resto de su cuenta:
    ///  - User.Id intacto (sigue siendo el mismo usuario).
    ///  - Historial cliente (hires como cliente, reviews dadas, conversations, MFA).
    ///  - Reviews recibidas como experto (etiquetadas con país antiguo vía
    ///    ExpertProfile.RelocatedFromCountry + Review.ReceivedInCountry — MUD-D).
    ///
    /// QUÉ HACE:
    ///   1. Preflight: bloquea si hay disputas Pending o hires en estado intermedio
    ///      (mismo patrón que AccountDeletionService y `reset-stripe`).
    ///   2. Cierra la cuenta Stripe Connect (Delete + fallback Reject — patrón R28-MUD-4).
    ///   3. Desactiva servicios actuales (SearchService.IsActive=false).
    ///   4. Marca ExpertProfile: RelocatedFromCountry=Country, RelocatedAt=now.
    ///   5. Resetea: StripeAccountId=null, PendingStripeAccountId=null, StripeStatus=NotRequested,
    ///      OnboardingCompleted=false, Country=null, Timezone=null, City=null,
    ///      Latitude/Longitude=null, IsOnVacation=true (para que no aparezca en búsquedas
    ///      hasta que complete el nuevo onboarding).
    ///   6. NO toca el User. La cuenta cliente sigue operativa.
    ///   7. Email al usuario con los siguientes pasos.
    ///
    /// Tras esto, el experto va a /become-expert que detecta `RelocatedFromCountry != null
    /// && OnboardingCompleted=false` y permite re-registro sin error EXPERT_PROFILE_ALREADY_EXISTS.
    /// </summary>
    public class ExpertRelocationService
    {
        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;
        private readonly Microsoft.Extensions.Logging.ILogger<ExpertRelocationService> _logger;
        // 🛡️ Round 28 MUD-BD: cola de pérdidas pendientes off-Stripe.
        private readonly ClawbackQueueService? _clawbackQueue;

        public ExpertRelocationService(
            AppDbContext context,
            ILoggingService loggingService,
            Microsoft.Extensions.Logging.ILogger<ExpertRelocationService> logger,
            ClawbackQueueService? clawbackQueue = null)
        {
            _context = context;
            _loggingService = loggingService;
            _logger = logger;
            _clawbackQueue = clawbackQueue;
        }

        public class RelocationPreflightResult
        {
            public bool CanProceed { get; set; }
            public string? BlockedReason { get; set; }
            public int PendingDisputes { get; set; }
            public int ActiveHires { get; set; }
            public int RecentRefunds { get; set; }
            public int ReceivedReviewsCount { get; set; }
            public int ActiveServicesCount { get; set; }
            public string? CurrentCountry { get; set; }
            public string? StripeAccountId { get; set; }
            // 🛡️ Round 28 MUD-AK: balance Stripe aún no liquidado (PIs capturados pero <2-7d antiguos).
            // Si > 0, cerrar la cuenta ahora hace que el dinero revierta al platform → pérdida real.
            // Se expone para que el frontend muestre el monto y avise al experto que espere al settlement.
            public decimal PendingBalanceMajorUnits { get; set; }
            public string? PendingBalanceCurrencies { get; set; } // "EUR:120.50,USD:30.00" para debug del wizard
        }

        public async Task<RelocationPreflightResult> PreflightAsync(int userId, CancellationToken ct = default)
        {
            var profile = await _context.ExpertProfiles
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.UserId == userId, ct);
            if (profile == null)
            {
                return new RelocationPreflightResult
                {
                    CanProceed = false,
                    BlockedReason = "Este usuario no tiene perfil de experto."
                };
            }

            var pendingDisputes = await _context.Disputes
                .AsNoTracking()
                .Where(d => (d.Status == "Pending" || d.Status == "Resolving")
                         && d.SearchHire != null
                         && (d.SearchHire.ExpertId == userId || d.ReporterId == userId))
                .CountAsync(ct);

            var activeHires = await _context.SearchHires
                .AsNoTracking()
                .Include(h => h.Status)
                .Where(h => h.ExpertId == userId
                         && h.Status != null
                         && !h.Status.IsFinalizationStatus)
                .CountAsync(ct);

            var recentRefunds = await _context.FinancialTransactions
                .AsNoTracking()
                .Where(ft => ft.UserId == userId
                          && ft.TransactionType == "Refund"
                          && ft.CreatedAt > System.DateTime.UtcNow.AddHours(-24))
                .CountAsync(ct);

            var receivedReviewsCount = await _context.Reviews
                .AsNoTracking()
                .CountAsync(r => r.ExpertId == userId, ct);

            var activeServicesCount = await _context.SearchServices
                .AsNoTracking()
                .CountAsync(s => s.ExpertProfileId == profile.Id && s.IsActive, ct);

            // 🛡️ Round 28 MUD-AK + MUD-AY: leer balance Stripe (Available + Pending) para
            // detectar dinero aún no liquidado. Si Pending > 0 (PI capturado pero <settlement
            // window), bloqueamos la mudanza porque al cerrar la cuenta ese balance pendiente
            // revierte al platform → pérdida real para el experto.
            //
            // MUD-AY: si la lectura del balance falla por timeout/5xx/rate-limit, somos
            // fail-CLOSED (no fail-open). La mudanza es irreversible y un Stripe outage no
            // debe permitir cerrar una cuenta con Pending invisible. Si Stripe está caído
            // 30 segundos, el experto reintenta el preflight más tarde — pérdida de UX es
            // pequeña, pérdida de dinero potencial es alta. El 404 (cuenta ya gone) sí es
            // benigno: no hay balance a perder.
            decimal pendingMajor = 0m;
            string? pendingCurrencies = null;
            bool pendingReadFailed = false;
            if (!string.IsNullOrEmpty(profile.StripeAccountId))
            {
                try
                {
                    var balSvc = new BalanceService();
                    var bal = await balSvc.GetAsync(new RequestOptions { StripeAccount = profile.StripeAccountId });
                    if (bal?.Pending != null && bal.Pending.Count > 0)
                    {
                        var parts = new List<string>();
                        foreach (var p in bal.Pending)
                        {
                            if (p.Amount <= 0) continue;
                            // Stripe HUF/ISK/TWD: minor units requeridos múltiplos de 100, divisor
                            // 100 sigue siendo correcto. True zero-decimal (JPY/KRW/etc) NO están
                            // en SupportedConnectCountries del proyecto.
                            var major = p.Amount / 100m;
                            pendingMajor += major;
                            parts.Add($"{p.Currency.ToUpperInvariant()}:{major:F2}");
                        }
                        if (parts.Count > 0)
                        {
                            pendingCurrencies = string.Join(",", parts);
                        }
                    }
                }
                catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Cuenta no existe en Stripe → tratamos como sin pending (benigno).
                }
                catch (System.Exception readEx)
                {
                    pendingReadFailed = true;
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL MUD-AY: failed to read Stripe Pending balance during relocation preflight — fail-closed",
                        details: $"UserId {userId} acct {profile.StripeAccountId}: {readEx.Message}. Bloqueamos la mudanza fail-CLOSED para evitar cerrar cuenta con Pending invisible si Stripe está caído. El experto debe reintentar más tarde. Si persiste >24h, admin debe verificar manualmente.",
                        userId: userId,
                        source: "ExpertRelocationService.MUD-AY.PendingReadFailed",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: profile.Id);
                }
            }

            var result = new RelocationPreflightResult
            {
                // 🛡️ MUD-AY: pendingReadFailed se incluye en el gate. Si Stripe API falla,
                // bloqueamos la mudanza hasta que el experto reintente (en lugar de permitir
                // cierre con Pending invisible).
                CanProceed = pendingDisputes == 0 && activeHires == 0 && recentRefunds == 0 && pendingMajor <= 0m && !pendingReadFailed,
                PendingDisputes = pendingDisputes,
                ActiveHires = activeHires,
                RecentRefunds = recentRefunds,
                ReceivedReviewsCount = receivedReviewsCount,
                ActiveServicesCount = activeServicesCount,
                CurrentCountry = profile.Country,
                StripeAccountId = profile.StripeAccountId,
                PendingBalanceMajorUnits = pendingMajor,
                PendingBalanceCurrencies = pendingCurrencies,
            };

            if (pendingDisputes > 0)
            {
                result.BlockedReason = $"Tienes {pendingDisputes} disputa(s) activa(s). Resuelve las disputas antes de mudarte para no dejar dinero atascado.";
            }
            else if (activeHires > 0)
            {
                result.BlockedReason = $"Tienes {activeHires} contratación(es) en curso. Espera a que finalicen (o cancélalas desde el panel) antes de mudarte.";
            }
            else if (recentRefunds > 0)
            {
                result.BlockedReason = $"Hay {recentRefunds} refund(s) en las últimas 24h. Espera a su settlement Stripe antes de mudarte.";
            }
            else if (pendingMajor > 0m)
            {
                // 🛡️ Round 28 MUD-AK: bloqueo crítico. Stripe Pending = dinero capturado pero no
                // liquidado (típico 2-7d). Cerrar ahora hace que ese balance se devuelve al
                // platform → pérdida real para el experto. Se le pide esperar al settlement.
                result.BlockedReason = $"Tienes {pendingCurrencies} pendiente(s) de liquidar en Stripe (cobros recientes). Espera 2-7 días al settlement antes de mudarte — si cierras la cuenta ahora, ese dinero se devuelve a la plataforma y NO podemos recuperarlo automáticamente.";
            }
            else if (pendingReadFailed)
            {
                // 🛡️ MUD-AY: fail-closed. Stripe API caído → no podemos verificar Pending.
                // Mejor bloquear y reintentar que cerrar a ciegas.
                result.BlockedReason = "No hemos podido comprobar tu balance pendiente en Stripe ahora mismo. Reintenta en unos minutos. Si persiste tras una hora, contacta soporte — no queremos cerrar tu cuenta sin verificar que no quedan cobros recientes sin liquidar.";
            }

            return result;
        }

        public async Task<(bool Success, string? ErrorMessage, List<object> StripeOps)> ExecuteAsync(
            int userId, string? reason, bool force, CancellationToken ct = default)
        {
            var profile = await _context.ExpertProfiles
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.UserId == userId, ct);
            if (profile == null)
            {
                return (false, "ExpertProfile no encontrado", new List<object>());
            }

            if (!force)
            {
                var preflight = await PreflightAsync(userId, ct);
                if (!preflight.CanProceed)
                {
                    return (false, preflight.BlockedReason, new List<object>());
                }
            }

            var stripeOps = new List<object>();
            var beforeCountry = profile.Country;
            var beforeStripeAccountId = profile.StripeAccountId;
            var beforePendingStripeAccountId = profile.PendingStripeAccountId;
            var expertProfileId = profile.Id;

            // 🛡️ Round 28 MUD-L (GAP-5 fix): para expertos US, verificar capability 1099 ANTES
            // de cerrar la cuenta. Sin ella Stripe NO genera el 1099-MISC final → exposición IRS
            // ($290-$630 por seller no reportado).
            if (string.Equals(beforeCountry, "US", System.StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(beforeStripeAccountId))
            {
                try
                {
                    var acctCheck = await new AccountService().GetAsync(beforeStripeAccountId);
                    var cap1099 = acctCheck?.Capabilities?.TaxReportingUs1099Misc;
                    var has1099 = string.Equals(cap1099, "active", System.StringComparison.OrdinalIgnoreCase)
                               || string.Equals(cap1099, "pending", System.StringComparison.OrdinalIgnoreCase);
                    if (!has1099)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "MUD-L: US expert relocating without tax_reporting_us_1099_misc capability — IRS exposure",
                            details: $"UserId {userId} (acct {beforeStripeAccountId}): la cuenta NO tiene capability `tax_reporting_us_1099_misc` activa (estado: {cap1099 ?? "none"}). Stripe NO emitirá 1099-MISC al cierre. ACCIÓN ADMIN: o (a) bloquear la mudanza y pedir al experto que active la capability primero (24h), o (b) generar 1099 manualmente al cierre de año.",
                            userId: userId,
                            source: "ExpertRelocationService.MUD-L.Tax1099Missing",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: expertProfileId,
                            additionalData: new { Capability1099 = cap1099, StripeAccountId = beforeStripeAccountId });
                    }
                }
                catch (System.Exception ex)
                {
                    await _loggingService.LogWarningAsync(
                        message: "MUD-L: failed to read capability tax_reporting_us_1099_misc before relocation",
                        details: $"UserId {userId}: {ex.Message}. Procedemos con la mudanza pero el admin debe verificar manualmente.",
                        userId: userId,
                        source: "ExpertRelocationService.MUD-L.Tax1099CheckFailed",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfileId);
                }
            }

            // 🛡️ Round 28 MUD-L (GAP-2 fix): drenar balance ANTES de Delete. Sin esto, si el
            // experto tiene saldo, Delete falla → caemos a Reject → Stripe reverte el balance al
            // platform → experto pierde su dinero. Patrón de AccountDeletionService.cs:2354-2425.
            async Task DrainBalanceIfAnyAsync(string acctId)
            {
                try
                {
                    var balanceService = new BalanceService();
                    var reqOpts = new RequestOptions { StripeAccount = acctId };
                    var acctBalance = await balanceService.GetAsync(reqOpts);

                    // 🛡️ Round 28 MUD-AK: si llegamos aquí con Pending>0, el preflight ha sido
                    // bypaseado (force=true típicamente por admin). Loggear Critical porque
                    // ese balance pendiente se va a perder al cerrar la cuenta — Stripe lo
                    // devuelve al platform y NO hay TransferReversal posible (el dinero
                    // todavía no estaba en Available, así que no hay charge.transfer.id
                    // contra el que clawbackear). Es money loss real.
                    if (acctBalance?.Pending != null)
                    {
                        foreach (var pending in acctBalance.Pending)
                        {
                            if (pending.Amount <= 0) continue;
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL MUD-AK: relocation closing Stripe acct with Pending balance — money will revert to platform",
                                details: $"UserId {userId} acct {acctId}: tiene {pending.Amount / 100m:F2} {pending.Currency.ToUpperInvariant()} en Pending. Al cerrar la cuenta Stripe devuelve este balance al platform y NO hay forma de hacer clawback automático (no es Available, no hay transfer.id contra el que reversar). ACCIÓN ADMIN: enviar el dinero al experto manualmente desde el platform balance una vez Stripe libere el settlement (2-7d). force=true bypasea esta guarda — solo usar si el experto acepta la pérdida explícitamente.",
                                userId: userId,
                                source: "ExpertRelocationService.MUD-AK.PendingLoss",
                                relatedEntityType: "ExpertProfile",
                                relatedEntityId: expertProfileId,
                                additionalData: new { Amount = pending.Amount / 100m, Currency = pending.Currency.ToUpperInvariant(), StripeAccountId = acctId });
                            stripeOps.Add(new { acctId, op = "pending_balance_lost", amount = pending.Amount / 100m, currency = pending.Currency.ToUpperInvariant() });
                            // 🛡️ MUD-BD: encolar en ClawbackQueues para que el admin tenga
                            // un dashboard donde marcar Resolved cuando reciba/transfiera.
                            if (_clawbackQueue != null)
                            {
                                await _clawbackQueue.EnqueueAsync(
                                    userId: userId,
                                    stripeAccountId: acctId,
                                    amountMajor: pending.Amount / 100m,
                                    currency: pending.Currency.ToUpperInvariant(),
                                    reason: "PendingBalance",
                                    notes: $"Relocation: Stripe Pending balance al cerrar cuenta. Settlement Stripe 2-7d → admin debe transferir off-Stripe al experto.");
                            }
                        }
                    }

                    if (acctBalance?.Available == null) return;
                    foreach (var avail in acctBalance.Available)
                    {
                        if (avail.Amount <= 0) continue;
                        try
                        {
                            var payoutSvc = new PayoutService();
                            var payoutOpts = new PayoutCreateOptions
                            {
                                Amount = avail.Amount,
                                Currency = avail.Currency,
                                Metadata = new Dictionary<string, string>
                                {
                                    { "reason", "expert_relocation_final_payout" },
                                    { "userId", userId.ToString() },
                                    { "expertProfileId", expertProfileId.ToString() }
                                }
                            };
                            var payoutReqOpts = new RequestOptions
                            {
                                StripeAccount = acctId,
                                // 🛡️ MUD-AE: incluir acctId + Amount. Sin acctId, mudanzas
                                // back-to-back (ES→IE→UK) reusan la key con balance distinto
                                // → Stripe idempotency_error. acctId varía por cada Stripe
                                // acct cerrada (cada mudanza es una acct distinta) → key
                                // siempre fresca.
                                IdempotencyKey = $"relocation-payout-{acctId}-{avail.Currency}-{avail.Amount}"
                            };
                            var payout = await payoutSvc.CreateAsync(payoutOpts, payoutReqOpts);
                            stripeOps.Add(new { acctId, op = "final_payout", amount = avail.Amount / 100m, currency = avail.Currency.ToUpperInvariant(), payoutId = payout.Id });
                        }
                        catch (StripeException payoutEx)
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "MUD-L: final payout failed before relocation Stripe close — balance will revert to platform",
                                details: $"UserId {userId} acct {acctId}: payout de {avail.Amount / 100m:F2} {avail.Currency.ToUpperInvariant()} FALLÓ: {payoutEx.StripeError?.Code} - {payoutEx.Message}. Si caemos a Reject, Stripe reverte el balance al platform. ACCIÓN ADMIN: identificar al experto y enviar el dinero manualmente.",
                                userId: userId,
                                source: "ExpertRelocationService.MUD-L.PayoutFailed",
                                relatedEntityType: "ExpertProfile",
                                relatedEntityId: expertProfileId,
                                additionalData: new { Amount = avail.Amount / 100m, Currency = avail.Currency.ToUpperInvariant(), PayoutError = payoutEx.StripeError?.Code, payoutEx.Message });
                            stripeOps.Add(new { acctId, op = "final_payout_failed", amount = avail.Amount / 100m, currency = avail.Currency.ToUpperInvariant(), error = payoutEx.StripeError?.Code });
                        }
                    }
                }
                catch (System.Exception readEx)
                {
                    stripeOps.Add(new { acctId, op = "balance_read_failed", error = readEx.Message });
                }
            }

            // Cerrar Stripe acct si existe.
            async Task TryCleanupStripeAccount(string? acctId, string label)
            {
                if (string.IsNullOrEmpty(acctId)) return;
                await DrainBalanceIfAnyAsync(acctId);

                // 🛡️ Round 28 MUD-BE: re-leer balance JUSTO ANTES del Delete. Si entre el
                // primer read (DrainBalanceIfAnyAsync líneas 287-340) y el Delete, un
                // Transfer en vuelo aterriza en Pending (webhook PI capturado segundos antes
                // de la mudanza), el primer log Critical MUD-AK no lo captura. La ventana
                // típica es ~100-500ms pero con webhook latency Stripe (~1-3s) puede
                // ocurrir si una compra justo concluyó. Sin servicios desactivados todavía
                // (línea 397, tras este cleanup), LoadMoneyService sigue aceptando
                // checkouts durante toda la ejecución.
                try
                {
                    var balSvc2 = new BalanceService();
                    var bal2 = await balSvc2.GetAsync(new RequestOptions { StripeAccount = acctId });
                    if (bal2?.Pending != null)
                    {
                        foreach (var pending in bal2.Pending)
                        {
                            if (pending.Amount <= 0) continue;
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL MUD-BE: in-flight transfer landed in Pending between drain and delete",
                                details: $"UserId {userId} acct {acctId}: en el segundo read del balance (post-drain, pre-delete) detectamos {pending.Amount / 100m:F2} {pending.Currency.ToUpperInvariant()} en Pending que NO existían en el primer read. Esto indica un Transfer en vuelo (webhook PI capturado entre Preflight y Cleanup). El dinero se pierde igual al Delete. ACCIÓN ADMIN: igual que MUD-AK — recuperar off-Stripe desde platform balance.",
                                userId: userId,
                                source: "ExpertRelocationService.MUD-BE.InFlightTransfer",
                                relatedEntityType: "ExpertProfile",
                                relatedEntityId: expertProfileId,
                                additionalData: new { Amount = pending.Amount / 100m, Currency = pending.Currency.ToUpperInvariant(), StripeAccountId = acctId });
                            stripeOps.Add(new { acctId, op = "inflight_pending_lost", amount = pending.Amount / 100m, currency = pending.Currency.ToUpperInvariant() });
                            // 🛡️ MUD-BD: encolar también este caso (in-flight race).
                            if (_clawbackQueue != null)
                            {
                                await _clawbackQueue.EnqueueAsync(
                                    userId: userId,
                                    stripeAccountId: acctId,
                                    amountMajor: pending.Amount / 100m,
                                    currency: pending.Currency.ToUpperInvariant(),
                                    reason: "InFlightTransfer",
                                    notes: $"Relocation: Transfer in-flight landed in Pending entre drain y Delete. Race típica ~100-500ms con webhook PI capturado. Recuperar off-Stripe igual que PendingBalance.");
                            }
                        }
                    }
                }
                catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Cuenta gone entre llamadas — proceder con el catch del Delete (también dará 404).
                }
                catch (System.Exception reReadEx)
                {
                    // Fallo en re-read: no abortamos, el primer read ya cubrió el caso normal.
                    stripeOps.Add(new { acctId, op = "second_balance_read_failed", error = reReadEx.Message });
                }

                try
                {
                    var svc = new AccountService();
                    await svc.DeleteAsync(acctId);
                    stripeOps.Add(new { acctId, label, op = "deleted" });
                }
                catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    stripeOps.Add(new { acctId, label, op = "already_gone_404" });
                }
                catch (StripeException delEx)
                {
                    // 🛡️ MUD-X: Si Delete falla (no es 404), reintentar una vez con backoff
                    // antes de fallar a Reject. Esto mejora el caso donde Stripe está transitoriamente
                    // unavailable (timeout, 5xx, rate-limit). Una mudanza fallida es más grave que
                    // una espera de 500ms adicional.
                    bool deleteRetrySucceeded = false;
                    if (delEx.HttpStatusCode != System.Net.HttpStatusCode.BadRequest)  // No reintentar bad requests
                    {
                        try
                        {
                            await Task.Delay(500);
                            var svc = new AccountService();
                            await svc.DeleteAsync(acctId);
                            stripeOps.Add(new { acctId, label, op = "deleted_on_retry", initialError = delEx.StripeError?.Code });
                            deleteRetrySucceeded = true;
                        }
                        catch (StripeException retryEx)
                        {
                            // Retry también falló, continuar a Reject
                            await _loggingService.LogWarningAsync(
                                message: "MUD-X: Stripe account delete retry also failed, falling back to Reject",
                                details: $"UserId {userId} acct {acctId}: Delete falló ({delEx.StripeError?.Code}), retry también falló ({retryEx.StripeError?.Code}). Intentando Reject.",
                                userId: userId,
                                source: "ExpertRelocationService.DeleteRetryFailed",
                                relatedEntityType: "ExpertProfile",
                                relatedEntityId: expertProfileId,
                                additionalData: new { StripeAccountId = acctId, Label = label, InitialError = delEx.StripeError?.Code, RetryError = retryEx.StripeError?.Code });
                        }
                    }

                    // Si Delete (o retry) no tuvo éxito, intentar Reject
                    if (!deleteRetrySucceeded)
                    {
                        try
                        {
                            var svc = new AccountService();
                            await svc.RejectAsync(acctId, new AccountRejectOptions { Reason = "other" });
                            stripeOps.Add(new { acctId, label, op = "rejected_after_delete_failed", deleteError = delEx.StripeError?.Code });
                        }
                        catch (System.Exception rejEx)
                        {
                            // 🛡️ Round 28 MUD-N (GAP-4 fix): si AMBAS ops fallan, la cuenta queda
                            // huérfana en Stripe y el admin debe limpiarla a mano. Log Critical
                            // (admin alert) — antes solo iba a stripeOps en memoria.
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL MUD-N: Stripe account delete AND reject both failed during relocation — orphan account in Stripe",
                                details: $"UserId {userId} acct {acctId} (label={label}): Delete falló ({delEx.StripeError?.Code} - {delEx.Message}); Reject también falló ({rejEx.Message}). La cuenta sigue VIVA en Stripe. ACCIÓN ADMIN: limpiar manualmente desde Stripe Dashboard → Connect → Accounts.",
                                userId: userId,
                                source: "ExpertRelocationService.MUD-N.OrphanAccount",
                                relatedEntityType: "ExpertProfile",
                                relatedEntityId: expertProfileId,
                                additionalData: new { StripeAccountId = acctId, Label = label, DeleteError = delEx.StripeError?.Code, RejectError = rejEx.Message });
                            stripeOps.Add(new { acctId, label, op = "delete_and_reject_failed", deleteError = delEx.Message, rejectError = rejEx.Message });
                        }
                    }
                }
                catch (System.Exception unexpectedEx)
                {
                    stripeOps.Add(new { acctId, label, op = "unexpected_error", error = unexpectedEx.Message });
                }
            }

            await TryCleanupStripeAccount(beforeStripeAccountId, "StripeAccountId");
            if (!string.Equals(beforePendingStripeAccountId, beforeStripeAccountId, System.StringComparison.Ordinal))
            {
                await TryCleanupStripeAccount(beforePendingStripeAccountId, "PendingStripeAccountId");
            }

            // Desactivar servicios actuales (preservar para histórico de hires).
            var deactivatedServices = await _context.SearchServices
                .Where(s => s.ExpertProfileId == profile.Id && s.IsActive)
                .ToListAsync(ct);
            foreach (var s in deactivatedServices)
            {
                s.IsActive = false;
            }

            // Marcar ExpertProfile como mudado.
            profile.RelocatedFromCountry = profile.Country;
            profile.RelocatedAt = System.DateTime.UtcNow;
            profile.StripeAccountId = null;
            profile.PendingStripeAccountId = null;
            profile.StripeStatus = StripeStatus.NotRequested;
            profile.StripeStatusDetails = null;
            profile.StripeFutureRequirements = null;
            profile.StripeFutureDueAt = null;
            profile.OnboardingCompleted = false;
            profile.Country = null;
            profile.City = null;
            profile.Timezone = "UTC";
            profile.Latitude = string.Empty;
            profile.Longitude = string.Empty;
            // 🛡️ Round 28 MUD-O (GAP-6 fix): NO usar IsOnVacation como hack — confunde la UX
            // ("estás de vacaciones" cuando en realidad se mudó). Los filtros de búsqueda ya
            // descartan perfiles con Country == null, así que basta con eso.

            await _context.SaveChangesAsync(ct);

            // 🛡️ Round 28 MUD-P (GAP-8 fix): notificación visible al usuario (campana + email).
            // Antes solo se loggeaba internamente — si el usuario cerraba el tab tras success
            // no tenía recordatorio del siguiente paso.
            await _loggingService.LogInfoAsync(
                message: "Mudanza ejecutada: completa tu registro en el nuevo país",
                details: $"Tu cuenta Stripe Connect (país {beforeCountry}) se ha cerrado correctamente. Tu perfil de usuario, tu historial como cliente y tus reseñas recibidas siguen intactos. Para volver a operar como experto, ve a 'Convertirse en experto' y completa el onboarding en tu nuevo país.",
                userId: userId,
                source: "ExpertRelocationService.UserNotification",
                relatedEntityType: "ExpertProfile",
                relatedEntityId: profile.Id,
                notifyUser: true);

            await _loggingService.LogCriticalAsync(
                message: "Expert relocation executed",
                details: $"UserId {userId}: mudanza desde {beforeCountry} → cuenta cerrada. Stripe acct anterior: {beforeStripeAccountId}. Servicios desactivados: {deactivatedServices.Count}. Reason: {reason ?? "(no especificada)"}.",
                userId: userId,
                source: "ExpertRelocationService.ExecuteAsync",
                relatedEntityType: "ExpertProfile",
                relatedEntityId: profile.Id,
                additionalData: new
                {
                    FromCountry = beforeCountry,
                    StripeAccountId = beforeStripeAccountId,
                    DeactivatedServices = deactivatedServices.Count,
                    StripeOps = stripeOps,
                    Force = force,
                });

            return (true, null, stripeOps);
        }
    }
}
