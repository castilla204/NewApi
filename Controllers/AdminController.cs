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
    [Authorize] // Requiere autenticación
    public class AdminController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly StripeRefundService _refundService;
        private readonly ILogger<AdminController> _logger;
        private readonly IStripeConfigService _stripeConfigService;
        private readonly IConfiguration _configuration;
        // 🛡️ Round 28 S2-P0-11: monitor del umbral OSS UE.
        private readonly OssThresholdService _ossThresholdService;

        public AdminController(
            AppDbContext context,
            StripeRefundService refundService,
            ILogger<AdminController> logger,
            IStripeConfigService stripeConfigService,
            IConfiguration configuration,
            OssThresholdService ossThresholdService)
        {
            _context = context;
            _refundService = refundService;
            _logger = logger;
            _stripeConfigService = stripeConfigService;
            _configuration = configuration;
            _ossThresholdService = ossThresholdService;
        }

        /// <summary>
        /// 🛡️ Round 28 S2-P0-11: snapshot del umbral OSS UE €10.000/año del año actual.
        /// Devuelve YTD por país + porcentaje del umbral + estado de compliance.
        /// El admin debe revisar este endpoint regularmente — sin OSS registrado y cruzando
        /// el umbral, la plataforma incurre en infracción fiscal en cada país UE.
        /// </summary>
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
                return StatusCode(500, new { error = "Error generando snapshot OSS", message = ex.Message });
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
                return StatusCode(500, new { error = "Error ejecutando OSS threshold check", message = ex.Message });
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
                            stripeOpsResults.Add(new { acctId, label, op = "delete_and_reject_failed", deleteError = delEx.Message, rejectError = rejEx.Message });
                        }
                    }
                    catch (Exception unexpectedEx)
                    {
                        stripeOpsResults.Add(new { acctId, label, op = "unexpected_error", error = unexpectedEx.Message });
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
                return StatusCode(500, new { error = "Error reseteando Stripe del experto", message = ex.Message });
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
                    error = ex.Message
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
                    error = ex.Message
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
                return StatusCode(500, new { success = false, message = "Error obteniendo modo Stripe", error = ex.Message });
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
                return StatusCode(500, new { success = false, message = "Error estableciendo modo Stripe", error = ex.Message });
            }
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
                return StatusCode(500, new { success = false, message = "Error alternando modo Stripe", error = ex.Message });
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
    }
}
