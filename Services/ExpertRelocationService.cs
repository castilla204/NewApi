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

            // Cerrar Stripe acct si existe.
            async Task TryCleanupStripeAccount(string? acctId, string label)
            {
                if (string.IsNullOrEmpty(acctId)) return;
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
            profile.IsOnVacation = true; // ocultar de búsquedas hasta nuevo onboarding

            await _context.SaveChangesAsync(ct);

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
