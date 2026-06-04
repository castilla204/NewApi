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

        public ExpertRelocationService(
            AppDbContext context,
            ILoggingService loggingService,
            Microsoft.Extensions.Logging.ILogger<ExpertRelocationService> logger)
        {
            _context = context;
            _loggingService = loggingService;
            _logger = logger;
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

            var result = new RelocationPreflightResult
            {
                CanProceed = pendingDisputes == 0 && activeHires == 0 && recentRefunds == 0,
                PendingDisputes = pendingDisputes,
                ActiveHires = activeHires,
                RecentRefunds = recentRefunds,
                ReceivedReviewsCount = receivedReviewsCount,
                ActiveServicesCount = activeServicesCount,
                CurrentCountry = profile.Country,
                StripeAccountId = profile.StripeAccountId,
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
