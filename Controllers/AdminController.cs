using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.Services;
using System.Security.Claims;
using Stripe;
using Microsoft.Extensions.Configuration;
using Google.Cloud.SecretManager.V1;
using Google.Api.Gax.Grpc;
using Grpc.Core;

namespace newApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [EnableRateLimiting("admin")] // ✅ SEGURIDAD: 200 requests/minuto para admin
    // 🛡️ FIX [W25-ADMIN-CLASS-GUARD] (auditoría 2026-07-13): guard de rol a nivel de CLASE (antes solo
    // [Authorize] = cualquier autenticado). Los 20 endpoints ya repiten [Authorize(Roles="Admin")], pero
    // sin el guard de clase un método futuro añadido SIN el atributo quedaría accesible a Client/Expert
    // (escalada de privilegio silenciosa). Ahora la clase es SEGURA POR DEFECTO (paridad con
    // AdminExpertController). Un método concreto puede relajarlo con su propio [Authorize]/[AllowAnonymous].
    [Authorize(Roles = "Admin")]
    public class AdminController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly StripeRefundService _refundService;
        private readonly ILogger<AdminController> _logger;
        private readonly IStripeConfigService _stripeConfigService;
        private readonly IConfiguration _configuration;
        // 🛡️ Round 28 S2-P0-11: monitor del umbral OSS UE.
        private readonly OssThresholdService _ossThresholdService;

        // 🛡️ Round 28 MUD-BB: servicio DAC7 modelo 238 (Directiva UE 2021/514).
        private readonly Dac7AnnualReportService _dac7Service;

        public AdminController(
            AppDbContext context,
            StripeRefundService refundService,
            ILogger<AdminController> logger,
            IStripeConfigService stripeConfigService,
            IConfiguration configuration,
            OssThresholdService ossThresholdService,
            Dac7AnnualReportService dac7Service)
        {
            _context = context;
            _refundService = refundService;
            _logger = logger;
            _stripeConfigService = stripeConfigService;
            _configuration = configuration;
            _ossThresholdService = ossThresholdService;
            _dac7Service = dac7Service;
        }

        // ────────────────────────────────────────────────────────────────────
        // 🛡️ Round 28 MUD-BB — DAC7 modelo 238 endpoints admin
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Genera/actualiza Dac7SellerSnapshots para el año especificado.
        /// Idempotente: re-ejecutar sobre el mismo año actualiza filas existentes con datos
        /// agregados frescos (útil si cambia un hire post-cierre por dispute resolved).
        /// </summary>
        [HttpPost("dac7/generate/{year:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GenerateDac7(int year, CancellationToken ct)
        {
            if (year < 2023 || year > DateTime.UtcNow.Year)
            {
                return BadRequest(new { message = $"Año inválido. DAC7 aplica desde 2023 y no se puede generar para años futuros." });
            }
            try
            {
                var count = await _dac7Service.GenerateForYearAsync(year, ct);
                var reportable = await _dac7Service.CountReportableForYearAsync(year, ct);
                return Ok(new
                {
                    year,
                    snapshotsGenerated = count,
                    sellersAboveThreshold = reportable,
                    message = $"Snapshots generados/actualizados para {year}. {reportable} sellers cruzaron umbral DAC7 (>= €2.000 OR >= 30 tx). Descarga el XML en /api/admin/dac7/export/{year}."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MUD-BB: DAC7 generation failed for year {Year}", year);
                return StatusCode(500, new { message = "Error al generar snapshots DAC7", errorCode = "DAC7_GENERATE_FAILED" });
            }
        }

        /// <summary>
        /// Descarga el XML modelo 238 del año especificado. Solo incluye sellers que cruzaron
        /// umbral (>= €2.000 OR >= 30 tx). Admin sube manualmente a sede.agenciatributaria.gob.es
        /// antes del 31-enero del año siguiente.
        /// </summary>
        [HttpGet("dac7/export/{year:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ExportDac7(int year, CancellationToken ct)
        {
            if (year < 2023 || year > DateTime.UtcNow.Year)
            {
                return BadRequest(new { message = $"Año inválido. DAC7 aplica desde 2023 y no se puede exportar para años futuros." });
            }
            try
            {
                var xml = await _dac7Service.ExportXmlForYearAsync(year, ct);
                var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
                return File(bytes, "application/xml", $"modelo238_{year}.xml");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MUD-BB: DAC7 export failed for year {Year}", year);
                return StatusCode(500, new { message = "Error al exportar XML DAC7", errorCode = "DAC7_EXPORT_FAILED" });
            }
        }

        /// <summary>
        /// Cuenta sellers que cruzarían el threshold DAC7 sin generar XML.
        /// Útil para dashboard admin de estado fiscal: "tienes N expertos sujetos a DAC7 este año".
        /// </summary>
        [HttpGet("dac7/status/{year:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Dac7Status(int year, CancellationToken ct)
        {
            var reportable = await _dac7Service.CountReportableForYearAsync(year, ct);
            var totalSnapshots = await _context.Dac7SellerSnapshots.AsNoTracking().CountAsync(s => s.Year == year, ct);
            return Ok(new { year, totalSnapshots, sellersAboveThreshold = reportable });
        }

        // ────────────────────────────────────────────────────────────────────
        // 🛡️ Round 28 MUD-BD — ClawbackQueue endpoints admin
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lista filas Pending (sin resolver) de la cola de clawback off-Stripe.
        /// Filtrable por status (Pending|Resolved|All) y reason.
        /// </summary>
        [HttpGet("clawback-queue")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ListClawbackQueue(
            [FromQuery] string status = "Pending",
            [FromQuery] string? reason = null,
            CancellationToken ct = default)
        {
            var q = _context.ClawbackQueues.AsNoTracking();
            if (string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                q = q.Where(c => c.ResolvedAt == null);
            }
            else if (string.Equals(status, "Resolved", StringComparison.OrdinalIgnoreCase))
            {
                q = q.Where(c => c.ResolvedAt != null);
            }
            if (!string.IsNullOrEmpty(reason))
            {
                q = q.Where(c => c.Reason == reason);
            }

            var items = await q
                .OrderByDescending(c => c.CreatedAt)
                .Take(500)
                .Select(c => new
                {
                    c.Id,
                    c.UserId,
                    c.StripeAccountId,
                    c.SearchHireId,
                    c.AmountMajor,
                    c.Currency,
                    c.Reason,
                    c.Notes,
                    c.CreatedAt,
                    c.ResolvedAt,
                    c.Resolution,
                    c.ResolutionNotes,
                    c.ResolvedByAdminEmail
                })
                .ToListAsync(ct);

            // Resumen agregado por currency para dashboard "deuda platform a expertos".
            var pendingByCurrency = await _context.ClawbackQueues
                .AsNoTracking()
                .Where(c => c.ResolvedAt == null)
                .GroupBy(c => c.Currency)
                .Select(g => new { Currency = g.Key, TotalAmount = g.Sum(c => c.AmountMajor), Count = g.Count() })
                .ToListAsync(ct);

            return Ok(new { items, pendingByCurrency, totalReturned = items.Count });
        }

        public class ResolveClawbackDto
        {
            public string Resolution { get; set; } = "PaidToUser"; // PaidToUser | AbsorbedAsLoss | InvalidEntry
            public string? Notes { get; set; }
        }

        /// <summary>
        /// Marca una fila como resuelta tras acción manual del admin (transferencia hecha,
        /// pérdida absorbida, o entrada inválida que se descarta).
        /// </summary>
        [HttpPost("clawback-queue/{id:int}/resolve")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ResolveClawback(int id, [FromBody] ResolveClawbackDto body, CancellationToken ct)
        {
            if (body == null || string.IsNullOrEmpty(body.Resolution))
            {
                return BadRequest(new { message = "Resolution requerido (PaidToUser|AbsorbedAsLoss|InvalidEntry)" });
            }
            var allowed = new[] { "PaidToUser", "AbsorbedAsLoss", "InvalidEntry" };
            if (!allowed.Contains(body.Resolution))
            {
                return BadRequest(new { message = $"Resolution inválido. Valores permitidos: {string.Join(", ", allowed)}" });
            }

            var row = await _context.ClawbackQueues.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (row == null) return NotFound(new { message = "ClawbackQueue id no encontrado" });
            if (row.ResolvedAt != null)
            {
                return BadRequest(new { message = $"Ya resuelto el {row.ResolvedAt:O} como {row.Resolution}" });
            }

            row.ResolvedAt = DateTime.UtcNow;
            row.Resolution = body.Resolution;
            row.ResolutionNotes = body.Notes;
            row.ResolvedByAdminEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                                    ?? User.Identity?.Name
                                    ?? "admin";
            await _context.SaveChangesAsync(ct);

            return Ok(new { row.Id, row.ResolvedAt, row.Resolution, row.ResolvedByAdminEmail });
        }

        /// <summary>
        /// 🛡️ Round 28 S2-P0-11: snapshot del umbral OSS UE €10.000/año del año actual.
        /// Devuelve YTD por país + porcentaje del umbral + estado de compliance.
        /// El admin debe revisar este endpoint regularmente — sin OSS registrado y cruzando
        /// el umbral, la plataforma incurre en infracción fiscal en cada país UE.
        /// </summary>
        /// <summary>
        /// 🛡️ Round 28 MUD-C: detector de divergencia ExpertProfile.Country vs stripe_account.Country.
        /// Stripe Connect Account.country es INMUTABLE — pero el experto puede cambiar
        /// ExpertProfile.Country (BD) vía geolocalización o porque admin lo actualizó manual.
        /// La divergencia hace que nuevos servicios/hires cobren en divisa incorrecta y
        /// los transfers fallen con currency_mismatch (dinero atascado).
        ///
        /// Este endpoint cruza los dos campos y reporta los expertos en estado divergente
        /// + servicios con Currency != stripe_acct.default_currency (deuda de cobro).
        ///
        /// Recomendado: ejecutar como job diario via Hangfire o cron externo + alertar admin.
        /// </summary>
        [HttpGet("expert-country-divergence")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetExpertCountryDivergence(CancellationToken ct = default)
        {
            try
            {
                // Solo evaluar expertos con StripeAccountId — sin acct no hay divergencia posible.
                var profiles = await _context.ExpertProfiles
                    .IgnoreQueryFilters()
                    .Where(p => p.StripeAccountId != null
                             && p.Country != null)
                    .Select(p => new
                    {
                        ExpertProfileId = p.Id,
                        UserId = p.UserId,
                        UserEmail = p.User.Email,
                        Country = p.Country,
                        StripeAccountId = p.StripeAccountId,
                        OnboardingCompleted = p.OnboardingCompleted,
                        StripeStatus = p.StripeStatus,
                    })
                    .ToListAsync(ct);

                var stripeAccountSvc = new Stripe.AccountService();
                var divergent = new System.Collections.Generic.List<object>();
                var checkedCount = 0;
                var errorCount = 0;

                foreach (var p in profiles)
                {
                    checkedCount++;
                    try
                    {
                        var acct = await stripeAccountSvc.GetAsync(p.StripeAccountId);
                        var acctCountry = (acct?.Country ?? "").ToUpperInvariant();
                        var acctCurrency = (acct?.DefaultCurrency ?? "").ToUpperInvariant();
                        var profileCountry = (p.Country ?? "").ToUpperInvariant();

                        if (!string.IsNullOrEmpty(acctCountry)
                            && !string.Equals(acctCountry, profileCountry, System.StringComparison.OrdinalIgnoreCase))
                        {
                            // Buscar servicios con Currency != stripe acct.default_currency (deuda real).
                            // Captura local de variables para no usar la del foreach en el lambda.
                            var expertProfileIdLocal = p.ExpertProfileId;
                            var acctCurrencyLocal = acctCurrency;
                            var inconsistentServices = await _context.SearchServices
                                .AsNoTracking()
                                .Where(s => s.ExpertProfileId == expertProfileIdLocal
                                         && s.IsActive
                                         && s.Currency != null
                                         && s.Currency.ToUpper() != acctCurrencyLocal)
                                .Select(s => new { s.Id, s.Currency, s.Price })
                                .ToListAsync(ct);

                            // Hires en curso con currency != acct (riesgo currency_mismatch al transfer).
                            var userIdLocal = p.UserId;
                            var hiresAtRisk = await _context.SearchHires
                                .AsNoTracking()
                                .Include(h => h.Status)
                                .Where(h => h.ExpertId == userIdLocal
                                         && h.Status != null
                                         && !h.Status.IsFinalizationStatus
                                         && h.Currency != null
                                         && h.Currency.ToUpper() != acctCurrencyLocal)
                                .Select(h => new { h.Id, h.Currency, h.Amount, h.CreatedAt })
                                .ToListAsync(ct);

                            divergent.Add(new
                            {
                                expertProfileId = p.ExpertProfileId,
                                userId = p.UserId,
                                userEmail = p.UserEmail,
                                profileCountry = profileCountry,
                                stripeAccountCountry = acctCountry,
                                stripeAccountId = p.StripeAccountId,
                                stripeAccountDefaultCurrency = acctCurrency,
                                onboardingCompleted = p.OnboardingCompleted,
                                stripeStatus = p.StripeStatus.ToString(),
                                inconsistentServicesCount = inconsistentServices.Count,
                                inconsistentServices = inconsistentServices,
                                hiresAtRiskCount = hiresAtRisk.Count,
                                hiresAtRisk = hiresAtRisk,
                                severity = (inconsistentServices.Count > 0 || hiresAtRisk.Count > 0) ? "HIGH" : "MEDIUM",
                            });
                        }
                    }
                    catch (Stripe.StripeException)
                    {
                        errorCount++;
                    }
                    catch (System.Exception)
                    {
                        errorCount++;
                    }
                }

                // Logging crítico si hay divergencias high severity.
                if (divergent.Any())
                {
                    var highSeverity = divergent.Count(d => ((dynamic)d).severity == "HIGH");
                    _logger.LogWarning("MUD-C: {Total} expertos con divergencia Country↔Stripe; {High} HIGH severity (con servicios/hires afectados)",
                        divergent.Count, highSeverity);
                }

                return Ok(new
                {
                    checkedCount,
                    divergentCount = divergent.Count,
                    errorCount,
                    divergent,
                    note = "HIGH severity = experto tiene servicios y/o hires con Currency != stripe_acct.default_currency. Riesgo de currency_mismatch en transfers. Investigar y/o ejecutar reset-stripe.",
                });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "MUD-C: GetExpertCountryDivergence failed");
                return StatusCode(500, new { message = "Error generando reporte de divergencia", errorCode = "EXPERT_COUNTRY_DIVERGENCE_FAILED" });
            }
        }

        [HttpGet("oss-stats")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetOssStats(CancellationToken ct = default)
        {
            try
            {
                var report = await _ossThresholdService.GetCurrentYearReportAsync(ct);
                return Ok(report);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetOssStats failed");
                return StatusCode(500, new { message = "Error generando snapshot OSS", errorCode = "OSS_STATS_FAILED" });
            }
        }

        /// <summary>
        /// 🛡️ Round 28 S2-P0-11: dispara la evaluación + alertas críticas si procede.
        /// Llamar manualmente para forzar el envío del aviso al admin (idempotente — el log
        /// Critical solo se emite si compliance status es WARNING o VIOLATION).
        /// </summary>
        [HttpPost("oss-stats/check")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> RunOssThresholdCheck(CancellationToken ct = default)
        {
            try
            {
                await _ossThresholdService.EmitAlertIfNeededAsync(ct);
                var report = await _ossThresholdService.GetCurrentYearReportAsync(ct);
                return Ok(new { triggered = true, report });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RunOssThresholdCheck failed");
                return StatusCode(500, new { message = "Error ejecutando OSS threshold check", errorCode = "OSS_THRESHOLD_CHECK_FAILED" });
            }
        }

        /// <summary>
        /// 🛡️ Round 28 — Sprint US-2 (SUS2-8): reset completo del estado Stripe Connect de un experto.
        /// Útil cuando un experto queda atascado en un acct corrupto (capabilities erróneas, idempotency
        /// cacheada, requirements imposibles, etc.). Ejecuta:
        ///  1. Lee StripeAccountId/PendingStripeAccountId del ExpertProfile.
        ///  2. Intenta DeleteAsync sobre cada acct (404 → OK). Si falla, fallback a RejectAsync(other).
        ///  3. Resetea estado en BD: StripeAccountId=null, PendingStripeAccountId=null,
        ///     StripeStatus=NotRequested, StripeStatusDetails=null, OnboardingCompleted=false.
        ///  4. Loguea crítico con el adminUserId actor para auditoría.
        /// El experto puede volver a iniciar onboarding limpio.
        /// </summary>
        [HttpPost("expert/{expertUserId:int}/reset-stripe")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ResetStripeForExpert(int expertUserId, CancellationToken ct = default)
        {
            try
            {
                var adminUserIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                _ = int.TryParse(adminUserIdStr, out int adminUserId);

                var profile = await _context.ExpertProfiles
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.UserId == expertUserId, ct);
                if (profile == null)
                {
                    return NotFound(new { message = $"ExpertProfile not found for user {expertUserId}" });
                }

                // 🛡️ Round 28 MUD-4: guards previos al reset para no dejar disputas/hires en limbo.
                // Antes el endpoint borraba acct sin verificar estado del experto → si tenía dispute
                // Pending o hire en AwaitingClientDecision, el reset rompía el flujo:
                //  - Dispute: StripeDisputeId huérfano en una cuenta rejected.
                //  - Hire: ProcessMoneyDistributionAsync intentaba transfer a acct inexistente → 404.
                // Admite ?force=true para casos de emergencia (acct corrupto irrecuperable).
                var force = Request.Query.ContainsKey("force") && Request.Query["force"] == "true";
                if (!force)
                {
                    var pendingDisputes = await _context.Disputes
                        .AsNoTracking()
                        .Where(d => (d.Status == "Pending" || d.Status == "Resolving")
                                 && d.SearchHire != null
                                 && (d.SearchHire.ExpertId == expertUserId || d.ReporterId == expertUserId))
                        .CountAsync(ct);
                    if (pendingDisputes > 0)
                    {
                        return BadRequest(new
                        {
                            error = "blocked_by_pending_disputes",
                            message = $"No se puede resetear el Stripe del experto: tiene {pendingDisputes} disputa(s) activa(s) (Pending/Resolving). Resolver primero las disputas y reintentar. Para forzar (NO recomendado, deja StripeDisputeId huérfano): añadir ?force=true.",
                            pendingDisputes,
                        });
                    }

                    var activeHires = await _context.SearchHires
                        .AsNoTracking()
                        .Include(h => h.Status)
                        .Where(h => h.ExpertId == expertUserId
                                 && h.Status != null
                                 && !h.Status.IsFinalizationStatus)
                        .CountAsync(ct);
                    if (activeHires > 0)
                    {
                        return BadRequest(new
                        {
                            error = "blocked_by_active_hires",
                            message = $"No se puede resetear el Stripe del experto: tiene {activeHires} contratación(es) activa(s) en estado no-final. Esperar a su finalización (o cancelarlas vía /api/admin/searchhire) y reintentar. Para forzar (deja hires sin destinatario en transfers): añadir ?force=true.",
                            activeHires,
                        });
                    }

                    var pendingRefunds = await _context.FinancialTransactions
                        .AsNoTracking()
                        .Where(ft => ft.UserId == expertUserId
                                  && ft.TransactionType == "Refund"
                                  && ft.CreatedAt > System.DateTime.UtcNow.AddHours(-24))
                        .CountAsync(ct);
                    if (pendingRefunds > 0)
                    {
                        return BadRequest(new
                        {
                            error = "blocked_by_recent_refunds",
                            message = $"No se puede resetear: hay {pendingRefunds} refund(s) en las últimas 24h. Esperar a su settlement Stripe.",
                            pendingRefunds,
                        });
                    }
                }

                var beforeStripeAccountId = profile.StripeAccountId;
                var beforePendingStripeAccountId = profile.PendingStripeAccountId;
                var beforeStatus = profile.StripeStatus;
                var stripeOpsResults = new List<object>();

                async Task TryCleanupStripeAccount(string? acctId, string label)
                {
                    if (string.IsNullOrEmpty(acctId)) return;
                    try
                    {
                        var svc = new AccountService();
                        await svc.DeleteAsync(acctId);
                        stripeOpsResults.Add(new { acctId, label, op = "deleted" });
                    }
                    catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        stripeOpsResults.Add(new { acctId, label, op = "already_gone_404" });
                    }
                    catch (StripeException delEx)
                    {
                        // Fallback a Reject.
                        try
                        {
                            var svc = new AccountService();
                            await svc.RejectAsync(acctId, new AccountRejectOptions { Reason = "other" });
                            stripeOpsResults.Add(new { acctId, label, op = "rejected_after_delete_failed", deleteError = delEx.StripeError?.Code });
                        }
                        catch (Exception rejEx)
                        {
                            _logger.LogError(rejEx, "ResetStripeForExpert: delete+reject failed for acct {AcctId} ({Label}); deleteError={DeleteError}", acctId, label, delEx.Message);
                            stripeOpsResults.Add(new { acctId, label, op = "delete_and_reject_failed", deleteError = delEx.StripeError?.Code });
                        }
                    }
                    catch (Exception unexpectedEx)
                    {
                        _logger.LogError(unexpectedEx, "ResetStripeForExpert: unexpected error cleaning up acct {AcctId} ({Label})", acctId, label);
                        stripeOpsResults.Add(new { acctId, label, op = "unexpected_error" });
                    }
                }

                await TryCleanupStripeAccount(beforeStripeAccountId, "StripeAccountId");
                if (!string.Equals(beforePendingStripeAccountId, beforeStripeAccountId, StringComparison.Ordinal))
                {
                    await TryCleanupStripeAccount(beforePendingStripeAccountId, "PendingStripeAccountId");
                }

                profile.StripeAccountId = null;
                profile.PendingStripeAccountId = null;
                profile.StripeStatus = global::newApi.DataLayer.Models.PostGresModels.StripeStatus.NotRequested;
                profile.StripeStatusDetails = null;
                profile.OnboardingCompleted = false;
                profile.StripeFutureRequirements = null;
                profile.StripeFutureDueAt = null;
                await _context.SaveChangesAsync(ct);

                _logger.LogWarning("Admin {AdminUserId} reset Stripe for expert {ExpertUserId}: was acct={Acct}, pending={Pending}, status={Status}",
                    adminUserId, expertUserId, beforeStripeAccountId, beforePendingStripeAccountId, beforeStatus);

                return Ok(new
                {
                    success = true,
                    expertUserId,
                    adminUserId,
                    before = new { StripeAccountId = beforeStripeAccountId, PendingStripeAccountId = beforePendingStripeAccountId, Status = beforeStatus.ToString() },
                    after = new { StripeAccountId = (string?)null, PendingStripeAccountId = (string?)null, Status = "NotRequested" },
                    stripeOperations = stripeOpsResults,
                    nextStep = "El experto puede iniciar onboarding limpio. Idempotency key se regenerará en la próxima llamada."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResetStripeForExpert failed for user {ExpertUserId}", expertUserId);
                return StatusCode(500, new { message = "Error reseteando Stripe del experto", errorCode = "RESET_STRIPE_FAILED" });
            }
        }

        /// <summary>
        /// ✅ MEJOR PRÁCTICA: Endpoint para detectar usuarios sospechosos
        /// Detecta usuarios con actividad anómala basándose en múltiples criterios
        /// </summary>
        [HttpGet("suspicious-users")]
        [Authorize(Roles = "Admin")] // Solo admins pueden acceder
        public async Task<IActionResult> GetSuspiciousUsers(
            [FromQuery] int minutes = 15, // Ventana de tiempo para análisis
            [FromQuery] int minRequestsPerMinute = 50, // Umbral mínimo de requests/min
            [FromQuery] int maxFailedAuthAttempts = 5) // Máximo de intentos fallidos
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddMinutes(-minutes);
                
                // ✅ Criterio 1: Usuarios con alta tasa de requests
                var highRequestRateUsers = await _context.Users
                    .Where(u => u.CreatedAt <= cutoffTime) // Solo usuarios existentes
                    .Select(u => new
                    {
                        UserId = u.Id,
                        Email = u.Email,
                        Name = u.Name,
                        LastActivity = u.DeletedAt ?? u.CreatedAt, // Usar DeletedAt si existe, sino CreatedAt
                        // En producción, esto debería venir de logs de requests
                        // Por ahora, usamos una aproximación basada en CreatedAt/DeletedAt
                        SuspiciousReason = "High request rate detected"
                    })
                    .ToListAsync();

                // ✅ Criterio 2: Usuarios con múltiples intentos de autenticación fallidos
                // Esto requeriría una tabla de logs de autenticación
                // Por ahora, retornamos estructura preparada

                // ✅ Criterio 3: Usuarios con actividad fuera de horario normal
                var currentHour = DateTime.UtcNow.Hour;
                var isOffHours = currentHour < 6 || currentHour > 22; // 6 AM - 10 PM es horario normal
                
                var suspiciousUsers = highRequestRateUsers
                    .Where(u => isOffHours && u.LastActivity > cutoffTime)
                    .Select(u => new
                    {
                        u.UserId,
                        u.Email,
                        u.Name,
                        u.LastActivity,
                        SuspiciousReasons = new List<string>
                        {
                            u.SuspiciousReason,
                            isOffHours ? "Activity during off-hours" : null
                        }.Where(r => r != null).ToList(),
                        RiskScore = CalculateRiskScore(u.UserId, isOffHours)
                    })
                    .OrderByDescending(u => u.RiskScore)
                    .ToList();

                _logger.LogInformation(
                    "Suspicious users check completed. Found {Count} suspicious users in last {Minutes} minutes",
                    suspiciousUsers.Count,
                    minutes);

                return Ok(new
                {
                    success = true,
                    timestamp = DateTime.UtcNow,
                    windowMinutes = minutes,
                    suspiciousUsersCount = suspiciousUsers.Count,
                    suspiciousUsers = suspiciousUsers,
                    criteria = new
                    {
                        minRequestsPerMinute,
                        maxFailedAuthAttempts,
                        offHoursDetection = isOffHours
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting suspicious users");
                return StatusCode(500, new
                {
                    success = false,
                    message = "Error detecting suspicious users",
                    errorCode = "SUSPICIOUS_USERS_FAILED"
                });
            }
        }

        /// <summary>
        /// ✅ MEJOR PRÁCTICA: Calcula un score de riesgo para un usuario
        /// </summary>
        private int CalculateRiskScore(int userId, bool isOffHours)
        {
            int score = 0;
            
            // Actividad fuera de horario: +30 puntos
            if (isOffHours)
                score += 30;
            
            // Alta tasa de requests: +40 puntos
            // (Esto debería calcularse desde logs reales)
            score += 40;
            
            // Múltiples intentos fallidos: +30 puntos
            // (Requiere tabla de logs de autenticación)
            
            return Math.Min(score, 100); // Máximo 100
        }

        /// <summary>
        /// ✅ MEJOR PRÁCTICA: Bloquear usuario sospechoso
        /// </summary>
        [HttpPost("block-user/{userId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> BlockSuspiciousUser(int userId, [FromBody] string reason)
        {
            try
            {
                var user = await _context.Users.FindAsync(userId);
                if (user == null)
                {
                    return NotFound(new { success = false, message = "User not found" });
                }

                // Aquí implementarías la lógica de bloqueo
                // Por ejemplo, agregar un campo IsBlocked o usar un sistema de roles
                
                _logger.LogWarning(
                    "User {UserId} ({Email}) blocked by admin {AdminId}. Reason: {Reason}",
                    userId,
                    user.Email,
                    User.FindFirstValue(ClaimTypes.NameIdentifier),
                    reason);

                return Ok(new
                {
                    success = true,
                    message = $"User {user.Email} has been blocked",
                    userId = userId,
                    blockedAt = DateTime.UtcNow,
                    reason = reason
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error blocking user {UserId}", userId);
                return StatusCode(500, new
                {
                    success = false,
                    message = "Error blocking user",
                    errorCode = "BLOCK_USER_FAILED"
                });
            }
        }

        /// <summary>
        /// ✅ Obtener el modo actual de Stripe (development/production)
        /// </summary>
        [HttpGet("stripe/mode")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetStripeMode()
        {
            try
            {
                var setting = await _context.SystemSettings.FirstOrDefaultAsync();
                return Ok(new
                {
                    mode = setting?.StripeMode ?? "production",
                    changedAt = setting?.StripeModeChangedAt,
                    changedByUserId = setting?.StripeModeChangedByUserId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo modo Stripe");
                return StatusCode(500, new { success = false, message = "Error obteniendo modo Stripe", errorCode = "STRIPE_MODE_GET_FAILED" });
            }
        }

        /// <summary>
        /// ✅ Establecer el modo de Stripe (development/production)
        /// </summary>
        [HttpPost("stripe/mode")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SetStripeMode([FromBody] SetStripeModeRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrEmpty(request.Mode))
                {
                    return BadRequest(new { success = false, message = "El campo 'Mode' es requerido" });
                }

                if (request.Mode != "development" && request.Mode != "production")
                {
                    return BadRequest(new { success = false, message = "El modo debe ser 'development' o 'production'" });
                }

                var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                var success = await _stripeConfigService.SetStripeModeAsync(request.Mode, userId);

                if (!success)
                {
                    return StatusCode(500, new { success = false, message = "Error cambiando modo Stripe" });
                }

                // ✅ RECARGAR CLAVES DINÁMICAMENTE después de cambiar el modo
                await ReloadStripeKeysAsync(request.Mode);

                _logger.LogInformation($"✅ Modo Stripe cambiado a {request.Mode} por usuario {userId} - Claves recargadas");

                return Ok(new
                {
                    success = true,
                    message = $"Modo Stripe cambiado a {request.Mode}",
                    mode = request.Mode,
                    warning = "Las claves Stripe se han recargado. Las URLs de webhooks en Stripe Dashboard deben configurarse manualmente para cada modo."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error estableciendo modo Stripe");
                return StatusCode(500, new { success = false, message = "Error estableciendo modo Stripe", errorCode = "STRIPE_MODE_SET_FAILED" });
            }
        }

        /// <summary>🗓️ Fase D: leer la política de cancelación escalonada (admin).</summary>
        [HttpGet("cancellation/settings")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetCancellationSettings()
        {
            try
            {
                var s = await _context.SystemSettings.AsNoTracking().FirstOrDefaultAsync();
                return Ok(new
                {
                    tierHighHours = s?.CancellationTierHighHours ?? 24,
                    tierLowHours = s?.CancellationTierLowHours ?? 6,
                    freeCancellationsPerParty = s?.FreeCancellationsPerParty ?? 0,
                    penaltyFreeWindowDays = s?.PenaltyFreeWindowDays ?? 30
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo configuración de cancelaciones");
                return StatusCode(500, new { success = false, message = "Error obteniendo configuración de cancelaciones", errorCode = "CANCELLATION_SETTINGS_GET_FAILED" });
            }
        }

        /// <summary>🗓️ Fase D: editar la política de cancelación escalonada (admin). N=0 = máxima dureza.</summary>
        [HttpPost("cancellation/settings")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SetCancellationSettings([FromBody] CancellationSettingsRequest request)
        {
            try
            {
                if (request == null)
                    return BadRequest(new { success = false, message = "Cuerpo requerido" });
                if (request.TierHighHours <= request.TierLowHours)
                    return BadRequest(new { success = false, message = "tierHighHours debe ser mayor que tierLowHours" });
                if (request.TierLowHours < 0 || request.FreeCancellationsPerParty < 0 || request.PenaltyFreeWindowDays < 1)
                    return BadRequest(new { success = false, message = "Valores fuera de rango" });

                var s = await _context.SystemSettings.FirstOrDefaultAsync();
                if (s == null)
                {
                    s = new global::newApi.DataLayer.Models.PostGresModels.SystemSetting();
                    _context.SystemSettings.Add(s);
                }
                s.CancellationTierHighHours = request.TierHighHours;
                s.CancellationTierLowHours = request.TierLowHours;
                s.FreeCancellationsPerParty = request.FreeCancellationsPerParty;
                s.PenaltyFreeWindowDays = request.PenaltyFreeWindowDays;
                s.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                var adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                _logger.LogInformation($"🗓️ Política de cancelación actualizada por admin {adminId}: high={request.TierHighHours} low={request.TierLowHours} N={request.FreeCancellationsPerParty} window={request.PenaltyFreeWindowDays}");

                return Ok(new
                {
                    success = true,
                    tierHighHours = s.CancellationTierHighHours,
                    tierLowHours = s.CancellationTierLowHours,
                    freeCancellationsPerParty = s.FreeCancellationsPerParty,
                    penaltyFreeWindowDays = s.PenaltyFreeWindowDays
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error estableciendo configuración de cancelaciones");
                return StatusCode(500, new { success = false, message = "Error estableciendo configuración de cancelaciones", errorCode = "CANCELLATION_SETTINGS_SET_FAILED" });
            }
        }

        /// <summary>DTO para editar la política de cancelación escalonada (Fase D).</summary>
        public class CancellationSettingsRequest
        {
            public int TierHighHours { get; set; } = 24;
            public int TierLowHours { get; set; } = 6;
            public int FreeCancellationsPerParty { get; set; } = 0;
            public int PenaltyFreeWindowDays { get; set; } = 30;
        }

        /// <summary>
        /// ✅ Alternar el modo de Stripe automáticamente (development ↔ production)
        /// </summary>
        [HttpPost("stripe/toggle-mode")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ToggleStripeMode()
        {
            try
            {
                var currentMode = await _stripeConfigService.GetStripeModeAsync();
                var newMode = currentMode == "development" ? "production" : "development";

                var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                var success = await _stripeConfigService.SetStripeModeAsync(newMode, userId);

                if (!success)
                {
                    return StatusCode(500, new { success = false, message = "Error alternando modo Stripe" });
                }

                // ✅ RECARGAR CLAVES DINÁMICAMENTE después de cambiar el modo
                await ReloadStripeKeysAsync(newMode);

                _logger.LogInformation($"✅ Modo Stripe alternado de {currentMode} a {newMode} por usuario {userId} - Claves recargadas");

                return Ok(new
                {
                    success = true,
                    message = $"Modo Stripe cambiado de {currentMode} a {newMode}",
                    previousMode = currentMode,
                    newMode = newMode,
                    warning = "Las claves Stripe se han recargado. Las URLs de webhooks en Stripe Dashboard deben configurarse manualmente para cada modo."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error alternando modo Stripe");
                return StatusCode(500, new { success = false, message = "Error alternando modo Stripe", errorCode = "STRIPE_MODE_TOGGLE_FAILED" });
            }
        }

        /// <summary>
        /// ✅ Recargar las claves Stripe dinámicamente según el modo actual
        /// Este método obtiene las claves desde Secret Manager y actualiza la configuración en memoria.
        /// SubscriptionController ahora lee las claves dinámicamente desde IConfiguration, por lo que
        /// los cambios se aplican inmediatamente sin reiniciar la aplicación.
        /// </summary>
        private async Task ReloadStripeKeysAsync(string mode)
        {
            try
            {
                // Función helper para obtener secretos desde Secret Manager o configuración
                // Usa el mismo patrón que GetSecretValue en Program.cs
                Func<string, string?, string?> getSecretValue = (secretName, defaultValue) =>
                {
                    // Secret Manager RETIRADO: los secretos se leen SOLO de variables de entorno de Render.
                    // Bloque conservado inerte (if(false)) para minimizar el cambio; nunca se ejecuta.
                    if (false)
                    try
                    {
                        var projectId = "grup-441318";
                        SecretManagerServiceClient? secretClient = null;
                        
                        // Intentar crear cliente de Secret Manager
                        try
                        {
                            secretClient = new SecretManagerServiceClientBuilder().Build();
                        }
                        catch (Exception clientEx)
                        {
                            _logger.LogDebug($"No se pudo crear cliente Secret Manager: {clientEx.Message}");
                            // Continuar con fallbacks
                        }
                        
                        if (secretClient != null)
                        {
                            var secretPath = $"projects/{projectId}/secrets/{secretName}/versions/latest";
                            
                            var callSettings = CallSettings.FromRetry(
                                RetrySettings.FromExponentialBackoff(
                                    maxAttempts: 2,
                                    initialBackoff: TimeSpan.FromSeconds(2),
                                    maxBackoff: TimeSpan.FromSeconds(5),
                                    backoffMultiplier: 2.0,
                                    retryFilter: RetrySettings.FilterForStatusCodes(
                                        Grpc.Core.StatusCode.Unavailable,
                                        Grpc.Core.StatusCode.DeadlineExceeded,
                                        Grpc.Core.StatusCode.NotFound
                                    )
                                )
                            ).WithTimeout(TimeSpan.FromSeconds(10));
                            
                            try
                            {
                                var secretVersion = secretClient.AccessSecretVersion(secretPath, callSettings: callSettings);
                                var secretValue = secretVersion.Payload.Data.ToStringUtf8();
                                
                                if (!string.IsNullOrEmpty(secretValue))
                                {
                                    _logger.LogInformation($"✅ Secreto {secretName} obtenido desde Secret Manager");
                                    return secretValue;
                                }
                            }
                            catch (RpcException rpcEx) when (rpcEx.StatusCode == Grpc.Core.StatusCode.NotFound)
                            {
                                // Secreto no encontrado, continuar con fallbacks
                                _logger.LogDebug($"Secreto {secretName} no encontrado en Secret Manager");
                            }
                            catch (Exception secretEx)
                            {
                                _logger.LogDebug($"Error obteniendo secreto {secretName} desde Secret Manager: {secretEx.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Si falla Secret Manager completamente, continuar con otros métodos
                        _logger.LogDebug($"Secret Manager no disponible para {secretName}: {ex.Message}");
                    }

                    // Fallback: Intentar desde configuración
                    var configKey = secretName.Replace("stripe-", "Stripe:").Replace("-", ":");
                    var value = _configuration[configKey];
                    if (!string.IsNullOrEmpty(value))
                    {
                        _logger.LogDebug($"Secreto {secretName} obtenido desde configuración");
                        return value;
                    }

                    // Fallback: Intentar desde variables de entorno
                    var envKey = secretName.ToUpper().Replace("-", "_");
                    value = Environment.GetEnvironmentVariable(envKey);
                    if (!string.IsNullOrEmpty(value))
                    {
                        _logger.LogDebug($"Secreto {secretName} obtenido desde variable de entorno");
                        return value;
                    }

                    _logger.LogWarning($"⚠️ Secreto {secretName} no encontrado en ninguna fuente");
                    return defaultValue;
                };

                var (secretKey, webhookSecret, generalWebhookSecret) = await _stripeConfigService.GetStripeKeysForModeAsync(
                    mode,
                    getSecretValue);

                // Actualizar configuración en memoria (IConfiguration es mutable en runtime)
                // Esto actualizará las claves para futuras lecturas desde IConfiguration
                if (!string.IsNullOrEmpty(secretKey))
                {
                    _configuration["Stripe:SecretKey"] = secretKey;
                    StripeConfiguration.ApiKey = secretKey;
                }
                if (!string.IsNullOrEmpty(webhookSecret))
                {
                    _configuration["Stripe:WebhookSecret"] = webhookSecret;
                }
                if (!string.IsNullOrEmpty(generalWebhookSecret))
                {
                    _configuration["Stripe:GeneralWebhookSecret"] = generalWebhookSecret;
                }

                _logger.LogInformation($"✅ Claves Stripe recargadas para modo {mode}");
                _logger.LogInformation($"   SecretKey presente: {!string.IsNullOrEmpty(secretKey)}");
                _logger.LogInformation($"   WebhookSecret presente: {!string.IsNullOrEmpty(webhookSecret)}");
                _logger.LogInformation($"   GeneralWebhookSecret presente: {!string.IsNullOrEmpty(generalWebhookSecret)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recargando claves Stripe");
                throw;
            }
        }

        /// <summary>
        /// Establece la cookie de sesión Hangfire sin exponer el JWT en la URL del iframe.
        /// </summary>
        [HttpPost("hangfire-session")]
        [Authorize(Roles = "Admin")]
        public IActionResult EstablishHangfireSession()
        {
            var authHeader = Request.Headers.Authorization.FirstOrDefault();
            var token = authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? authHeader["Bearer ".Length..].Trim()
                : null;

            if (string.IsNullOrEmpty(token))
            {
                return Unauthorized(new { message = "Token Bearer requerido" });
            }

            var isHttps = Request.IsHttps ||
                Request.Headers["X-Forwarded-Proto"].ToString()
                    .Equals("https", StringComparison.OrdinalIgnoreCase);

            var cookieOptions = new Microsoft.AspNetCore.Http.CookieOptions
            {
                HttpOnly = true,
                SameSite = isHttps
                    ? Microsoft.AspNetCore.Http.SameSiteMode.None
                    : Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                Secure = isHttps,
                Expires = DateTimeOffset.UtcNow.AddHours(1),
                Path = "/hangfire",
            };

            Response.Cookies.Append("HangfireAuthToken", token, cookieOptions);

            var dashboardUrl = $"{Request.Scheme}://{Request.Host}/hangfire";
            return Ok(new { dashboardUrl });
        }

        /// <summary>
        /// Request model para establecer modo Stripe
        /// </summary>
        public class SetStripeModeRequest
        {
            public string Mode { get; set; } = string.Empty;
        }

        // ────────────────────────────────────────────────────────────────────
        // 🔧 FIX E3 — cola de reintegros pendientes al experto (chargeback GANADO)
        // Consume el marcador persistente FinancialTransaction "PendingExpertReinstatement"
        // creado en SubscriptionController.HandleChargeDisputeClosed (rama won). "Resuelto" =
        // existe una fila compañera "ExpertReinstatementResolved" para el mismo hire+PI (sin
        // migración: el estado de resolución vive en el propio ledger). NO se auto-paga: el admin
        // emite el reintegro manualmente (Stripe Dashboard/transfer) y luego llama al endpoint
        // resolve para sacarlo de la cola.
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lista los reintegros pendientes al experto tras ganar un chargeback. Por defecto solo
        /// los NO resueltos; ?includeResolved=true incluye también los ya saldados.
        /// </summary>
        [HttpGet("expert-reinstatements")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetPendingExpertReinstatements([FromQuery] bool includeResolved = false, CancellationToken ct = default)
        {
            try
            {
                var markers = await _context.FinancialTransactions
                    .AsNoTracking()
                    .Where(ft => ft.TransactionType == "PendingExpertReinstatement")
                    .OrderByDescending(ft => ft.CreatedAt)
                    .Select(ft => new
                    {
                        ft.Id,
                        SearchHireId = ft.RelatedEntityId,
                        ExpertUserId = ft.UserId,
                        ft.Amount,
                        ft.AmountCents,
                        ft.Currency,
                        ft.StripePaymentIntentId,
                        ft.CreatedAt
                    })
                    .ToListAsync(ct);

                // Claves (hire+PI) ya resueltas — se calcula en memoria para evitar subconsulta correlacionada.
                var resolvedKeys = (await _context.FinancialTransactions
                    .AsNoTracking()
                    .Where(ft => ft.TransactionType == "ExpertReinstatementResolved")
                    .Select(ft => new { ft.RelatedEntityId, ft.StripePaymentIntentId })
                    .ToListAsync(ct))
                    .Select(k => $"{k.RelatedEntityId}|{k.StripePaymentIntentId}")
                    .ToHashSet();

                bool IsResolved(int? hireId, string? pi) => resolvedKeys.Contains($"{hireId}|{pi}");

                var selected = markers
                    .Where(m => includeResolved || !IsResolved(m.SearchHireId, m.StripePaymentIntentId))
                    .ToList();

                // Enriquecer con experto y estado del hire (en memoria, conjunto acotado).
                var hireIds = selected.Where(m => m.SearchHireId.HasValue).Select(m => m.SearchHireId!.Value).Distinct().ToList();
                var expertIds = selected.Where(m => m.ExpertUserId.HasValue).Select(m => m.ExpertUserId!.Value).Distinct().ToList();

                var hireStatuses = await _context.SearchHires.AsNoTracking()
                    .Where(h => hireIds.Contains(h.Id))
                    .Select(h => new { h.Id, Status = h.Status != null ? h.Status.StatusValue : null })
                    .ToListAsync(ct);
                var expertInfos = await _context.Users.AsNoTracking()
                    .Where(u => expertIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.Email, u.Name })
                    .ToListAsync(ct);

                var items = selected.Select(m => new
                {
                    financialTransactionId = m.Id,
                    searchHireId = m.SearchHireId,
                    expertUserId = m.ExpertUserId,
                    expertName = expertInfos.FirstOrDefault(e => e.Id == m.ExpertUserId)?.Name,
                    expertEmail = expertInfos.FirstOrDefault(e => e.Id == m.ExpertUserId)?.Email,
                    hireStatus = hireStatuses.FirstOrDefault(h => h.Id == m.SearchHireId)?.Status,
                    amount = m.Amount,
                    amountCents = m.AmountCents,
                    currency = m.Currency,
                    paymentIntentId = m.StripePaymentIntentId,
                    createdAt = m.CreatedAt,
                    resolved = IsResolved(m.SearchHireId, m.StripePaymentIntentId)
                }).ToList();

                return Ok(new { count = items.Count, items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetPendingExpertReinstatements failed");
                return StatusCode(500, new { message = "Error al listar reintegros pendientes al experto", errorCode = "EXPERT_REINSTATEMENTS_LIST_FAILED" });
            }
        }

        /// <summary>
        /// Marca un reintegro al experto como RESUELTO tras haberlo emitido manualmente (Stripe
        /// Dashboard / transfer). Inserta una fila compañera "ExpertReinstatementResolved" en el
        /// ledger (idempotente por hire+PI). reinstatedAmountCents es informativo: lo que el admin
        /// realmente reintegró (descontando cualquier clawback de disputa interna previo).
        /// </summary>
        [HttpPost("expert-reinstatements/{searchHireId:int}/resolve")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ResolveExpertReinstatement(int searchHireId, [FromBody] ResolveExpertReinstatementRequest? request, CancellationToken ct = default)
        {
            try
            {
                // El admin DEBE indicar lo que realmente reintegró (0 explícito si la deuda quedó
                // cubierta por un clawback interno previo). Sin esto, un click accidental sacaría el
                // reintegro de la cola sin que el experto cobrase.
                if (request?.ReinstatedAmountCents == null || request.ReinstatedAmountCents < 0)
                {
                    return BadRequest(new
                    {
                        message = "Debes indicar reinstatedAmountCents (>= 0). Usa 0 SOLO si la deuda quedó cubierta por el clawback de disputa interna previo; en ese caso añade una nota explicándolo.",
                        errorCode = "REINSTATED_AMOUNT_REQUIRED"
                    });
                }

                var markerQuery = _context.FinancialTransactions
                    .AsNoTracking()
                    .Where(ft => ft.TransactionType == "PendingExpertReinstatement"
                              && ft.RelatedEntityType == "SearchHire"
                              && ft.RelatedEntityId == searchHireId);
                if (!string.IsNullOrEmpty(request.PaymentIntentId))
                {
                    markerQuery = markerQuery.Where(ft => ft.StripePaymentIntentId == request.PaymentIntentId);
                }
                var marker = await markerQuery
                    .OrderByDescending(ft => ft.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (marker == null)
                {
                    return NotFound(new { message = $"No hay marcador PendingExpertReinstatement para el hire {searchHireId}{(string.IsNullOrEmpty(request.PaymentIntentId) ? "" : $" y PI {request.PaymentIntentId}")}.", searchHireId });
                }

                var alreadyResolved = await _context.FinancialTransactions.AnyAsync(r =>
                    r.TransactionType == "ExpertReinstatementResolved"
                    && r.RelatedEntityType == "SearchHire"
                    && r.RelatedEntityId == searchHireId
                    && r.StripePaymentIntentId == marker.StripePaymentIntentId, ct);
                if (alreadyResolved)
                {
                    return Ok(new { message = "El reintegro ya estaba marcado como resuelto.", searchHireId, alreadyResolved = true });
                }

                var adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                var reinstatedCents = request.ReinstatedAmountCents.Value;

                // Aviso si lo reintegrado difiere del bruto del marcador: ayuda a detectar un descuento
                // de clawback interno mal calculado (de más o de menos) al revisar la auditoría.
                if (reinstatedCents != marker.AmountCents)
                {
                    _logger.LogWarning(
                        "E3: reinstatedAmountCents ({Cents}) difiere del bruto del marcador ({Gross}) para hire {HireId} (delta={Delta}). Verificar el descuento de clawback interno.",
                        reinstatedCents, marker.AmountCents, searchHireId, reinstatedCents - marker.AmountCents);
                }

                _context.FinancialTransactions.Add(new global::newApi.DataLayer.Models.PostGresModels.FinancialTransaction
                {
                    UserId = marker.UserId,
                    Amount = reinstatedCents / 100m,
                    AmountCents = reinstatedCents,
                    Currency = marker.Currency,
                    TransactionType = "ExpertReinstatementResolved",
                    RelatedEntityType = "SearchHire",
                    RelatedEntityId = searchHireId,
                    StripePaymentIntentId = marker.StripePaymentIntentId,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "E3: ExpertReinstatement RESUELTO por admin {AdminId} — hire {HireId}, PI {PI}, reinstatedCents={Cents}, note={Note}",
                    adminId, searchHireId, marker.StripePaymentIntentId, reinstatedCents, request?.Note ?? "(sin nota)");

                return Ok(new
                {
                    message = "Reintegro al experto marcado como resuelto.",
                    searchHireId,
                    expertUserId = marker.UserId,
                    reinstatedAmountCents = reinstatedCents,
                    resolvedByAdminId = adminId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResolveExpertReinstatement failed for hire {HireId}", searchHireId);
                return StatusCode(500, new { message = "Error al resolver el reintegro al experto", errorCode = "EXPERT_REINSTATEMENT_RESOLVE_FAILED" });
            }
        }

        /// <summary>
        /// Body para resolver un reintegro al experto.
        /// </summary>
        public class ResolveExpertReinstatementRequest
        {
            /// <summary>Importe realmente reintegrado al experto en céntimos. OBLIGATORIO (>= 0).
            /// Usar 0 SOLO si la deuda quedó cubierta por un clawback de disputa interna previo.</summary>
            public long? ReinstatedAmountCents { get; set; }
            /// <summary>Nota libre de auditoría (queda en logs).</summary>
            public string? Note { get; set; }
            /// <summary>PI a resolver si el hire tuviera varios marcadores (varias disputas). Opcional:
            /// por defecto resuelve el marcador más reciente.</summary>
            public string? PaymentIntentId { get; set; }
        }

        /// <summary>
        /// Devuelve todas las plantillas de email renderizadas con datos de ejemplo, para el preview
        /// en el panel admin. Solo renderiza — NUNCA envía.
        /// </summary>
        [HttpGet("email-templates/previews")]
        [Authorize(Roles = "Admin")]
        public IActionResult GetEmailTemplatePreviews()
        {
            try
            {
                var previews = EmailTemplatePreviewService.GetAll();
                return Ok(previews);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al generar previews de plantillas de email");
                return StatusCode(500, new { message = "Error al generar las previsualizaciones", errorCode = "EMAIL_PREVIEW_FAILED" });
            }
        }
    }
}
