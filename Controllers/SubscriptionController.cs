using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using newApi.Services;
using SubscriptionService = Stripe.SubscriptionService;
using newApi.Common;
using Google.Api;
using Google.Cloud.Storage.V1;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    [EnableRateLimiting("payment")] // ✅ SEGURIDAD: 10 requests/minuto para operaciones de pago
    public partial class SubscriptionController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ISubscriptionService _subscriptionService;
        private readonly IConfiguration _configuration;
        private readonly StorageClient _storageClient;
        // ✅ CAMBIO: Eliminados campos readonly - ahora se leen dinámicamente desde IConfiguration
        // Esto permite que las claves se actualicen cuando cambia el modo Stripe sin reiniciar la aplicación
        private readonly SystemStatusService _systemStatusService;
        private readonly StripeRefundService _refundService;
        private readonly IAuthorizationServices _authService;
        private readonly ILoggingService _loggingService;
        private readonly IInvoiceService _invoiceService;
        private readonly IStripeValidationService _stripeValidationService;
        private readonly IAppointmentService _appointmentService;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        // 🔧 FISCAL FLIP: perfil fiscal de la plataforma (default IsVatRegistered=false → pre-alta).
        private readonly global::newApi.Configuration.PlatformFiscalProfile _fiscalProfile;

        // ✅ Propiedades para leer claves dinámicamente desde configuración
        private string? WebhookSecret => _configuration["Stripe:WebhookSecret"];
        private string? GeneralWebhookSecret => _configuration["Stripe:GeneralWebhookSecret"];
        private string? StripeSecretKey => _configuration["Stripe:SecretKey"];

        // ✅ COMENTADO: Ya no necesario - Stripe usa default automático configurado en Dashboard
        // Según docs oficiales Stripe 2026, se recomienda usar "unspecified" y configurar
        // "Automatic" como default en Dashboard (Tax Settings → "Incluir impuestos en los precios")
        // private static string GetTaxBehaviorForCurrency(string currency)
        // {
        //     return currency?.ToLower() switch
        //     {
        //         "usd" => "exclusive",
        //         "cad" => "exclusive",
        //         _ => "inclusive" // EUR, GBP, MXN, etc.
        //     };
        // }

        // 🛡️ Round 28 MUD-BF: sync DAC7 datos legales desde account.updated.
        private readonly global::newApi.Services.Dac7DataSyncService? _dac7DataSync;

        public SubscriptionController(AppDbContext context, IConfiguration configuration, ISubscriptionService subscriptionService, StorageClient storageClient, SystemStatusService systemStatusService, IAuthorizationServices authService, ILoggingService loggingService, StripeRefundService refundService, IStripeValidationService stripeValidationService, IInvoiceService invoiceService, IAppointmentService appointmentService, IServiceScopeFactory serviceScopeFactory, Microsoft.Extensions.Options.IOptions<global::newApi.Configuration.PlatformFiscalProfile> fiscalProfile, global::newApi.Services.Dac7DataSyncService? dac7DataSync = null)
        {
            _context = context;
            _systemStatusService = systemStatusService;
            _subscriptionService = subscriptionService;
            _configuration = configuration;
            _authService = authService;
            _storageClient = storageClient;
            _loggingService = loggingService;
            _refundService = refundService;
            _stripeValidationService = stripeValidationService;
            _invoiceService = invoiceService;
            _appointmentService = appointmentService;
            _serviceScopeFactory = serviceScopeFactory;
            _fiscalProfile = fiscalProfile?.Value ?? new global::newApi.Configuration.PlatformFiscalProfile();
            _dac7DataSync = dac7DataSync;

            // ✅ Actualizar StripeConfiguration.ApiKey dinámicamente
            // Se actualizará cada vez que se acceda a StripeSecretKey
            UpdateStripeApiKey();
        }

        /// <summary>
        /// ✅ Actualizar StripeConfiguration.ApiKey desde configuración
        /// </summary>
        private void UpdateStripeApiKey()
        {
            var secretKey = StripeSecretKey;
            if (!string.IsNullOrEmpty(secretKey))
            {
                StripeConfiguration.ApiKey = secretKey;
            }
        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue
        /// </summary>
        private async Task<int> GetStatusIdByValueAsync(string statusValue)
        {
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == "SearchHireStatus");
            
            if (systemStatus == null)
            {
                // ⚠️ FRENTE 8: estado no encontrado → AVISAR en vez de rebobinar a "pending" en silencio.
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: SearchHireStatus value not found - defaulting to 'pending'",
                    details: $"GetStatusIdByValueAsync could not resolve StatusValue '{statusValue}' (SearchHireStatus). Defaulting to pending (1); verify the status is seeded. This can silently misroute a hire.",
                    source: "SubscriptionController.GetStatusIdByValueAsync",
                    relatedEntityType: "SearchHire");
                return 1;
            }

            return systemStatus.Id;
        }

        private async Task<(bool IsValid, ExpertProfile ExpertProfile, string ErrorMessage)> ValidateExpertOnboardingAsync(int userId)
        {
            var expertProfile = await _context.ExpertProfiles
                .FirstOrDefaultAsync(ep => ep.UserId == userId);

            if (expertProfile == null)
            {
                return (false, null, "Expert profile not found");
            }

            // 🛡️ Round 11 — S-A FIX: gate de PendingVerification para operaciones generales del experto.
            // ANTES: exigía `ChargesEnabled && PayoutsEnabled`. En "separate charges & transfers" el
            // experto onboarda SOLO con la capability "transfers" — ChargesEnabled es FALSE de forma
            // LEGÍTIMA y permanente. Esto bloqueaba al experto durante toda la ventana de verificación
            // aunque Stripe ya hubiese habilitado payouts/transfers.
            // AHORA: alineado con StripeValidationService (R-F4): basta con PayoutsEnabled +
            // Capabilities.Transfers == "active". Coincide con docs Stripe — account-capabilities:
            // "the connected account has payouts_enabled: true (can receive transfers) but
            //  charges_enabled: false" es un estado VÁLIDO para platforms en separate-charges-and-transfers.
            if (expertProfile.StripeStatus == StripeStatus.PendingVerification)
            {
                try
                {
                    if (!string.IsNullOrEmpty(expertProfile.StripeAccountId))
                    {
                        var accountService = new AccountService();
                        var account = await accountService.GetAsync(expertProfile.StripeAccountId);

                        // Permitir operar si Stripe ha activado payouts + capability transfers.
                        if (account.PayoutsEnabled && account.Capabilities?.Transfers == "active")
                        {
                            return (true, expertProfile, null);
                        }
                    }
                }
                catch
                {
                    // Si falla la consulta, ser conservador y bloquear
                }
            }
            
            if (expertProfile.StripeStatus != StripeStatus.Approved || !expertProfile.OnboardingCompleted)
            {
                return (false, expertProfile, GetStatusMessage(expertProfile.StripeStatus));
            }

            return (true, expertProfile, null);
        }

        private string GetStatusMessage(StripeStatus status)
        {
            // 🛡️ Round 28 v2: mensajes ULTRA CORTOS. El frontend ya muestra los requirements
            // como lista visual (chips rojo/ámbar), así que aquí solo va el headline + acción.
            // Sin emojis (los pone el frontend), sin párrafos. Una sola idea por estado.
            return status switch
            {
                StripeStatus.NotRequested => "Configura tu cuenta para empezar a recibir pagos.",
                StripeStatus.Pending => "Estamos creando tu cuenta. Sigue con la verificación cuando quieras.",
                StripeStatus.ActionRequired => "Te quedan unos detalles por rellenar. Cuando los completes, tu cuenta se activará automáticamente.",
                StripeStatus.PendingVerification => "Stripe está revisando tu documentación. Te avisamos en cuanto termine.",
                StripeStatus.RequirementsDue => "Stripe te pide actualizar algunos datos antes del plazo.",
                StripeStatus.RequirementsPastDue => "Te faltan detalles por rellenar. Cuando los completes, tu cuenta se reactivará automáticamente.",
                StripeStatus.RestrictedSoon => "Tu cuenta se restringirá pronto si no completas estos datos.",
                StripeStatus.Restricted => "Tu cuenta está limitada. Completa los datos pendientes para reactivarla.",
                StripeStatus.Disabled => "Stripe pausó los pagos. Contacta con soporte para resolverlo.",
                StripeStatus.Approved => "Tu cuenta está activa y lista para cobrar.",
                StripeStatus.Rejected => "Stripe rechazó tu cuenta. Revisa el motivo más abajo.",
                StripeStatus.Deauthorized => "Tu cuenta se desconectó. Vuelve a conectarla para seguir cobrando.",
                _ => "Estado desconocido. Contacta con soporte."
            };
        }

        private class StripeAccountState
        {
            public StripeStatus Status { get; set; } = StripeStatus.Pending;
            public bool OnboardingCompleted { get; set; }
            public string? StatusDetails { get; set; }
            public string? DisabledReason { get; set; }
            public IReadOnlyList<string> BlockingRequirements { get; set; } = Array.Empty<string>();
            public IReadOnlyList<string> FutureRequirements { get; set; } = Array.Empty<string>();
            public bool FutureRequirementsPastDue { get; set; }
            public bool FutureRequirementsCurrentlyDue { get; set; }
            public DateTime? FutureRequirementsDueAt { get; set; }
            // 🔧 Round 26 ACC-UPD-7: lista de errores de requirements (code+reason+requirement)
            // ya recolectada en EvaluateStripeAccount. Antes solo se usaba en la rama Rejected.
            // Ahora la exponemos para que BuildStatusDetails pueda surfacearla en otras ramas
            // (ActionRequired, RequirementsPastDue, Restricted, Disabled, PendingVerification).
            public IReadOnlyList<string> RequirementErrors { get; set; } = Array.Empty<string>();
        }

        private StripeAccountState EvaluateStripeAccount(Account account)
        {
            var requirements = account.Requirements ?? new AccountRequirements();
            var futureRequirements = account.FutureRequirements ?? new AccountFutureRequirements();

            var currentlyDue = requirements.CurrentlyDue ?? new List<string>();
            var pastDue = requirements.PastDue ?? new List<string>();
            var pendingVerification = requirements.PendingVerification ?? new List<string>();
            var requirementErrors = new List<string>();
            if (requirements.Errors != null)
            {
                foreach (var error in requirements.Errors)
                {
                    // 🛡️ Round 28: traducir el error code a texto humano + nombre del requirement en español.
                    // Antes se concatenaba RAW: "Code: verification_document_failed, Reason: ..., Requirement: individual.verification.document"
                    // Ahora: "Documento ilegible (documento de identidad)"
                    var humanError = GetErrorDescription(error.Code ?? error.Reason ?? "unknown");
                    var humanRequirement = !string.IsNullOrEmpty(error.Requirement)
                        ? GetRequirementDescription(error.Requirement)
                        : null;
                    requirementErrors.Add(humanRequirement != null
                        ? $"{humanError} ({humanRequirement})"
                        : humanError);
                }
            }

            var disabledReason = requirements.DisabledReason ?? string.Empty;
            var futureCurrentlyDue = futureRequirements.CurrentlyDue ?? new List<string>();
            var futureEventuallyDue = futureRequirements.EventuallyDue ?? new List<string>();
            var futurePastDue = futureRequirements.PastDue ?? new List<string>();

            bool transfersActive = account.Capabilities?.Transfers == "active";
            bool chargesEnabled = account.ChargesEnabled;
            bool payoutsEnabled = account.PayoutsEnabled && transfersActive;
            bool detailsSubmitted = account.DetailsSubmitted;
            bool tosAccepted = account.TosAcceptance?.Date != null;  
            var state = new StripeAccountState
            {
                DisabledReason = disabledReason,
                BlockingRequirements = currentlyDue.Concat(pastDue).Distinct().ToArray(),
                FutureRequirements = futureCurrentlyDue.Concat(futureEventuallyDue).Concat(futurePastDue).Distinct().ToArray(),
                FutureRequirementsCurrentlyDue = futureCurrentlyDue.Any(),
                FutureRequirementsPastDue = futurePastDue.Any(),
                // 🔧 Round 26 ACC-UPD-7: propagar la lista de errores para que BuildStatusDetails
                // pueda mostrarlos en ramas no-Rejected (antes solo GetRejectionMessage los usaba).
                RequirementErrors = requirementErrors.ToArray()
            };

            // 🛡️ Round 12 — D5 FIX: preferir el deadline REAL de Stripe sobre el heurístico.
            // Antes: heurístico inventado UtcNow±N días → engaña al experto mostrándole una fecha
            // que NO es la de Stripe (el deadline puede ser +14d, +45d o +3d, no siempre +7/+30).
            // Ahora: leer account.FutureRequirements.CurrentDeadline (DateTime?) y caer al heurístico
            // SOLO si Stripe no asignó deadline (campo nullable por diseño del API).
            // Doc: https://docs.stripe.com/api/accounts/object#account_object-requirements-current_deadline
            state.FutureRequirementsDueAt =
                futureRequirements.CurrentDeadline
                ?? requirements.CurrentDeadline
                ?? (state.FutureRequirementsPastDue ? DateTime.UtcNow.AddDays(-1)
                  : state.FutureRequirementsCurrentlyDue ? DateTime.UtcNow.AddDays(7)
                  : futureEventuallyDue.Any() ? DateTime.UtcNow.AddDays(30)
                  : (DateTime?)null);

            if (!string.IsNullOrEmpty(disabledReason) && IsPermanentRejection(disabledReason))
            {
                state.Status = StripeStatus.Rejected;
                state.OnboardingCompleted = false;
                state.StatusDetails = GetRejectionMessage(disabledReason, requirementErrors);
                return state;
            }

            if (!string.IsNullOrEmpty(disabledReason))
            {
                // ✅ FIX: Si disabledReason es pending_verification pero charges/payouts están habilitados,
                // no bloquear - Stripe permite operar durante verificación
                if ((disabledReason == "requirements.pending_verification" || disabledReason == "requirements.pending_review") 
                    && chargesEnabled && payoutsEnabled)
                {
                    // Stripe permite operar durante pending_verification si los pagos están habilitados
                    // Marcar como Approved si todo lo demás está bien
                    if (detailsSubmitted && tosAccepted)
                    {
                        state.Status = StripeStatus.Approved;
                        state.OnboardingCompleted = true;
                        state.StatusDetails = BuildStatusDetails(state);
                        return state;
                    }
                }
                
                // 🛡️ Round 11 — S-B FIX: alinear con enum oficial Stripe 2026.
                // Docs: https://docs.stripe.com/api/accounts/object#account_object-requirements-disabled_reason
                // Valores oficiales: action_required.requested_capabilities, listed, other, platform_paused,
                // rejected.fraud, rejected.incomplete_verification, rejected.listed, rejected.other,
                // rejected.platform_fraud, rejected.platform_other, rejected.platform_terms_of_service,
                // rejected.terms_of_service, requirements.past_due, requirements.pending_verification, under_review.
                //
                // ANTES: mapeaba reasons que NO existen (requirements.missing, requirements.currently_due,
                // requirements.pending_review, platform_disabled, platform_suspended). El reason oficial
                // `action_required.requested_capabilities` caía en `_ => Disabled` → UX equivocada
                // (botón "Contactar Stripe" en lugar de "Resolver requisitos"). Y `platform_paused`
                // (la plataforma pausó al experto desde Dashboard — TEMPORAL y resoluble) caía en Disabled.
                state.Status = disabledReason switch
                {
                    // Temporal — resoluble por el experto resolviendo requisitos
                    "action_required.requested_capabilities" => StripeStatus.ActionRequired,
                    "requirements.past_due" => StripeStatus.RequirementsPastDue,
                    "requirements.pending_verification" => StripeStatus.PendingVerification,
                    "under_review" => StripeStatus.PendingVerification,
                    // Temporal — la plataforma pausó al experto (resoluble desde el Dashboard de la plataforma)
                    "platform_paused" => StripeStatus.Restricted,
                    // Definitivo (rejected.*) — necesita soporte; algunos sub-tipos vienen de la plataforma
                    _ when disabledReason.StartsWith("rejected.") => StripeStatus.Rejected,
                    // `listed` (lista de sanciones OFAC/PEP) — Stripe no autoriza; permanente sin soporte compliance
                    "listed" => StripeStatus.Rejected,
                    // Catch-all anteriores que no están en el enum oficial pero que aparecían en código legacy
                    "requirements.missing" or "requirements.currently_due" or "requirements.pending_review" => StripeStatus.ActionRequired,
                    "platform_disabled" or "platform_suspended" => StripeStatus.Restricted,
                    _ when disabledReason.StartsWith("requirements.") => StripeStatus.Restricted,
                    _ => StripeStatus.Disabled
                };
                state.OnboardingCompleted = false;
                state.StatusDetails = BuildStatusDetails(state);
                return state;
            }

            if (pastDue.Any() || futurePastDue.Any())
            {
                state.Status = StripeStatus.RequirementsPastDue;
            }
            else if (currentlyDue.Any())
            {
                state.Status = StripeStatus.ActionRequired;
            }
            // ✅ FIX: PendingVerification no debe bloquear si charges_enabled: true
            // Stripe permite operar durante pending_verification si los pagos están habilitados
            else if (pendingVerification.Any() && (!chargesEnabled || !payoutsEnabled))
            {
                // Solo marcar como PendingVerification si los pagos están bloqueados
                state.Status = StripeStatus.PendingVerification;
            }
            else if (futureCurrentlyDue.Any())
            {
                state.Status = StripeStatus.RestrictedSoon;
            }
            else if (futureEventuallyDue.Any())
            {
                state.Status = StripeStatus.RequirementsDue;
            }
            else if (!chargesEnabled || !payoutsEnabled || !transfersActive)
            {
                state.Status = StripeStatus.Disabled;
            }
            else if (detailsSubmitted && tosAccepted && chargesEnabled && payoutsEnabled)
            {
                // ✅ FIX: Aprobar incluso si hay pending_verification, si charges y payouts están habilitados
                // Esto permite que los expertos operen mientras Stripe revisa documentos
                state.Status = StripeStatus.Approved;
                state.OnboardingCompleted = true;
            }
            else if (!detailsSubmitted || !tosAccepted)
            {
                state.Status = StripeStatus.Pending;
            }
            else
            {
                // ✅ FIX: Si llegamos aquí y charges/payouts están habilitados, aprobar
                // incluso si hay pending_verification (Stripe permite operar)
                if (chargesEnabled && payoutsEnabled)
                {
                    state.Status = StripeStatus.Approved;
                    state.OnboardingCompleted = true;
                }
                else
                {
                    state.Status = StripeStatus.PendingVerification;
                }
            }

            state.StatusDetails = BuildStatusDetails(state);
            return state;
        }

        private string BuildStatusDetails(StripeAccountState state)
        {
            // 🛡️ Round 28 v2: mensaje del backend = HEADLINE corto + (opcional) lista de requirements
            // y errores en un formato parseable por el frontend.
            // El frontend NO muestra ya el headline como párrafo; lo usa como subtítulo. Las listas
            // las pinta como chips visuales (rojo = past_due, ámbar = currently_due).
            //
            // FORMATO DEL TEXTO DEVUELTO (clave: stripeStatusDetails):
            //   "<headline>. [Requisitos pendientes: A, B.] [Stripe indicó requisitos vencidos: C.]
            //    [Errores a corregir: X (Y); Z (W).]"
            //
            // El frontend parsea cada bloque con regex; si no detecta nada, muestra el headline solo.
            var details = new List<string> { GetStatusMessage(state.Status) };

            if (state.BlockingRequirements.Any() &&
                (state.Status == StripeStatus.ActionRequired ||
                 state.Status == StripeStatus.RequirementsPastDue ||
                 state.Status == StripeStatus.Restricted ||
                 state.Status == StripeStatus.Disabled))
            {
                var translated = state.BlockingRequirements.Select(GetRequirementDescription);
                details.Add($"Requisitos pendientes: {string.Join(", ", translated)}.");
            }

            if (state.FutureRequirements.Any() &&
                (state.Status == StripeStatus.RequirementsDue ||
                 state.Status == StripeStatus.RestrictedSoon ||
                 state.Status == StripeStatus.RequirementsPastDue))
            {
                var label = state.FutureRequirementsPastDue ? "requisitos vencidos" : "requisitos futuros";
                var translatedFuture = state.FutureRequirements.Select(GetRequirementDescription);
                details.Add($"Stripe indicó {label}: {string.Join(", ", translatedFuture)}.");
            }

            if (state.RequirementErrors.Any() &&
                (state.Status == StripeStatus.ActionRequired ||
                 state.Status == StripeStatus.RequirementsPastDue ||
                 state.Status == StripeStatus.Restricted ||
                 state.Status == StripeStatus.Disabled ||
                 state.Status == StripeStatus.PendingVerification))
            {
                details.Add($"Errores a corregir: {string.Join("; ", state.RequirementErrors)}.");
            }

            return string.Join(" ", details.Where(d => !string.IsNullOrWhiteSpace(d)));
        }

        private void ApplyStripeAccountState(ExpertProfile expertProfile, StripeAccountState state, string? stripeAccountId = null)
        {
            if (!string.IsNullOrEmpty(stripeAccountId))
            {
                expertProfile.StripeAccountId ??= stripeAccountId;
            }

            expertProfile.PendingStripeAccountId = null;
            expertProfile.StripeStatus = state.Status;
            expertProfile.OnboardingCompleted = state.OnboardingCompleted;
            expertProfile.StripeStatusDetails = state.StatusDetails ?? GetStatusMessage(state.Status);
            // 🛡️ Round 28 — Sprint US-2 (SUS2-4): traducir los keys raw de Stripe a texto humano.
            // Antes: string.Join con "individual.ssn_last_4, external_account" → el experto veía
            // literales sin traducir en StripeStatusCard "Próximos requisitos". Ahora pasamos cada
            // key por GetRequirementDescription para que aparezca "Últimos 4 dígitos del NIE/SSN,
            // información bancaria". Bug aplicable a TODOS los países, no solo US.
            expertProfile.StripeFutureRequirements = state.FutureRequirements.Any()
                ? string.Join(", ", state.FutureRequirements.Select(r => GetRequirementDescription(r)))
                : null;
            expertProfile.StripeFutureDueAt = state.FutureRequirementsDueAt;
        }

        private async Task NotifyStripeStatusTransitionAsync(ExpertProfile expertProfile, StripeStatus previousStatus, StripeAccountState state, string source)
        {
            var details = state.StatusDetails ?? GetStatusMessage(state.Status);

            switch (state.Status)
            {
                case StripeStatus.Approved:
                    await _loggingService.LogInfoAsync(
                        message: "Cuenta de Stripe aprobada",
                        details: details,
                        userId: expertProfile.UserId,
                        source: source,
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile.Id,
                        notifyUser: true);
                    break;
                case StripeStatus.Rejected:
                    await NotifyExpertOnly(expertProfile.UserId, state.DisabledReason ?? "rejected");
                    await _loggingService.LogErrorAsync(
                        message: "Cuenta de Stripe rechazada",
                        details: details,
                        userId: expertProfile.UserId,
                        source: source,
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile.Id,
                        notifyUser: true);
                    break;
                case StripeStatus.ActionRequired:
                case StripeStatus.RequirementsDue:
                case StripeStatus.RestrictedSoon:
                case StripeStatus.PendingVerification:
                    await _loggingService.LogWarningAsync(
                        message: "Stripe require acciones para el experto",
                        details: details,
                        userId: expertProfile.UserId,
                        source: source,
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile.Id,
                        notifyUser: true);
                    break;
                case StripeStatus.RequirementsPastDue:
                case StripeStatus.Restricted:
                case StripeStatus.Disabled:
                    await _loggingService.LogErrorAsync(
                        message: "Cuenta de Stripe restringida o deshabilitada",
                        details: details,
                        userId: expertProfile.UserId,
                        source: source,
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile.Id,
                        notifyUser: true);
                    break;
                default:
                    await _loggingService.LogInfoAsync(
                        message: "Actualización de estado de Stripe",
                        details: details,
                        userId: expertProfile.UserId,
                        source: source,
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile.Id);
                    break;
            }
        }

        private async Task<ExpertProfile?> FindExpertProfileForAccountAsync(Account account)
        {
            var profile = await _context.ExpertProfiles
                .FirstOrDefaultAsync(ep => ep.StripeAccountId == account.Id || ep.PendingStripeAccountId == account.Id);

            if (profile != null)
            {
                // 🛡️ Round 28 MUD-M (GAP-3 fix): si la cuenta fue cerrada en una mudanza
                // muy reciente (< 15 min) e ignoramos el webhook tardío de Stripe (account.updated
                // y capability.updated con disabled_reason='rejected.other' que llegan después de
                // DeleteAsync/RejectAsync), evitamos que ApplyStripeAccountState sobrescriba
                // StripeStatus + StripeStatusDetails recién reseteados — el experto vería
                // "cuenta rechazada" en lugar de la guía hacia /become-expert.
                if (profile.RelocatedAt != null
                    && profile.RelocatedAt.Value > System.DateTime.UtcNow.AddMinutes(-15))
                {
                    return null;
                }
                return profile;
            }

            if (account.Metadata != null && account.Metadata.TryGetValue("userId", out var userIdValue) && int.TryParse(userIdValue, out var userId))
            {
                var byMeta = await _context.ExpertProfiles.FirstOrDefaultAsync(ep => ep.UserId == userId);
                // MUD-M (GAP-3): mismo guard para el fallback por userId metadata.
                if (byMeta != null
                    && byMeta.RelocatedAt != null
                    && byMeta.RelocatedAt.Value > System.DateTime.UtcNow.AddMinutes(-15)
                    && string.IsNullOrEmpty(byMeta.StripeAccountId))
                {
                    return null;
                }
                return byMeta;
            }

            return null;
        }

        // ❌ ELIMINADO: CancelSubscription - Suscripciones periódicas ya no se usan
        // ❌ ELIMINADO: CreateCheckoutSession - Suscripciones periódicas ya no se usan (Mode = "subscription")
        // ❌ ELIMINADO: GetSubscriptionPlans - Suscripciones periódicas ya no se usan
        // ❌ ELIMINADO: GetSubscriptionDetails - Suscripciones periódicas ya no se usan
        // ❌ ELIMINADO: GetCurrentSubscription - Suscripciones periódicas ya no se usan

        [HttpPost("expert-onboarding")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> CreateExpertOnboarding()
        {
            var requestId = Guid.NewGuid().ToString();
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // ✅ LOG: Inicio del proceso
                await _loggingService.LogInfoAsync(
                    message: "Expert onboarding request started",
                    details: $"User {userId} requested expert onboarding. RequestId: {requestId}",
                    userId: userId,
                    source: "SubscriptionController.CreateExpertOnboarding",
                    relatedEntityType: "ExpertProfile",
                    additionalData: new { RequestId = requestId });

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Expert profile not found for onboarding",
                        details: $"User {userId} requested onboarding but expert profile not found. RequestId: {requestId}",
                        userId: userId,
                        source: "SubscriptionController.CreateExpertOnboarding",
                        relatedEntityType: "ExpertProfile",
                        additionalData: new { RequestId = requestId });
                    return NotFound(new { message = "Expert profile not found" });
                }

                // ✅ LOG: Estado actual del perfil
                await _loggingService.LogInfoAsync(
                    message: "Expert profile status check",
                    details: $"User {userId} - StripeStatus: {expertProfile.StripeStatus}, OnboardingCompleted: {expertProfile.OnboardingCompleted}, StripeAccountId: {expertProfile.StripeAccountId ?? "null"}, PendingStripeAccountId: {expertProfile.PendingStripeAccountId ?? "null"}. RequestId: {requestId}",
                    userId: userId,
                    source: "SubscriptionController.CreateExpertOnboarding",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: expertProfile.Id,
                    additionalData: new { 
                        RequestId = requestId,
                        StripeStatus = expertProfile.StripeStatus.ToString(),
                        OnboardingCompleted = expertProfile.OnboardingCompleted,
                        HasStripeAccountId = !string.IsNullOrEmpty(expertProfile.StripeAccountId),
                        HasPendingStripeAccountId = !string.IsNullOrEmpty(expertProfile.PendingStripeAccountId)
                    });

                // ✅ VALIDACIÓN: Si está Approved pero no tiene StripeAccountId real, no permitir onboarding
                if (expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted && string.IsNullOrEmpty(expertProfile.StripeAccountId))
                {
                    await _loggingService.LogWarningAsync(
                        message: "Onboarding requested but account already approved without StripeAccountId",
                        details: $"User {userId} requested onboarding but StripeStatus is Approved and OnboardingCompleted is true, but StripeAccountId is null. This should not happen. RequestId: {requestId}",
                        userId: userId,
                        source: "SubscriptionController.CreateExpertOnboarding",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile.Id,
                        additionalData: new { RequestId = requestId });
                    
                    return BadRequest(new { 
                        message = "Tu cuenta ya está aprobada pero no tiene una cuenta de Stripe asociada. Por favor, contacta al soporte técnico.",
                        stripeStatus = "Approved",
                        onboardingCompleted = true,
                        hasStripeAccount = false,
                        requestId = requestId
                    });
                }

                // 🔧 Round 26 ONBOARD-1: token de reintento para invalidar la idempotency key
                // tras un rechazo temporal. Por defecto vacío → la key sigue siendo determinista
                // por userId (preserva la protección original contra retries de red). Si el flujo
                // entra a la rama de cleanup por rechazo temporal, se asigna un Guid fresco que
                // se sufija a la idempotency key de accounts.create, evitando que Stripe devuelva
                // el acct rechazado cacheado durante 24h.
                string onboardingAttemptToken = string.Empty;

                // ⚠️ BLOQUEAR SOLO si es un rechazo permanente; permitir reintentos si es temporal
                if (expertProfile.StripeStatus == StripeStatus.Rejected)
                {
                    // Obtener el motivo del rechazo desde Stripe
                    string disabledReason = null;
                    // Primero intentar obtener de Stripe directamente
                    if (!string.IsNullOrEmpty(expertProfile.StripeAccountId))
                    {
                        try
                        {
                            var accountServiceForCreate = new AccountService();
                            var accountForCreate = await accountServiceForCreate.GetAsync(expertProfile.StripeAccountId);
                            disabledReason = accountForCreate.Requirements?.DisabledReason;
                        }
                        catch (StripeException ex)
                        {
                            // P0-4: no tragar el error en silencio. Mantener flujo (la rama
                            // de fallback usa expertProfile.StripeStatusDetails) pero dejar traza.
                            await _loggingService.LogWarningAsync(
                                message: "Stripe error consultando estado de cuenta de experto (rejection/disabled reason)",
                                details: $"StripeException al recuperar Account.Requirements.DisabledReason. ExpertProfileId: {expertProfile?.Id}, StripeAccountId: {expertProfile?.StripeAccountId}, StripeCode: {ex.StripeError?.Code}, StripeType: {ex.StripeError?.Type}, Message: {ex.Message}",
                                source: "SubscriptionController." + nameof(CreateExpertOnboarding),
                                relatedEntityType: "ExpertProfile",
                                relatedEntityId: expertProfile?.Id);
                        }
                    }

                    // Si no se pudo obtener de Stripe, intentar extraer del StripeStatusDetails
                    if (string.IsNullOrEmpty(disabledReason) && !string.IsNullOrEmpty(expertProfile.StripeStatusDetails))
                    {
                        disabledReason = ExtractRejectionReasonFromDetails(expertProfile.StripeStatusDetails);
                        if (!string.IsNullOrEmpty(disabledReason))
                        {
                        }
                        else
                        {
                        }
                    }
                    else if (string.IsNullOrEmpty(disabledReason))
                    {
                    }

                    // Si es un rechazo permanente, bloquear
                    if (IsPermanentRejection(disabledReason))
                    {
                        string rejectionInfo = "Tu cuenta de pagos fue rechazada por Stripe.";
                        if (!string.IsNullOrEmpty(expertProfile.StripeStatusDetails))
                        {
                            rejectionInfo = expertProfile.StripeStatusDetails;
                        }

                        return BadRequest(new {
                            message = "No se puede crear una nueva cuenta. " + rejectionInfo + " Por favor, contacta al soporte técnico para revisar tu situación.",
                            blocked = true,
                            reason = "account_permanently_rejected",
                            rejectionReason = disabledReason,
                            rejectionDetails = expertProfile.StripeStatusDetails
                        });
                    }
                    else
                    {
                        // Es un rechazo temporal (requirements.past_due, etc.), permitir reintentar
                        // 🛡️ Round 28 — Sprint US-2 (SUS2-7): borrar el acct rechazado de Stripe
                        // best-effort antes de limpiar la BD. Antes el acct quedaba huérfano en
                        // Stripe consumiendo el contador de cuentas Connect y disparando webhooks
                        // account.updated sin owner local. Si Delete falla (balance>0, etc.),
                        // intentamos Reject(other) — Stripe lo permite con balance pendiente y se
                        // encarga de reversar al platform automáticamente. El delete local del
                        // ExpertProfile.StripeAccountId continúa SIEMPRE para no bloquear la UX.
                        var stripeAcctToCleanup = expertProfile.StripeAccountId;
                        if (!string.IsNullOrEmpty(stripeAcctToCleanup))
                        {
                            try
                            {
                                var cleanupSvc = new AccountService();
                                await cleanupSvc.DeleteAsync(stripeAcctToCleanup);
                                await _loggingService.LogInfoAsync(
                                    message: "Rejected Stripe account deleted from Stripe",
                                    details: $"Stripe account {stripeAcctToCleanup} (rechazo temporal) eliminado en Stripe antes de permitir restart del experto {userId}.",
                                    userId: userId,
                                    source: "SubscriptionController.CreateExpertOnboarding.RejectedTempCleanup",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfile.Id);
                            }
                            catch (StripeException stripeDelEx) when (stripeDelEx.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                // Ya no existe — perfecto, continuar.
                                await _loggingService.LogInfoAsync(
                                    message: "Rejected Stripe account already gone (404)",
                                    details: $"Stripe account {stripeAcctToCleanup} no existe en Stripe — continuamos con restart.",
                                    userId: userId,
                                    source: "SubscriptionController.CreateExpertOnboarding.RejectedTempCleanup",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfile.Id);
                            }
                            catch (StripeException stripeDelEx)
                            {
                                // Delete falló (balance, capability activa, etc.) — intentar Reject como fallback.
                                try
                                {
                                    var rejectSvc = new AccountService();
                                    await rejectSvc.RejectAsync(stripeAcctToCleanup, new AccountRejectOptions { Reason = "other" });
                                    await _loggingService.LogCriticalAsync(
                                        message: "Rejected Stripe account REJECTED (Delete failed — Stripe reverses balance to platform)",
                                        details: $"Stripe account {stripeAcctToCleanup}: Delete falló ({stripeDelEx.StripeError?.Code}: {stripeDelEx.Message}). Reject(other) ejecutado OK — balance pendiente revertido al platform. ACCIÓN ADMIN: identificar dueño y reconciliar.",
                                        userId: userId,
                                        source: "SubscriptionController.CreateExpertOnboarding.RejectedTempCleanup",
                                        relatedEntityType: "ExpertProfile",
                                        relatedEntityId: expertProfile.Id,
                                        additionalData: new { StripeAccountId = stripeAcctToCleanup, stripeDelEx.StripeError?.Code, stripeDelEx.Message });
                                }
                                catch (Exception rejEx)
                                {
                                    // Ambos fallaron — cuenta queda huérfana en Stripe; admin debe limpiar manualmente.
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL: BOTH Delete AND Reject failed on rejected Stripe account",
                                        details: $"Stripe account {stripeAcctToCleanup}: Delete y Reject fallaron. Delete: {stripeDelEx.Message}. Reject: {rejEx.Message}. URGENTE: limpiar manualmente en Stripe Dashboard. El delete local del experto SÍ continúa.",
                                        userId: userId,
                                        source: "SubscriptionController.CreateExpertOnboarding.RejectedTempCleanup",
                                        relatedEntityType: "ExpertProfile",
                                        relatedEntityId: expertProfile.Id,
                                        additionalData: new { StripeAccountId = stripeAcctToCleanup, DeleteError = stripeDelEx.Message, RejectError = rejEx.Message });
                                }
                            }
                            catch (Exception unexpectedEx)
                            {
                                // Best-effort: no abortar el restart.
                                await _loggingService.LogWarningAsync(
                                    message: "Unexpected error deleting rejected Stripe account (continuing restart)",
                                    details: $"Stripe account {stripeAcctToCleanup}: error inesperado: {unexpectedEx.Message}. Cleanup local continúa.",
                                    userId: userId,
                                    source: "SubscriptionController.CreateExpertOnboarding.RejectedTempCleanup",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfile.Id);
                            }
                        }
                        // Limpiar la cuenta rechazada y permitir crear una nueva
                        expertProfile.StripeAccountId = null;
                        expertProfile.PendingStripeAccountId = null;
                        expertProfile.StripeStatus = StripeStatus.NotRequested;
                        expertProfile.StripeStatusDetails = null;
                        expertProfile.OnboardingCompleted = false;
                        // 🔧 Round 26 ONBOARD-1: generar token fresco para evitar reusar el acct
                        // rechazado vía la caché de idempotency de Stripe (24h por key).
                        onboardingAttemptToken = Guid.NewGuid().ToString("N");
                        await _context.SaveChangesAsync();
                        // Continuar con el flujo normal de creación de cuenta
                    }
                }

                if (!string.IsNullOrEmpty(expertProfile.StripeAccountId))
                {
                    // ✅ LOG: Intentando crear link para cuenta existente
                    await _loggingService.LogInfoAsync(
                        message: "Creating Stripe account link for existing account",
                        details: $"User {userId} has StripeAccountId: {expertProfile.StripeAccountId}, Status: {expertProfile.StripeStatus}, OnboardingCompleted: {expertProfile.OnboardingCompleted}",
                        userId: userId,
                        source: "SubscriptionController.CreateExpertOnboarding",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile.Id);

                    // Clean up PendingStripeAccountId if it exists (shouldn't happen but just in case)
                    if (!string.IsNullOrEmpty(expertProfile.PendingStripeAccountId))
                    {
                        expertProfile.PendingStripeAccountId = null;
                        expertProfile.OnboardingCompleted = true;
                        await _context.SaveChangesAsync();
                    }
                    
                    // If expert already has a completed Stripe account, create a login link instead
                    // 🔧 Round 26 ONBOARD-3: alinear con los otros 5 AccountLink (eventually_due)
                    // para que el experto vea también requisitos futuros antes de su deadline.
                    var existingAccountLinkOptions = new AccountLinkCreateOptions
                    {
                        Account = expertProfile.StripeAccountId,
                        RefreshUrl = $"{(_configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com")}/refresh-onboarding",
                        ReturnUrl = $"{(_configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com")}/complete-onboarding",
                        Type = "account_onboarding",
                        Collect = "eventually_due"
                    };
                    
                    var existingAccountLinkService = new AccountLinkService();
                    
                    try
                    {
                        var existingAccountLink = await existingAccountLinkService.CreateAsync(existingAccountLinkOptions);
                        await _loggingService.LogInfoAsync(
                            message: "Stripe account link created successfully",
                            details: $"Account link created for StripeAccountId: {expertProfile.StripeAccountId}",
                            userId: userId,
                            source: "SubscriptionController.CreateExpertOnboarding",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: expertProfile.Id);
                        // 🔧 Round 26 LOGINLINK-2: bandera honesta — la URL devuelta es un
                        // AccountLink (account_onboarding), NO un LoginLink de Dashboard. El
                        // legacy true mentía al frontend, que ahora dispone de /create-login-link.
                        return Ok(new { url = existingAccountLink.Url, isLoginLink = false });
                    }
                    catch (StripeException ex)
                    {
                        // ✅ LOG DETALLADO: Error de Stripe
                        await _loggingService.LogErrorAsync(
                            message: "Failed to create Stripe account link",
                            details: $"StripeException creating account link. StripeAccountId: {expertProfile.StripeAccountId}, StripeStatus: {expertProfile.StripeStatus}, OnboardingCompleted: {expertProfile.OnboardingCompleted}, Error: {ex.Message}, StripeErrorCode: {ex.StripeError?.Code}, StripeErrorType: {ex.StripeError?.Type}",
                            userId: userId,
                            source: "SubscriptionController.CreateExpertOnboarding",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: expertProfile.Id,
                            additionalData: new { 
                                StripeAccountId = expertProfile.StripeAccountId,
                                StripeStatus = expertProfile.StripeStatus.ToString(),
                                OnboardingCompleted = expertProfile.OnboardingCompleted,
                                StripeErrorCode = ex.StripeError?.Code,
                                StripeErrorType = ex.StripeError?.Type,
                                StripeErrorMessage = ex.Message
                            });
                        return StatusCode(500, new { 
                            message = "Failed to create Stripe account link",
                            error = ex.Message,
                            stripeErrorCode = ex.StripeError?.Code,
                            stripeErrorType = ex.StripeError?.Type,
                            details = $"StripeAccountId: {expertProfile.StripeAccountId} may not exist in Stripe"
                        });
                    }
                }

                if (!string.IsNullOrEmpty(expertProfile.PendingStripeAccountId))
                {
                    // Si tiene cuenta pendiente pero no completó onboarding, crear nuevo link para continuar
                    var pendingAccountLinkOptions = new AccountLinkCreateOptions
                    {
                        Account = expertProfile.PendingStripeAccountId,
                        RefreshUrl = $"{(_configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com")}/refresh-onboarding",
                        ReturnUrl = $"{(_configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com")}/complete-onboarding",
                        Type = "account_onboarding",
                        Collect = "eventually_due"
                    };
                    
                    var pendingAccountLinkService = new AccountLinkService();
                    
                    try
                    {
                        var pendingAccountLink = await pendingAccountLinkService.CreateAsync(pendingAccountLinkOptions);
                        return Ok(new { url = pendingAccountLink.Url, isLoginLink = false });
                    }
                    catch (StripeException ex)
                    {
                        // ✅ LOG DETALLADO: capturar el error EXACTO de Stripe (antes se ocultaba)
                        await _loggingService.LogErrorAsync(
                            message: "Failed to create onboarding link for pending account",
                            details: $"StripeException creating account link for PendingStripeAccountId: {expertProfile.PendingStripeAccountId}, Error: {ex.Message}, StripeErrorCode: {ex.StripeError?.Code}, StripeErrorType: {ex.StripeError?.Type}",
                            userId: userId,
                            source: "SubscriptionController.CreateExpertOnboarding",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: expertProfile.Id,
                            additionalData: new {
                                PendingStripeAccountId = expertProfile.PendingStripeAccountId,
                                StripeErrorCode = ex.StripeError?.Code,
                                StripeErrorType = ex.StripeError?.Type,
                                StripeErrorMessage = ex.Message
                            });

                        // ✅ AUTO-RECUPERACIÓN: si la cuenta pendiente ya no existe en el modo/clave
                        // actual (creada en otro modo test/live, clave rotada, o cuenta borrada),
                        // limpiarla en memoria y dejar que el flujo de abajo cree una cuenta nueva.
                        // Así el usuario deja de quedarse atascado en "Continuar verificación".
                        // Stripe a veces devuelve este caso con Code=null y Type=invalid_request_error
                        // (mensaje: "...account that is not connected to your platform or does not exist"),
                        // típico cuando la cuenta pendiente se creó en otra cuenta/modo (live vs test).
                        var pendingMsg = ex.Message ?? "";
                        var pendingAccountUnusable =
                            ex.StripeError?.Code == "resource_missing"
                            || ex.StripeError?.Code == "account_invalid"
                            || pendingMsg.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                            || pendingMsg.Contains("not connected to your platform", StringComparison.OrdinalIgnoreCase);
                        if (pendingAccountUnusable)
                        {
                            expertProfile.PendingStripeAccountId = null;
                            // Sin return: cae al flujo de creación de cuenta nueva más abajo
                        }
                        else
                        {
                            return StatusCode(500, new {
                                message = "Failed to create onboarding link",
                                error = ex.Message,
                                stripeErrorCode = ex.StripeError?.Code,
                                stripeErrorType = ex.StripeError?.Type
                            });
                        }
                    }
                }

                // Limpiar cualquier PendingStripeAccountId anterior antes de crear nueva cuenta
                if (!string.IsNullOrEmpty(expertProfile.PendingStripeAccountId))
                {
                    expertProfile.PendingStripeAccountId = null;
                }

                // 🛡️ Round 28 MUD-V (GAP): validar Country ANTES de marcar Pending +
                // SaveChanges. Antes, si Country=null (experto recién mudado) llegaba aquí,
                // primero se persistía StripeStatus=Pending y LUEGO fallaba — dejando el
                // perfil en Pending sin cuenta Stripe → loop infinito de "Continuar
                // Verificación" que siempre da 400. Mover el guard arriba evita el side-effect.
                var preFlightCountry = expertProfile.Country?.Trim().ToUpperInvariant();
                if (!SupportedConnectCountries.IsSupported(preFlightCountry))
                {
                    var isRelocating = expertProfile.RelocatedFromCountry != null
                                    && string.IsNullOrEmpty(expertProfile.StripeAccountId);
                    await _loggingService.LogWarningAsync(
                        message: "Onboarding de experto bloqueado: pais no soportado o ausente (pre-Pending guard)",
                        details: $"UserId {userId}, ExpertProfileId {expertProfile.Id}, Country='{expertProfile.Country ?? "null"}', Relocating={isRelocating}. Bloqueado ANTES de set StripeStatus=Pending para evitar estado persistido inconsistente.",
                        userId: userId,
                        source: "SubscriptionController.CreateExpertOnboarding.CountryPreFlight",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile.Id);
                    return BadRequest(new
                    {
                        message = isRelocating
                            ? "Para completar tu mudanza, primero selecciona tu nuevo país en 'Convertirse en experto'."
                            : "Tu pais todavia no esta disponible para recibir pagos en la plataforma. Si crees que es un error, contacta con soporte.",
                        blocked = true,
                        reason = isRelocating ? "relocation_pending_country_required" : "unsupported_country",
                        country = expertProfile.Country,
                        redirectTo = isRelocating ? "/become-expert" : null,
                    });
                }

                // Marcar como pendiente antes de crear la cuenta
                expertProfile.StripeStatus = StripeStatus.Pending;
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var recoveryProfile = await recoveryContext.ExpertProfiles
                        .FirstOrDefaultAsync(ep => ep.UserId == userId);
                    
                    if (recoveryProfile != null)
                    {
                        recoveryProfile.StripeStatus = StripeStatus.Pending;
                        await recoveryContext.SaveChangesAsync();
                        expertProfile = recoveryProfile; // Actualizar referencia
                    }
                    else
                    {
                        return StatusCode(500, new { 
                            message = "Failed to save Stripe account status", 
                            details = "Connection disposed and recovery failed",
                            error = "CONNECTION_DISPOSED"
                        });
                    }
                }
                catch (ObjectDisposedException disposedEx)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var recoveryProfile = await recoveryContext.ExpertProfiles
                        .FirstOrDefaultAsync(ep => ep.UserId == userId);
                    
                    if (recoveryProfile != null)
                    {
                        recoveryProfile.StripeStatus = StripeStatus.Pending;
                        await recoveryContext.SaveChangesAsync();
                        expertProfile = recoveryProfile; // Actualizar referencia
                    }
                    else
                    {
                        return StatusCode(500, new { 
                            message = "Failed to save Stripe account status", 
                            details = disposedEx.Message,
                            error = "CONNECTION_DISPOSED"
                        });
                    }
                }
                // 🔧 FIX internacional: usar el país REAL del experto (no "ES" fijo). Validamos contra los países
                // soportados por una plataforma EEA en separate charges & transfers (EEA + US/CA/GB/CH). Si el país
                // falta o no está soportado, bloqueamos el onboarding (crear la cuenta con país equivocado es
                // IRREVERSIBLE). LatAm (MX, AR...) requiere Cross-border payouts vía Stripe Sales → no se habilita aquí.
                // Lista compartida (newApi.Common.SupportedConnectCountries) — MISMA fuente que el gate del
                // paso 1 en UserService.BecomeExpert, para que paso 1 y paso 2 no diverjan.
                var expertConnectCountry = expertProfile.Country?.Trim().ToUpperInvariant();
                if (!SupportedConnectCountries.IsSupported(expertConnectCountry))
                {
                    await _loggingService.LogWarningAsync(
                        message: "Onboarding de experto bloqueado: pais no soportado o ausente",
                        details: $"UserId {userId}, ExpertProfileId {expertProfile.Id}, Country='{expertProfile.Country ?? "null"}'. No soportado para Stripe Connect (separate charges & transfers desde plataforma EEA).",
                        userId: userId,
                        source: "SubscriptionController.BecomeExpert",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile.Id);
                    return BadRequest(new {
                        message = "Tu pais todavia no esta disponible para recibir pagos en la plataforma. Si crees que es un error, contacta con soporte.",
                        blocked = true,
                        reason = "unsupported_country",
                        country = expertProfile.Country
                    });
                }

                // 🛡️ Round 11 — S-D + S-F + S-A FIX para CreateExpertOnboarding:
                //
                // 1) NO se setea TosAcceptance: usamos Stripe-hosted onboarding (AccountLink type=
                //    account_onboarding). Stripe recoge la aceptación y aplica service_agreement="full"
                //    por defecto. NO cambiar a "recipient": Stripe REQUIERE "full" para cross-border
                //    payouts EEA→{EEA,US,CA,GB,CH} (docs.stripe.com/connect/cross-border-payouts:
                //    "You must use the Full service agreement... If you require the Recipient Service
                //    Agreement, use Global payouts"). "recipient" es para Global payouts (contrato
                //    Stripe Sales distinto) y añade 24h de settlement extra + sin soporte directo de
                //    Stripe al experto.
                //
                // 2) NO se setea BusinessType: antes hardcodeaba "individual" → expertos con
                //    SL/SLU/SA/comunidad de bienes quedaban atrapados como persona física (Stripe
                //    Express NO permite cambiar business_type tras crear el AccountLink). Sin el
                //    field, el hosted onboarding pide al experto que elija "Persona física / Empresa"
                //    como primer paso (selector en español traducido por Stripe).
                //
                // 3) Capabilities por país — 🛡️ Round 28 Sprint US:
                //    - EEA + CH + LI: solo `transfers` (separate charges and transfers, cliente
                //      cobra a la plataforma, experto solo recibe payouts).
                //    - US, CA, GB: Stripe EXIGE también `card_payments` aunque la capability quede
                //      latente y el experto NO cobre directamente. El error sin esto:
                //      "You cannot request the `transfers` capability without the `card_payments`
                //       capability for accounts in US."
                //    Centralizado en StripeConnectCapabilities.BuildCapabilitiesFor(country).
                var accountOptions = new AccountCreateOptions
                {
                    Type = "express",
                    Country = expertConnectCountry,
                    Email = User.FindFirst(ClaimTypes.Email)?.Value,
                    Capabilities = global::newApi.Common.StripeConnectCapabilities.BuildCapabilitiesFor(expertConnectCountry),
                    // 🛡️ Round 28 — Sprint US-2 (SUS2-2): service_agreement="full" explícito.
                    // Stripe usa "full" por default, pero el Platform Profile del Dashboard puede
                    // sobrescribirlo a "recipient" (Global Payouts contract). Si alguien lo cambia
                    // accidentalmente, las nuevas cuentas US/CA/GB se crearían como recipient y la
                    // combinación con card_payments + transfers fallaría con invalid_request_error.
                    // Setearlo aquí blinda contra ese cambio. Stripe-hosted onboarding sigue
                    // recogiendo la aceptación TOS — NO setear Date/Ip/UserAgent aquí (rompería el
                    // flujo hosted; Stripe lo recoge en la pantalla del experto).
                    TosAcceptance = new AccountTosAcceptanceOptions
                    {
                        ServiceAgreement = "full"
                    },
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() }
                    }
                };

                var accountService = new AccountService();
                Account account;
                try
                {
                    // 🛡️ Round 14 — Q9-P1 FIX: idempotency key determinista por userId.
                    // Sin esto, un retry de red (cliente con conexión lenta, timeout, retry de
                    // axios) generaba un Connect account NUEVO en Stripe cada vez → cuentas
                    // huérfanas zombies acumuladas. La clave por userId garantiza que el experto
                    // tenga UN único acct para siempre (Stripe rechaza con idempotency_error si
                    // el body difiere, lo cual es exactamente lo que queremos).
                    // 🔧 Round 26 ONBOARD-1: si venimos de un cleanup por rechazo temporal,
                    // sufijar la key con un token fresco. La caché de idempotency de Stripe (24h)
                    // habría devuelto el MISMO acct rechazado bajo la key determinista, dejando
                    // al experto atrapado en bucle de rechazo durante un día. El token vacío en
                    // flujo normal preserva la protección original contra retries de red.
                    // 🛡️ Round 28 Sprint US: prefijo bumped a "v2" para INVALIDAR las respuestas
                    // cacheadas que se generaron con el bug de capabilities (cuentas US que
                    // fallaron con la key antigua antes de este fix). Sin este bump, los
                    // expertos US/CA/GB que probaron pre-fix recibirían el mismo error 24h.
                    // 🛡️ Round 28 Sprint US-2: bumped a "v3" tras añadir tax_reporting_us_1099_misc
                    // + service_agreement="full" explícito. Sin este bump, los expertos US que
                    // probaron en v2 (sin tax_reporting) reusarían la respuesta cacheada y se
                    // perderían la capability nueva.
                    // 🛡️ Round 28 MUD-AE: incluir RelocatedAt.Ticks en la idempotency key.
                    // Sin este discriminador, el segundo (o tercero, etc.) ciclo de mudanza
                    // reusa la key `account-onboarding-v3-{userId}` con un body distinto
                    // (Country/Capabilities cambian) → Stripe responde idempotency_error y
                    // el experto queda atrapado 24h con "Failed to create Stripe account".
                    // RelocatedAt cambia en cada ExpertRelocationService.ExecuteAsync, así
                    // que da una key fresca por mudanza. Determinista para retries de red
                    // dentro de la misma mudanza (preserva la protección anti-doble-acct).
                    // null para usuarios que nunca se han mudado → preserva clave histórica.
                    var relocationDiscriminator = expertProfile.RelocatedAt.HasValue
                        ? $"-reloc{expertProfile.RelocatedAt.Value.Ticks}"
                        : string.Empty;
                    var idempotencyKeyValue = string.IsNullOrEmpty(onboardingAttemptToken)
                        ? $"account-onboarding-v3-{userId}{relocationDiscriminator}"
                        : $"account-onboarding-v3-{userId}{relocationDiscriminator}-{onboardingAttemptToken}";
                    var accountIdempotencyOptions = new Stripe.RequestOptions
                    {
                        IdempotencyKey = idempotencyKeyValue
                    };
                    account = await accountService.CreateAsync(accountOptions, accountIdempotencyOptions);
                }
                catch (StripeException ex)
                {
                    // Antes este catch se tragaba el error (500 genérico, sin log) → la causa real de
                    // por qué falla accounts.create se perdía. Ahora lo registramos y lo devolvemos.
                    await _loggingService.LogErrorAsync(
                        message: "Failed to create Stripe Connect account",
                        details: $"AccountService.CreateAsync threw for user {userId}. StripeError: Code={ex.StripeError?.Code}, Type={ex.StripeError?.Type}, DeclineCode={ex.StripeError?.DeclineCode}, Message={ex.Message}",
                        userId: userId > 0 ? userId : null,
                        source: "SubscriptionController.CreateExpertOnboarding",
                        relatedEntityType: "ExpertProfile",
                        additionalData: new {
                            ExceptionType = ex.GetType().Name,
                            StripeErrorCode = ex.StripeError?.Code,
                            StripeErrorType = ex.StripeError?.Type,
                            ExceptionMessage = ex.Message
                        });
                    return StatusCode(500, new {
                        message = "Failed to create Stripe account",
                        error = ex.Message,
                        code = ex.StripeError?.Code,
                        type = ex.StripeError?.Type
                    });
                }

                // ✅ FIX CRÍTICO: NO usar transacciones manuales con ExecutionStrategy habilitado
                // Guardar primero el estado antes de crear el link (sin transacción para evitar conflicto con ExecutionStrategy)
                expertProfile.PendingStripeAccountId = account.Id;
                expertProfile.OnboardingCompleted = false;
                expertProfile.StripeStatus = StripeStatus.Pending;
                
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var recoveryProfile = await recoveryContext.ExpertProfiles
                        .FirstOrDefaultAsync(ep => ep.UserId == userId);
                    
                    if (recoveryProfile != null)
                    {
                        recoveryProfile.PendingStripeAccountId = account.Id;
                        recoveryProfile.OnboardingCompleted = false;
                        recoveryProfile.StripeStatus = StripeStatus.Pending;
                        await recoveryContext.SaveChangesAsync();
                        expertProfile = recoveryProfile; // Actualizar referencia
                    }
                    else
                    {
                        return StatusCode(500, new { 
                            message = "Failed to save Stripe account status", 
                            details = "Connection disposed and recovery failed",
                            error = "CONNECTION_DISPOSED"
                        });
                    }
                }
                catch (ObjectDisposedException disposedEx)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var recoveryProfile = await recoveryContext.ExpertProfiles
                        .FirstOrDefaultAsync(ep => ep.UserId == userId);
                    
                    if (recoveryProfile != null)
                    {
                        recoveryProfile.PendingStripeAccountId = account.Id;
                        recoveryProfile.OnboardingCompleted = false;
                        recoveryProfile.StripeStatus = StripeStatus.Pending;
                        await recoveryContext.SaveChangesAsync();
                        expertProfile = recoveryProfile; // Actualizar referencia
                    }
                    else
                    {
                        return StatusCode(500, new { 
                            message = "Failed to save Stripe account status", 
                            details = disposedEx.Message,
                            error = "CONNECTION_DISPOSED"
                        });
                    }
                }
                
                // Crear el link de onboarding después de guardar
                var linkOptions = new AccountLinkCreateOptions
                {
                    Account = account.Id,
                    RefreshUrl = $"{(_configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com")}/refresh-onboarding",
                    ReturnUrl = $"{(_configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com")}/complete-onboarding",
                    Type = "account_onboarding",
                    Collect = "eventually_due"
                };

                var linkService = new AccountLinkService();
                AccountLink accountLink;
                try
                {
                    accountLink = await linkService.CreateAsync(linkOptions);
                    return Ok(new { url = accountLink.Url });
                }
                catch (StripeException ex)
                {
                    // 🛡️ R7 FIX: Critical log con accountId huérfano. Antes el account Stripe quedaba
                    // creado (línea 867) y guardado como PendingStripeAccountId (línea 895), pero si
                    // CreateAsync del AccountLink fallaba, el cliente recibía 500 sin que el admin se
                    // enterara. Si el usuario NO reintentaba (frustrado), el account Stripe quedaba
                    // huérfano sin completar onboarding indefinidamente. Cobramos comisión a la
                    // plataforma por account creado pero nunca activo. Critical permite que admin
                    // monitorice cuántos accounts hay en este estado para limpiar/recordar al user.
                    await _loggingService.LogCriticalAsync(
                        message: "R7: AccountLink creation falló — Stripe account huérfano hasta retry",
                        details: $"User {userId}: Stripe account {account.Id} creado y guardado como PendingStripeAccountId, pero AccountLinkService.CreateAsync falló: {ex.StripeError?.Code} / {ex.StripeError?.Type} / {ex.Message}. El usuario verá error 500 al frontend. Si NO reintenta startOnboarding, el account queda huérfano (sin link de onboarding). ACCIÓN ADMIN: monitorizar (a) hires si el usuario reintenta o no en 7 días, (b) limpiar accounts huérfanos en Stripe Dashboard si el usuario abandona.",
                        userId: userId,
                        source: "SubscriptionController.CreateExpertOnboarding.R7",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile?.Id,
                        additionalData: new
                        {
                            StripeAccountId = account.Id,
                            ex.StripeError?.Code,
                            ex.StripeError?.Type,
                            ExceptionMessage = ex.Message
                        });
                    return StatusCode(500, new
                    {
                        message = "No pudimos generar el enlace de configuración. Reintenta en unos minutos para reusar tu cuenta pendiente.",
                        error = ex.Message
                    });
                }
            }
            catch (Exception ex)
            {
                // ✅ LOG: Error general
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int.TryParse(userIdClaim, out int userId);
                await _loggingService.LogErrorAsync(
                    message: "Failed to process expert onboarding",
                    details: $"Exception in CreateExpertOnboarding. UserId: {userId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId > 0 ? userId : null,
                    source: "SubscriptionController.CreateExpertOnboarding",
                    relatedEntityType: "ExpertProfile",
                    additionalData: new { 
                        ExceptionType = ex.GetType().Name,
                        ExceptionMessage = ex.Message,
                        InnerException = ex.InnerException?.Message
                    });
                return StatusCode(500, new { 
                    message = "Failed to process expert onboarding",
                    error = ex.Message,
                    errorType = ex.GetType().Name
                });
            }
        }

        /// <summary>
        /// Crea un enlace de cuenta de Stripe Connect para que el experto pueda actualizar sus datos bancarios
        /// </summary>
        [HttpPost("create-account-link")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> CreateAccountLink()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return NotFound(new { message = "Expert profile not found" });
                }

                if (string.IsNullOrEmpty(expertProfile.StripeAccountId))
                {
                    return BadRequest(new { message = "Stripe account not found. Please complete onboarding first." });
                }

                if (expertProfile.StripeStatus == StripeStatus.Rejected)
                {
                    return BadRequest(new { message = "La cuenta de pagos fue rechazada por Stripe. No se puede abrir el panel. Reinicia el onboarding para crear una cuenta nueva." });
                }

                // ✅ CORRECCIÓN: Para cuentas Express, Stripe solo permite account_onboarding (no account_update)
                // Obtener info de la cuenta desde Stripe para verificar tipo y requirements
                var accountService = new AccountService();
                Account stripeAccount;
                try
                {
                    stripeAccount = await accountService.GetAsync(expertProfile.StripeAccountId);
                }
                catch (StripeException ex)
                {
                    return StatusCode(500, new { message = "Error al obtener información de Stripe", error = ex.Message });
                }

                // ⚠️ IMPORTANTE: Las cuentas Express NO soportan account_update, solo account_onboarding
                // Según documentación oficial de Stripe: "You cannot create account_update type Account Links 
                // for Express accounts. Valid types for Express accounts are [account_onboarding]."
                // Por lo tanto, siempre usamos account_onboarding para cuentas Express
                // account_onboarding funciona tanto para completar requirements como para editar información
                string linkType = "account_onboarding";
                
                // Opcional: Si en el futuro usas cuentas Custom o Standard, podrías usar account_update:
                // if (stripeAccount.Type == "custom" || stripeAccount.Type == "standard") {
                //     bool hasRequirementsPending = (stripeAccount.Requirements?.CurrentlyDue?.Count ?? 0) > 0 ||
                //                                   (stripeAccount.Requirements?.PastDue?.Count ?? 0) > 0 ||
                //                                   !string.IsNullOrEmpty(stripeAccount.Requirements?.DisabledReason);
                //     linkType = hasRequirementsPending ? "account_onboarding" : "account_update";
                // }
                
                // Crear un enlace de cuenta de Stripe Connect para actualizar datos bancarios
                var accountLinkService = new Stripe.AccountLinkService();
                // 🔧 Round 26 ONBOARD-3: añadir Collect=eventually_due para account_onboarding
                // (account_update ignora el campo). Alinea con los otros 5 AccountLink y evita
                // que el experto solo vea currently_due aquí.
                // 🔧 Round 26 ACCOUNTLINK-3: ReturnUrl pasa a /complete-onboarding como los otros
                // 5 AccountLink — StripeOnboardingReturnPage llama a syncStripeStatus antes de
                // volver al panel, evitando la ventana de hasta 2 min de estado stale tras editar.
                var accountLinkOptions = new Stripe.AccountLinkCreateOptions
                {
                    Account = expertProfile.StripeAccountId,
                    // 🛡️ Round 13 — N12 FIX: alinear con los otros 6 puntos del controller que ya usan
                    // /refresh-onboarding. Antes apuntaba a /expert-panel?refresh=true pero ExpertPanelPage
                    // no maneja ese query param → el usuario era devuelto al panel sin contexto cuando el
                    // AccountLink expiraba. /refresh-onboarding → StripeOnboardingReturnPage que ya
                    // regenera link y redirige a Stripe transparente.
                    RefreshUrl = $"{(_configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com")}/refresh-onboarding",
                    ReturnUrl = $"{(_configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com")}/complete-onboarding", // URL de retorno después de actualizar datos (sync antes de panel)
                    Type = linkType, // ✅ account_update para cuentas aprobadas, account_onboarding para requirements pendientes
                    Collect = linkType == "account_onboarding" ? "eventually_due" : null
                };

                var accountLink = await accountLinkService.CreateAsync(accountLinkOptions);
                return Ok(new {
                    message = "Enlace de cuenta creado exitosamente",
                    accountLinkUrl = accountLink.Url
                });
            }
            catch (StripeException stripeEx)
            {
                // 🔧 Round 26 ONBOARD-IDEM-3: log detallado del StripeError. El bloque anterior
                // (try GetAsync sin log + return 500) ya queda cubierto por el catch interno;
                // este catch externo cubre CreateAsync del AccountLink y otras llamadas Stripe.
                try
                {
                    var userIdClaimErr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    int.TryParse(userIdClaimErr, out int userIdErr);
                    await _loggingService.LogErrorAsync(
                        message: "Error de Stripe al crear el enlace de cuenta",
                        details: $"StripeException en CreateAccountLink. Code={stripeEx.StripeError?.Code}, Type={stripeEx.StripeError?.Type}, DeclineCode={stripeEx.StripeError?.DeclineCode}, Message={stripeEx.Message}",
                        userId: userIdErr > 0 ? userIdErr : null,
                        source: "SubscriptionController.CreateAccountLink",
                        relatedEntityType: "ExpertProfile",
                        additionalData: new {
                            ExceptionType = stripeEx.GetType().Name,
                            StripeErrorCode = stripeEx.StripeError?.Code,
                            StripeErrorType = stripeEx.StripeError?.Type
                        });
                }
                catch { /* no romper el handler de error si el logging falla */ }
                return StatusCode(500, new {
                    message = "Error de Stripe al crear el enlace de cuenta",
                    error = stripeEx.Message,
                    code = stripeEx.StripeError?.Code,
                    type = stripeEx.StripeError?.Type
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// 🛡️ Round 12 — D1 FIX: Crea un LoginLink de Stripe para que el experto APROBADO
        /// acceda al Express Dashboard real (vista de payouts, balance, transactions, ajuste
        /// de cuenta bancaria, configuración de payout schedule, etc.).
        ///
        /// Distinto del AccountLink:
        ///  - AccountLink → flujo de onboarding/verificación KYC (formularios)
        ///  - LoginLink   → Express Dashboard (panel de gestión)
        ///
        /// Sólo permitido cuando el experto está APPROVED + OnboardingCompleted. Si no, no
        /// hay Dashboard que abrir (la cuenta aún no terminó setup).
        ///
        /// Doc: https://docs.stripe.com/api/account/create_login_link
        /// </summary>
        [HttpPost("create-login-link")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> CreateLoginLink()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return NotFound(new { message = "Expert profile not found" });
                }

                if (string.IsNullOrEmpty(expertProfile.StripeAccountId))
                {
                    return BadRequest(new { message = "Stripe account not found. Please complete onboarding first." });
                }

                // Gate: el Express Dashboard sólo tiene sentido si la cuenta está aprobada.
                // Para cuentas no aprobadas, el panel del experto en nuestra UI debe ofrecer
                // CreateAccountLink (onboarding) en lugar de este endpoint.
                if (expertProfile.StripeStatus != StripeStatus.Approved || !expertProfile.OnboardingCompleted)
                {
                    return BadRequest(new
                    {
                        message = "El Express Dashboard solo está disponible para cuentas completamente aprobadas.",
                        currentStatus = expertProfile.StripeStatus.ToString(),
                        onboardingCompleted = expertProfile.OnboardingCompleted
                    });
                }

                try
                {
                    // LoginLinks son single-use y de duración corta — generamos uno por click.
                    // En Stripe.net v50, el service se llama AccountLoginLinkService (no LoginLinkService —
                    // fue renombrado en v46 al prefijar los servicios anidados con el nombre del padre).
                    var loginLinkService = new Stripe.AccountLoginLinkService();
                    var loginLink = await loginLinkService.CreateAsync(expertProfile.StripeAccountId);
                    return Ok(new
                    {
                        message = "Login link creado exitosamente",
                        loginLinkUrl = loginLink.Url,
                        // 🛡️ Bandera honesta para el frontend (a diferencia del legacy `isLoginLink` de
                        // CreateAccountLink que mentía — ese es siempre un AccountLink).
                        isLoginLink = true
                    });
                }
                catch (StripeException stripeEx)
                {
                    await _loggingService.LogErrorAsync(
                        message: "D1: Falló creación de LoginLink Stripe",
                        details: $"ExpertProfileId {expertProfile.Id}, StripeAccountId {expertProfile.StripeAccountId}, Code={stripeEx.StripeError?.Code}: {stripeEx.Message}",
                        userId: userId,
                        source: "SubscriptionController.CreateLoginLink",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile.Id);
                    return StatusCode(500, new
                    {
                        message = "Error al crear el login link de Stripe",
                        error = stripeEx.Message,
                        code = stripeEx.StripeError?.Code
                    });
                }
            }
            catch (Exception ex)
            {
                try
                {
                    await _loggingService.LogErrorAsync(
                        message: "D1: Excepción inesperada en CreateLoginLink",
                        details: ex.Message,
                        source: "SubscriptionController.CreateLoginLink");
                }
                catch { /* swallow */ }
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// 🛡️ N12: verifica que una Checkout Session existe en Stripe, está completada,
        /// y que el userId del metadata coincide con el usuario autenticado.
        /// La PaymentSuccessPage debe llamarlo ANTES de mostrar "pago exitoso", para evitar que
        /// un atacante navegue a /success sin haber pagado y vea confirmación falsa.
        /// </summary>
        [HttpGet("verify-checkout-session/{sessionId}")]
        [Authorize]
        public async Task<IActionResult> VerifyCheckoutSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || !sessionId.StartsWith("cs_"))
            {
                return BadRequest(new { valid = false, message = "Invalid session id" });
            }
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdClaim, out var requesterId))
            {
                return Unauthorized(new { valid = false, message = "Invalid token" });
            }
            try
            {
                var stripeSession = await new Stripe.Checkout.SessionService().GetAsync(sessionId);
                var paid = string.Equals(stripeSession?.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(stripeSession?.Status, "complete", StringComparison.OrdinalIgnoreCase);
                var metadataUserId = 0;
                int.TryParse(stripeSession?.Metadata?.GetValueOrDefault("userId"), out metadataUserId);
                if (paid && metadataUserId == requesterId)
                {
                    return Ok(new { valid = true });
                }
                return Ok(new { valid = false, paid, mismatch = metadataUserId != requesterId });
            }
            catch (Stripe.StripeException stripeEx) when (stripeEx.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Ok(new { valid = false, notFound = true });
            }
            catch (Exception)
            {
                return StatusCode(500, new { valid = false, message = "Verification error" });
            }
        }

        [HttpGet("onboarding-status")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> GetOnboardingStatus()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return NotFound(new { message = "Expert profile not found" });
                }

                // Permitir acceso al panel si está Approved o si está Deauthorized pero con contrataciones activas
                var hasActiveHires = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .AnyAsync(sh => sh.ExpertId == userId && sh.Status.StatusValue == "pending");

                var status = new OnboardingStatusDto
                {
                    HasStripeAccount = !string.IsNullOrEmpty(expertProfile.StripeAccountId),
                    HasPendingOnboarding = !string.IsNullOrEmpty(expertProfile.PendingStripeAccountId),
                    OnboardingCompleted = expertProfile.OnboardingCompleted,
                    StripeAccountId = expertProfile.StripeAccountId,
                    StripeStatus = expertProfile.StripeStatus.ToString(),
                    StripeStatusDetails = expertProfile.StripeStatusDetails,
                    // ✅ FIX: Permitir PendingVerification si charges_enabled: true (Stripe permite operar)
                    CanAccessStripe =
                        (expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted)
                        || expertProfile.StripeStatus == StripeStatus.PendingVerification // Permitir acceso durante verificación
                        || ((expertProfile.StripeStatus == StripeStatus.Deauthorized || expertProfile.StripeStatus == StripeStatus.Rejected) && hasActiveHires),
                    // ✅ FUTURE REQUIREMENTS
                    StripeFutureRequirements = expertProfile.StripeFutureRequirements,
                    StripeFutureDueAt = expertProfile.StripeFutureDueAt
                };

                return Ok(status);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to get onboarding status" });
            }
        }

        [HttpGet("expert-status")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> GetExpertStatus()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return NotFound(new { message = "Expert profile not found" });
                }

                // Si la cuenta está rechazada, obtener información detallada de Stripe
                string rejectionReason = null;
                if (expertProfile.StripeStatus == StripeStatus.Rejected)
                {
                    // Primero intentar obtener de Stripe directamente
                    if (!string.IsNullOrEmpty(expertProfile.StripeAccountId))
                    {
                        try
                        {
                            var accountService = new AccountService();
                            var account = await accountService.GetAsync(expertProfile.StripeAccountId);
                            rejectionReason = account.Requirements?.DisabledReason;
                        }
                        catch (StripeException ex)
                        {
                            // P0-4: no tragar el error en silencio. Mantener flujo (la rama
                            // de fallback usa expertProfile.StripeStatusDetails) pero dejar traza.
                            await _loggingService.LogWarningAsync(
                                message: "Stripe error consultando estado de cuenta de experto (rejection/disabled reason)",
                                details: $"StripeException al recuperar Account.Requirements.DisabledReason. ExpertProfileId: {expertProfile?.Id}, StripeAccountId: {expertProfile?.StripeAccountId}, StripeCode: {ex.StripeError?.Code}, StripeType: {ex.StripeError?.Type}, Message: {ex.Message}",
                                source: "SubscriptionController." + nameof(GetExpertStatus),
                                relatedEntityType: "ExpertProfile",
                                relatedEntityId: expertProfile?.Id);
                        }
                    }
                    
                    // Si no se pudo obtener de Stripe, intentar extraer del StripeStatusDetails
                    if (string.IsNullOrEmpty(rejectionReason) && !string.IsNullOrEmpty(expertProfile.StripeStatusDetails))
                    {
                        rejectionReason = ExtractRejectionReasonFromDetails(expertProfile.StripeStatusDetails);
                        if (!string.IsNullOrEmpty(rejectionReason))
                        {
                        }
                    }
                }

                // Permitir acceso al panel si está Approved o si está Deauthorized pero con contrataciones activas
                var hasActiveHires = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .AnyAsync(sh => sh.ExpertId == userId && sh.Status.StatusValue == "pending");

                var status = new ExpertStatusDto
                {
                    HasStripeAccount = !string.IsNullOrEmpty(expertProfile.StripeAccountId),
                    HasPendingOnboarding = !string.IsNullOrEmpty(expertProfile.PendingStripeAccountId),
                    OnboardingCompleted = expertProfile.OnboardingCompleted,
                    StripeStatus = expertProfile.StripeStatus.ToString(),
                    StripeStatusDetails = expertProfile.StripeStatusDetails,
                    StripeAccountId = expertProfile.StripeAccountId,
                    // ✅ FIX: Permitir PendingVerification si charges_enabled: true (Stripe permite operar)
                    // PendingVerification es informativo, no bloqueante si Stripe permite operar
                    CanAccessStripe = (expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted)
                        || expertProfile.StripeStatus == StripeStatus.PendingVerification // Permitir acceso durante verificación
                        || ((expertProfile.StripeStatus == StripeStatus.Deauthorized || expertProfile.StripeStatus == StripeStatus.Rejected) && hasActiveHires),
                    CanCreateServices = (expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted)
                        || expertProfile.StripeStatus == StripeStatus.PendingVerification, // Permitir durante verificación
                    CanReceivePayments = (expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted)
                        || expertProfile.StripeStatus == StripeStatus.PendingVerification, // Permitir durante verificación
                    StatusMessage = GetStatusMessage(expertProfile.StripeStatus),
                    // ✅ FUTURE REQUIREMENTS
                    StripeFutureRequirements = expertProfile.StripeFutureRequirements,
                    StripeFutureDueAt = expertProfile.StripeFutureDueAt,
                    // Permitir reintentar si:
                    // - No ha solicitado cuenta (NotRequested)
                    // - Está Pending sin cuenta pendiente
                    // - Está Rejected PERO es un rechazo temporal (requirements.past_due, etc.)
                    CanRetryOnboarding = CalculateCanRetryOnboarding(expertProfile.StripeStatus, expertProfile.PendingStripeAccountId, rejectionReason),
                    RejectionReason = rejectionReason
                };

                return Ok(status);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to get expert status" });
            }
        }


        [HttpPost("sync-stripe-status")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> SyncStripeStatus()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return NotFound(new { message = "Expert profile not found" });
                }

                if (string.IsNullOrEmpty(expertProfile.StripeAccountId))
                {
                    return BadRequest(new { message = "No Stripe account found to sync" });
                }

                // Verificar el estado actual en Stripe
                var accountService = new AccountService();
                Account account;
                try
                {
                    account = await accountService.GetAsync(expertProfile.StripeAccountId);
                }
                catch (StripeException ex)
                {
                    // 🔧 Round 26 ONBOARD-IDEM-3: registrar el StripeError antes del 500 para
                    // poder triagear por código (rate limit, auth, account_invalid…).
                    await _loggingService.LogErrorAsync(
                        message: "Failed to retrieve Stripe account status",
                        details: $"AccountService.GetAsync threw for user {userId} (StripeAccountId {expertProfile?.StripeAccountId}). Code={ex.StripeError?.Code}, Type={ex.StripeError?.Type}, DeclineCode={ex.StripeError?.DeclineCode}, Message={ex.Message}",
                        userId: userId,
                        source: "SubscriptionController.SyncStripeStatus",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile?.Id,
                        additionalData: new {
                            ExceptionType = ex.GetType().Name,
                            StripeErrorCode = ex.StripeError?.Code,
                            StripeErrorType = ex.StripeError?.Type
                        });
                    return StatusCode(500, new {
                        message = "Failed to retrieve Stripe account status",
                        code = ex.StripeError?.Code,
                        type = ex.StripeError?.Type
                    });
                }

                var previousStatus = expertProfile.StripeStatus;
                var accountState = EvaluateStripeAccount(account);

                ApplyStripeAccountState(expertProfile, accountState, account.Id);

                await _context.SaveChangesAsync();

                // 🛡️ Round 28 MUD-BF: sincronizar datos legales DAC7 (DOB/IBAN/Address) al
                // snapshot del año actual. Non-blocking — si falla, el account.updated sigue.
                if (_dac7DataSync != null)
                {
                    await _dac7DataSync.SyncFromAccountAsync(account, expertProfile.Id);
                }

                if (previousStatus != accountState.Status)
                {
                    await NotifyStripeStatusTransitionAsync(
                        expertProfile,
                        previousStatus,
                        accountState,
                        "SubscriptionController.SyncStripeStatus");
                }

                var status = new StripeSyncStatusDto
                {
                    HasStripeAccount = !string.IsNullOrEmpty(expertProfile.StripeAccountId),
                    HasPendingOnboarding = !string.IsNullOrEmpty(expertProfile.PendingStripeAccountId),
                    OnboardingCompleted = expertProfile.OnboardingCompleted,
                    StripeStatus = expertProfile.StripeStatus.ToString(),
                    StripeStatusDetails = expertProfile.StripeStatusDetails,
                    StripeAccountId = expertProfile.StripeAccountId,
                    CanAccessStripe = !string.IsNullOrEmpty(expertProfile.StripeAccountId) && expertProfile.OnboardingCompleted,
                    StripeAccountStatus = new StripeAccountStatusDto
                    {
                        ChargesEnabled = account.ChargesEnabled,
                        PayoutsEnabled = account.PayoutsEnabled,
                        DetailsSubmitted = account.DetailsSubmitted
                    }
                };

                return Ok(status);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to sync Stripe status" });
            }
        }

        [HttpPost("restart-onboarding")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> RestartOnboarding()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return NotFound(new { message = "Expert profile not found" });
                }

                // ⚠️ BLOQUEAR SOLO si es un rechazo permanente; permitir reintentos si es temporal
                if (expertProfile.StripeStatus == StripeStatus.Rejected)
                {
                    // Obtener el motivo del rechazo desde Stripe
                    string disabledReason = null;
                    // Primero intentar obtener de Stripe directamente
                    if (!string.IsNullOrEmpty(expertProfile.StripeAccountId))
                    {
                        try
                        {
                            var accountServiceForCreate = new AccountService();
                            var accountForCreate = await accountServiceForCreate.GetAsync(expertProfile.StripeAccountId);
                            disabledReason = accountForCreate.Requirements?.DisabledReason;
                        }
                        catch (StripeException ex)
                        {
                            // P0-4: no tragar el error en silencio. Mantener flujo (la rama
                            // de fallback usa expertProfile.StripeStatusDetails) pero dejar traza.
                            await _loggingService.LogWarningAsync(
                                message: "Stripe error consultando estado de cuenta de experto (rejection/disabled reason)",
                                details: $"StripeException al recuperar Account.Requirements.DisabledReason. ExpertProfileId: {expertProfile?.Id}, StripeAccountId: {expertProfile?.StripeAccountId}, StripeCode: {ex.StripeError?.Code}, StripeType: {ex.StripeError?.Type}, Message: {ex.Message}",
                                source: "SubscriptionController." + nameof(RestartOnboarding),
                                relatedEntityType: "ExpertProfile",
                                relatedEntityId: expertProfile?.Id);
                        }
                    }
                    
                    // Si no se pudo obtener de Stripe, intentar extraer del StripeStatusDetails
                    if (string.IsNullOrEmpty(disabledReason) && !string.IsNullOrEmpty(expertProfile.StripeStatusDetails))
                    {
                        disabledReason = ExtractRejectionReasonFromDetails(expertProfile.StripeStatusDetails);
                        if (!string.IsNullOrEmpty(disabledReason))
                        {
                        }
                        else
                        {
                        }
                    }
                    else if (string.IsNullOrEmpty(disabledReason))
                    {
                    }
                    
                    // Si es un rechazo permanente, bloquear
                    if (IsPermanentRejection(disabledReason))
                    {
                        string rejectionInfo = "Tu cuenta de pagos fue rechazada por Stripe.";
                        if (!string.IsNullOrEmpty(expertProfile.StripeStatusDetails))
                        {
                            rejectionInfo = expertProfile.StripeStatusDetails;
                        }
                        
                        return BadRequest(new { 
                            message = "No se puede crear una nueva cuenta. " + rejectionInfo + " Por favor, contacta al soporte técnico para revisar tu situación.",
                            blocked = true,
                            reason = "account_permanently_rejected",
                            rejectionReason = disabledReason,
                            rejectionDetails = expertProfile.StripeStatusDetails
                        });
                    }
                    else
                    {
                        // Es un rechazo temporal (requirements.past_due, etc.), permitir reintentar
                        // Limpiar la cuenta rechazada y permitir crear una nueva
                        expertProfile.StripeAccountId = null;
                        expertProfile.PendingStripeAccountId = null;
                        expertProfile.StripeStatus = StripeStatus.NotRequested;
                        expertProfile.StripeStatusDetails = null;
                        expertProfile.OnboardingCompleted = false;
                        await _context.SaveChangesAsync();
                        // Continuar con el flujo normal
                    }
                }

                // Si ya tiene cuenta completada y NO está rechazada, crear login link en lugar de reiniciar
                if (!string.IsNullOrEmpty(expertProfile.StripeAccountId) && expertProfile.OnboardingCompleted)
                {
                    // 🔧 Round 26 ONBOARD-3: añadir Collect=eventually_due para alinear con los otros 5 AccountLink.
                    // 🔧 Round 26 LOGINLINK-2: isLoginLink=false — esto es un AccountLink (account_onboarding),
                    // no un LoginLink de Express Dashboard. La bandera honesta evita que el frontend lo
                    // interprete como dashboard. Para abrir Dashboard real existe /create-login-link.
                    var restartLinkOptions = new AccountLinkCreateOptions
                    {
                        Account = expertProfile.StripeAccountId,
                        RefreshUrl = $"{(_configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com")}/refresh-onboarding",
                        ReturnUrl = $"{(_configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com")}/complete-onboarding",
                        Type = "account_onboarding",
                        Collect = "eventually_due"
                    };

                    var restartLinkService = new AccountLinkService();

                    try
                    {
                        var restartAccountLink = await restartLinkService.CreateAsync(restartLinkOptions);
                        return Ok(new { url = restartAccountLink.Url, isLoginLink = false });
                    }
                    catch (StripeException ex)
                    {
                        // 🔧 Round 26 ONBOARD-IDEM-3: log detallado del StripeError antes del 500
                        // para poder triagear por código Stripe sin instrumentar de cero.
                        await _loggingService.LogErrorAsync(
                            message: "Failed to create Stripe account link for restart onboarding",
                            details: $"AccountLinkService.CreateAsync threw for user {userId} (StripeAccountId {expertProfile?.StripeAccountId}). Code={ex.StripeError?.Code}, Type={ex.StripeError?.Type}, DeclineCode={ex.StripeError?.DeclineCode}, Message={ex.Message}",
                            userId: userId,
                            source: "SubscriptionController.RestartOnboarding",
                            relatedEntityType: "ExpertProfile",
                            relatedEntityId: expertProfile?.Id,
                            additionalData: new {
                                ExceptionType = ex.GetType().Name,
                                StripeErrorCode = ex.StripeError?.Code,
                                StripeErrorType = ex.StripeError?.Type
                            });
                        return StatusCode(500, new {
                            message = "Failed to create Stripe account link",
                            code = ex.StripeError?.Code,
                            type = ex.StripeError?.Type
                        });
                    }
                }

                // Si no tiene cuenta pendiente, redirigir al endpoint de crear onboarding
                if (string.IsNullOrEmpty(expertProfile.PendingStripeAccountId))
                {
                    return await CreateExpertOnboarding();
                }

                // Si tiene cuenta pendiente, crear nuevo link de onboarding
                var pendingLinkOptions = new AccountLinkCreateOptions
                {
                    Account = expertProfile.PendingStripeAccountId,
                    RefreshUrl = $"{(_configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com")}/refresh-onboarding",
                    ReturnUrl = $"{(_configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com")}/complete-onboarding",
                    Type = "account_onboarding",
                    Collect = "eventually_due"
                };

                var pendingLinkService = new AccountLinkService();
                AccountLink pendingAccountLink;
                try
                {
                    pendingAccountLink = await pendingLinkService.CreateAsync(pendingLinkOptions);
                }
                catch (StripeException ex)
                {
                    // 🔧 Round 26 ONBOARD-IDEM-3: log con StripeError para observabilidad.
                    await _loggingService.LogErrorAsync(
                        message: "Failed to create new onboarding link (pending account)",
                        details: $"AccountLinkService.CreateAsync threw for user {userId} (PendingStripeAccountId {expertProfile?.PendingStripeAccountId}). Code={ex.StripeError?.Code}, Type={ex.StripeError?.Type}, DeclineCode={ex.StripeError?.DeclineCode}, Message={ex.Message}",
                        userId: userId,
                        source: "SubscriptionController.RestartOnboarding",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile?.Id,
                        additionalData: new {
                            ExceptionType = ex.GetType().Name,
                            StripeErrorCode = ex.StripeError?.Code,
                            StripeErrorType = ex.StripeError?.Type
                        });
                    return StatusCode(500, new {
                        message = "Failed to create new onboarding link",
                        code = ex.StripeError?.Code,
                        type = ex.StripeError?.Type
                    });
                }

                return Ok(new { url = pendingAccountLink.Url });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to restart onboarding" });
            }
        }

        // 🔧 FIX (#4): la funcionalidad "cargar saldo" fue ELIMINADA (el webhook ignora estas sesiones). Se
        // quita la ruta HTTP y se marca [NonAction] para que NADIE pueda iniciar un cobro que se auto-captura
        // (mode=payment sin captura manual) SIN contrapartida ni registro en BD. El frontend no la invoca.
        //
        // 🛡️ Round 28 MUD-AN: defensa-en-profundidad. El [NonAction] previo confiaba SOLO en que nadie
        // quitase el atributo. Si alguien lo eliminara (refactor, merge accidental, IDE auto-fix), el
        // método tenía cap €1000 pinned EUR — vector de fraud explotable. Ahora el método aborta con
        // log critical + throw aunque alguien quite el [NonAction] o lo invoque vía reflection.
        [NonAction]
        public async Task<IActionResult> LoadMoney([FromBody] LoadMoneyDto request)
        {
            // 🛡️ MUD-AN + MUD-AW: kill-switch defensivo. La ruta está deshabilitada
            // permanentemente. Si por accidente alguien quitase [NonAction] o invocase por
            // reflexión, esto genera alerta admin + 410 Gone en lugar de crear un PI sin
            // captura manual.
            //
            // MUD-AW: invocación vía reflexión sin HttpContext hace `User` accede a
            // ControllerBase.User → HttpContext.User → NRE. Sin esta protección el log
            // critical NUNCA se emitía (la NRE escapaba antes), perdiendo el threat model
            // que el fix pretendía cubrir. Try/catch garantiza log + 410.
            int? killUserId = null;
            try
            {
                var killUserIdClaim = HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (int.TryParse(killUserIdClaim, out int parsedKill))
                {
                    killUserId = parsedKill;
                }
            }
            catch { /* invocación sin HttpContext — log debe seguir siendo emitido */ }
            try
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL MUD-AN: deprecated LoadMoney endpoint was invoked",
                    details: $"User {killUserId?.ToString() ?? "(no-http-context)"} tried to invoke the disabled LoadMoney endpoint. Either [NonAction] was removed by mistake or the method was called via reflection. This endpoint creates auto-captured PIs without DB record — fraud vector. Action: verify route table + redeploy.",
                    userId: killUserId,
                    source: "SubscriptionController.LoadMoney.MUD-AN.KillSwitch",
                    relatedEntityType: "Payment",
                    additionalData: new { Amount = request?.Amount, Endpoint = "LoadMoney" });
            }
            catch { /* nunca dejar que un fallo de logging deje pasar el body legacy */ }
            return StatusCode(410, new { message = "This endpoint has been permanently disabled. Use /api/Subscription/load-money-service instead." });

#pragma warning disable CS0162 // Unreachable code detected — código histórico bajo `return` previo
            // ⚠️ Código antiguo conservado bajo guard imposible para referencia histórica.
            if (true) { throw new InvalidOperationException("MUD-AN: LoadMoney is disabled"); }
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                if (request.Amount <= 0 || request.Amount > 1000)
                {
                    return BadRequest(new { message = "Amount must be between 0.01 and 1000" });
                }

                // P2-2: pre-check de Users (IsBlocked) ANTES de crear la sesión Stripe.
                // El FOR UPDATE + commit inmediato anterior NO bloqueaba nada útil:
                // no había mutación posterior sobre Users dentro de esta ruta, así que
                // el lock se liberaba sin proteger nada. Se reduce a una lectura normal.
                var user = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == userId);
                
                if (user == null)
                {
                    return NotFound(new { message = "User not found" });
                }

                // ✅ VALIDACIÓN: Usuario bloqueado no puede realizar pagos
                if (user.IsBlocked)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Blocked user attempted to load money",
                        details: $"Blocked user {user.Id} ({user.Email}) attempted to load money",
                        userId: user.Id,
                        source: "SubscriptionController.LoadMoney",
                        relatedEntityType: "User",
                        relatedEntityId: user.Id
                    );
                    return Unauthorized(new { message = "User account is blocked" });
                }

                var domain = _configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com";
                var options = new SessionCreateOptions
                {
                    PaymentMethodTypes = new List<string> { "card" },
                    LineItems = new List<SessionLineItemOptions>
                    {
                        new SessionLineItemOptions
                        {
                            PriceData = new SessionLineItemPriceDataOptions
                            {
                                Currency = "eur",
                                UnitAmount = checked((long)Math.Round(request.Amount * 100)),
                                ProductData = new SessionLineItemPriceDataProductDataOptions
                                {
                                    Name = "Load Money"
                                }
                                // ✅ STRIPE TAX (Docs 2026): NO especificar TaxBehavior para que Stripe use el default automático configurado en Dashboard
                                // Si el Dashboard está en "Automático", Stripe aplicará según moneda: USD/CAD → exclusive, resto → inclusive
                                // Si se especifica, solo se permiten: "inclusive" o "exclusive" (no "unspecified" ni "automatic")
                            },
                            Quantity = 1
                        }
                    },
                    // ✅ STRIPE TAX: Habilitar cálculo automático de tax
                    AutomaticTax = new SessionAutomaticTaxOptions
                    {
                        Enabled = true,
                        Liability = new SessionAutomaticTaxLiabilityOptions { Type = "self" } // 🔧 FIX: plataforma = responsable fiscal (MoR)
                    },
                    TaxIdCollection = new SessionTaxIdCollectionOptions { Enabled = true }, // 🔧 FIX: recoge NIF/VAT -> reverse charge B2B
                    BillingAddressCollection = "required", // 🔧 FIX: direccion fiable para AutomaticTax correcto por pais
                    Mode = "payment",
                    SuccessUrl = $"{domain}/success?session_id={{CHECKOUT_SESSION_ID}}",
                    CancelUrl = domain + "/cancel",
                    CustomerEmail = User.FindFirst(ClaimTypes.Email)?.Value,
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() },
                        // 🛡️ A9 FIX: InvariantCulture para separador "." universal (evita 1,5 vs 1.5 en es-ES)
                        // → si webhook handler parsea con culture distinta, no rompe el round-trip
                        { "amount", request.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                    }
                };

                var service = new SessionService();
                Session session;
                try
                {
                    var idempotencyKey = $"loadmoney-{userId}-{request.Amount:F2}-{DateTime.UtcNow:yyyyMMddHHmm}";
                    session = await service.CreateAsync(options, new RequestOptions { IdempotencyKey = idempotencyKey });
                }
                catch (StripeException e)
                {
                    // 🚨 LOG CRÍTICO: Error de Stripe al crear sesión de pago (afecta dinero)
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Stripe error creating checkout session for load money",
                        details: $"Failed to create Stripe checkout session for user {userId} to load {request.Amount}€. Stripe Error: {e.Message}, Type: {e.StripeError?.Type}, Code: {e.StripeError?.Code}",
                        userId: userId,
                        source: "SubscriptionController.LoadMoney",
                        relatedEntityType: "Payment",
                        additionalData: new { 
                            Action = "LoadMoney",
                            Amount = request.Amount,
                            UserId = userId,
                            StripeError = e.Message,
                            StripeErrorType = e.StripeError?.Type,
                            StripeErrorCode = e.StripeError?.Code
                        }
                    );
                    
                    return StatusCode(500, new { message = e.Message });
                }

                return Ok(new { url = session.Url });
            }
            catch (Exception ex)
            {
                // ⚠️ LOG WARNING: Error general al crear sesión de carga de dinero (el error de Stripe ya se loguea como CRITICAL arriba)
                var userIdForLog = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int userIdValue) ? userIdValue : (int?)null;
                await _loggingService.LogWarningAsync(
                    message: "Error creating load money session",
                    details: $"Failed to create load money session: {ex.Message}. Note: Stripe-specific errors are logged separately as CRITICAL.",
                    userId: userIdForLog,
                    source: "SubscriptionController.LoadMoney",
                    relatedEntityType: "Payment",
                    additionalData: new { 
                        Action = "LoadMoney",
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                
                return StatusCode(500, new { message = "Failed to create load money session" });
            }
#pragma warning restore CS0162
        }


        [HttpPost("load-money-service")]
        public async Task<IActionResult> LoadMoneyService([FromBody] LoadMoneyServiceDto request)
        {
            // 🚨 VALIDACIÓN DE ENTRADA
            if (request == null)
            {
                return BadRequest(new { message = "Request cannot be null" });
            }

            if (request.ServiceId <= 0)
            {
                return BadRequest(new { message = "Invalid service ID" });
            }

            if (request.Amount <= 0)
            {
                return BadRequest(new { message = "Amount must be greater than 0" });
            }
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var service = await _context.SearchServices
                    .Include(ss => ss.ExpertProfile)
                    .FirstOrDefaultAsync(ss => ss.Id == request.ServiceId);
                if (service == null)
                {
                    return NotFound(new { message = "Service not found" });
                }

                if (service.Price != request.Amount || service.Price <= 0 || service.Price > 1000)
                {
                    return BadRequest(new { message = "Service price mismatch or invalid amount (must be between 0.01 and 1000.00)" });
                }

                // 🚨 FIX C9: servicio SIN experto (FK OnDelete SetNull) → sin destino de payout. Rechazar
                // ANTES de crear la Checkout Session. (LoadMoneyService ni siquiera validaba el experto.)
                if (service.ExpertProfile == null)
                {
                    return BadRequest(new { message = "Este servicio no está disponible para contratar" });
                }

                // 🛡️ N8 FIX: servicio desactivado entre catálogo y checkout init → cliente cobrado por
                // servicio inactivo. Bloqueamos en el momento de crear la Stripe Session.
                if (!service.IsActive)
                {
                    return BadRequest(new { message = "Este servicio no está activo actualmente" });
                }

                // 🛡️ N7 FIX: validar centralizado que el experto puede recibir pagos (StripeStatus=Approved,
                // OnboardingCompleted, capability transfers). Sin esto, LoadMoneyService dejaba pasar
                // checkouts hacia expertos rechazados/pending → validación tardía post-capture en webhook,
                // que requiere refund (costoso + UX malo).
                var n7Validation = await _stripeValidationService.ValidateExpertCanReceivePaymentsAsync(
                    service.ExpertProfile, "contratar servicio");
                if (!n7Validation.IsValid)
                {
                    return BadRequest(new
                    {
                        message = n7Validation.ErrorMessage,
                        stripeStatus = n7Validation.StripeStatus,
                        requiresStripeSetup = n7Validation.RequiresStripeSetup,
                        canRetry = n7Validation.CanRetry
                    });
                }

                // 🚨 VALIDACIÓN CRÍTICA: Verificar que el experto no se contrate a sí mismo
                // ✅ IMPORTANTE: Esta validación DEBE hacerse ANTES de crear el checkout session
                // para evitar perder comisiones de Stripe al hacer refunds
                if (service.ExpertProfile != null && service.ExpertProfile.UserId == userId)
                {
                    return BadRequest(new { message = "No puedes contratarte a ti mismo como experto" });
                }

                // P2-2: pre-check de Users (IsBlocked) ANTES de crear la sesión Stripe.
                // El FOR UPDATE + commit inmediato anterior NO protegía mutación alguna
                // dentro de esta ruta (la creación de SearchHire se hace en el webhook).
                // Se reduce a una lectura normal.
                var user = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == userId);
                
                if (user == null)
                {
                    return NotFound(new { message = "User not found" });
                }

                // ✅ VALIDACIÓN: Usuario bloqueado no puede contratar servicios
                if (user.IsBlocked)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Blocked user attempted to pay for service",
                        details: $"Blocked user {user.Id} ({user.Email}) attempted to pay for service {request.ServiceId}",
                        userId: user.Id,
                        source: "SubscriptionController.LoadMoneyService",
                        relatedEntityType: "User",
                        relatedEntityId: user.Id
                    );
                    return Unauthorized(new { message = "User account is blocked" });
                }

                // 🚨 VALIDACIÓN CRÍTICA: Verificar teléfono antes del pago
                // ✅ IMPORTANTE: Esta validación DEBE hacerse ANTES de crear el checkout session


                // 💳 NO SE NECESITA VERIFICAR BALANCE - SIEMPRE SE PAGA CON STRIPE

                // 🚨 PROTECCIÓN CONTRA CONTRATACIONES DUPLICADAS
                var pendingStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Pending.ToStringValue());
                var awaitingStatusId = await GetStatusIdByValueAsync(SearchHireStatus.AwaitingClientDecision.ToStringValue());
                var disputedStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());

                var existingHire = await _context.SearchHires
                    .FirstOrDefaultAsync(sh => sh.ClientId == userId &&
                                              sh.SearchServiceId == service.Id &&
                                              (sh.StatusId == pendingStatusId ||
                                               sh.StatusId == awaitingStatusId ||
                                               sh.StatusId == disputedStatusId));

                if (existingHire != null)
                {
                    return BadRequest(new { message = "Ya tienes una contratación activa para este servicio" });
                }

                // 🛡️ N17 FIX: límite global de hires Pending por cliente — evita DoS / spam (el
                // existingHire de arriba protege solo por (cliente,servicio), un atacante puede
                // saltarlo creando N hires a servicios distintos del mismo experto). Límite arbitrario 20.
                const int N17_MAX_PENDING_HIRES_PER_CLIENT = 20;
                var n17PendingCount = await _context.SearchHires
                    .AsNoTracking()
                    .CountAsync(sh => sh.ClientId == userId && sh.StatusId == pendingStatusId);
                if (n17PendingCount >= N17_MAX_PENDING_HIRES_PER_CLIENT)
                {
                    return BadRequest(new
                    {
                        message = $"Has alcanzado el límite de {N17_MAX_PENDING_HIRES_PER_CLIENT} contrataciones pendientes simultáneas. Completa o cancela algunas antes de crear nuevas."
                    });
                }

                // 💳 SIEMPRE PAGAR CON STRIPE - NO USAR SALDO INTERNO
                var amountToCharge = service.Price;

                var domain = _configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com";

                // 🛡️ Round 28 MUD-9: replicar bloque MUD-6 también aquí (LoadMoneyService).
                // Antes el endpoint leía service.Currency sin verificar contra Stripe acct
                // → si BD divergía (legacy/mudanza), currency_mismatch al captura.
                var lmsCheckoutCurrency = (service.Currency ?? "EUR").ToLowerInvariant();
                if (!string.IsNullOrEmpty(service.ExpertProfile?.StripeAccountId))
                {
                    try
                    {
                        var lmsStripeAcctService = new Stripe.AccountService();
                        var lmsStripeAcct = await lmsStripeAcctService.GetAsync(service.ExpertProfile.StripeAccountId);
                        var lmsAcctDefault = lmsStripeAcct?.DefaultCurrency;
                        if (!string.IsNullOrEmpty(lmsAcctDefault) && !string.Equals(lmsAcctDefault, lmsCheckoutCurrency, StringComparison.OrdinalIgnoreCase))
                        {
                            await _loggingService.LogWarningAsync(
                                message: "LoadMoneyService MUD-9: currency mismatch overriden to Stripe acct currency",
                                details: $"Service {service.Id}: BD says {lmsCheckoutCurrency.ToUpperInvariant()}, Stripe acct {service.ExpertProfile.StripeAccountId} default_currency is {lmsAcctDefault.ToUpperInvariant()}. Overriding to {lmsAcctDefault.ToUpperInvariant()}.",
                                userId: userId,
                                source: "SubscriptionController.LoadMoneyService.MUD9",
                                relatedEntityType: "SearchService",
                                relatedEntityId: service.Id);
                            lmsCheckoutCurrency = lmsAcctDefault.Trim().ToLowerInvariant();
                        }
                    }
                    catch (Stripe.StripeException stripeReadEx) when (stripeReadEx.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return BadRequest(new
                        {
                            message = "Este experto no puede recibir cobros en este momento (cuenta de pagos no disponible).",
                            errorCode = "EXPERT_STRIPE_ACCOUNT_NOT_FOUND"
                        });
                    }
                    catch (Exception)
                    {
                        // Best-effort: si Stripe no responde, usar BD.
                    }
                }

                var options = new SessionCreateOptions
                {
                    PaymentMethodTypes = new List<string> { "card" },
                    LineItems = new List<SessionLineItemOptions>
                    {
                        new SessionLineItemOptions
                        {
                            PriceData = new SessionLineItemPriceDataOptions
                            {
                                // 🌍 Round 25 FIX (M4-1): no hardcodear "eur" — Stripe rechaza el checkout
                                // si el currency del PriceData no coincide con el del Stripe Account del experto
                                // (Connect Express). Usamos el SearchService.Currency (ISO 4217 en mayúsculas)
                                // y lo pasamos a Stripe en minúsculas como exige la API.
                                // 🛡️ Round 28 Sprint 3: defensa NPE — si Currency es null en una fila legacy,
                                // caer a "eur" en vez de NullReferenceException.
                                // 🛡️ Round 28 MUD-9: usar checkoutCurrency validado contra Stripe acct.
                                Currency = lmsCheckoutCurrency,
                                UnitAmount = checked((long)Math.Round(amountToCharge * 100)),
                                ProductData = new SessionLineItemPriceDataProductDataOptions
                                {
                                    Name = $"Payment for Service {service.Id}"
                                }
                                // ✅ STRIPE TAX (Docs 2026): NO especificar TaxBehavior para que Stripe use el default automático configurado en Dashboard
                                // Si el Dashboard está en "Automático", Stripe aplicará según moneda: USD/CAD → exclusive, resto → inclusive
                                // Si se especifica, solo se permiten: "inclusive" o "exclusive" (no "unspecified" ni "automatic")
                            },
                            Quantity = 1
                        }
                    },
                    // ✅ STRIPE TAX: Habilitar cálculo automático de tax basado en ubicación del comprador
                    AutomaticTax = new SessionAutomaticTaxOptions
                    {
                        Enabled = true, // Habilita cálculo auto basado en IP, billing/shipping address
                        Liability = new SessionAutomaticTaxLiabilityOptions { Type = "self" } // 🔧 FIX: plataforma = responsable fiscal (MoR)
                    },
                    TaxIdCollection = new SessionTaxIdCollectionOptions { Enabled = true }, // 🔧 FIX: recoge NIF/VAT -> reverse charge B2B
                    BillingAddressCollection = "required", // 🔧 FIX: direccion fiable para AutomaticTax correcto por pais
                    Mode = "payment",
                    SuccessUrl = $"{domain}/success?session_id={{CHECKOUT_SESSION_ID}}&userId={userId}",
                    CancelUrl = $"{domain}/cancel",
                    CustomerEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown@example.com",
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() },
                        { "serviceId", request.ServiceId.ToString() },
                        // 🛡️ A9 FIX: InvariantCulture para consistencia hash idempotency + parse webhook
                        { "amount", amountToCharge.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                        { "pendingHire", "true" }
                    },
                    // ✅ CAPTURA MANUAL: Autoriza el pago pero no lo captura hasta validar todo en el webhook
                    // Esto evita perder comisiones si algo falla después del pago
                    PaymentIntentData = new SessionPaymentIntentDataOptions
                    {
                        CaptureMethod = "manual"
                    }
                };

                var stripeService = new SessionService();
                Session session;
                try
                {
                    // 🔧 FIX #6 + regresión: clave determinista por (usuario,servicio,importe). El body aquí solo
                    // varía con el precio del servicio, así que incluir el importe basta: mismo importe => misma
                    // clave (deduplica doble-clic), importe distinto => clave distinta (no rompe con
                    // idempotency_error). El guard de contratación activa evita el re-cobro del mismo servicio.
                    var idempotencyKey = IdempotencyKeyHelper.ForCheckout(
                        userId, request.ServiceId,
                        amountToCharge.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    session = await stripeService.CreateAsync(options, new RequestOptions { IdempotencyKey = idempotencyKey });
                }
                catch (StripeException ex)
                {
                    // 🚨 LOG CRÍTICO: Error de Stripe al crear sesión de pago para servicio (afecta dinero)
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Stripe error creating checkout session for service payment",
                        details: $"Failed to create Stripe checkout session for user {userId} to pay for service {request.ServiceId}. Stripe Error: {ex.Message}, Type: {ex.StripeError?.Type}, Code: {ex.StripeError?.Code}",
                        userId: userId,
                        source: "SubscriptionController.LoadMoneyService",
                        relatedEntityType: "Payment",
                        relatedEntityId: request.ServiceId,
                        additionalData: new { 
                            Action = "LoadMoneyService",
                            ServiceId = request.ServiceId,
                            UserId = userId,
                            StripeError = ex.Message,
                            StripeErrorType = ex.StripeError?.Type,
                            StripeErrorCode = ex.StripeError?.Code
                        }
                    );
                    
                    return StatusCode(500, new { message = ex.Message });
                }

                return Ok(new { url = session.Url });
            }
            catch (Exception ex)
            {
                // 🚨 LOG CRÍTICO: Error general al crear sesión de pago para servicio
                var userIdForLog = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int userIdValue) ? userIdValue : (int?)null;
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Error creating load money session for service",
                    details: $"Failed to create load money session for service {request.ServiceId}: {ex.Message}",
                    userId: userIdForLog,
                    source: "SubscriptionController.LoadMoneyService",
                    relatedEntityType: "Payment",
                    relatedEntityId: request.ServiceId,
                    additionalData: new { 
                        Action = "LoadMoneyService",
                        ServiceId = request.ServiceId,
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                
                return StatusCode(500, new { message = "Failed to create load money session" });
            }
        }

        [HttpPost("webhook")]
        [AllowAnonymous]
        [DisableRateLimiting] // 🔁 webhook autenticado por firma de Stripe; NO limitar — un 429 hace que Stripe reintente/encole y en una ráfaga (todas las entregas comparten la IP del proxy de Render) se perderían eventos
        public async Task<IActionResult> HandleStripeWebhook()
        {
            // ✅ LOG DIAGNÓSTICO: Inicio del webhook
            var webhookStartTime = DateTime.UtcNow;
            var originalColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                var separator = new string('=', 80);
                Console.WriteLine($"\n{separator}");
                Console.WriteLine($"📥 [WEBHOOK] Iniciando procesamiento de webhook Connect");
                Console.WriteLine($"{separator}");
                Console.WriteLine($"Timestamp: {webhookStartTime:yyyy-MM-dd HH:mm:ss.fff}");
                Console.ForegroundColor = originalColor;
            }
            catch { }
            
            // ✅ SEGURIDAD CRÍTICA: Habilitar buffering para permitir múltiples lecturas del body
            Request.EnableBuffering();
            var json = await new StreamReader(Request.Body).ReadToEndAsync();
            Request.Body.Position = 0; // ✅ CORRECCIÓN: Reposicionar DESPUÉS de leer
            // ✅ SEGURIDAD: Convertir StringValues a string (puede venir como array)
            var signatureHeader = Request.Headers["Stripe-Signature"].ToString();
            string? currentEventId = null;
            string? currentEventType = null;
            string? currentAccountId = null;
            bool eventMarkedProcessing = false;
            try
            {
                // ✅ SEGURIDAD CRÍTICA: Validar signature antes de procesar
                // EventUtility.ConstructEvent valida la signature y lanza StripeException si es inválida
                // Esto previene ataques de replay e inyección de eventos falsos
                
                // ✅ Actualizar Stripe API Key antes de usar (por si cambió el modo)
                UpdateStripeApiKey();
                
                // 🔍 DIAGNÓSTICO: Determinar qué webhook secret usar
                string? webhookSecretToUse = WebhookSecret;
                
                // 🔧 FIX (hallazgo D): SIN fallback al GeneralWebhookSecret. Son secretos de firma de
                // ENDPOINTS DISTINTOS de Stripe; verificar un evento de Connect con el secret general hace
                // fallar la firma (400) y, tras los reintentos, Stripe descarta el evento (p.ej. account.updated)
                // → estado de cuentas conectadas desincronizado, además enmascarando un fallo de config como
                // "firma inválida / posible ataque". Si falta el secret de Connect, caemos directos al log
                // CRÍTICO + 400 de abajo (accionable y con alerta a admin).
                
                if (string.IsNullOrEmpty(webhookSecretToUse))
                {
                    // 🚨 LOG CRÍTICO: Webhook secret no configurado
                    var eventType = GetEventTypeFromJson(json);
                    var accountId = GetAccountIdFromJson(json);
                    
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Webhook secret not configured for Connect events",
                        details: $"Stripe:WebhookSecret is empty for /webhook endpoint. Event type: {eventType}, Account: {accountId}. " +
                                $"This is the CONNECT endpoint secret (from Stripe Dashboard → Connect → Webhooks → Your Connect endpoint → Signing secret). " +
                                $"If using general platform webhooks (payment_intent.*, charge.*), configure Stripe:GeneralWebhookSecret SEPARATELY for the /webhook-general endpoint. " +
                                $"INSTRUCCIONES: 1) Ve al Dashboard de Stripe → Developers → Webhooks → endpoint de CONNECT (NO el general). " +
                                $"2) Copia el secret específico de Connect (whsec_...). " +
                                $"3) Configúralo con: dotnet user-secrets set \"Stripe:WebhookSecret\" \"whsec_...\" " +
                                $"O como variable de entorno: STRIPE_WEBHOOK_SECRET=whsec_...",
                        source: "SubscriptionController.HandleStripeWebhook",
                        relatedEntityType: "Webhook",
                        relatedEntityId: null,
                        additionalData: new {
                            HasWebhookSecret = !string.IsNullOrEmpty(WebhookSecret),
                            HasGeneralWebhookSecret = !string.IsNullOrEmpty(GeneralWebhookSecret),
                            EventType = eventType,
                            AccountId = accountId,
                            HasSignature = !string.IsNullOrEmpty(signatureHeader),
                            Instructions = "Configure Stripe:WebhookSecret (CONNECT endpoint) — distinct from Stripe:GeneralWebhookSecret (platform events)"
                        }
                    );
                    return BadRequest(new {
                        error = "Webhook secret not configured (CONNECT endpoint)",
                        instructions = "Configure Stripe:WebhookSecret (CONNECT endpoint signing secret) — this is distinct from Stripe:GeneralWebhookSecret (which is for /webhook-general). Source: Stripe Dashboard → Connect → Webhooks → Your Connect endpoint → Signing secret",
                        eventType = eventType,
                        accountId = accountId
                    });
                }
                
                if (string.IsNullOrEmpty(signatureHeader))
                {
                    await _loggingService.LogWarningAsync(
                        message: "Stripe signature header missing in webhook request",
                        details: "Stripe-Signature header is missing from the webhook request",
                        userId: null,
                        source: "SubscriptionController.HandleStripeWebhook",
                        relatedEntityType: "Webhook"
                    );
                    return BadRequest(new { error = "Stripe signature header missing" });
                }
                
                // ✅ STRIPE API VERSION: Permitir diferentes versiones de API con advertencia
                // El webhook endpoint en Stripe Dashboard puede estar configurado con una versión diferente
                // a la que espera el SDK. Esto es seguro siempre que validemos la signature correctamente.
                var stripeEvent = EventUtility.ConstructEvent(
                    json, 
                    signatureHeader, 
                    webhookSecretToUse,
                    throwOnApiVersionMismatch: false // ⚠️ Permite procesar eventos de diferentes versiones de API
                );
                
                // ⚠️ ADVERTENCIA: Si hay mismatch de versión, loguear para actualizar el webhook endpoint
                if (stripeEvent.ApiVersion != null)
                {
                    var expectedVersion = "2025-11-17.clover"; // Versión esperada por Stripe.NET 50.0.0
                    if (stripeEvent.ApiVersion != expectedVersion)
                    {
                        var warningMessage = $"⚠️ Stripe webhook API version mismatch: Received '{stripeEvent.ApiVersion}', but SDK expects '{expectedVersion}'. " +
                                           $"Consider updating the webhook endpoint in Stripe Dashboard to use API version '{expectedVersion}' for better compatibility.";
                        
                        // ✅ Mostrar en consola para visibilidad inmediata (en amarillo porque es warning)
                        var separatorApiVersionConnect3 = new string('=', 80);
                        var originalColorApiVersionConnect3 = Console.ForegroundColor;
                        try
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"\n{separatorApiVersionConnect3}");
                            Console.WriteLine($"⚠️ [STRIPE WEBHOOK] API Version Mismatch");
                            Console.WriteLine($"{separatorApiVersionConnect3}");
                            Console.WriteLine($"Received: {stripeEvent.ApiVersion}");
                            Console.WriteLine($"Expected: {expectedVersion}");
                            Console.WriteLine($"Event Type: {stripeEvent.Type}");
                            Console.WriteLine($"Event ID: {stripeEvent.Id}");
                            Console.WriteLine($"Recommendation: Update webhook endpoint in Stripe Dashboard to use API version '{expectedVersion}'");
                            Console.WriteLine($"{separatorApiVersionConnect3}\n");
                            Console.ForegroundColor = originalColorApiVersionConnect3;
                        }
                        catch
                        {
                            Console.WriteLine($"\n{separatorApiVersionConnect3}");
                            Console.WriteLine($"⚠️ [STRIPE WEBHOOK] API Version Mismatch");
                            Console.WriteLine($"{separatorApiVersionConnect3}");
                            Console.WriteLine($"Received: {stripeEvent.ApiVersion}");
                            Console.WriteLine($"Expected: {expectedVersion}");
                            Console.WriteLine($"Event Type: {stripeEvent.Type}");
                            Console.WriteLine($"Event ID: {stripeEvent.Id}");
                            Console.WriteLine($"Recommendation: Update webhook endpoint in Stripe Dashboard to use API version '{expectedVersion}'");
                            Console.WriteLine($"{separatorApiVersionConnect3}\n");
                        }
                        
                        await _loggingService.LogWarningAsync(
                            message: "Stripe webhook API version mismatch",
                            details: warningMessage,
                            userId: null,
                            source: "SubscriptionController.HandleStripeWebhook",
                            relatedEntityType: "Webhook",
                            relatedEntityId: null
                        );
                    }
                }
                
                currentEventId = stripeEvent.Id;
                currentEventType = stripeEvent.Type;
                currentAccountId = stripeEvent.Account;

                // 🔒 IDEMPOTENCIA ATÓMICA: reclamar el evento (insert-first con índice único en EventId).
                if (!await TryBeginProcessingEventAsync(stripeEvent.Id, stripeEvent.Type, stripeEvent.Account))
                {
                    return Ok(new { message = "Event already processed" });
                }
                eventMarkedProcessing = true;

                // ✅ LOG DIAGNÓSTICO: Evento recibido
                var originalColor1 = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    var separator1 = new string('=', 80);
                    Console.WriteLine($"\n{separator1}");
                    Console.WriteLine($"📨 [WEBHOOK] Evento recibido");
                    Console.WriteLine($"{separator1}");
                    Console.WriteLine($"EventId: {stripeEvent.Id}");
                    Console.WriteLine($"EventType: {stripeEvent.Type}");
                    Console.WriteLine($"AccountId: {stripeEvent.Account ?? "N/A"}");
                    Console.WriteLine($"ApiVersion: {stripeEvent.ApiVersion ?? "N/A"}");
                    Console.WriteLine($"Created: {stripeEvent.Created:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"{separator1}\n");
                    Console.ForegroundColor = originalColor1;
                }
                catch { }
                
                // (La idempotencia ya se reclamó de forma atómica más arriba con TryBeginProcessingEventAsync)

                switch (stripeEvent.Type)
                {
                    // Los eventos de pago se manejan en el webhook general

                    case "account.application.authorized":
                        // Este evento indica que el usuario autorizó la aplicación (OAuth)
                        // Solo actualizar el ID, pero NO marcar como aprobado hasta que llegue account.updated
                        var authorizedApp = stripeEvent.Data.Object as Application;
                        if (authorizedApp != null)
                        {
                            // ✅ CORRECCIÓN: Solo actualizar PendingStripeAccountId → StripeAccountId
                            // NO cambiar el estado, esperar a account.updated para verificación real
                            var authorizedExpertProfile = await _context.ExpertProfiles
                                .FirstOrDefaultAsync(ep => ep.PendingStripeAccountId == stripeEvent.Account);
                            
                            if (authorizedExpertProfile != null)
                            {
                                authorizedExpertProfile.StripeAccountId = stripeEvent.Account;
                                authorizedExpertProfile.PendingStripeAccountId = null;
                                // ✅ IMPORTANTE: NO cambiar StripeStatus aquí, mantener como Pending
                                // El estado se actualizará en account.updated cuando Stripe realmente apruebe
                                await _context.SaveChangesAsync();
                            }
                        }
                        break;

                    case "account.application.deauthorized":
                        var deauthorizedApp = stripeEvent.Data.Object as Application;
                        var accountId = stripeEvent.Account;

                        if (string.IsNullOrEmpty(accountId))
                        {
                            await _loggingService.LogWarningAsync(
                                message: "Stripe deauthorization event without account id",
                                details: $"event_id={stripeEvent.Id}, application_id={deauthorizedApp?.Id ?? "n/a"}",
                                userId: null,
                                source: "SubscriptionController.account.application.deauthorized",
                                relatedEntityType: "StripeAccount",
                                relatedEntityId: null);
                            break;
                        }

                        var deauthorizedExpertProfile = await _context.ExpertProfiles
                            .FirstOrDefaultAsync(ep => ep.StripeAccountId == accountId || ep.PendingStripeAccountId == accountId);

                        if (deauthorizedExpertProfile == null)
                        {
                            await _loggingService.LogWarningAsync(
                                message: "Stripe account deauthorized without matching expert profile",
                                details: $"account_id={accountId}, application_id={deauthorizedApp?.Id ?? "n/a"}",
                                userId: null,
                                source: "SubscriptionController.account.application.deauthorized",
                                relatedEntityType: "StripeAccount",
                                relatedEntityId: null);
                            break;
                        }

                        var previousStatus = deauthorizedExpertProfile.StripeStatus;
                        var deauthorizedState = new StripeAccountState
                        {
                            Status = StripeStatus.Deauthorized,
                            OnboardingCompleted = false,
                            StatusDetails = $"{GetStatusMessage(StripeStatus.Deauthorized)} Stripe desconectó tu cuenta el {DateTime.UtcNow:yyyy-MM-dd}."
                        };

                        {
                            // 🛡️ Round 28 TX-1: envuelto en CreateExecutionStrategy().ExecuteAsync.
                            // Antes: BeginTransactionAsync sin strategy chocaba con EnableRetryOnFailure
                            // (NpgsqlRetryingExecutionStrategy) → InvalidOperationException en SaveChangesAsync
                            // al disparar Stripe account.application.deauthorized tras AccountDeletion.
                            // El comentario antiguo sobre "PgBouncer no admite savepoints" YA NO APLICA —
                            // ahora usamos Render PostgreSQL nativo (ver Program.cs:746).
                            var deauthStrategy = _context.Database.CreateExecutionStrategy();
                            await deauthStrategy.ExecuteAsync(async () =>
                            {
                                await using var deauthTransaction = await _context.Database.BeginTransactionAsync();
                                try
                                {
                                    ApplyStripeAccountState(deauthorizedExpertProfile, deauthorizedState);

                                    // Stripe recomienda desvincular por completo la cuenta
                                    deauthorizedExpertProfile.StripeAccountId = null;
                                    deauthorizedExpertProfile.PendingStripeAccountId = null;

                                    await _context.SaveChangesAsync();
                                    await deauthTransaction.CommitAsync();
                                }
                                catch
                                {
                                    try { await deauthTransaction.RollbackAsync(); } catch { }
                                    throw;
                                }
                            });
                        }

                        var deauthReason = $"Stripe desconectó la cuenta (application={deauthorizedApp?.Id ?? "n/a"})";
                        await HandleAccountDeauthorization(deauthorizedExpertProfile.UserId, deauthReason);

                        if (previousStatus != deauthorizedState.Status)
                        {
                            await NotifyStripeStatusTransitionAsync(
                                deauthorizedExpertProfile,
                                previousStatus,
                                deauthorizedState,
                                "SubscriptionController.account.application.deauthorized");
                        }

                        break;

                    case "account.updated":
                        // ✅ LOG DIAGNÓSTICO: Inicio de procesamiento account.updated
                        var originalColor2 = Console.ForegroundColor;
                        try
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            var separator2 = new string('=', 80);
                            Console.WriteLine($"\n{separator2}");
                            Console.WriteLine($"🔄 [WEBHOOK] Procesando account.updated");
                            Console.WriteLine($"{separator2}");
                            Console.WriteLine($"EventId: {stripeEvent.Id}");
                            Console.WriteLine($"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                            Console.ForegroundColor = originalColor2;
                        }
                        catch { }
                        
                        var account = stripeEvent.Data.Object as Account;
                        if (account == null)
                        {
                            // ✅ LOG ERROR: Account es null
                            await _loggingService.LogErrorAsync(
                                message: "account.updated: Account object is null",
                                details: $"EventId: {stripeEvent.Id}, EventType: {stripeEvent.Type}",
                                source: "SubscriptionController.account.updated",
                                relatedEntityType: "Webhook");
                            break;
                        }

                        // ✅ LOG DIAGNÓSTICO: Account recibido
                        try
                        {
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"AccountId: {account.Id}");
                            Console.WriteLine($"ChargesEnabled: {account.ChargesEnabled}");
                            Console.WriteLine($"PayoutsEnabled: {account.PayoutsEnabled}");
                            Console.WriteLine($"DetailsSubmitted: {account.DetailsSubmitted}");
                            Console.WriteLine($"TosAcceptance: {(account.TosAcceptance?.Date != null ? "Accepted" : "Not accepted")}");
                            Console.ForegroundColor = originalColor2;
                        }
                        catch { }

                        var idempotencyKey = stripeEvent.Request?.IdempotencyKey; // (solo para trazas)
                        // 🔧 FIX (#7b): usar SIEMPRE stripeEvent.Id como clave de idempotencia, igual que
                        // TryBeginProcessingEventAsync. Antes, si idempotencyKey venía poblado, account.updated
                        // marcaba el evento con req_... creando una fila DUPLICADA (evt_... + req_...) en
                        // ProcessedWebhookEvents.
                        var eventIdToCheck = stripeEvent.Id;
                        // 🔧 FIX (regresión introducida por #7b): ELIMINADO el guard
                        //   `if (await IsEventProcessedAsync(eventIdToCheck)) break;`
                        // Era redundante Y estaba roto: la idempotencia YA se reclama atómicamente arriba con
                        // TryBeginProcessingEventAsync(stripeEvent.Id) (línea ~1938, que devuelve 200 si el evento
                        // ya estaba procesado). Ese reclamo inserta una fila Status="Processing", de modo que este
                        // guard encontraba su PROPIA fila (dentro de la ventana de 5 min) y hacía break SIEMPRE
                        // → account.updated NO se procesaba nunca (incluido el rechazo de cuenta del experto, que
                        // protege dinero). ApplyStripeAccountState es idempotente, así que reprocesar en un
                        // reintento de Stripe es seguro.

                        var profileToUpdate = await FindExpertProfileForAccountAsync(account);
                        if (profileToUpdate == null)
                        {
                            // ✅ LOG ERROR: Profile no encontrado
                            await _loggingService.LogWarningAsync(
                                message: "Stripe account updated without matching expert profile",
                                details: $"account_id={account.Id}",
                                userId: null,
                                source: "SubscriptionController.account.updated",
                                relatedEntityType: "StripeAccount",
                                relatedEntityId: null);

                            await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, null, "Skipped", "Expert profile not found");
                            eventMarkedProcessing = false; // 🛡️ A1: evitar que el bloque final pise "Skipped" con "Success"
                            break;
                        }

                        // ✅ LOG DIAGNÓSTICO: Profile encontrado
                        try
                        {
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"ExpertProfile encontrado:");
                            Console.WriteLine($"  ProfileId: {profileToUpdate.Id}");
                            Console.WriteLine($"  UserId: {profileToUpdate.UserId}");
                            Console.WriteLine($"  Estado actual: {profileToUpdate.StripeStatus}");
                            Console.WriteLine($"  OnboardingCompleted: {profileToUpdate.OnboardingCompleted}");
                            Console.ForegroundColor = originalColor2;
                        }
                        catch { }

                        try
                        {
                            var currentPreviousStatus = profileToUpdate.StripeStatus;
                                
                                // ✅ LOG DIAGNÓSTICO: Evaluando estado
                                try
                                {
                                    Console.ForegroundColor = ConsoleColor.Yellow;
                                    Console.WriteLine($"Evaluando estado de Stripe account...");
                                    Console.ForegroundColor = originalColor2;
                                }
                                catch { }

                                // 🛡️ Round 28 MUD-BT: Stripe NO garantiza orden de entrega de webhooks
                                // (docs.stripe.com/webhooks#delivery-guarantees). Si la cuenta hace 2
                                // transiciones rápidas (Approved→Restricted→Approved) y los webhooks
                                // llegan en orden inverso, el handler aplica el evento VIEJO sobre la
                                // BD ya actualizada. Resultado: BD queda Restricted aunque Stripe live
                                // es Approved → drift permanente (capability.updated YA hace re-fetch).
                                // Fix: re-fetch live state SIEMPRE en account.updated. Si Stripe API
                                // falla, fallback al payload del evento (al menos no es peor que antes).
                                Account liveAccount = account;
                                try
                                {
                                    liveAccount = await new AccountService().GetAsync(account.Id);
                                }
                                catch (StripeException refetchEx)
                                {
                                    await _loggingService.LogWarningAsync(
                                        message: "MUD-BT: account.updated live re-fetch failed, using event payload",
                                        details: $"Account {account.Id}: {refetchEx.StripeError?.Code} - {refetchEx.Message}. Webhook ordering drift puede dejarse sin corregir.",
                                        source: "SubscriptionController.account.updated.MUD-BT",
                                        relatedEntityType: "ExpertProfile",
                                        relatedEntityId: profileToUpdate.Id);
                                }
                                var state = EvaluateStripeAccount(liveAccount);

                                // ✅ LOG DIAGNÓSTICO: Estado evaluado
                                try
                                {
                                    Console.ForegroundColor = ConsoleColor.Cyan;
                                    Console.WriteLine($"Estado evaluado:");
                                    Console.WriteLine($"  Estado anterior: {currentPreviousStatus}");
                                    Console.WriteLine($"  Estado nuevo: {state.Status}");
                                    Console.WriteLine($"  OnboardingCompleted: {state.OnboardingCompleted}");
                                    Console.WriteLine($"  Cambio de estado: {currentPreviousStatus != state.Status}");
                                    Console.ForegroundColor = originalColor2;
                                }
                                catch { }

                                ApplyStripeAccountState(profileToUpdate, state, liveAccount.Id);

                                // ✅ LOG DIAGNÓSTICO: Guardando cambios
                                try
                                {
                                    Console.ForegroundColor = ConsoleColor.Yellow;
                                    Console.WriteLine($"Guardando cambios en base de datos...");
                                    Console.ForegroundColor = originalColor2;
                                }
                                catch { }

                                // ✅ FIX CRÍTICO: NO usar transacciones manuales con ExecutionStrategy habilitado
                                // Guardar directamente sin transacción para evitar conflicto con ExecutionStrategy
                                try
                                {
                                    await _context.SaveChangesAsync();

                                    // 🛡️ Round 28 MUD-BH: sincronizar datos legales DAC7 al snapshot del año
                                    // actual. Antes esto solo se llamaba en el endpoint manual /sync-status
                                    // (cuando el experto pulsaba un botón). Stripe envía account.updated
                                    // CADA vez que cambia DOB/IBAN/Address — esa es la fuente canónica.
                                    // Sin esto, el modelo 238 anual DAC7 podría reportar datos antiguos
                                    // (~€300/seller de multa AEAT). Non-blocking.
                                    if (_dac7DataSync != null)
                                    {
                                        try { await _dac7DataSync.SyncFromAccountAsync(liveAccount, profileToUpdate.Id); }
                                        catch (Exception dac7Ex)
                                        {
                                            await _loggingService.LogWarningAsync(
                                                message: "MUD-BH: DAC7 sync from webhook failed (non-blocking)",
                                                details: $"ExpertProfile {profileToUpdate.Id} acct {account.Id}: {dac7Ex.Message}. El webhook account.updated continua; el snapshot DAC7 puede quedar desfasado hasta el siguiente account.updated.",
                                                source: "SubscriptionController.account.updated.MUD-BH",
                                                relatedEntityType: "Dac7SellerSnapshot",
                                                relatedEntityId: profileToUpdate.Id);
                                        }
                                    }
                                }
                                catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                                {
                                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                                    var recoveryProfile = await recoveryContext.ExpertProfiles
                                        .FirstOrDefaultAsync(ep => ep.StripeAccountId == account.Id);
                                    
                                    if (recoveryProfile != null)
                                    {
                                        var recoveryPreviousStatus = recoveryProfile.StripeStatus;
                                        var recoveryState = EvaluateStripeAccount(account);
                                        ApplyStripeAccountState(recoveryProfile, recoveryState, account.Id);
                                        await recoveryContext.SaveChangesAsync();
                                        
                                        if (recoveryPreviousStatus != recoveryState.Status)
                                        {
                                            await NotifyStripeStatusTransitionAsync(
                                                recoveryProfile,
                                                recoveryPreviousStatus,
                                                recoveryState,
                                                "SubscriptionController.account.updated");
                                        }
                                        
                                        await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, recoveryProfile.UserId);
                                        // Recovery exitoso, continuar con el flujo normal
                                        profileToUpdate = recoveryProfile; // Actualizar referencia
                                        currentPreviousStatus = recoveryPreviousStatus;
                                        state = recoveryState;
                                    }
                                    else
                                    {
                                        await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, null, "Failed", "Connection disposed and recovery failed");
                                        throw new Exception("Connection disposed and recovery failed");
                                    }
                                }
                                catch (ObjectDisposedException disposedEx)
                                {
                                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                                    using var recoveryScope2 = _serviceScopeFactory.CreateScope();
                                    var recoveryContext2 = recoveryScope2.ServiceProvider.GetRequiredService<AppDbContext>();
                                    var recoveryProfile2 = await recoveryContext2.ExpertProfiles
                                        .FirstOrDefaultAsync(ep => ep.StripeAccountId == account.Id);
                                    
                                    if (recoveryProfile2 != null)
                                    {
                                        var recoveryPreviousStatus2 = recoveryProfile2.StripeStatus;
                                        var recoveryState2 = EvaluateStripeAccount(account);
                                        ApplyStripeAccountState(recoveryProfile2, recoveryState2, account.Id);
                                        await recoveryContext2.SaveChangesAsync();
                                        
                                        if (recoveryPreviousStatus2 != recoveryState2.Status)
                                        {
                                            await NotifyStripeStatusTransitionAsync(
                                                recoveryProfile2,
                                                recoveryPreviousStatus2,
                                                recoveryState2,
                                                "SubscriptionController.account.updated");
                                        }
                                        
                                        await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, recoveryProfile2.UserId);
                                        // Recovery exitoso, continuar con el flujo normal
                                        profileToUpdate = recoveryProfile2; // Actualizar referencia
                                        currentPreviousStatus = recoveryPreviousStatus2;
                                        state = recoveryState2;
                                    }
                                    else
                                    {
                                        await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, null, "Failed", disposedEx.Message);
                                        throw new Exception($"Connection disposed: {disposedEx.Message}");
                                    }
                                }
                                
                                // ✅ LOG DIAGNÓSTICO: Cambios guardados exitosamente
                                try
                                {
                                    Console.ForegroundColor = ConsoleColor.Green;
                                    Console.WriteLine($"✅ Cambios guardados exitosamente");
                                    Console.WriteLine($"  StripeStatus: {profileToUpdate.StripeStatus}");
                                    Console.WriteLine($"  OnboardingCompleted: {profileToUpdate.OnboardingCompleted}");
                                    Console.ForegroundColor = originalColor2;
                                }
                                catch { }
                                
                                if (currentPreviousStatus != state.Status)
                                {
                                    await NotifyStripeStatusTransitionAsync(
                                        profileToUpdate,
                                        currentPreviousStatus,
                                        state,
                                        "SubscriptionController.account.updated");
                                }

                                // 🛡️ V10 FIX: expandir cobertura — antes solo Approved→Rejected disparaba el
                                // manejo de hires activos. PERO Stripe puede degradar la cuenta a Disabled /
                                // Restricted / RequirementsPastDue / Deauthorized SIN pasar por Rejected.
                                // En esos casos transfers_enabled se pone false → cualquier hire Pending del
                                // experto se queda zombi (no puede recibir transfer post-captura) y el
                                // cliente sigue con el dinero retenido sin saberlo. Si Stripe envía además
                                // capability.updated, el handler de línea ~2540 también dispara, pero
                                // capability.updated NO está garantizado en todos los flujos de degradación
                                // (sobre todo cuando es la plataforma quien restringe vía Dashboard).
                                // Defensa: detectar cualquier salida de Approved hacia un estado que bloquea
                                // transfers. La función HandleApprovedAccountRejection es idempotente, así que
                                // si capability.updated dispara después, no hay doble cancelación.
                                // 🛡️ Round 11 — S-C FIX: NO incluir RequirementsPastDue en blocksTransfers.
                                // Stripe docs (handle-verification-updates): requirements.past_due es
                                // típicamente TEMPORAL — el experto sube el documento que faltaba y vuelve
                                // a Approved en horas. Cancelar y reembolsar hires Pending sanos por una
                                // degradación de 24h es agresivo y destruye servicios contratados.
                                //
                                // Las capas que ya protegen el dinero quedan intactas:
                                //  - StripeValidationService bloquea CREAR nuevas hires (status != Approved).
                                //  - RefundService.cs:1238 verifica live PayoutsEnabled+transfers antes del
                                //    transfer real. Si sigue bloqueado al finalizar, los reintentos Hangfire
                                //    cubren ~3.6h y luego RefundFailedAt queda para reconciliación manual.
                                //
                                // Sólo cancelamos hires en transiciones DEFINITIVAS (Rejected) o pausas
                                // pesadas (Disabled, Restricted permanente, Deauthorized).
                                var blocksTransfers = state.Status == StripeStatus.Rejected
                                                   || state.Status == StripeStatus.Disabled
                                                   || state.Status == StripeStatus.Restricted
                                                   || state.Status == StripeStatus.Deauthorized;

                                // 🛡️ Round 28 MUD-BM: el guard previo solo cancelaba hires si el estado
                                // PREVIO era exactamente Approved. Si la cuenta degrada en 2 webhooks
                                // (Approved → RequirementsPastDue → Rejected), el 2º evento ve
                                // previousStatus=RequirementsPastDue y NO cancela los hires del 1º.
                                // Resultado: hires zombi creados durante Approved. La función
                                // HandleApprovedAccountRejection es idempotente, así que ampliar el
                                // guard a estados intermedios (que permitían tener hires activos en
                                // transit) no produce doble cancelación.
                                var couldHaveActiveHires =
                                       currentPreviousStatus == StripeStatus.Approved
                                    || currentPreviousStatus == StripeStatus.RequirementsPastDue
                                    || currentPreviousStatus == StripeStatus.PendingVerification
                                    || currentPreviousStatus == StripeStatus.ActionRequired
                                    || currentPreviousStatus == StripeStatus.RestrictedSoon
                                    || currentPreviousStatus == StripeStatus.RequirementsDue;

                                if (couldHaveActiveHires && blocksTransfers)
                                {
                                    await HandleApprovedAccountRejection(profileToUpdate.Id,
                                        state.DisabledReason ?? $"account_updated_to_{state.Status}");
                                }

                                // 🛡️ Round 28 MUD-CB: recovery automático — si la cuenta vuelve a
                                // Approved desde un estado de bloqueo (Rejected/Disabled/Restricted/
                                // Deauthorized), los hires que quedaron con RequiresManualReview=true
                                // por la rejection anterior se vuelven backlog falso (el experto ya
                                // está operativo, esos hires probablemente fueron refunded en su
                                // momento). Sin limpieza, admin ve indefinidamente flags zombi.
                                //
                                // Solo limpiamos flags en hires que estén en estado FINALIZADO
                                // (Cancelled, Completed, dispute_resolved_*) — los activos siguen
                                // necesitando review real.
                                var wasBlocked = currentPreviousStatus == StripeStatus.Rejected
                                              || currentPreviousStatus == StripeStatus.Disabled
                                              || currentPreviousStatus == StripeStatus.Restricted
                                              || currentPreviousStatus == StripeStatus.Deauthorized;
                                if (wasBlocked && state.Status == StripeStatus.Approved)
                                {
                                    try
                                    {
                                        var clearedCount = await _context.SearchHires
                                            .Include(sh => sh.Status)
                                            .Where(sh => sh.ExpertId == profileToUpdate.UserId
                                                      && sh.RequiresManualReview == true
                                                      && sh.Status != null
                                                      && sh.Status.IsFinalizationStatus)
                                            .ExecuteUpdateAsync(set => set
                                                .SetProperty(sh => sh.RequiresManualReview, false)
                                                .SetProperty(sh => sh.UpdatedAt, DateTime.UtcNow));
                                        if (clearedCount > 0)
                                        {
                                            await _loggingService.LogInfoAsync(
                                                message: $"MUD-CB: auto-cleared {clearedCount} RequiresManualReview flags on account recovery",
                                                details: $"ExpertProfile {profileToUpdate.Id} (UserId {profileToUpdate.UserId}) transitioned {currentPreviousStatus} → Approved. {clearedCount} hires en estado finalizado tenían RequiresManualReview=true por el bloqueo previo — limpiados automáticamente. Admin ya no los verá en backlog.",
                                                userId: profileToUpdate.UserId,
                                                source: "SubscriptionController.account.updated.MUD-CB",
                                                relatedEntityType: "ExpertProfile",
                                                relatedEntityId: profileToUpdate.Id);
                                        }
                                    }
                                    catch (Exception clearEx)
                                    {
                                        // Non-blocking — flags zombi no son bloqueantes para el webhook.
                                        await _loggingService.LogWarningAsync(
                                            message: "MUD-CB: failed to clear RequiresManualReview flags",
                                            details: $"ExpertProfile {profileToUpdate.Id}: {clearEx.Message}. Flags zombi se quedan en BD; admin debe limpiarlos a mano si quiere.",
                                            source: "SubscriptionController.account.updated.MUD-CB",
                                            relatedEntityType: "ExpertProfile",
                                            relatedEntityId: profileToUpdate.Id);
                                    }
                                }
                                
                                await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, profileToUpdate.UserId);
                        }
                        catch (Exception ex)
                        {
                            // ✅ LOG ERROR: Excepción general
                            var originalColor5 = Console.ForegroundColor;
                            try
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                var separator5 = new string('=', 80);
                                Console.Error.WriteLine($"\n{separator5}");
                                Console.Error.WriteLine($"🔴 [WEBHOOK ERROR] Excepción general en account.updated");
                                Console.Error.WriteLine($"{separator5}");
                                Console.Error.WriteLine($"EventId: {eventIdToCheck}");
                                Console.Error.WriteLine($"AccountId: {account.Id}");
                                Console.Error.WriteLine($"Error Type: {ex.GetType().Name}");
                                Console.Error.WriteLine($"Error Message: {ex.Message}");
                                Console.Error.WriteLine($"Stack Trace: {ex.StackTrace}");
                                Console.Error.WriteLine($"{separator5}\n");
                                Console.ForegroundColor = originalColor5;
                            }
                            catch { }
                            
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Excepción general en account.updated",
                                details: $"EventId: {eventIdToCheck}, AccountId: {account.Id}, Error Type: {ex.GetType().Name}, Error: {ex.Message}, StackTrace: {ex.StackTrace}",
                                source: "SubscriptionController.account.updated",
                                relatedEntityType: "Webhook",
                                additionalData: new { EventId = eventIdToCheck, AccountId = account.Id, ErrorType = ex.GetType().Name, Error = ex.Message, StackTrace = ex.StackTrace });
                            
                            // Marcar evento como fallido
                            await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, profileToUpdate?.UserId, "Failed", ex.Message);
                            if (eventMarkedProcessing && currentEventId != null)
                            {
                                await MarkEventAsProcessedAsync(
                                    currentEventId,
                                    currentEventType ?? stripeEvent.Type,
                                    currentAccountId,
                                    null,
                                    "Error",
                                    ex.Message);
                                eventMarkedProcessing = false;
                            }
                            // ✅ FRENTE 8: devolver NO-2xx para que Stripe REINTENTE en vez de perder la
                            // transición de estado del experto. El evento queda en "Error", y
                            // TryBeginProcessingEventAsync lo re-reclama en el reintento; account.updated es
                            // idempotente (solo fija el estado Stripe desde la cuenta), así que reprocesar es seguro.
                            return StatusCode(500, new { message = "Error processing account.updated; Stripe will retry" });
                        }

                        break;
                    case "account.external_account.deleted":
                    case "account.external_account.updated":
                    case "account.external_account.created":
                        // 🛡️ Round 28 MUD-BU: experto cambia/borra/añade cuenta bancaria desde
                        // Stripe Dashboard. ANTES caía en default → ignorado. Consecuencias:
                        // - Dac7SellerSnapshot.IbanLast4 queda obsoleto → modelo 238 incorrecto.
                        // - Si borra el ÚNICO bank, los próximos transfer.failed sin contexto.
                        // - Sin notificación al experto del cambio crítico.
                        //
                        // Fix: re-fetch account live + re-sync Dac7. Si transfer falla luego,
                        // el handler de transfer.failed (MUD-BN) ya notifica al experto.
                        try
                        {
                            var externalAccountId = (stripeEvent.Data?.Object as IHasId)?.Id;
                            var connectedAcctId = stripeEvent.Account;
                            if (!string.IsNullOrEmpty(connectedAcctId))
                            {
                                var externalProfile = await _context.ExpertProfiles
                                    .FirstOrDefaultAsync(ep => ep.StripeAccountId == connectedAcctId);
                                if (externalProfile != null)
                                {
                                    Stripe.Account? freshAcct = null;
                                    try { freshAcct = await new AccountService().GetAsync(connectedAcctId); }
                                    catch (StripeException) { /* acct gone, no resync */ }

                                    if (freshAcct != null && _dac7DataSync != null)
                                    {
                                        await _dac7DataSync.SyncFromAccountAsync(freshAcct, externalProfile.Id);
                                    }

                                    if (string.Equals(stripeEvent.Type, "account.external_account.deleted", System.StringComparison.Ordinal))
                                    {
                                        await _loggingService.LogWarningAsync(
                                            message: "🏦 Has eliminado tu cuenta bancaria de Stripe",
                                            details: $"Detectamos que has borrado una cuenta bancaria de tu Connect account ({externalAccountId}). Si era tu única cuenta, los próximos pagos a tu banco fallarán hasta que añadas una nueva en el panel Stripe. Si fue un cambio intencional (renovación de IBAN), ignora este aviso.",
                                            userId: externalProfile.UserId,
                                            source: "SubscriptionController.account.external_account.deleted.MUD-BU",
                                            relatedEntityType: "ExpertProfile",
                                            relatedEntityId: externalProfile.Id,
                                            notifyUser: true);
                                    }
                                }
                            }
                        }
                        catch (Exception extEx)
                        {
                            await _loggingService.LogWarningAsync(
                                message: "MUD-BU: external_account event processing failed (non-blocking)",
                                details: $"EventType={stripeEvent.Type} acct={stripeEvent.Account}: {extEx.Message}.",
                                source: "SubscriptionController.account.external_account.MUD-BU",
                                relatedEntityType: "ExpertProfile");
                        }
                        await MarkEventAsProcessedAsync(stripeEvent.Id, stripeEvent.Type, stripeEvent.Account, null);
                        break;
                    case "transfer.failed":
                        // 🔧 FIX B4: delega en el método compartido (también llamado desde /webhook-general,
                        // que es donde Stripe entrega los eventos de la cuenta PLATAFORMA en separate charges
                        // & transfers). Idempotencia por EventId evita doble proceso si llegara por ambos.
                        await HandleTransferFailed(stripeEvent.Data.Object as Transfer);
                        break;

                    // 🔁 P6 (CRÍTICO): confirmación del clawback al experto tras un chargeback. La reversión
                    // se ENCOLA en HandleChargeDisputeCreated (ReverseExpertTransferForChargebackAsync), pero su
                    // RESULTADO sólo se conoce aquí: Stripe emite transfer.reversed cuando el/los reversal(es)
                    // se aplican. Sin este handler no sabíamos si la reversión falló o fue parcial.
                    case "transfer.reversed":
                        await HandleTransferReversed(stripeEvent.Data.Object as Transfer);
                        break;

                    // Los eventos de suscripción y facturas se manejan en el webhook general

                    case "capability.updated":
                        {
                            var capability = stripeEvent.Data.Object as Stripe.Capability;
                            if (capability != null)
                            {
                                var capabilityAccountId = capability.AccountId ?? stripeEvent.Account;
                                // 🔧 Round 26 CAP-2: quitar el gate `status == "inactive"` y procesar
                                // también eventos active-con-nuevos-requirements. Stripe documenta que
                                // capability.updated dispara "whenever a capability has new requirements
                                // or a new status" — ignorar los active perdía requisitos scoped a la
                                // capability (transfers) que nunca aparecían en el rollup account-level.
                                bool isCapabilityInactive = string.Equals(capability.Status, "inactive", StringComparison.OrdinalIgnoreCase);
                                bool hasCapabilityRequirements =
                                    (capability.Requirements?.CurrentlyDue?.Count ?? 0) > 0 ||
                                    (capability.Requirements?.PastDue?.Count ?? 0) > 0 ||
                                    (capability.Requirements?.Errors?.Count ?? 0) > 0;

                                if (!string.IsNullOrEmpty(capabilityAccountId) &&
                                    (string.Equals(capability.Id, "card_payments", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(capability.Id, "transfers", StringComparison.OrdinalIgnoreCase)) &&
                                    (isCapabilityInactive || hasCapabilityRequirements))
                                {
                                    var capabilityProfile = await _context.ExpertProfiles
                                        .FirstOrDefaultAsync(ep => ep.StripeAccountId == capabilityAccountId);
                                    // 🛡️ Round 28 MUD-AI (mirror MUD-M): si llegó un capability.updated
                                    // tardío para una cuenta cerrada en una mudanza muy reciente (<15min),
                                    // ignorarlo. Sin esto, el webhook degradaba a Restricted/Disabled la
                                    // cuenta que el usuario acaba de cerrar voluntariamente, sobrescribiendo
                                    // el NotRequested del flujo de mudanza.
                                    if (capabilityProfile != null
                                        && capabilityProfile.RelocatedAt != null
                                        && capabilityProfile.RelocatedAt.Value > System.DateTime.UtcNow.AddMinutes(-15))
                                    {
                                        capabilityProfile = null; // late event para acct cerrada → drop
                                    }
                                    if (capabilityProfile != null)
                                    {
                                        // 🛡️ Round 28 MUD-BR (follow-up MUD-BI): 3er sitio del array activeStatusValues.
                                        // ANTES esto NO incluía TransferFailed → si experto tenía SOLO hires en TransferFailed
                                        // y capability.updated llegaba con transfers=inactive, activeHiresCount=0 → no
                                        // disparaba HandleApprovedAccountRejection → hires zombi como pre-MUD-BI.
                                        var activeStatusValues = new[]
                                        {
                                            SearchHireStatus.Pending.ToStringValue(),
                                            SearchHireStatus.AwaitingClientDecision.ToStringValue(),
                                            SearchHireStatus.Disputed.ToStringValue(),
                                            SearchHireStatus.TransferFailed.ToStringValue()
                                        };
                                        var activeHiresCount = await _context.SearchHires
                                            .Include(sh => sh.Status)
                                            .Where(sh => sh.ExpertId == capabilityProfile.UserId && activeStatusValues.Contains(sh.Status.StatusValue))
                                            .CountAsync();

                                        // 🛡️ Round 14 — Q12 FIX: degradar StripeStatus SIEMPRE que una capability se
                                        // vuelva inactive, no solo cuando haya hires activos. Antes: si el experto no
                                        // tenía hires activos en el momento del webhook, se ignoraba → StripeStatus
                                        // quedaba en Approved, servicios visibles públicamente, clientes contrataban,
                                        // transfer fallaba en finalize → dinero atascado.
                                        // Ahora: re-evaluamos el estado live de Stripe y aplicamos el StripeStatus
                                        // resultante (Restricted/Disabled). HandleApprovedAccountRejection sigue
                                        // disparándose solo si hay hires activos (la lógica de cancelar+refund no
                                        // tiene sentido sin hires).
                                        // 🔧 Round 26 CAP-3: capturar previousStatus para notificar al experto
                                        // si la capability degrada la cuenta (la rama account.updated ya lo hace;
                                        // capability.updated no garantiza account.updated emparejado).
                                        var capabilityPreviousStatus = capabilityProfile.StripeStatus;
                                        StripeAccountState? capabilityRefreshedState = null;
                                        // 🛡️ MUD-BK: refreshedAccount fuera del try para usarlo después
                                        // en el check de card_payments latente (necesitamos transfers
                                        // + payouts_enabled live).
                                        Stripe.Account? refreshedAccount = null;
                                        bool capabilitySaveFailed = false;
                                        try
                                        {
                                            var accountService = new Stripe.AccountService();
                                            refreshedAccount = await accountService.GetAsync(capabilityAccountId);
                                            capabilityRefreshedState = EvaluateStripeAccount(refreshedAccount);
                                            ApplyStripeAccountState(capabilityProfile, capabilityRefreshedState);
                                            await _context.SaveChangesAsync();
                                        }
                                        catch (Exception evalEx)
                                        {
                                            await _loggingService.LogWarningAsync(
                                                message: "Q12: capability.updated re-eval falló — fallback a Restricted",
                                                details: $"AccountId {capabilityAccountId}, ExpertProfileId {capabilityProfile.Id}: {evalEx.Message}. Marcando Restricted como precaución.",
                                                userId: capabilityProfile.UserId,
                                                source: "SubscriptionController.capability.updated.Q12",
                                                relatedEntityType: "ExpertProfile",
                                                relatedEntityId: capabilityProfile.Id);
                                            capabilityProfile.StripeStatus = StripeStatus.Restricted;
                                            // 🔧 Round 26 CAP-5: no tragar el fallo del save de fallback.
                                            // Antes `try { save } catch { /* swallow */ }` perdía la transición
                                            // a Restricted en contención DB → el experto quedaba Approved con
                                            // capability inactive (escenario fail-open invisible). Ahora loguea
                                            // como CRITICAL y devuelve 500 para que Stripe reintente.
                                            try
                                            {
                                                await _context.SaveChangesAsync();
                                            }
                                            catch (Exception saveEx)
                                            {
                                                capabilitySaveFailed = true;
                                                await _loggingService.LogCriticalAsync(
                                                    message: "CAP-5: SaveChangesAsync de fallback falló en capability.updated",
                                                    details: $"AccountId {capabilityAccountId}, ExpertProfileId {capabilityProfile.Id}: el save de Restricted falló tras fallar el re-eval. Error: {saveEx.Message}. InnerException: {saveEx.InnerException?.Message}. La cuenta queda potencialmente Approved con capability inactive — Stripe reintentará el webhook.",
                                                    userId: capabilityProfile.UserId,
                                                    source: "SubscriptionController.capability.updated.CAP-5",
                                                    relatedEntityType: "ExpertProfile",
                                                    relatedEntityId: capabilityProfile.Id,
                                                    additionalData: new {
                                                        AccountId = capabilityAccountId,
                                                        capability.Id,
                                                        capability.Status,
                                                        ExceptionType = saveEx.GetType().Name,
                                                        ExceptionMessage = saveEx.Message
                                                    });
                                            }
                                            // Sintetizar un estado mínimo para que el notifier pueda procesarlo.
                                            capabilityRefreshedState = new StripeAccountState
                                            {
                                                Status = StripeStatus.Restricted,
                                                OnboardingCompleted = capabilityProfile.OnboardingCompleted,
                                                StatusDetails = capabilityProfile.StripeStatusDetails,
                                                DisabledReason = $"capability_{capability.Id}_{capability.Status}"
                                            };
                                        }

                                        // 🔧 Round 26 CAP-3: notificar al experto si el estado cambió.
                                        // Espejo de la rama account.updated (líneas ~2624-2631).
                                        if (!capabilitySaveFailed &&
                                            capabilityRefreshedState != null &&
                                            capabilityPreviousStatus != capabilityProfile.StripeStatus)
                                        {
                                            await NotifyStripeStatusTransitionAsync(
                                                capabilityProfile,
                                                capabilityPreviousStatus,
                                                capabilityRefreshedState,
                                                "SubscriptionController.capability.updated");
                                        }

                                        // 🛡️ Round 28 MUD-BK + MUD-BY: NO cancelar hires por card_payments inactive si
                                        // transfers sigue active. En US/CA/GB la capability card_payments es
                                        // "latente" (Stripe la exige por KYC pero el flujo separate-charges
                                        // de la plataforma no la usa — clientes pagan a platform, platform
                                        // transfiere via capability transfers). Cancelar hires destruye
                                        // servicios contratados sin razón financiera real.
                                        //
                                        // MUD-BY: distinguir transfers="pending" (transitorio: Stripe re-verificando
                                        // KYC, dura minutos a horas) de transfers="inactive" (permanente, requiere
                                        // intervención). Solo cancelar si transfers está DEFINITIVAMENTE bloqueado.
                                        // pending NO cancela hires — esperamos al siguiente account.updated.
                                        var transfersCapability = refreshedAccount?.Capabilities?.Transfers;
                                        var transfersBlockedPermanently = transfersCapability == "inactive";
                                        var payoutsStillEnabled = refreshedAccount?.PayoutsEnabled == true;
                                        var realBlockingDeactivation =
                                            capability.Id != "card_payments"
                                            || transfersBlockedPermanently
                                            || !payoutsStillEnabled;

                                        if (activeHiresCount > 0 && isCapabilityInactive && realBlockingDeactivation)
                                        {
                                            await _loggingService.LogCriticalAsync(
                                                message: "CRITICAL: Stripe capability inactive on expert with active hires",
                                                details: $"Capability '{capability.Id}' became inactive on account {capabilityAccountId}. ExpertId={capabilityProfile.UserId}, active hires={activeHiresCount}. Triggering HandleApprovedAccountRejection.",
                                                userId: capabilityProfile.UserId,
                                                source: "SubscriptionController.capability.updated",
                                                relatedEntityType: "ExpertProfile",
                                                relatedEntityId: capabilityProfile.Id,
                                                additionalData: new { capability.Id, capability.Status, AccountId = capabilityAccountId, ActiveHires = activeHiresCount, TransfersCapability = transfersCapability, PayoutsEnabled = payoutsStillEnabled });
                                            await HandleApprovedAccountRejection(capabilityProfile.Id, $"capability_{capability.Id}_inactive");
                                        }
                                        else if (activeHiresCount > 0 && isCapabilityInactive && !realBlockingDeactivation)
                                        {
                                            // card_payments latente inactivada pero transfers+payouts siguen ok.
                                            // Solo log Warning — no cancelamos hires.
                                            await _loggingService.LogWarningAsync(
                                                message: "MUD-BK/BY: capability degraded but transfers OK — hires NOT cancelled",
                                                details: $"Capability '{capability.Id}' status={capability.Status} en {capabilityAccountId}. transfers={transfersCapability ?? "(none)"} payouts_enabled={payoutsStillEnabled}. ExpertId={capabilityProfile.UserId}, active hires={activeHiresCount}. Servicios siguen operativos. Si transfers=pending es transitorio (KYC re-eval, <24h); si transfers=active la capability afectada es latente (e.g., card_payments en US/CA/GB en separate-charges flow). NO cancelamos hires hasta que transfers pase a 'inactive' definitivo.",
                                                userId: capabilityProfile.UserId,
                                                source: "SubscriptionController.capability.updated.MUD-BK",
                                                relatedEntityType: "ExpertProfile",
                                                relatedEntityId: capabilityProfile.Id,
                                                additionalData: new { capability.Id, capability.Status, AccountId = capabilityAccountId, ActiveHires = activeHiresCount });
                                        }

                                        // 🔧 Round 26 CAP-5: si el save de fallback falló, devolver 500 para
                                        // que Stripe reintente este capability.updated.
                                        if (capabilitySaveFailed)
                                        {
                                            await MarkEventAsProcessedAsync(stripeEvent.Id, stripeEvent.Type, capabilityAccountId, capabilityProfile.UserId, "Failed", "CAP-5: SaveChangesAsync fallback failed");
                                            return StatusCode(500, new { message = "capability.updated fallback save failed; Stripe will retry" });
                                        }
                                    }
                                }
                            }
                        }
                        break;

                    default:
                        // 🛡️ Round 13 — N10 FIX: paridad con A6 (round 9) en el webhook general.
                        // Antes este default era silencioso: cualquier nuevo event type Connect
                        // (treasury.*, radar.*, terminal.*, identity.* si Stripe los empezase a
                        // entregar al Connect endpoint) pasaba desapercibido. Ahora se loguea con
                        // idempotencia por EventId — sin spam, máxima observabilidad.
                        await _loggingService.LogInfoAsync(
                            message: $"Stripe Connect webhook event no manejado: {stripeEvent.Type}",
                            details: $"Event {stripeEvent.Id} de tipo '{stripeEvent.Type}' recibido al endpoint Connect pero no procesado. Si este tipo se vuelve recurrente, considerar añadir un handler explícito.",
                            source: "SubscriptionController.HandleStripeWebhook.default",
                            relatedEntityType: "StripeEvent",
                            relatedEntityId: null,
                            additionalData: new
                            {
                                EventId = stripeEvent.Id,
                                EventType = stripeEvent.Type,
                                StripeAccount = stripeEvent.Account,
                                Created = stripeEvent.Created
                            });
                        break;
                }

                if (eventMarkedProcessing)
                {
                    await MarkEventAsProcessedAsync(stripeEvent.Id, stripeEvent.Type, stripeEvent.Account, null, "Success");
                    eventMarkedProcessing = false;
                }
                
                // ✅ LOG DIAGNÓSTICO: Webhook Connect completado exitosamente
                var webhookEndTime = DateTime.UtcNow;
                var webhookDuration = (webhookEndTime - webhookStartTime).TotalMilliseconds;
                var originalColorGen2 = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    var separatorGen2 = new string('=', 80);
                    Console.WriteLine($"\n{separatorGen2}");
                    Console.WriteLine($"✅ [WEBHOOK] Webhook Connect procesado exitosamente");
                    Console.WriteLine($"{separatorGen2}");
                    Console.WriteLine($"EventId: {currentEventId ?? "N/A"}");
                    Console.WriteLine($"EventType: {currentEventType ?? "N/A"}");
                    Console.WriteLine($"AccountId: {currentAccountId ?? "N/A"}");
                    Console.WriteLine($"Duración total: {webhookDuration:F2}ms");
                    Console.WriteLine($"Timestamp: {webhookEndTime:yyyy-MM-dd HH:mm:ss.fff}");
                    Console.WriteLine($"{separatorGen2}\n");
                    Console.ForegroundColor = originalColorGen2;
                }
                catch { }
                
                return Ok();
            }
            catch (StripeException e)
            {
                // ✅ LOG ERROR: StripeException en consola en rojo
                var originalColor7 = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    var separator7 = new string('=', 80);
                    Console.Error.WriteLine($"\n{separator7}");
                    Console.Error.WriteLine($"🔴 [WEBHOOK ERROR] StripeException");
                    Console.Error.WriteLine($"{separator7}");
                    Console.Error.WriteLine($"Error Type: {e.GetType().Name}");
                    Console.Error.WriteLine($"Error Message: {e.Message}");
                    Console.Error.WriteLine($"StripeError Type: {e.StripeError?.Type ?? "N/A"}");
                    Console.Error.WriteLine($"StripeError Code: {e.StripeError?.Code ?? "N/A"}");
                    Console.Error.WriteLine($"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    Console.Error.WriteLine($"{separator7}\n");
                    Console.ForegroundColor = originalColor7;
                }
                catch { }
                
                // ✅ SEGURIDAD: Si la signature es inválida, ConstructEvent lanza StripeException
                // Esto previene ataques de replay e inyección de eventos falsos
                if (e.Message?.Contains("signature") == true || e.Message?.Contains("Invalid signature") == true)
                {
                    // 🚨 LOG CRÍTICO: Intento de ataque con signature inválida
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Invalid webhook signature - potential attack",
                        details: $"Invalid webhook signature detected. This could be a security attack. Signature: {signatureHeader?.Substring(0, Math.Min(50, signatureHeader?.Length ?? 0))}...",
                        source: "SubscriptionController.HandleStripeWebhook",
                        relatedEntityType: "Security",
                        additionalData: new { 
                            Action = "WebhookSignatureValidation",
                            SignatureHeader = signatureHeader?.Substring(0, Math.Min(50, signatureHeader?.Length ?? 0)),
                            Error = e.Message
                        }
                    );
                    
                    return BadRequest(new { error = "Invalid webhook signature" });
                }
                // 🚨 LOG CRÍTICO: Error de Stripe en webhook (puede afectar dinero)
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Stripe webhook error",
                    details: $"Stripe exception in webhook handler: {e.Message}, Type: {e.StripeError?.Type}, Code: {e.StripeError?.Code}",
                    source: "SubscriptionController.HandleStripeWebhook",
                    relatedEntityType: "Webhook",
                    additionalData: new { 
                        Action = "StripeWebhook",
                        StripeError = e.Message,
                        StripeErrorType = e.StripeError?.Type,
                        StripeErrorCode = e.StripeError?.Code,
                        Payload = json?.Substring(0, Math.Min(500, json?.Length ?? 0))
                    }
                );
                if (eventMarkedProcessing && currentEventId != null)
                {
                    await MarkEventAsProcessedAsync(
                        currentEventId,
                        currentEventType ?? "unknown",
                        currentAccountId,
                        null,
                        "Failed",
                        e.Message);
                    eventMarkedProcessing = false;
                }
                // 🔁 A5: error de Stripe NO-firma (api_connection, rate_limit, lock_timeout, etc.) es
                // transitorio → devolver 500 para que Stripe REINTENTE la entrega. Antes devolvía 400, que
                // Stripe trata como permanente y NO reintenta → el evento se perdía. (La firma inválida sí
                // devuelve 400 más arriba.) El evento quedó marcado "Failed" → TryBeginProcessingEvent lo
                // re-reclama en la reentrega.
                return StatusCode(500, new { error = e.Message });
            }
            catch (Exception e)
            {
                // ✅ LOG ERROR: Excepción general en consola en rojo
                var originalColor8 = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    var separator8 = new string('=', 80);
                    Console.Error.WriteLine($"\n{separator8}");
                    Console.Error.WriteLine($"🔴 [WEBHOOK ERROR] Excepción general");
                    Console.Error.WriteLine($"{separator8}");
                    Console.Error.WriteLine($"Error Type: {e.GetType().Name}");
                    Console.Error.WriteLine($"Error Message: {e.Message}");
                    Console.Error.WriteLine($"Stack Trace: {e.StackTrace}");
                    Console.Error.WriteLine($"Inner Exception: {e.InnerException?.Message ?? "None"}");
                    Console.Error.WriteLine($"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    Console.Error.WriteLine($"Duración total: {(DateTime.UtcNow - webhookStartTime).TotalMilliseconds:F2}ms");
                    Console.Error.WriteLine($"{separator8}\n");
                    Console.ForegroundColor = originalColor8;
                }
                catch { }
                
                // 🚨 LOG CRÍTICO: Error general en webhook (puede afectar dinero)
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: General webhook error",
                    details: $"General exception in webhook handler: {e.Message}, Type: {e.GetType().Name}, StackTrace: {e.StackTrace}",
                    source: "SubscriptionController.HandleStripeWebhook",
                    relatedEntityType: "Webhook",
                    additionalData: new { 
                        Action = "StripeWebhook",
                        Exception = e.Message,
                        ExceptionType = e.GetType().Name,
                        StackTrace = e.StackTrace,
                        InnerException = e.InnerException?.Message,
                        Payload = json?.Substring(0, Math.Min(500, json?.Length ?? 0))
                    }
                );
                if (eventMarkedProcessing && currentEventId != null)
                {
                    await MarkEventAsProcessedAsync(
                        currentEventId,
                        currentEventType ?? "unknown",
                        currentAccountId,
                        null,
                        "Failed",
                        e.Message);
                    eventMarkedProcessing = false;
                }
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        [HttpPost("webhook-general")]
        [AllowAnonymous]
        [DisableRateLimiting] // 🔁 webhook autenticado por firma de Stripe; NO limitar (igual que /webhook)
        public async Task<IActionResult> HandleGeneralStripeWebhook()
        {
            // ✅ LOG DIAGNÓSTICO: Inicio del webhook general
            var webhookGeneralStartTime = DateTime.UtcNow;
            var originalColorGen1 = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                var separatorGen1 = new string('=', 80);
                Console.WriteLine($"\n{separatorGen1}");
                Console.WriteLine($"📥 [WEBHOOK GENERAL] Iniciando procesamiento de webhook general");
                Console.WriteLine($"{separatorGen1}");
                Console.WriteLine($"Timestamp: {webhookGeneralStartTime:yyyy-MM-dd HH:mm:ss.fff}");
                Console.ForegroundColor = originalColorGen1;
            }
            catch { }
            
            // ✅ SEGURIDAD CRÍTICA: Habilitar buffering para permitir múltiples lecturas del body
            Request.EnableBuffering();
            var json = await new StreamReader(Request.Body).ReadToEndAsync();
            Request.Body.Position = 0; // ✅ CORRECCIÓN: Reposicionar DESPUÉS de leer
            // ✅ SEGURIDAD: Convertir StringValues a string (puede venir como array)
            var signatureHeader = Request.Headers["Stripe-Signature"].ToString();
            string? currentEventId = null;
            string? currentEventType = null;
            string? currentAccountId = null;
            bool eventMarkedProcessing = false;
            try
            {
                // ✅ SEGURIDAD CRÍTICA: Validar signature antes de procesar
                // EventUtility.ConstructEvent valida la signature y lanza StripeException si es inválida
                // Esto previene ataques de replay e inyección de eventos falsos
                // ✅ Actualizar Stripe API Key antes de usar (por si cambió el modo)
                UpdateStripeApiKey();
                
                if (string.IsNullOrEmpty(GeneralWebhookSecret))
                {
                    return BadRequest(new { error = "Webhook secret not configured" });
                }
                
                if (string.IsNullOrEmpty(signatureHeader))
                {
                    return BadRequest(new { error = "Stripe signature header missing" });
                }
                
                // ✅ STRIPE API VERSION: Permitir diferentes versiones de API con advertencia
                // El webhook endpoint en Stripe Dashboard puede estar configurado con una versión diferente
                // a la que espera el SDK. Esto es seguro siempre que validemos la signature correctamente.
                var stripeEvent = EventUtility.ConstructEvent(
                    json, 
                    signatureHeader, 
                    GeneralWebhookSecret,
                    throwOnApiVersionMismatch: false // ⚠️ Permite procesar eventos de diferentes versiones de API
                );
                
                // ⚠️ ADVERTENCIA: Si hay mismatch de versión, loguear para actualizar el webhook endpoint
                if (stripeEvent.ApiVersion != null)
                {
                    var expectedVersion = "2025-11-17.clover"; // Versión esperada por Stripe.NET 50.0.0
                    if (stripeEvent.ApiVersion != expectedVersion)
                    {
                        var warningMessage = $"⚠️ Stripe webhook API version mismatch: Received '{stripeEvent.ApiVersion}', but SDK expects '{expectedVersion}'. " +
                                           $"Consider updating the webhook endpoint in Stripe Dashboard to use API version '{expectedVersion}' for better compatibility.";
                        
                        // ✅ Mostrar en consola para visibilidad inmediata (en amarillo porque es warning)
                        var separator = new string('=', 80);
                        var originalColor = Console.ForegroundColor;
                        try
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"\n{separator}");
                            Console.WriteLine($"⚠️ [STRIPE WEBHOOK] API Version Mismatch");
                            Console.WriteLine($"{separator}");
                            Console.WriteLine($"Received: {stripeEvent.ApiVersion}");
                            Console.WriteLine($"Expected: {expectedVersion}");
                            Console.WriteLine($"Event Type: {stripeEvent.Type}");
                            Console.WriteLine($"Event ID: {stripeEvent.Id}");
                            Console.WriteLine($"Recommendation: Update webhook endpoint in Stripe Dashboard to use API version '{expectedVersion}'");
                            Console.WriteLine($"{separator}\n");
                            Console.ForegroundColor = originalColor;
                        }
                        catch
                        {
                            Console.WriteLine($"\n{separator}");
                            Console.WriteLine($"⚠️ [STRIPE WEBHOOK] API Version Mismatch");
                            Console.WriteLine($"{separator}");
                            Console.WriteLine($"Received: {stripeEvent.ApiVersion}");
                            Console.WriteLine($"Expected: {expectedVersion}");
                            Console.WriteLine($"Event Type: {stripeEvent.Type}");
                            Console.WriteLine($"Event ID: {stripeEvent.Id}");
                            Console.WriteLine($"Recommendation: Update webhook endpoint in Stripe Dashboard to use API version '{expectedVersion}'");
                            Console.WriteLine($"{separator}\n");
                        }
                        
                        await _loggingService.LogWarningAsync(
                            message: "Stripe webhook API version mismatch",
                            details: warningMessage,
                            userId: null,
                            source: "SubscriptionController.HandleGeneralStripeWebhook",
                            relatedEntityType: "Webhook",
                            relatedEntityId: null
                        );
                    }
                }
                
                currentEventId = stripeEvent.Id;
                currentEventType = stripeEvent.Type;
                currentAccountId = stripeEvent.Account;
                // 🔒 IDEMPOTENCIA ATÓMICA: reclamar el evento (insert-first con índice único en EventId).
                // Elimina la carrera "comprobar y luego insertar" entre entregas concurrentes de Stripe.
                if (!await TryBeginProcessingEventAsync(stripeEvent.Id, stripeEvent.Type, stripeEvent.Account))
                {
                    return Ok(new { message = "Event already processed" });
                }
                eventMarkedProcessing = true;

                switch (stripeEvent.Type)
                {
                    case "payment_intent.succeeded":
                        var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
                        if (paymentIntent != null)
                        {
                            await HandlePaymentIntentSucceeded(paymentIntent);
                        }
                        else
                        {
                        }
                        break;

                    case "payment_intent.payment_failed":
                        var failedPaymentIntent = stripeEvent.Data.Object as PaymentIntent;
                        if (failedPaymentIntent != null)
                        {
                            await HandlePaymentIntentFailed(failedPaymentIntent);
                        }
                        else
                        {
                        }
                        break;

                    // 🛡️ Round 9 — A6 FIX: PSD2 / SCA (3D Secure). Cuando la tarjeta del cliente requiere
                    // autenticación adicional (≥30€ EEA), Stripe emite payment_intent.requires_action.
                    // El cliente debe completar el challenge en el redirect de Checkout; Stripe re-emitirá
                    // payment_intent.succeeded o payment_intent.payment_failed después.
                    // NO mover el SearchHire de estado — solo observabilidad para detectar tasas anómalas
                    // de abandono SCA (señal de fraude o problemas de UX con la pasarela).
                    case "payment_intent.requires_action":
                        var requiresActionPi = stripeEvent.Data.Object as PaymentIntent;
                        if (requiresActionPi != null)
                        {
                            await _loggingService.LogInfoAsync(
                                message: "PSD2 SCA challenge requerido (3DS)",
                                details: $"PaymentIntent {requiresActionPi.Id} requiere acción del cliente (NextAction={requiresActionPi.NextAction?.Type}). Esperando a que el cliente complete la autenticación en el redirect de Stripe.",
                                source: "SubscriptionController.HandleGeneralStripeWebhook.requires_action",
                                relatedEntityType: "PaymentIntent",
                                relatedEntityId: null,
                                additionalData: new
                                {
                                    PaymentIntentId = requiresActionPi.Id,
                                    Amount = requiresActionPi.Amount,
                                    Currency = requiresActionPi.Currency,
                                    NextActionType = requiresActionPi.NextAction?.Type,
                                    Status = requiresActionPi.Status,
                                    Metadata = requiresActionPi.Metadata
                                });
                        }
                        break;

                    // ⏳ A-v (ALTO): con CAPTURA MANUAL, una autorización no capturada expira a ~7 días y
                    // Stripe emite payment_intent.canceled. Antes caía en default → el hire quedaba colgado en
                    // 'pending' para siempre. Ahora se cancela y se notifica (no hubo cobro → sin refund).
                    case "payment_intent.canceled":
                        var canceledPaymentIntent = stripeEvent.Data.Object as PaymentIntent;
                        if (canceledPaymentIntent != null)
                        {
                            await HandlePaymentIntentCanceled(canceledPaymentIntent);
                        }
                        break;

                    // 🛡️ Round 9 — A6 FIX: checkout.session.expired (Stripe vence sesiones tras 24h sin
                    // completar). Antes caía en default → la sesión abandonada permanecía como pendiente
                    // en BD sin trazabilidad. Loguear para limpieza y métricas de funnel.
                    case "checkout.session.expired":
                        var expiredSession = stripeEvent.Data.Object as Session;
                        if (expiredSession != null)
                        {
                            await _loggingService.LogInfoAsync(
                                message: "Checkout session expirada sin pago",
                                details: $"Sesión {expiredSession.Id} expiró sin completarse (TTL ~24h). Metadata: {JsonSerializer.Serialize(expiredSession.Metadata ?? new Dictionary<string, string>())}",
                                source: "SubscriptionController.HandleGeneralStripeWebhook.session.expired",
                                relatedEntityType: "CheckoutSession",
                                relatedEntityId: null,
                                additionalData: new
                                {
                                    SessionId = expiredSession.Id,
                                    Mode = expiredSession.Mode,
                                    ExpiresAt = expiredSession.ExpiresAt,
                                    Metadata = expiredSession.Metadata
                                });
                        }
                        break;

                    case "checkout.session.completed":
                        var session = stripeEvent.Data.Object as Session;
                        if (session != null && session.Mode == "payment")
                        {
                            // ✅ VALIDACIÓN: Verificar que PaymentIntentId no sea null
                            if (string.IsNullOrEmpty(session.PaymentIntentId))
                            {
                                if (eventMarkedProcessing && currentEventId != null)
                                {
                                    await MarkEventAsProcessedAsync(
                                        currentEventId,
                                        currentEventType ?? stripeEvent.Type,
                                        currentAccountId,
                                        null,
                                        "Failed",
                                        "PaymentIntentId is missing from session");
                                    eventMarkedProcessing = false;
                                }
                                // 🛡️ A3 FIX: devolver 500 (no 400) para que Stripe REINTENTE. Si el
                                // PaymentIntentId vino vacío por un timeout transitorio de Stripe API,
                                // perder el evento equivale a perder el pago. Si es payload genuinamente
                                // corrupto, Stripe desistirá tras ~3 días con backoff y queda visible
                                // como "Failed" en BD para inspección manual. TryBeginProcessingEventAsync
                                // re-reclama eventos en estado "Failed" en los reintentos.
                                return StatusCode(500, new { error = "PaymentIntentId missing - Stripe will retry" });
                            }

                            // 🔍 IDEMPOTENCIA: Verificar si ya se procesó este evento
                            var existingTransaction = await _context.FinancialTransactions
                                .FirstOrDefaultAsync(ft => ft.StripePaymentIntentId == session.PaymentIntentId && 
                                                          ft.TransactionType == "ServicePayment");

                            if (existingTransaction != null)
                            {
                                if (eventMarkedProcessing && currentEventId != null)
                                {
                                    await MarkEventAsProcessedAsync(
                                        currentEventId,
                                        currentEventType ?? stripeEvent.Type,
                                        currentAccountId,
                                        null,
                                        "Success");
                                    eventMarkedProcessing = false;
                                }
                                return Ok(new { message = "Event already processed" }); // ✅ Idempotencia
                            }

                            if (int.TryParse(session.Metadata.GetValueOrDefault("userId", "0"), out int userId) &&
                                decimal.TryParse(session.Metadata.GetValueOrDefault("amount", "0"), out decimal amount) &&
                                bool.TryParse(session.Metadata.GetValueOrDefault("pendingHire", "false"), out bool pendingHire))
                            {
                                // 🚨 P1 FIX (Finding #1): validar explícitamente userId>0 y amount>0 ANTES de
                                // procesar. Si vienen 0 (metadata corrupta), antes pasaba silente al else o se
                                // procesaba con userId=0 → FK violation. Ahora log CRÍTICO inmediato + abort.
                                if (userId <= 0 || amount <= 0)
                                {
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL P1: Invalid userId/amount in checkout.session metadata",
                                        details: $"Session {session.Id} parsed userId={userId} amount={amount} pendingHire={pendingHire}. Metadata corrupta — el cobro pudo haber pasado pero NO se procesa hire. ACCIÓN ADMIN: refund manual del PaymentIntent {session.PaymentIntentId} desde Stripe Dashboard.",
                                        source: "SubscriptionController.HandleGeneralStripeWebhook",
                                        relatedEntityType: "Payment",
                                        relatedEntityId: null,
                                        additionalData: new
                                        {
                                            SessionId = session.Id,
                                            PaymentIntentId = session.PaymentIntentId,
                                            UserId = userId,
                                            Amount = amount,
                                            PendingHire = pendingHire,
                                            Metadata = session.Metadata
                                        });
                                    if (eventMarkedProcessing && currentEventId != null)
                                    {
                                        await MarkEventAsProcessedAsync(currentEventId, currentEventType ?? stripeEvent.Type, currentAccountId, null, "Failed", "Invalid userId/amount in metadata");
                                        eventMarkedProcessing = false;
                                    }
                                    return StatusCode(500, new { error = "Invalid userId/amount in metadata - Stripe will retry" });
                                }

                                if (pendingHire && int.TryParse(session.Metadata.GetValueOrDefault("serviceId", "0"), out int serviceId))
                                {
                                    // 🚨 P1 FIX (Finding #1): validar serviceId>0 antes de delegar.
                                    if (serviceId <= 0)
                                    {
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL P1: Invalid serviceId in checkout.session metadata (pendingHire=true)",
                                            details: $"Session {session.Id} parsed serviceId={serviceId} con pendingHire=true. NO se crea hire. ACCIÓN ADMIN: refund manual del PaymentIntent {session.PaymentIntentId}.",
                                            userId: userId,
                                            source: "SubscriptionController.HandleGeneralStripeWebhook",
                                            relatedEntityType: "Payment",
                                            relatedEntityId: null,
                                            additionalData: new { SessionId = session.Id, PaymentIntentId = session.PaymentIntentId, ServiceId = serviceId, Metadata = session.Metadata });
                                        if (eventMarkedProcessing && currentEventId != null)
                                        {
                                            await MarkEventAsProcessedAsync(currentEventId, currentEventType ?? stripeEvent.Type, currentAccountId, null, "Failed", "Invalid serviceId in metadata");
                                            eventMarkedProcessing = false;
                                        }
                                        return StatusCode(500, new { error = "Invalid serviceId in metadata - Stripe will retry" });
                                    }
                                    await HandlePendingHireCompleted(userId, amount, serviceId, session.Metadata, session);
                                }
                                else
                                {
                                    // ✅ VALIDACIÓN: Verificar PaymentIntentId antes de usarlo
                                    var paymentIntentId = session.PaymentIntentId ?? "unknown";
                                    // ✅ REMOVED: Load money functionality eliminated - all payments are direct Stripe
                                }
                            }
                            else
                            {
                                
                                // 🚨 LOG CRÍTICO: Metadata inválida en sesión de pago (afecta dinero)
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Invalid metadata in payment session",
                                    details: $"Invalid metadata format in checkout session {session.Id}. PaymentIntentId: {session.PaymentIntentId}, Metadata: {JsonSerializer.Serialize(session.Metadata)}",
                                    source: "SubscriptionController.HandleGeneralStripeWebhook",
                                    relatedEntityType: "Payment",
                                    relatedEntityId: null,
                                    additionalData: new { 
                                        SessionId = session.Id,
                                        PaymentIntentId = session.PaymentIntentId,
                                        Mode = session.Mode,
                                        Metadata = session.Metadata
                                    }
                                );
                                
                                if (eventMarkedProcessing && currentEventId != null)
                                {
                                    await MarkEventAsProcessedAsync(
                                        currentEventId,
                                        currentEventType ?? stripeEvent.Type,
                                        currentAccountId,
                                        null,
                                        "Failed",
                                        "Invalid metadata format");
                                    eventMarkedProcessing = false;
                                }
                                // 🛡️ A3 FIX: 500 (no 400). Metadata inválida puede ser fallo
                                // transitorio del extractor (deserialización) o de Stripe (campos vacíos
                                // temporalmente). 400 hace que Stripe abandone tras 1 entrega; 500
                                // permite reintento durante ~3 días. Queda como "Failed" en BD.
                                return StatusCode(500, new { error = "Invalid metadata format - Stripe will retry" });
                            }
                        }
                        else if (session != null && session.Mode == "subscription")
                        {
                            // ✅ IGNORAR: Suscripciones periódicas ya no se usan
                        }
                        else
                        {
                        }
                        break;

                    // ⚖️ DISPUTAS (chargebacks): antes caían en default y se ignoraban en silencio,
                    // dejando a la plataforma con la pérdida (Stripe retira el cargo + comisión) sin
                    // revertir el transfer ya pagado al experto. Ahora se detectan, registran y alertan.
                    case "charge.dispute.created":
                        await HandleChargeDisputeCreated(stripeEvent.Data.Object as Stripe.Dispute);
                        break;

                    case "charge.dispute.closed":
                        await HandleChargeDisputeClosed(stripeEvent.Data.Object as Stripe.Dispute);
                        break;

                    case "charge.dispute.funds_withdrawn":
                    case "charge.dispute.funds_reinstated":
                        await HandleChargeDisputeFundsEvent(stripeEvent.Type, stripeEvent.Data.Object as Stripe.Dispute);
                        break;

                    // 🛡️ A4: charge.dispute.updated — cambios en una disputa abierta (evidencia, motivo).
                    case "charge.dispute.updated":
                        await HandleChargeDisputeUpdated(stripeEvent.Data.Object as Stripe.Dispute);
                        break;

                    case "charge.refunded":
                        await HandleChargeRefunded(stripeEvent.Data.Object as Charge);
                        break;

                    // 🛡️ V11 FIX: charge.refund.updated — refund cambió status (pending→failed/canceled).
                    // Refund a tarjeta caducada falla DESPUÉS de creación → cliente no recibe dinero.
                    case "charge.refund.updated":
                        await HandleChargeRefundUpdated(stripeEvent.Data.Object as Refund);
                        break;

                    // 🛡️ Finding #7 FIX: charge.refund.created — registrar la creación del refund para
                    // poder detectar refunds rechazados temprano (antes de que llegue charge.refund.updated).
                    case "charge.refund.created":
                        await HandleChargeRefundCreated(stripeEvent.Data.Object as Refund);
                        break;

                    // 🛡️ A4: charge.failed — el cargo falló a nivel de "charge" (no de payment_intent).
                    case "charge.failed":
                        await HandleChargeFailed(stripeEvent.Data.Object as Charge);
                        break;

                    // 🛡️ Round 14 — Q4 FIX: Stripe Radar Early Fraud Warning.
                    // Stripe detecta sospecha de fraude ~48h-7d post-cobro (antes de que el banco
                    // del cliente real abra chargeback formal). Si refundamos PROACTIVAMENTE
                    // ahora: ahorro la dispute fee (~15€) + protejo el dispute rate de la
                    // plataforma (métrica que Stripe usa para suspender Connect accounts).
                    //
                    // El handler NO refunda automáticamente — solo alerta CRÍTICO al admin con
                    // todo el contexto. Refund proactivo requiere decisión humana porque puede
                    // afectar al experto (clawback del transfer ya hecho).
                    //
                    // Doc: https://docs.stripe.com/disputes/prevention/early-fraud-warnings
                    case "radar.early_fraud_warning.created":
                        var efw = stripeEvent.Data.Object as Stripe.Radar.EarlyFraudWarning;
                        if (efw != null)
                        {
                            // 🚨 Finding #18 FIX: marcar el hire correspondiente como RequiresManualReview
                            // para que aparezca en el digest del admin. Antes solo se logueaba — el hire
                            // seguía completable normalmente mientras Stripe ya sospechaba fraude.
                            int? hireIdForEfw = null;
                            int? efwUserId = null;
                            if (!string.IsNullOrEmpty(efw.ChargeId))
                            {
                                try
                                {
                                    // Resolver PaymentIntent desde ChargeId vía Stripe
                                    var chargeSvc = new Stripe.ChargeService();
                                    var chargeFromEfw = await chargeSvc.GetAsync(efw.ChargeId);
                                    var piId = chargeFromEfw?.PaymentIntentId;
                                    if (!string.IsNullOrEmpty(piId))
                                    {
                                        var (hid, _, cid) = await FindHireForPaymentIntentAsync(piId);
                                        hireIdForEfw = hid;
                                        efwUserId = cid;
                                        if (hid.HasValue)
                                        {
                                            var efwHire = await _context.SearchHires.FirstOrDefaultAsync(h => h.Id == hid.Value);
                                            if (efwHire != null && !efwHire.RequiresManualReview)
                                            {
                                                efwHire.RequiresManualReview = true;
                                                efwHire.UpdatedAt = DateTime.UtcNow;
                                                await _context.SaveChangesAsync();
                                            }
                                        }
                                    }
                                }
                                catch (Exception efwLookupEx)
                                {
                                    // Best-effort: aunque falle el lookup, el log critical sigue su curso
                                    await _loggingService.LogWarningAsync(
                                        message: "EFW hire lookup failed (best-effort)",
                                        details: $"EfwId={efw.Id} ChargeId={efw.ChargeId}: {efwLookupEx.Message}",
                                        source: "SubscriptionController.HandleGeneralStripeWebhook.radar.efw",
                                        relatedEntityType: "Charge");
                                }
                            }

                            await _loggingService.LogCriticalAsync(
                                message: "🚨 STRIPE RADAR — Early Fraud Warning (acción admin)",
                                details: $"Stripe detectó posible fraude en charge {efw.ChargeId} (FraudType={efw.FraudType}, Actionable={efw.Actionable}). ACCIÓN ADMIN: revisar y refundar proactivamente ANTES de que el banco abra chargeback (ahorro ~15€ dispute fee + protege dispute rate). EFW Id: {efw.Id}." + (hireIdForEfw.HasValue ? $" SearchHire {hireIdForEfw} marcado RequiresManualReview." : " (no se encontró SearchHire asociado)"),
                                userId: efwUserId,
                                source: "SubscriptionController.HandleGeneralStripeWebhook.radar.efw",
                                relatedEntityType: "Charge",
                                relatedEntityId: hireIdForEfw,
                                additionalData: new
                                {
                                    EfwId = efw.Id,
                                    ChargeId = efw.ChargeId,
                                    FraudType = efw.FraudType,
                                    Actionable = efw.Actionable,
                                    EfwCreated = efw.Created,
                                    SearchHireId = hireIdForEfw
                                });
                        }
                        break;

                    case "payout.paid":
                    case "payout.failed":
                    case "payout.canceled": // 🛡️ A4: ahora también canceled (alerta crítica)
                        await HandlePayoutEvent(stripeEvent.Type, stripeEvent.Data.Object as Payout, stripeEvent.Account);
                        break;

                    // 🔧 FIX B4 (ROUTING): en separate charges & transfers el Transfer lo crea la PLATAFORMA
                    // (sin StripeAccount), así que transfer.reversed/transfer.failed son eventos de "Your account"
                    // y Stripe los entrega a ESTE endpoint (plataforma), no a /webhook (Connect). Se manejan en
                    // AMBOS por robustez ante la config del Dashboard; TryBeginProcessingEventAsync (idempotencia
                    // por EventId) evita doble proceso si llegaran por los dos. transfer.reversed confirma el
                    // clawback (P6); transfer.failed marca el hire para revisión.
                    case "transfer.reversed":
                        await HandleTransferReversed(stripeEvent.Data.Object as Transfer);
                        break;

                    case "transfer.failed":
                        await HandleTransferFailed(stripeEvent.Data.Object as Transfer);
                        break;

                    // 🛡️ Finding #19 FIX: transfer.created — informational. La plataforma crea el Transfer
                    // localmente al ejecutar ProcessMoneyDistribution; este evento confirma que Stripe lo
                    // aceptó. Útil para detectar transfers creados externamente (Dashboard) sin contraparte
                    // en FT. Idempotencia: EventId-level vía TryBeginProcessingEventAsync.
                    case "transfer.created":
                        await HandleTransferCreated(stripeEvent.Data.Object as Transfer);
                        break;

                    // 🛡️ Finding #20 FIX: balance.available — visibilidad del saldo de plataforma para
                    // detectar caídas inesperadas (chargebacks, disputas, payouts fallidos).
                    case "balance.available":
                        await HandleBalanceAvailable(stripeEvent.Data.Object);
                        break;

                    case "invoice.payment_succeeded":
                    case "invoice.payment_failed":
                    case "customer.subscription.created":
                    case "customer.subscription.updated":
                    case "customer.subscription.deleted":
                        // ✅ IGNORAR: Suscripciones periódicas ya no se usan
                        break;

                    default:
                        // 🛡️ Round 9 — A6 FIX: antes el default ignoraba en silencio cualquier evento
                        // desconocido. Si Stripe introduce un nuevo event type relevante (p.ej.
                        // radar.early_fraud_warning.created, review.opened, treasury.*), perdíamos la
                        // señal por completo. Ahora lo logueamos con throttle implícito (idempotencia
                        // por EventId ya filtra duplicados) para detectar nuevos tipos sin spam.
                        await _loggingService.LogInfoAsync(
                            message: $"Stripe webhook event no manejado: {stripeEvent.Type}",
                            details: $"Event {stripeEvent.Id} de tipo '{stripeEvent.Type}' recibido pero no procesado. Si este tipo se vuelve recurrente, considerar añadir un handler explícito.",
                            source: "SubscriptionController.HandleGeneralStripeWebhook.default",
                            relatedEntityType: "StripeEvent",
                            relatedEntityId: null,
                            additionalData: new
                            {
                                EventId = stripeEvent.Id,
                                EventType = stripeEvent.Type,
                                StripeAccount = stripeEvent.Account,
                                Created = stripeEvent.Created
                            });
                        break;
                }

                if (eventMarkedProcessing)
                {
                    await MarkEventAsProcessedAsync(stripeEvent.Id, stripeEvent.Type, stripeEvent.Account, null, "Success");
                    eventMarkedProcessing = false;
                }
                
                // ✅ LOG DIAGNÓSTICO: Webhook general completado exitosamente
                var webhookGeneralEndTimeFinal = DateTime.UtcNow;
                var webhookGeneralDurationFinal = (webhookGeneralEndTimeFinal - webhookGeneralStartTime).TotalMilliseconds;
                var originalColorGenFinal = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    var separatorGenFinal = new string('=', 80);
                    Console.WriteLine($"\n{separatorGenFinal}");
                    Console.WriteLine($"✅ [WEBHOOK GENERAL] Webhook general procesado exitosamente");
                    Console.WriteLine($"{separatorGenFinal}");
                    Console.WriteLine($"EventId: {currentEventId ?? "N/A"}");
                    Console.WriteLine($"EventType: {currentEventType ?? "N/A"}");
                    Console.WriteLine($"AccountId: {currentAccountId ?? "N/A"}");
                    Console.WriteLine($"Duración total: {webhookGeneralDurationFinal:F2}ms");
                    Console.WriteLine($"Timestamp: {webhookGeneralEndTimeFinal:yyyy-MM-dd HH:mm:ss.fff}");
                    Console.WriteLine($"{separatorGenFinal}\n");
                    Console.ForegroundColor = originalColorGenFinal;
                }
                catch { }
                
                return Ok();
            }
            catch (StripeException e)
            {
                // ✅ SEGURIDAD: Si la signature es inválida, ConstructEvent lanza StripeException
                // Esto previene ataques de replay e inyección de eventos falsos
                if (e.Message?.Contains("signature") == true || e.Message?.Contains("Invalid signature") == true)
                {
                    // 🚨 LOG CRÍTICO: Intento de ataque con signature inválida en webhook general
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Invalid general webhook signature - potential attack",
                        details: $"Invalid general webhook signature detected. This could be a security attack. Signature: {signatureHeader?.Substring(0, Math.Min(50, signatureHeader?.Length ?? 0))}...",
                        source: "SubscriptionController.HandleGeneralStripeWebhook",
                        relatedEntityType: "Security",
                        additionalData: new { 
                            Action = "GeneralWebhookSignatureValidation",
                            SignatureHeader = signatureHeader?.Substring(0, Math.Min(50, signatureHeader?.Length ?? 0)),
                            Error = e.Message
                        }
                    );
                    
                    return BadRequest(new { error = "Invalid webhook signature" });
                }
                // 🚨 LOG CRÍTICO: Error de Stripe en webhook general (puede afectar dinero)
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Stripe general webhook error",
                    details: $"Stripe exception in general webhook handler: {e.Message}, Type: {e.StripeError?.Type}, Code: {e.StripeError?.Code}",
                    source: "SubscriptionController.HandleGeneralStripeWebhook",
                    relatedEntityType: "Webhook",
                    additionalData: new { 
                        Action = "GeneralStripeWebhook",
                        StripeError = e.Message,
                        StripeErrorType = e.StripeError?.Type,
                        StripeErrorCode = e.StripeError?.Code,
                        Payload = json?.Substring(0, Math.Min(500, json?.Length ?? 0))
                    }
                );
                if (eventMarkedProcessing && currentEventId != null)
                {
                    await MarkEventAsProcessedAsync(
                        currentEventId,
                        currentEventType ?? "unknown",
                        currentAccountId,
                        null,
                        "Failed",
                        e.Message);
                    eventMarkedProcessing = false;
                }
                // 🔁 A5: error de Stripe NO-firma (api_connection, rate_limit, lock_timeout, etc.) es
                // transitorio → devolver 500 para que Stripe REINTENTE la entrega. Antes devolvía 400, que
                // Stripe trata como permanente y NO reintenta → el evento se perdía. (La firma inválida sí
                // devuelve 400 más arriba.) El evento quedó marcado "Failed" → TryBeginProcessingEvent lo
                // re-reclama en la reentrega.
                return StatusCode(500, new { error = e.Message });
            }
            catch (Exception e)
            {
                // 🚨 LOG CRÍTICO: Error general en webhook general (puede afectar dinero)
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: General webhook error",
                    details: $"General exception in general webhook handler: {e.Message}",
                    source: "SubscriptionController.HandleGeneralStripeWebhook",
                    relatedEntityType: "Webhook",
                    additionalData: new { 
                        Action = "GeneralStripeWebhook",
                        Exception = e.Message,
                        StackTrace = e.StackTrace,
                        Payload = json?.Substring(0, Math.Min(500, json?.Length ?? 0))
                    }
                );
                if (eventMarkedProcessing && currentEventId != null)
                {
                    await MarkEventAsProcessedAsync(
                        currentEventId,
                        currentEventType ?? "unknown",
                        currentAccountId,
                        null,
                        "Failed",
                        e.Message);
                    eventMarkedProcessing = false;
                }
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        // TODO P3-9 (DEFERRED): Reemplazar EnsurePaymentCapturedAsync síncrono por outbox completo.
        // 1) Marcar SearchHire.CaptureStatus = "Pending" en la misma transacción que crea el hire.
        // 2) Tras commit, BackgroundJob.Enqueue<IPaymentCaptureService>(s => s.CaptureForHireAsync(hire.Id)).
        // 3) PaymentCaptureService usa FOR UPDATE + IdempotencyKey = $"capture-{hireId}" y marca Captured/Failed.
        // 4) Watchdog RecurringJob cada 30 min recoge CaptureStatus="Pending" antiguos (>1h) y reencola.
        // P1-5 ya cubre happy path + compensación; queda pendiente la reescritura por riesgo alto
        // (HandlePendingHireCompleted tiene cientos de líneas y depende del webhook flow).
        private async Task HandlePendingHireCompleted(int userId, decimal amount, int serviceId, Dictionary<string, string> metadata, Session session)
        {
            // ✅ VALIDACIÓN: Verificar que session y PaymentIntentId no sean null
            if (session == null)
            {
                // ✅ No lanzar excepción aquí - retornar silenciosamente para no fallar el webhook
                // El webhook ya respondió 200 OK, pero el procesamiento falló
                await _loggingService.LogCriticalAsync(
                    $"Session is null in HandlePendingHireCompleted",
                    $"UserId: {userId}, ServiceId: {serviceId}",
                    userId,
                    "SubscriptionController.HandlePendingHireCompleted",
                    "Payment",
                    serviceId,
                    new { UserId = userId, ServiceId = serviceId }
                );
                return;
            }

            if (string.IsNullOrEmpty(session.PaymentIntentId))
            {
                // ✅ No lanzar excepción aquí - retornar silenciosamente para no fallar el webhook
                await _loggingService.LogCriticalAsync(
                    $"PaymentIntentId is null or empty in HandlePendingHireCompleted",
                    $"UserId: {userId}, ServiceId: {serviceId}, SessionId: {session.Id}",
                    userId,
                    "SubscriptionController.HandlePendingHireCompleted",
                    "Payment",
                    serviceId,
                    new { UserId = userId, ServiceId = serviceId, SessionId = session.Id }
                );
                return;
            }
            // P2-2: lectura simple de Users. No hay mutación posterior sobre la fila
            // que dependa de un lock pesimista (sólo se usa user.Email para envío
            // de factura más abajo). El FOR UPDATE con commit inmediato anterior no
            // protegía nada — el lock se liberaba antes de cualquier mutación.
            // 🛡️ N5 FIX: IgnoreQueryFilters para que webhooks de usuarios borrados
            // SE PROCESEN igual (crear el SearchHire, mover dinero, vincular factura)
            // en lugar de salir silenciosos por el query filter global User.IsDeleted=false.
            // Sin esto, si el cliente borra su cuenta MIENTRAS un checkout.session.completed
            // está en flight, el hire nunca se crea pero el dinero SÍ se cobró → fondos en limbo.
            var user = await _context.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }
            if (user.IsDeleted)
            {
                // Webhook llegó tras delete del cliente: log critical para que admin reembolse manual.
                await _loggingService.LogCriticalAsync(
                    message: "N5: webhook checkout.session.completed for DELETED user",
                    details: $"User {userId} ya está marcado IsDeleted. El cliente borró su cuenta entre el checkout y el webhook. Dinero cobrado pero no se puede crear hire. ACCIÓN: refund manual desde Stripe Dashboard o reconciliación. ServiceId={serviceId}.",
                    userId: userId,
                    source: "SubscriptionController.HandlePendingHireCompleted.N5",
                    relatedEntityType: "SearchService",
                    relatedEntityId: serviceId);
                return;
            }

            var service = await _context.SearchServices.FindAsync(serviceId);
            if (service == null)
            {
                // 🚨 P1 FIX (Finding #1): el servicio no existe (borrado, ID corrupto, etc.) pero
                // el cliente YA pagó. Antes salía silente → dinero atrapado sin hire ni log crítico.
                // Ahora log CRÍTICO + intento de cancel/refund para no perder el caso.
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL P1: SearchService not found - payment received without service",
                    details: $"Webhook checkout.session.completed: ServiceId={serviceId} no existe en SearchServices, pero el cliente {userId} pagó. PaymentIntentId: {session.PaymentIntentId}. ACCIÓN AUTO: cancel/refund del PI. ACCIÓN ADMIN: verificar y notificar al cliente.",
                    userId: userId,
                    source: "SubscriptionController.HandlePendingHireCompleted",
                    relatedEntityType: "SearchService",
                    relatedEntityId: serviceId,
                    additionalData: new { ServiceId = serviceId, PaymentIntentId = session.PaymentIntentId, SessionId = session.Id });
                if (!string.IsNullOrEmpty(session.PaymentIntentId))
                {
                    await CancelOrRefundDuplicatePaymentIntentAsync(session.PaymentIntentId, userId, 0);
                }
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }

            // ✅ OPTIMIZACIÓN: Construir DTOs desde campos individuales de metadata (evita límite de 500 caracteres)
            // En lugar de deserializar JSON completo, construir desde campos individuales
            CreateSearchDto searchDto;
            CreateSearchParameterDto parameterDto;
            
            try
            {
                // Construir SearchDto desde campos individuales
                // ✅ Usar el serviceId que ya viene como parámetro del método (más confiable que metadata)
                searchDto = new CreateSearchDto
                {
                    ServiceId = serviceId, // ✅ Usar el parámetro del método directamente
                    Title = metadata.GetValueOrDefault("searchTitle", ""),
                    Description = metadata.GetValueOrDefault("searchDescription", ""),
                    Frequency = int.TryParse(metadata.GetValueOrDefault("frequency", "24"), out int parsedFrequency) ? parsedFrequency : 24,
                    IsActive = true,
                    StartDate = DateTime.UtcNow
                };

                // Construir ParameterDto desde campos individuales
                parameterDto = new CreateSearchParameterDto
                {
                    Keywords = metadata.GetValueOrDefault("keywords", ""),
                    UserSearch = metadata.GetValueOrDefault("userSearch", ""),
                    Latitude = metadata.GetValueOrDefault("latitude", ""),
                    Longitude = metadata.GetValueOrDefault("longitude", ""),
                    LocationName = metadata.GetValueOrDefault("locationName", ""),
                    ShippingAvailable = false, // Valor por defecto
                    StrictMatchOnly = false, // Valor por defecto
                    Category = int.TryParse(metadata.GetValueOrDefault("categoryId", ""), out int paramCategory) ? paramCategory : null,
                    LocationRange = int.TryParse(metadata.GetValueOrDefault("locationRange", ""), out int locationRange) ? locationRange : null,
                    MinPrice = null,
                    MaxPrice = null,
                    BrandId = null,
                    ModelId = null,
                    ServiceTypeId = int.TryParse(metadata.GetValueOrDefault("serviceTypeId", ""), out int serviceTypeId) ? serviceTypeId : null,
                    PlatformIds = new List<int>() // Valor por defecto
                };
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync(
                    message: "Error constructing DTOs from metadata",
                    details: $"Failed to construct SearchDto/ParameterDto from metadata. Error: {ex.Message}",
                    userId: userId,
                    source: "SubscriptionController.HandlePendingHireCompleted",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: null
                );
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }

            if (searchDto == null || parameterDto == null)
            {
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }

            var activeSearchCount = await _context.Searches.CountAsync(s => s.UserId == userId && s.IsActive);
            var subscriptionLimits = await _subscriptionService.GetUserSubscriptionLimits(userId);

            //PARA MANEJAR SUSCRIPCIONES
            //if (activeSearchCount >= subscriptionLimits.MaxSearches)
            //{
            //    _logger.LogError("User has reached max searches: userId={UserId}, maxSearches={MaxSearches}", userId, subscriptionLimits.MaxSearches);
            //    throw new Exception($"User has reached the limit of {subscriptionLimits.MaxSearches} active searches");
            //}
            //if (searchDto.Frequency < subscriptionLimits.MinSearchInterval)
            //{
            //    _logger.LogError("Search frequency below minimum: userId={UserId}, frequency={Frequency}, minInterval={MinInterval}", userId, searchDto.Frequency, subscriptionLimits.MinSearchInterval);
            //    throw new Exception($"Minimum search interval is {subscriptionLimits.MinSearchInterval} hours");
            //}

            // ✅ COMENTADO: Verificación de teléfono ya no es necesaria
            /*
            if (!user.PhoneVerified)
            {
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }
            */

            // ✅ FIX CRÍTICO: Obtener StatusId ANTES de iniciar la transacción para evitar conflictos con ExecutionStrategy
            var pendingStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Pending.ToStringValue());
            // 🔒 FIX C8: IDs de estados ACTIVOS (resueltos por StatusValue, robusto ante re-seed) para el guard
            // anti-doble-hire. Se obtienen ANTES de la transacción (mismo motivo: evitar ExecutionStrategy).
            var awaitingDecisionStatusIdForGuard = await GetStatusIdByValueAsync(SearchHireStatus.AwaitingClientDecision.ToStringValue());
            var disputedStatusIdForGuard = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());
            
            // ✅ FIX CRÍTICO: Obtener TODAS las queries ANTES de iniciar la transacción para evitar ExecutionStrategy
            // Estas queries activan ExecutionStrategy automáticamente si están dentro de una transacción
            var expertProfile = await _context.ExpertProfiles
                .AsNoTracking() // ✅ FIX: AsNoTracking evita que EF Core intente usar ExecutionStrategy
                .FirstOrDefaultAsync(z => z.Id == service.ExpertProfileId);

            // 🛡️ D1 FIX: si el SearchService apunta a un ExpertProfile.Id que no existe
            // (data corruption, race de eliminación), expertProfile sería null y antes el
            // fallback `?? 0` creaba el SearchHire con ExpertId=0 → FK violation al
            // SaveChanges. Abortamos con log critical para que el admin investigue (el
            // checkout ya cobró al cliente; necesita refund o reasignación del servicio).
            if (expertProfile == null)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: HandlePendingHireCompleted aborted — ExpertProfile not found",
                    details: $"Checkout cobrado pero SearchService.ExpertProfileId={service.ExpertProfileId} no resuelve a ningún ExpertProfile. ClientId={userId} ServiceId={service.Id}. ACCIÓN: refund manual del cliente o reasignación del servicio. NO se creó SearchHire (evita FK violation con ExpertId=0).",
                    userId: userId,
                    source: "SubscriptionController.HandlePendingHireCompleted",
                    relatedEntityType: "SearchService",
                    relatedEntityId: service.Id);
                return;
            }
            var expertuserid = expertProfile.UserId; // ya no necesita ?? 0

            // Validar que el experto no se contrate a sí mismo
            if (expertuserid == userId)
            {
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }

            // 🛡️ R9 FIX: validar IsOnVacation EN EL WEBHOOK. Antes solo se validaba al CREAR
            // el checkout session (línea ~537 SearchController). Race window: si el experto
            // activa vacation mode ENTRE checkout creation (cliente abre Stripe) y webhook
            // (cliente paga), el hire se creaba para experto inactivo → experto recibe
            // notificación de hire que no puede atender → conflict + manual refund.
            // Solución: si IsOnVacation al llegar el webhook, refund automático del cliente
            // y log Critical para que admin pueda contactar al experto (puede ser olvido).
            // NO se crea SearchHire (el cliente recibe refund completo).
            if (expertProfile.IsOnVacation)
            {
                await _loggingService.LogCriticalAsync(
                    message: "R9: Webhook llegó pero experto está en vacation mode → refund automático",
                    details: $"User {userId} pagó por servicio {service.Id} del experto {expertuserid}, pero el experto activó IsOnVacation entre checkout y webhook. PaymentIntentId: {session.PaymentIntentId}. ACCIÓN AUTO: refund/cancel del PI. ACCIÓN ADMIN: verificar si el experto desactivó vacation por error y notificar al cliente.",
                    userId: userId,
                    source: "SubscriptionController.HandlePendingHireCompleted.R9",
                    relatedEntityType: "SearchService",
                    relatedEntityId: service.Id,
                    additionalData: new
                    {
                        ExpertUserId = expertuserid,
                        ExpertProfileId = expertProfile.Id,
                        PaymentIntentId = session.PaymentIntentId,
                        Action = "auto_refund_vacation_race"
                    });

                // Cancel del PI directamente (estamos en checkout.session.completed → PI en
                // requires_capture por manual capture). Si por algún motivo ya estuviera
                // succeeded, devolverá StripeException y el admin lo procesará manualmente.
                try
                {
                    var piSvc = new Stripe.PaymentIntentService();
                    await piSvc.CancelAsync(session.PaymentIntentId,
                        new Stripe.PaymentIntentCancelOptions { CancellationReason = "abandoned" },
                        new Stripe.RequestOptions { IdempotencyKey = $"r9-vacation-cancel-{session.PaymentIntentId}" });
                }
                catch (Exception cancelEx)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "R9: cancel del PI falló — admin debe procesar refund manualmente",
                        details: $"PaymentIntentId: {session.PaymentIntentId}. Error: {cancelEx.Message}. El cliente {userId} pagó pero no se le ha refundeado/cancelado. URGENTE.",
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted.R9",
                        relatedEntityType: "PaymentIntent",
                        relatedEntityId: null);
                }
                return;
            }

            // 🛡️ Round 13 — N7 FIX: race window análoga a R9 (vacation mode) para service.IsActive.
            // Si el experto desactivó (eliminó del catálogo) el servicio ENTRE checkout y webhook,
            // se creaba un SearchHire sobre un servicio "fantasma" — cliente cobrado, experto sin
            // saber del hire, servicio invisible. Mismo refund automático + log critical.
            if (!service.IsActive)
            {
                await _loggingService.LogCriticalAsync(
                    message: "N7: Webhook llegó pero el servicio fue desactivado por el experto → refund automático",
                    details: $"User {userId} pagó por servicio {service.Id} (experto {expertuserid}), pero el experto eliminó el servicio del catálogo entre checkout y webhook. PaymentIntentId: {session.PaymentIntentId}. ACCIÓN AUTO: cancel del PI. ACCIÓN ADMIN: notificar al cliente que el servicio ya no está disponible.",
                    userId: userId,
                    source: "SubscriptionController.HandlePendingHireCompleted.N7",
                    relatedEntityType: "SearchService",
                    relatedEntityId: service.Id,
                    additionalData: new
                    {
                        ExpertUserId = expertuserid,
                        ExpertProfileId = expertProfile?.Id,
                        PaymentIntentId = session.PaymentIntentId,
                        Action = "auto_refund_service_deleted_race"
                    });

                try
                {
                    var piSvc = new Stripe.PaymentIntentService();
                    await piSvc.CancelAsync(session.PaymentIntentId,
                        new Stripe.PaymentIntentCancelOptions { CancellationReason = "abandoned" },
                        new Stripe.RequestOptions { IdempotencyKey = $"n7-service-deleted-cancel-{session.PaymentIntentId}" });
                }
                catch (Exception cancelEx)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "N7: cancel del PI falló — admin debe procesar refund manualmente",
                        details: $"PaymentIntentId: {session.PaymentIntentId}. Error: {cancelEx.Message}. El cliente {userId} pagó pero no se le ha refundeado/cancelado por servicio desactivado. URGENTE.",
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted.N7",
                        relatedEntityType: "PaymentIntent",
                        relatedEntityId: null);
                }
                return;
            }

            // Obtener la disponibilidad actual del experto al momento de la contratación
            int? currentAvailabilityId = null;
            if (expertProfile != null)
            {
                var currentAvailability = await _context.ExpertAvailabilities
                    .AsNoTracking() // ✅ FIX: AsNoTracking evita que EF Core intente usar ExecutionStrategy
                    .Where(ea => ea.ExpertId == expertProfile.Id && ea.IsActive && ea.EffectiveTo == null)
                    .OrderByDescending(ea => ea.EffectiveFrom)
                    .FirstOrDefaultAsync();
                currentAvailabilityId = currentAvailability?.Id;
            }

            // ✅ INTERNACIONALIZACIÓN: Obtener timezone y country del experto al momento de crear la contratación
            // Esto crea un snapshot que protege las contrataciones activas si el experto cambia de ubicación
            var expertTimezone = expertProfile?.Timezone ?? "UTC";
            var expertCountry = expertProfile?.Country;

            // ✅ FIX CRÍTICO: Obtener platforms ANTES de iniciar la transacción
            List<Platform> platforms = new List<Platform>();
            if (parameterDto.PlatformIds != null && parameterDto.PlatformIds.Any())
            {
                platforms = await _context.Platforms
                    .AsNoTracking() // ✅ FIX: AsNoTracking evita que EF Core intente usar ExecutionStrategy
                    .Where(p => parameterDto.PlatformIds.Contains(p.Id))
                    .ToListAsync();
                if (platforms.Count != parameterDto.PlatformIds.Count)
                {
                    return; // ✅ CORRECTO: Salir silenciosamente si hay IDs inválidos
                }
            }
            
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
            using var transaction = await _context.Database.BeginTransactionAsync();
            SearchHire? searchHire = null;
            int searchHireId = 0; // ✅ FIX: Declarar searchHireId antes del try para que esté disponible en catch
            try
            {
                    // 🔒 FIX C8 (doble-hire cross-flow / carrera replicas:2): serializa por (ClientId, ServiceId)
                    // con un advisory lock de transacción (se libera al COMMIT/ROLLBACK). El 2º webhook concurrente
                    // espera aquí; al entrar, ya ve el hire del 1º. Si ya existe un SearchHire ACTIVO para
                    // (ClientId, ServiceId), el PI entrante es un DUPLICADO (otro flujo/carrera): se cancela/reembolsa
                    // y NO se crea 2º hire (la idempotencia de #6 solo cubre el mismo PI; esto cubre PIs distintos).
                    await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0}, {1})", userId, service.Id);
                    var existingActiveHire = await _context.SearchHires
                        .AsNoTracking()
                        .FirstOrDefaultAsync(sh => sh.ClientId == userId
                                                && sh.SearchServiceId == service.Id
                                                && (sh.StatusId == pendingStatusId
                                                    || sh.StatusId == awaitingDecisionStatusIdForGuard
                                                    || sh.StatusId == disputedStatusIdForGuard));
                    if (existingActiveHire != null)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "Duplicate active SearchHire detected at webhook - aborting 2nd charge",
                            details: $"Ya existe SearchHire activo #{existingActiveHire.Id} para ClientId {userId}, ServiceId {service.Id}. PaymentIntent entrante {session.PaymentIntentId} se trata como DUPLICADO: se cancela/reembolsa y NO se crea un 2º hire.",
                            userId: userId,
                            source: "SubscriptionController.HandlePendingHireCompleted",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: existingActiveHire.Id,
                            additionalData: new { PaymentIntentId = session.PaymentIntentId, ExistingHireId = existingActiveHire.Id, ClientId = userId, ServiceId = service.Id });

                        await CancelOrRefundDuplicatePaymentIntentAsync(session.PaymentIntentId, userId, existingActiveHire.Id);

                        await _loggingService.LogWarningAsync(
                            message: "Contratación duplicada evitada",
                            details: "Ya tenías una contratación activa para este servicio. No se te ha cobrado por segunda vez.",
                            userId: userId,
                            source: "SubscriptionController.HandlePendingHireCompleted",
                            relatedEntityType: "Payment",
                            relatedEntityId: service.Id,
                            notifyUser: true);

                        await transaction.CommitAsync(); // libera el advisory lock; no se persiste nada (no hubo Add)
                        return;
                    }

                    // ✅ REMOVED: Balance system eliminated - all payments are direct Stripe
                
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                    try
                    {
                        await transaction.RollbackAsync();
                    }
                    catch { }
                    
                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    // Re-crear la búsqueda en el nuevo contexto si es necesario
                    // Por ahora, solo loguear el error
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Connection disposed in HandlePendingHireCompleted - initial SaveChanges",
                        details: $"Connection disposed while saving initial changes. UserId: {userId}, ServiceId: {serviceId}",
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted",
                        relatedEntityType: "Payment",
                        relatedEntityId: serviceId
                    );
                    return; // Salir si no se puede recuperar
                }
                catch (ObjectDisposedException disposedEx)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                    try
                    {
                        await transaction.RollbackAsync();
                    }
                    catch { }
                    
                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Connection disposed in HandlePendingHireCompleted - initial SaveChanges",
                        details: $"Connection disposed while saving initial changes. UserId: {userId}, ServiceId: {serviceId}",
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted",
                        relatedEntityType: "Payment",
                        relatedEntityId: serviceId
                    );
                    return; // Salir si no se puede recuperar
                }

                // Create search
                var search = new Search
                {
                    UserId = userId,
                    Frequency = searchDto.Frequency,
                    Title = searchDto.Title,
                    Description = searchDto.Description,
                    IsActive = searchDto.IsActive,
                    NextExecution = DateTime.UtcNow,
                    StartDate = searchDto.StartDate,
                    CreatedAt = DateTime.UtcNow
                };
                await _context.Searches.AddAsync(search);
                // 🔧 FIX (hallazgo C): sin recovery con autocommit (rompía la atomicidad). Si la conexión se
                // cae aquí, la excepción sube al catch externo → rollback total + 500 → Stripe reintenta idempotente.
                    await _context.SaveChangesAsync();

                // Create search parameters
                var searchParameter = new SearchParameter
                {
                    Keywords = parameterDto.Keywords,
                    UserSearch = parameterDto.UserSearch,
                    Latitude = parameterDto.Latitude,
                    Longitude = parameterDto.Longitude,
                    LocationName = parameterDto.LocationName, // ✅ NUEVO: Incluir LocationName
                    ShippingAvailable = parameterDto.ShippingAvailable,
                    StrictMatchOnly = parameterDto.StrictMatchOnly,
                    Category = parameterDto.Category,
                    LocationRange = parameterDto.LocationRange,
                    MinPrice = parameterDto.MinPrice,
                    MaxPrice = parameterDto.MaxPrice,
                    BrandId = parameterDto.BrandId,
                    ModelId = parameterDto.ModelId,
                    ServiceTypeId = parameterDto.ServiceTypeId,
                    SearchId = search.Id
                };
                await _context.SearchParameters.AddAsync(searchParameter);
                // 🔧 FIX (hallazgo C): sin recovery con autocommit. Si la conexión se cae, sube al catch externo
                // → rollback total + 500 → Stripe reintenta idempotente.
                    await _context.SaveChangesAsync();

                // Create platform associations (platforms ya obtenidos antes de la transacción)
                if (platforms.Any())
                {
                    foreach (var platform in platforms)
                    {
                        _context.SearchParameterPlatforms.Add(new SearchParameterPlatform
                        {
                            SearchParameterId = searchParameter.SearchParameterId,
                            PlatformId = platform.Id
                        });
                    }
                }

                // ✅ STRIPE TAX: Obtener tax breakdown de la Checkout Session (NO PaymentIntent)
                // El tax breakdown está en la Session, no en el PaymentIntent
                // 🛡️ N1 FIX: si el experto editó service.Price entre el checkout init y este webhook,
                // service.Price aquí refleja el precio NUEVO, no el que se cobró. La metadata "amount"
                // se grabó al crear la Session con el precio del momento (snapshot), así que la usamos
                // como fallback de PRIORIDAD ALTA cuando session.AmountTotal no esté disponible.
                // Si session.AmountTotal sí viene (caso normal), prevalecerá sobre este snapshot.
                decimal totalAmount = service.Price;
                if (decimal.TryParse(metadata.GetValueOrDefault("amount", ""), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var metadataAmountSnapshot) && metadataAmountSnapshot > 0)
                {
                    if (Math.Abs(metadataAmountSnapshot - service.Price) > 0.01m)
                    {
                        await _loggingService.LogWarningAsync(
                            message: "N1: service.Price cambió entre checkout init y completed",
                            details: $"SessionId {session.Id}: precio en metadata snapshot={metadataAmountSnapshot}€, precio live ahora={service.Price}€. Usaremos el snapshot (lo que realmente se cobró).",
                            userId: userId,
                            source: "SubscriptionController.HandlePendingHireCompleted.N1",
                            relatedEntityType: "SearchService",
                            relatedEntityId: service.Id);
                    }
                    totalAmount = metadataAmountSnapshot;
                }
                decimal? taxAmount = null;
                decimal? baseAmount = null;
                // 🔧 FIX D11 (IVA no recaudado): flag para marcar el hire si Stripe devolvió tax=0 por
                // falta de registro fiscal (taxability_reason "not_collecting"), NO por reverse charge B2B.
                bool taxNotCollectedNeedsReview = false;

                // 🔧 FISCAL FLIP: declaraciones aquí (outer scope) para que estén visibles en la creación
                // del SearchHire más abajo. Se POBLAN dentro del try (donde existe sessionWithTax).
                string? clientVatNumber = null;
                string? clientVatCountryCode = null;

                try
                {
                    var sessionService = new SessionService();
                    var sessionGetOptions = new SessionGetOptions
                    {
                        Expand = new List<string>
                        {
                            "total_details.breakdown", // breakdown detallado (taxability_reason por línea)
                            // 🔧 FISCAL FLIP: TaxIds del cliente (necesarios para capturar NIF cliente y, en el
                            // futuro, validar contra VIES y aplicar reverse-charge). TaxIdCollection=true ya está
                            // activado en el checkout; sin este expand vienen null en la respuesta.
                            "customer_details.tax_ids"
                        }
                    };
                    var sessionWithTax = await sessionService.GetAsync(session.Id, sessionGetOptions);

                    // 🔧 FISCAL FLIP: extraer NIF cliente desde customer_details.tax_ids. Se persiste SIEMPRE
                    // (también pre-flip) para tener histórico. Validación VIES vendrá vía IViesValidator (stub hoy).
                    try
                    {
                        var taxIds = sessionWithTax?.CustomerDetails?.TaxIds;
                        if (taxIds != null)
                        {
                            // Priorizar eu_vat (intracomunitario → reverse-charge); fallback es_cif (NIF/CIF nacional).
                            var preferred = taxIds.FirstOrDefault(t => t.Type == "eu_vat")
                                           ?? taxIds.FirstOrDefault(t => t.Type == "es_cif");
                            if (preferred != null && !string.IsNullOrWhiteSpace(preferred.Value))
                            {
                                var raw = preferred.Value.Trim();
                                if (preferred.Type == "eu_vat" && raw.Length >= 2)
                                {
                                    // eu_vat: prefijo país = 2 primeros chars (ej. "ESB12345678" → "ES" + "B12345678").
                                    clientVatCountryCode = raw.Substring(0, 2).ToUpperInvariant();
                                    clientVatNumber = raw.Substring(2);
                                }
                                else
                                {
                                    clientVatNumber = raw;
                                    clientVatCountryCode = "ES";
                                }
                            }
                        }
                    }
                    catch (Exception vatEx)
                    {
                        // Fallo extrayendo NIF: NO bloqueamos la contratación. Log warning.
                        await _loggingService.LogWarningAsync(
                            message: "No se pudo extraer NIF cliente de Stripe TaxIds (no bloqueante)",
                            details: $"Session {session.Id}: {vatEx.Message}",
                            userId: userId,
                            source: "SubscriptionController.HandlePendingHireCompleted",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: null
                        );
                    }

                    // 🛡️ Round 28 MUD-AO: persistir TaxId/FiscalCountry en Users (no solo en SearchHire).
                    // Antes el NIF se guardaba SOLO en SearchHire.ClientVatNumber. Para DAC7 (EU Directive
                    // 2021/514) la plataforma debe reportar TIN consolidado por seller cross-hire, no
                    // por contratación individual. Sin esto, la generación del modelo 238 anual tiene
                    // que reconstruir el TIN por aggregate del último hire de cada usuario — frágil y
                    // pierde el TIN si el último hire es anonymizado por GDPR.
                    try
                    {
                        var userToUpdate = await _context.Users
                            .FirstOrDefaultAsync(u => u.Id == userId);
                        if (userToUpdate != null)
                        {
                            var anyMutation = false;
                            if (!string.IsNullOrWhiteSpace(clientVatNumber)
                                && (string.IsNullOrWhiteSpace(userToUpdate.TaxId)
                                    || !string.Equals(userToUpdate.TaxId, clientVatNumber, System.StringComparison.OrdinalIgnoreCase)))
                            {
                                userToUpdate.TaxId = clientVatNumber;
                                userToUpdate.TaxIdCountry = clientVatCountryCode; // ej "ES", "DE"
                                anyMutation = true;
                            }
                            // FiscalCountry desde customer_details.address.country (más fiable que el
                            // VAT prefix para usuarios sin VAT — el VAT es opcional, address no).
                            var customerCountry = sessionWithTax?.CustomerDetails?.Address?.Country;
                            if (!string.IsNullOrWhiteSpace(customerCountry))
                            {
                                var normalized = customerCountry.Trim().ToUpperInvariant();
                                if (!string.Equals(userToUpdate.FiscalCountry, normalized, System.StringComparison.Ordinal))
                                {
                                    userToUpdate.FiscalCountry = normalized;
                                    userToUpdate.FiscalCountryChangedAt = DateTime.UtcNow;
                                    anyMutation = true;
                                }
                            }
                            if (anyMutation)
                            {
                                await _context.SaveChangesAsync();
                                // 🛡️ Round 28 MUD-AR: redactar TaxId en logs (PII fiscal RGPD Art.9 +
                                // LOPDGDD). El admin solo necesita confirmar que la persistencia
                                // ocurrió, no el valor exacto. Si necesita el valor, está en la
                                // tabla Users (acceso controlado por rol). Mostramos los últimos
                                // 3 chars para soporte forense + el país (no es PII por sí solo).
                                var redactedTaxId = string.IsNullOrEmpty(userToUpdate.TaxId)
                                    ? "(none)"
                                    : userToUpdate.TaxId.Length <= 3
                                        ? "***"
                                        : "***" + userToUpdate.TaxId.Substring(userToUpdate.TaxId.Length - 3);
                                await _loggingService.LogInfoAsync(
                                    message: "MUD-AO: Users tax fields synced from Stripe checkout",
                                    details: $"User {userId}: TaxId={redactedTaxId} ({userToUpdate.TaxIdCountry ?? "?"}), FiscalCountry={userToUpdate.FiscalCountry ?? "?"} (from session {session.Id}). Modelo 347 / reverse-charge B2B aggregation. NB: para modelo 238 DAC7 falta TIN del experto, no del cliente.",
                                    userId: userId,
                                    source: "SubscriptionController.HandlePendingHireCompleted.MUD-AO",
                                    relatedEntityType: "User",
                                    relatedEntityId: userId);
                            }
                        }
                    }
                    catch (Exception persistEx)
                    {
                        // No bloqueamos checkout por fallo de wiring fiscal — el SearchHire ya tiene el VAT.
                        await _loggingService.LogWarningAsync(
                            message: "MUD-AO: failed to persist Users tax fields (non-blocking)",
                            details: $"User {userId} session {session.Id}: {persistEx.Message}. SearchHire.ClientVatNumber sigue con el dato; el wiring Users es defensa-en-profundidad para DAC7.",
                            userId: userId,
                            source: "SubscriptionController.HandlePendingHireCompleted.MUD-AO",
                            relatedEntityType: "User",
                            relatedEntityId: userId);
                    }

                    if (sessionWithTax.AmountTotal.HasValue)
                    {
                        totalAmount = sessionWithTax.AmountTotal.Value / 100m; // Total pagado (en centavos, dividir por 100)
                        taxAmount = (sessionWithTax.TotalDetails?.AmountTax ?? 0) / 100m; // IVA (en centavos, dividir por 100)
                        baseAmount = totalAmount - taxAmount; // Base pre-tax
                        
                        // 🔧 FIX D11 (IVA no recaudado): Stripe puede devolver AmountTax==0 SIN error en dos
                        // escenarios MUY distintos que antes se trataban igual (0 = IVA legítimo):
                        //   a) status "requires_location_inputs" → faltan datos de ubicación (ya contemplado).
                        //   b) status "complete" + amount_tax 0 → lo explica taxability_reason (por línea, en el
                        //      breakdown ya expandido): "reverse_charge" = B2B intracomunitario, 0 LEGÍTIMO (no
                        //      alertar); "not_collecting"/otros = NO hay registro fiscal en la jurisdicción del
                        //      comprador → siendo MoR, es IVA NO recaudado → alerta + revisión.
                        // (NB: automatic_tax.status NO tiene "not_collecting"/"collecting"; esos son taxability_reason.)
                        var taxabilityReasons = sessionWithTax.TotalDetails?.Breakdown?.Taxes?
                            .Select(t => t.TaxabilityReason)
                            .Where(r => !string.IsNullOrEmpty(r))
                            .ToList() ?? new List<string>();
                        bool isReverseChargeOnly = taxabilityReasons.Count > 0
                            && taxabilityReasons.All(r => r == "reverse_charge");

                        if (sessionWithTax.AutomaticTax?.Status == "requires_location_inputs")
                        {
                            // Stripe necesita más información de ubicación - usar precio completo como fallback
                            await _loggingService.LogWarningAsync(
                                message: "Stripe Tax requires location inputs - using full amount as base",
                                details: $"Session {session.Id} requires location inputs for tax calculation. Using full amount {totalAmount}€ as base amount.",
                                userId: userId,
                                source: "SubscriptionController.HandlePendingHireCompleted",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: null
                            );
                            baseAmount = totalAmount;
                            taxAmount = 0;
                            // 🔧 FISCAL FLIP: solo marcar para revisión si la plataforma YA está registrada.
                            // Sin alta, no hay obligación de recaudar → no es incidencia.
                            taxNotCollectedNeedsReview = _fiscalProfile.IsReadyForFlip();
                        }
                        else if (taxAmount == 0 && !isReverseChargeOnly)
                        {
                            // 🔧 FISCAL FLIP (gate D11): el tratamiento depende del estado fiscal de la plataforma.
                            //   IsVatRegistered=false (pre-alta, hoy): IVA=0 sin reverse_charge es NORMAL — no
                            //     estamos registrados, no recaudamos. LogInfo solo para auditoría, sin alerta
                            //     crítica y sin RequiresManualReview (si no, alarmaría en CADA venta).
                            //   IsVatRegistered=true (post-alta): SÍ es anómalo (como MoR deberíamos recaudar).
                            //     Comportamiento previo (Critical + RequiresManualReview) intacto.
                            if (_fiscalProfile.IsReadyForFlip())
                            {
                                await _loggingService.LogCriticalAsync(
                                    message: "IVA no recaudado: Stripe Tax devolvió 0 sin reverse charge — falta registro fiscal",
                                    details: $"Session {session.Id}: automatic_tax.status='{sessionWithTax.AutomaticTax?.Status}', amount_tax=0, taxability_reason=[{string.Join(",", taxabilityReasons)}]. " +
                                             $"Siendo la plataforma Merchant of Record, un 0 no-reverse-charge implica que NO hay registro fiscal activo en la jurisdicción del comprador. " +
                                             $"ACCIÓN: revisar alta OSS / registro fiscal en Stripe Tax y regularizar esta venta ({totalAmount}€). El hire se marca RequiresManualReview; el cobro NO se bloquea.",
                                    userId: userId,
                                    source: "SubscriptionController.HandlePendingHireCompleted",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: null,
                                    additionalData: new { SessionId = session.Id, AutomaticTaxStatus = sessionWithTax.AutomaticTax?.Status, TaxabilityReasons = taxabilityReasons, TotalAmount = totalAmount }
                                );
                                taxNotCollectedNeedsReview = true;
                            }
                            else
                            {
                                // Pre-flip: traza informativa, sin alerta ni marca de revisión.
                                await _loggingService.LogInfoAsync(
                                    message: "Tax 0 sin reverse_charge — plataforma NO registrada fiscalmente (esperado pre-alta)",
                                    details: $"Session {session.Id}: amount_tax=0, taxability_reason=[{string.Join(",", taxabilityReasons)}], total={totalAmount}€. PlatformFiscal.IsVatRegistered=false → comportamiento normal (recibo simple). Cuando se haga el flip, este caso pasará a Critical + RequiresManualReview.",
                                    userId: userId,
                                    source: "SubscriptionController.HandlePendingHireCompleted",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: null
                                );
                            }
                            baseAmount = totalAmount;
                            taxAmount = 0;
                        }
                        // else: tax > 0 (collecting normal) o reverse_charge legítimo → no se toca nada.
                    }
                    else
                    {
                        // ✅ FALLBACK: Si AmountTotal no tiene valor, usar service.Price como base
                        await _loggingService.LogWarningAsync(
                            message: "Stripe Session AmountTotal is null - using service price as base",
                            details: $"Session {session.Id} does not have AmountTotal. Using service price {totalAmount}€ as base amount.",
                            userId: userId,
                            source: "SubscriptionController.HandlePendingHireCompleted",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: null
                        );
                        baseAmount = totalAmount; // totalAmount ya es service.Price
                        taxAmount = 0;
                    }
                }
                catch (Exception taxEx)
                {
                    // Si falla obtener tax breakdown, usar precio completo como fallback
                    await _loggingService.LogWarningAsync(
                        message: "Failed to get tax breakdown from Stripe Session - using full amount as base",
                        details: $"Error getting tax breakdown from Session {session.Id}: {taxEx.Message}. Using full amount {totalAmount}€ as base amount.",
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: null
                    );
                    baseAmount = totalAmount;
                    taxAmount = 0;
                }

                // (clientVatNumber/clientVatCountryCode ya fueron declarados arriba y poblados dentro del
                // try donde existe sessionWithTax; aquí solo se usan en la creación del SearchHire.)

                // 🛡️ V8 FIX: capturar snapshot de % vigentes (congela reparto contractual al
                // checkout — si admin cambia StatusConfiguration después, RefundService usa snapshot).
                decimal? clientPctSnapshot = null, expertPctSnapshot = null, platformPctSnapshot = null;
                try
                {
                    var moneyConfigAtCreation = await _systemStatusService.GetMoneyDistributionConfigAsync(
                        SearchHireStatus.Pending.ToStringValue(),
                        service.CategoryId,
                        service.ServiceType?.ServiceTypeCategoryId);
                    if (moneyConfigAtCreation != null)
                    {
                        clientPctSnapshot = moneyConfigAtCreation.ClientPercentage;
                        expertPctSnapshot = moneyConfigAtCreation.ExpertPercentage;
                        platformPctSnapshot = moneyConfigAtCreation.PlatformPercentage;
                    }
                }
                catch (Exception v8Ex)
                {
                    await _loggingService.LogWarningAsync(
                        message: "V8: snapshot capture failed (fallback a config live al resolver)",
                        details: $"User {userId} ServiceId {service.Id}: {v8Ex.Message}",
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted.V8",
                        relatedEntityType: "SearchService",
                        relatedEntityId: service.Id);
                }

                // Create search hire
                searchHire = new SearchHire
                {
                    ClientId = userId,
                    ExpertId = expertuserid,
                    SearchServiceId = service.Id,
                    SearchId = search.Id,
                        StatusId = pendingStatusId, // ✅ FIX: Usar StatusId obtenido antes de la transacción
                    Amount = totalAmount, // Total con IVA (€110)
                    BaseAmount = baseAmount, // Base sin IVA (€90.91) ✅ STRIPE TAX
                    TaxAmount = taxAmount, // IVA (€19.09) ✅ STRIPE TAX
                    // 🌍 Round 21: snapshot del currency del SearchService — inmutable durante toda la vida del hire.
                    // 🛡️ Round 28 MUD-AH (CRITICAL): preferimos sessionWithTax.Currency (lo que Stripe
                    // realmente cobró tras MUD-6/7/9 override por divergencia con Stripe acct DefaultCurrency).
                    // Antes guardábamos service.Currency stale → RefundService.cs:1332 abortaba TODOS los
                    // transfers + refunds en hires donde el override ocurrió → money locked en
                    // "RequiresManualReview" forever. Fallback "EUR" para defensiva legacy.
                    Currency = (session?.Currency ?? service.Currency ?? "eur").ToUpperInvariant(),
                    CreatedAt = DateTime.UtcNow,
                    CompletionDeadline = DateTime.UtcNow.AddDays(7),
                    ExpertAvailabilityId = currentAvailabilityId, // Guardar la disponibilidad usada
                    ExpertTimezone = expertTimezone, // ✅ INTERNACIONALIZACIÓN: Snapshot del timezone del lugar de contratación
                    ExpertCountry = expertCountry, // ✅ INTERNACIONALIZACIÓN: Snapshot del país del lugar de contratación
                    RequiresManualReview = taxNotCollectedNeedsReview, // 🔧 FIX D11: IVA no recaudado → revisión fiscal del admin
                    // 🛡️ Round 28 MUD-AJ: marcar Pending para activar watchdog. Sin esto el
                    // campo permanecía null y PlatformMaintenanceService.ProcessExpiringPaymentIntents
                    // (cron 30 min) NUNCA recogía hires huérfanos cuyo EnsurePaymentCapturedAsync
                    // crasheaba/timeout entre crear hire y commit del PI → PI expira en 7d sin
                    // cobro pero hire row queda como pending fantasma.
                    CaptureStatus = "Pending",
                    ClientVatNumber = clientVatNumber,                  // 🔧 FISCAL FLIP: NIF cliente (puede ser null)
                    ClientVatCountryCode = clientVatCountryCode,        // 🔧 FISCAL FLIP: país NIF cliente (puede ser null)
                    ClientPercentageSnapshot = clientPctSnapshot,       // 🛡️ V8 FIX: congela % contractual
                    ExpertPercentageSnapshot = expertPctSnapshot,
                    PlatformPercentageSnapshot = platformPctSnapshot
                };
                    // ✅ REMOVED: Balance verification eliminated - all payments are direct Stripe

                    // ✅ REMOVED: No restrictions on multiple service hires - users can contract the same service multiple times

                    // ✅ REMOVED: Balance deduction eliminated - all payments are direct Stripe
                
                _context.SearchHires.Add(searchHire);
                // 🔧 FIX (hallazgo C): sin recovery con autocommit (era lo que dejaba un SearchHire huérfano sin
                // ServicePayment, o filas zombi descorrelacionadas, al cascadear hasta capturar y commitear sobre
                // una transacción muerta). Si la conexión se cae, sube al catch externo → rollback total + 500 →
                // Stripe reintenta idempotente (guard de ServicePayment por PaymentIntentId + clave capture-{hireId}).
                    await _context.SaveChangesAsync(); // ✅ SAVE FIRST to get the real ID
                    searchHireId = searchHire.Id;

                // 🌍 Round 21: aserción explícita del snapshot de currency — facilita auditar que
                // el hire heredó el currency correcto del SearchService al momento de la creación.
                await _loggingService.LogInfoAsync(
                    message: "Hire currency snapshot",
                    details: $"Hire {searchHire.Id} created with Currency={searchHire.Currency} from service {service.Id}",
                    userId: userId,
                    source: "SubscriptionController.HandlePendingHireCompleted",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHire.Id);

                // Migrar chat pre-contratación → conversación post-hire
                try
                {
                    await ConversationMigrationHelper.EnsurePostHireConversationAsync(
                        _context, searchHire, _loggingService);
                }
                catch (Exception migrateEx)
                {
                    await _loggingService.LogErrorAsync(
                        message: "Error migrating pre-hire conversation after Stripe payment",
                        details: migrateEx.Message,
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId);
                }

                var paymentTransaction = new FinancialTransaction
                {
                    UserId = userId,
                    Amount = -totalAmount, // 🔧 FIX (#3): registrar lo REALMENTE cobrado (con IVA), coherente con SearchHire.Amount. Antes -service.Price (base) descuadraba el ledger interno con tax exclusive y mostraba un importe erróneo al usuario.
                    AmountCents = -checked((long)Math.Round(totalAmount * 100)), // 🔧 céntimos exactos cobrados (con IVA)
                    Currency = searchHire.Currency, // 🌍 Round 25: snapshot ISO 4217 desde el hire recién creado
                    TransactionType = "ServicePayment",
                    RelatedEntityType = "SearchHire",
                        RelatedEntityId = searchHireId, // ✅ FIX: Usar searchHireId guardado
                        StripePaymentIntentId = session.PaymentIntentId, // ✅ ADDED: Track Stripe payment intent
                    CreatedAt = DateTime.UtcNow
                };
                _context.FinancialTransactions.Add(paymentTransaction);

                // 🔧 FIX (hallazgo C): sin recovery con autocommit. El ServicePayment debe commitear ATÓMICAMENTE
                // con el SearchHire (misma transacción, commit en CommitAsync). Si la conexión se cae, sube al
                // catch externo → rollback total + 500 → Stripe reintenta idempotente.
                    await _context.SaveChangesAsync();

                if (string.IsNullOrEmpty(session.PaymentIntentId))
                {
                    await LogPaymentCaptureFailureAsync(
                        paymentIntentId: "missing",
                        userId: userId,
                        serviceId: serviceId,
                        failureReason: "Stripe checkout session did not include a PaymentIntentId.",
                        searchHireId: searchHireId); // ✅ FIX: Usar searchHireId guardado
                    throw new InvalidOperationException("PaymentIntentId is missing from checkout session.");
                }

                // 🔒 FIX B5: revalidar EN VIVO que el experto puede cobrar ANTES de capturar. La validación del
                // pre-checkout fue hasta ~7 días antes (captura manual diferida); el experto pudo romper/deshabilitar
                // su cuenta Connect en el intervalo. Si capturáramos igual, cobraríamos al cliente y el transfer
                // posterior fallaría (dinero retenido sin destino). Fail-closed: si NO puede cobrar, NO capturamos,
                // cancelamos el PI (requires_capture → sin cargo), notificamos, rollback y RETURN (no relanzamos
                // para no entrar en bucle de reintentos de Stripe: el cobro está legítimamente abortado).
                var captureValidation = await _stripeValidationService.ValidateExpertCanReceivePaymentsAsync(
                    expertProfile, "completar el cobro de la contratación");
                if (!captureValidation.IsValid)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Expert can no longer receive payments at capture time - aborting capture",
                        details: $"PaymentIntent {session.PaymentIntentId} (SearchHire {searchHireId}, ServiceId {serviceId}) NO se captura: el experto dejó de poder cobrar entre el checkout y la captura. StripeStatus: {captureValidation.StripeStatus}. Motivo: {captureValidation.ErrorMessage}. Se cancela el PI; el cliente NO es cobrado y la contratación se aborta (rollback).",
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { PaymentIntentId = session.PaymentIntentId, SearchHireId = searchHireId, ServiceId = serviceId, ExpertUserId = expertuserid, StripeStatus = captureValidation.StripeStatus, Reason = captureValidation.ErrorMessage });

                    try
                    {
                        var preCapturePiService = new PaymentIntentService();
                        var preCapturePi = await preCapturePiService.GetAsync(session.PaymentIntentId);
                        if (preCapturePi.Status == "requires_capture")
                        {
                            await preCapturePiService.CancelAsync(session.PaymentIntentId);
                        }
                    }
                    catch (Exception cancelEx)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Failed to cancel uncapturable PaymentIntent after expert payout validation failed",
                            details: $"PaymentIntent {session.PaymentIntentId}, SearchHire {searchHireId}: {cancelEx.Message}. ACTION REQUIRED: revisar/cancelar el PI manualmente desde Stripe.",
                            userId: userId,
                            source: "SubscriptionController.HandlePendingHireCompleted",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId);
                    }

                    await _loggingService.LogWarningAsync(
                        message: "Contratación no completada: el experto no está disponible para cobros",
                        details: $"No se ha realizado ningún cargo. El experto no puede recibir pagos ahora mismo, por lo que la contratación del servicio {serviceId} no se completó. No se te ha cobrado nada.",
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted",
                        relatedEntityType: "Payment",
                        relatedEntityId: serviceId,
                        notifyUser: true);

                    await transaction.RollbackAsync();
                    return;
                }

                await EnsurePaymentCapturedAsync(session.PaymentIntentId, userId, serviceId, searchHireId); // ✅ FIX: Usar searchHireId guardado

                // 🛡️ Round 28 MUD-AJ + MUD-AT: marcar Captured tras éxito del capture. El
                // watchdog (PlatformMaintenanceService.ProcessExpiringPaymentIntentsAsync) NO
                // procesa hires con CaptureStatus="Captured".
                //
                // MUD-AT: NO hacer SaveChangesAsync extra aquí — EF flushea la mutación al
                // CommitAsync de abajo. El SaveChanges intermedio anterior abría ventana de
                // fallo (Stripe captured OK pero conexión BD caída entre SaveChanges y Commit)
                // sin recovery, mientras el catch(commitEx) sí tiene compensación Stripe.
                // Asignación pura → EF lo persiste atómicamente con el commit.
                searchHire.CaptureStatus = "Captured";

                try
                {
                    await transaction.CommitAsync();
                }
                catch (Exception commitEx)
                {
                    // Compensación: la captura YA pasó a Stripe pero el commit local falló.
                    // Refund/cancel el PI para no quedarse con dinero capturado sin SearchHire.
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Commit failed after Stripe capture - compensating",
                        details: $"PaymentIntent {session.PaymentIntentId} ya capturado en Stripe pero CommitAsync local falló (SearchHire {searchHireId}). Iniciando compensación. Commit error: {commitEx.Message}",
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { PaymentIntentId = session.PaymentIntentId, SearchHireId = searchHireId, CommitError = commitEx.Message });

                    try
                    {
                        var piService = new PaymentIntentService();
                        var pi = await piService.GetAsync(session.PaymentIntentId);
                        if (pi.Status == "succeeded")
                        {
                            // Ya capturado: refund total inmediato (idempotente por IdempotencyKey).
                            var refundService = new RefundService();
                            await refundService.CreateAsync(new RefundCreateOptions
                            {
                                PaymentIntent = session.PaymentIntentId,
                                Reason = "requested_by_customer"
                            }, new RequestOptions { IdempotencyKey = $"compensate-{searchHireId}" });
                        }
                        else if (pi.Status == "requires_capture")
                        {
                            // PI todavía cancelable.
                            await piService.CancelAsync(session.PaymentIntentId);
                        }
                    }
                    catch (Exception compensationEx)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Failed to compensate Stripe capture after commit failure",
                            details: $"PaymentIntent {session.PaymentIntentId}, SearchHire {searchHireId}: {compensationEx.Message}. ACTION REQUIRED: refund manual desde Stripe Dashboard.",
                            userId: userId,
                            source: "SubscriptionController.HandlePendingHireCompleted",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { PaymentIntentId = session.PaymentIntentId, SearchHireId = searchHireId, CompensationError = compensationEx.Message });
                    }

                    throw;
                }

                // ✅ Crear automáticamente la cita en estado "awaiting_appointment" con timer de 24h
                // Esto asegura que el cliente tenga 24 horas para proponer una fecha/hora
                try
                {
                    // Verificar que no exista ya una cita (por si acaso)
                    var existingAppointment = await _context.Appointments
                        .FirstOrDefaultAsync(a => a.SearchHireId == searchHireId); // ✅ FIX: Usar searchHireId guardado
                    
                    if (existingAppointment == null)
                    {
                        // Obtener el estado "awaiting_appointment"
                        var awaitingStatus = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                      s.StatusValue == "awaiting_appointment");
                        
                        if (awaitingStatus != null)
                        {
                            // ✅ Crear Appointment sin fecha/hora/ubicación - se asignarán cuando el cliente proponga
                            var appointment = new Appointment
                            {
                                SearchHireId = searchHireId, // ✅ FIX: Usar searchHireId guardado
                                StatusId = awaitingStatus.Id,
                                // ProposedDate, ProposedTime, Location son nullable - se asignarán en ProposeAppointment
                                // 🌍 Round 21: snapshot del timezone del experto al crear la cita.
                                ProposerTimezone = expertTimezone ?? "Europe/Madrid",
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            _context.Appointments.Add(appointment);
                            await _context.SaveChangesAsync();

                            // Crear timer para propuesta del cliente (24 horas)
                            var proposalTimer = new AppointmentTimer
                            {
                                AppointmentId = appointment.Id,
                                TimerType = "proposal",
                                StartTime = DateTime.UtcNow,
                                EndTime = DateTime.UtcNow.AddHours(24),
                                IsExpired = false,
                                CreatedAt = DateTime.UtcNow
                            };

                            _context.AppointmentTimers.Add(proposalTimer);
                            await _context.SaveChangesAsync();

                            // Programar scheduled job para cuando expire el timer (24 horas)
                            try
                            {
                                // ✅ Usar método wrapper con nombre descriptivo para Hangfire
                                var jobId = BackgroundJob.Schedule<IAppointmentService>(
                                    service => service.ProcessProposalTimerAsync(proposalTimer.Id),
                                    proposalTimer.EndTime - DateTime.UtcNow
                                );

                                // Guardar el JobId en el timer
                                proposalTimer.HangfireJobId = jobId;
                                await _context.SaveChangesAsync();

                                await _loggingService.LogInfoAsync(
                                    message: "Hangfire job programado exitosamente para timer de appointment",
                                    details: $"Timer {proposalTimer.Id} para Appointment {appointment.Id} programado. JobId: {jobId}, EndTime: {proposalTimer.EndTime}",
                                    userId: userId,
                                    source: "SubscriptionController.HandlePendingHireCompleted",
                                    relatedEntityType: "AppointmentTimer",
                                    relatedEntityId: proposalTimer.Id
                                );
                            }
                            catch (Exception hangfireEx)
                            {
                                // ✅ LOG: Error al programar job de Hangfire (no crítico, el timer se creó)
                                await _loggingService.LogWarningAsync(
                                    message: "Failed to schedule Hangfire job for appointment timer",
                                    details: $"Timer {proposalTimer.Id} created successfully but Hangfire job scheduling failed. Error: {hangfireEx.Message}, StackTrace: {hangfireEx.StackTrace}",
                                    userId: userId,
                                    source: "SubscriptionController.HandlePendingHireCompleted",
                                    relatedEntityType: "AppointmentTimer",
                                    relatedEntityId: proposalTimer.Id,
                                    additionalData: new { 
                                        TimerId = proposalTimer.Id,
                                        AppointmentId = appointment.Id,
                                        SearchHireId = searchHireId,
                                        Exception = hangfireEx.Message,
                                        ErrorType = hangfireEx.GetType().Name,
                                        StackTrace = hangfireEx.StackTrace,
                                        InnerException = hangfireEx.InnerException?.Message
                                    }
                                );
                                // Continuar sin el job de Hangfire - el timer se creó correctamente
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 🚨 LOG CRÍTICO: Error al crear cita automática y timer inicial
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Failed to create automatic appointment and initial timer",
                        details: $"Error creating automatic appointment for SearchHire {searchHireId} in HandlePendingHireCompleted. " + // ✅ FIX: Usar searchHireId guardado
                                $"The SearchHire was confirmed but the Appointment/Timer flow failed. " +
                                $"Error: {ex.Message}. StackTrace: {ex.StackTrace}",
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId, // ✅ FIX: Usar searchHireId guardado
                        additionalData: new { 
                            Action = "CreateAutomaticAppointment",
                            SearchHireId = searchHireId, // ✅ FIX: Usar searchHireId guardado
                            ClientId = userId,
                            ExpertId = expertuserid,
                            Exception = ex.Message
                        },
                        notifyUser: false // No asustar al usuario, pero alertar a admins
                    );
                }

                // ✅ Notificar al cliente y experto cuando se confirma la contratación
                await _loggingService.LogInfoAsync(
                    message: "Contratación confirmada",
                    details: $"Tu pago se procesó correctamente. La contratación #{searchHireId} está activa y el experto ha sido notificado.", // ✅ FIX: Usar searchHireId guardado
                    userId: userId,
                    source: "SubscriptionController.HandlePendingHireCompleted",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId, // ✅ FIX: Usar searchHireId guardado
                    notifyUser: true
                );

                // ✅ Enviar factura por email al cliente (en segundo plano con Hangfire)
                if (!string.IsNullOrEmpty(user.Email))
                {
                    try
                    {
                        Hangfire.BackgroundJob.Enqueue<IInvoiceService>(service =>
                            service.SendInvoiceByEmailBackgroundJob(searchHireId, user.Email)); // ✅ FIX: Usar searchHireId guardado
                        
                        await _loggingService.LogInfoAsync(
                            message: "Factura encolada para envío por email",
                            details: $"Factura para SearchHire {searchHireId} encolada en Hangfire para envío a {user.Email}",
                            userId: userId,
                            source: "SubscriptionController.HandlePendingHireCompleted",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId
                        );
                    }
                    catch (Exception invoiceEx)
                    {
                        // ✅ LOG: Error al encolar email de factura (no crítico, la contratación se completó)
                        await _loggingService.LogWarningAsync(
                            message: "Failed to enqueue invoice email job",
                            details: $"Hangfire job enqueue failed for SearchHire {searchHireId}. Error: {invoiceEx.Message}. The hire was completed successfully, but the invoice email will not be sent automatically.",
                            userId: userId,
                            source: "SubscriptionController.HandlePendingHireCompleted",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                SearchHireId = searchHireId,
                                Email = user.Email,
                                Exception = invoiceEx.Message,
                                ErrorType = invoiceEx.GetType().Name,
                                StackTrace = invoiceEx.StackTrace,
                                InnerException = invoiceEx.InnerException?.Message
                            }
                        );
                    }
                }

                // ✅ Notificar al experto sobre la nueva contratación
                if (expertuserid > 0)
                {
                    await _loggingService.LogInfoAsync(
                        message: "Nueva contratación recibida",
                        // 🛡️ MUD-AH: usar service.Currency para experts no-EUR.
                        details: $"Has recibido una nueva contratación #{searchHireId} por {service.Price} {(service.Currency ?? "EUR").ToUpperInvariant()}. Revisa los detalles y contacta con el cliente.",
                        userId: expertuserid,
                        source: "SubscriptionController.HandlePendingHireCompleted",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId, // ✅ FIX: Usar searchHireId guardado
                        notifyUser: true
                    );
                }

            }
            catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
            {
                // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                try
                {
                    await transaction.RollbackAsync();
                }
                catch
                {
                    // Ignorar errores de rollback si la conexión ya está disposed
                }
                
                // Log error pero no reintentar - el webhook ya respondió 200 OK
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Connection disposed in HandlePendingHireCompleted",
                    details: $"Connection disposed while processing pending hire. UserId: {userId}, ServiceId: {serviceId}, SessionId: {session?.Id}",
                    userId: userId,
                    source: "SubscriptionController.HandlePendingHireCompleted",
                    relatedEntityType: "Payment",
                    relatedEntityId: serviceId,
                    additionalData: new { 
                        UserId = userId, 
                        ServiceId = serviceId, 
                        SessionId = session?.Id,
                        Error = dbEx.Message
                    }
                );
                // 🔧 FIX (hallazgo C): RELANZAR para que el webhook devuelva 500 y Stripe REINTENTE el evento.
                // Antes se tragaba el error (200 OK) y la compra se perdía. El reintento es idempotente.
                throw;
            }
            catch (ObjectDisposedException disposedEx)
            {
                // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                try
                {
                    await transaction.RollbackAsync();
                }
                catch
                {
                    // Ignorar errores de rollback si la conexión ya está disposed
                }
                
                // Log error pero no reintentar - el webhook ya respondió 200 OK
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Connection disposed in HandlePendingHireCompleted",
                    details: $"Connection disposed while processing pending hire. UserId: {userId}, ServiceId: {serviceId}, SessionId: {session?.Id}",
                    userId: userId,
                    source: "SubscriptionController.HandlePendingHireCompleted",
                    relatedEntityType: "Payment",
                    relatedEntityId: serviceId,
                    additionalData: new { 
                        UserId = userId, 
                        ServiceId = serviceId, 
                        SessionId = session?.Id,
                        Error = disposedEx.Message
                    }
                );
                // 🔧 FIX (hallazgo C): RELANZAR → 500 → Stripe reintenta idempotente (antes se perdía la compra).
                throw;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();

                if (!string.IsNullOrEmpty(session?.PaymentIntentId))
                {
                    var isStripeCaptureFailure =
                        ex is StripeException ||
                        (ex is InvalidOperationException &&
                         ex.Message.Contains("PaymentIntent", StringComparison.OrdinalIgnoreCase));

                    if (isStripeCaptureFailure)
                    {
                        await LogPaymentCaptureFailureAsync(
                            paymentIntentId: session.PaymentIntentId,
                            userId: userId,
                            serviceId: serviceId,
                            failureReason: ex.Message,
                            searchHireId: searchHireId > 0 ? searchHireId : (int?)null);

                        await _loggingService.LogWarningAsync(
                            message: "Intento de pago no capturado",
                            details: $"No pudimos completar el cobro del servicio {serviceId}. El cargo no se realizó y el cliente debe reintentar el pago.",
                            userId: userId,
                            source: "SubscriptionController.HandlePendingHireCompleted",
                            relatedEntityType: "Payment",
                            relatedEntityId: serviceId,
                            additionalData: new { PaymentIntentId = session.PaymentIntentId, SearchHireId = searchHireId > 0 ? searchHireId : (int?)null },
                            notifyUser: true
                        );
                    }
                    else
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Pending hire processing persistence failure",
                            details: $"SearchHire {searchHireId} failed after checkout completion. The error is not a Stripe capture API failure. Reason: {ex.Message}",
                            userId: userId,
                            source: "SubscriptionController.HandlePendingHireCompleted",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId > 0 ? searchHireId : serviceId,
                            additionalData: new
                            {
                                PaymentIntentId = session.PaymentIntentId,
                                SearchHireId = searchHireId > 0 ? searchHireId : (int?)null,
                                ServiceId = serviceId,
                                ErrorType = ex.GetType().Name,
                                Exception = ex.Message,
                                InnerException = ex.InnerException?.Message
                            });
                    }
                }

                throw;
            }
            });
        }

        // ❌ ELIMINADO: ProcessAutomaticRefundOnError - No se usa (reemplazado por captura manual)

        /// <summary>
        /// FIX C8: cancela (si requires_capture) o reembolsa (si succeeded) un PaymentIntent DUPLICADO —un 2º
        /// cobro del mismo (cliente, servicio) llegado por otro flujo o por carrera de webhooks—. Idempotente
        /// (refund con clave propia dup-refund-{pi}); best-effort con log crítico si falla.
        /// </summary>
        private async Task CancelOrRefundDuplicatePaymentIntentAsync(string paymentIntentId, int userId, int existingHireId)
        {
            if (string.IsNullOrEmpty(paymentIntentId)) return;
            try
            {
                var piService = new PaymentIntentService();
                var pi = await piService.GetAsync(paymentIntentId);
                if (pi.Status == "requires_capture")
                {
                    await piService.CancelAsync(paymentIntentId); // aún no capturado → sin cargo al cliente
                }
                else if (pi.Status == "succeeded")
                {
                    await new Stripe.RefundService().CreateAsync(
                        new RefundCreateOptions { PaymentIntent = paymentIntentId, Reason = "duplicate" },
                        new RequestOptions { IdempotencyKey = $"dup-refund-{paymentIntentId}" });
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Failed to cancel/refund duplicate PaymentIntent",
                    details: $"PaymentIntent {paymentIntentId} (hire activo existente #{existingHireId}): {ex.Message}. ACTION REQUIRED: cancelar/reembolsar manualmente en Stripe.",
                    userId: userId,
                    source: "SubscriptionController.CancelOrRefundDuplicatePaymentIntentAsync",
                    relatedEntityType: "Payment",
                    relatedEntityId: existingHireId);
            }
        }

        private async Task EnsurePaymentCapturedAsync(string paymentIntentId, int userId, int serviceId, int searchHireId)
        {
            var paymentIntentService = new PaymentIntentService();
            PaymentIntent paymentIntent;

            try
            {
                paymentIntent = await paymentIntentService.GetAsync(paymentIntentId);
            }
            catch (StripeException ex)
            {
                await LogPaymentCaptureFailureAsync(paymentIntentId, userId, serviceId, $"Stripe error retrieving PaymentIntent: {ex.Message}", searchHireId, ex);
                throw;
            }

            if (paymentIntent.Status == "requires_capture")
            {
                try
                {
                    await paymentIntentService.CaptureAsync(
                        paymentIntentId,
                        null,
                        new RequestOptions { IdempotencyKey = $"capture-{searchHireId}" });
                }
                catch (StripeException ex)
                {
                    await LogPaymentCaptureFailureAsync(paymentIntentId, userId, serviceId, $"Stripe error capturing PaymentIntent: {ex.Message}", searchHireId, ex);
                    throw;
                }
            }
            else if (paymentIntent.Status == "succeeded")
            {
                return;
            }
            else
            {
                var message = $"PaymentIntent {paymentIntentId} is in '{paymentIntent.Status}' state and cannot be captured.";
                await LogPaymentCaptureFailureAsync(paymentIntentId, userId, serviceId, message, searchHireId);
                throw new InvalidOperationException(message);
            }
        }

        private async Task LogPaymentCaptureFailureAsync(string paymentIntentId, int userId, int serviceId, string failureReason, int? searchHireId = null, Exception? exception = null, [System.Runtime.CompilerServices.CallerMemberName] string callerMember = "")
        {
            var resolvedSource = string.IsNullOrEmpty(callerMember)
                ? "SubscriptionController.HandlePendingHireCompleted"
                : $"SubscriptionController.{callerMember}";

            await _loggingService.LogCriticalAsync(
                message: "CRITICAL: Payment capture failure",
                details: $"Failed to capture PaymentIntent {paymentIntentId}. Reason: {failureReason}",
                userId: null,
                source: resolvedSource,
                relatedEntityType: "Payment",
                relatedEntityId: serviceId,
                additionalData: new
                {
                    PaymentIntentId = paymentIntentId,
                    SearchHireId = searchHireId,
                    ClientId = userId,
                    Reason = failureReason,
                    Exception = exception?.Message
                }
            );
        }

        /// <summary>
        /// Registra fallos críticos de refund para alertar a administradores
        /// </summary>
        private async Task LogCriticalRefundFailure(string paymentIntentId, int userId, int serviceId, Exception error, [System.Runtime.CompilerServices.CallerMemberName] string callerMember = "")
        {
            // 🌍 Round 25: heredar el currency del ServicePayment original si lo encontramos.
            // Si no, default "EUR" — es solo un marcador (Amount=0) y no afecta a la contabilidad.
            var originalCurrency = await _context.FinancialTransactions
                .Where(ft => ft.StripePaymentIntentId == paymentIntentId
                          && ft.TransactionType == "ServicePayment")
                .Select(ft => ft.Currency)
                .FirstOrDefaultAsync() ?? "EUR";

            // 💾 Registrar fallo crítico en base de datos para seguimiento
            var criticalError = new FinancialTransaction
            {
                UserId = userId,
                Amount = 0, // No hay monto en caso de error
                AmountCents = 0,
                Currency = originalCurrency,
                TransactionType = "CriticalRefundFailure",
                RelatedEntityType = "ErrorRecovery",
                RelatedEntityId = 0,
                StripePaymentIntentId = paymentIntentId,
                CreatedAt = DateTime.UtcNow
            };

            _context.FinancialTransactions.Add(criticalError);
            await _context.SaveChangesAsync();

            var resolvedSource = string.IsNullOrEmpty(callerMember)
                ? "SubscriptionController.LogCriticalRefundFailure"
                : $"SubscriptionController.{callerMember}";

            await _loggingService.LogCriticalAsync(
                $"Critical refund failure - PaymentIntentId: {paymentIntentId}",
                error.Message,
                userId,
                resolvedSource,
                "Payment",
                serviceId,
                new { PaymentIntentId = paymentIntentId, ServiceId = serviceId, Error = error.Message }
            );
        }

        /// <summary>
        /// Endpoint temporal para crear la tabla LogType
        /// </summary>
        [HttpPost("create-log-type-table")]
        public async Task<IActionResult> CreateLogTypeTable()
        {
            try
            {
                // 🔐 SEGURIDAD: Solo administradores
                if (!_authService.IsAdmin(User))
                {
                    return Unauthorized(new { message = "Admin access required" });
                }
                // Crear tabla LogTypes
                await _context.Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE IF NOT EXISTS ""LogTypes"" (
                        ""Id"" SERIAL PRIMARY KEY,
                        ""Name"" VARCHAR(100) NOT NULL,
                        ""Description"" VARCHAR(500),
                        ""Category"" VARCHAR(50) NOT NULL,
                        ""Severity"" VARCHAR(20) NOT NULL,
                        ""RequiresAdminNotification"" BOOLEAN NOT NULL DEFAULT FALSE,
                        ""RequiresEmailAlert"" BOOLEAN NOT NULL DEFAULT FALSE,
                        ""RequiresSmsAlert"" BOOLEAN NOT NULL DEFAULT FALSE,
                        ""IsActive"" BOOLEAN NOT NULL DEFAULT TRUE,
                        ""CreatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                        ""UpdatedAt"" TIMESTAMP WITH TIME ZONE
                    );
                ");
                // Agregar columnas a la tabla Logs si no existen
                await _context.Database.ExecuteSqlRawAsync(@"
                    DO $$ 
                    BEGIN
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Logs' AND column_name = 'AdditionalData') THEN
                            ALTER TABLE ""Logs"" ADD COLUMN ""AdditionalData"" TEXT;
                        END IF;
                        
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Logs' AND column_name = 'LogTypeId') THEN
                            ALTER TABLE ""Logs"" ADD COLUMN ""LogTypeId"" INTEGER;
                        END IF;
                        
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Logs' AND column_name = 'RelatedEntityId') THEN
                            ALTER TABLE ""Logs"" ADD COLUMN ""RelatedEntityId"" INTEGER;
                        END IF;
                        
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Logs' AND column_name = 'RelatedEntityType') THEN
                            ALTER TABLE ""Logs"" ADD COLUMN ""RelatedEntityType"" TEXT;
                        END IF;
                    END $$;
                ");
                // Crear índice si no existe
                await _context.Database.ExecuteSqlRawAsync(@"
                    CREATE INDEX IF NOT EXISTS ""IX_Logs_LogTypeId"" ON ""Logs"" (""LogTypeId"");
                ");
                // Agregar foreign key si no existe
                await _context.Database.ExecuteSqlRawAsync(@"
                    DO $$
                    BEGIN
                        IF NOT EXISTS (
                            SELECT 1 FROM information_schema.table_constraints 
                            WHERE constraint_name = 'FK_Logs_LogTypes_LogTypeId'
                        ) THEN
                            ALTER TABLE ""Logs"" 
                            ADD CONSTRAINT ""FK_Logs_LogTypes_LogTypeId"" 
                            FOREIGN KEY (""LogTypeId"") REFERENCES ""LogTypes""(""Id"");
                        END IF;
                    END $$;
                ");
                // Insertar tipos de logs por defecto
                await _context.Database.ExecuteSqlRawAsync(@"
                    INSERT INTO ""LogTypes"" (""Name"", ""Description"", ""Category"", ""Severity"", ""RequiresAdminNotification"", ""RequiresEmailAlert"", ""RequiresSmsAlert"", ""IsActive"", ""CreatedAt"")
                    VALUES 
                    -- Critical Log Types
                    ('TRANSFER_FAILED', 'Transfer to expert failed but service completed', 'Critical', 'Critical', true, true, false, true, NOW()),
                    ('REFUND_FAILED', 'Automatic refund failed after payment', 'Critical', 'Critical', true, true, false, true, NOW()),
                    ('PAYMENT_PROCESSING_ERROR', 'Error processing payment in Stripe', 'Critical', 'Critical', true, true, false, true, NOW()),
                    ('STRIPE_WEBHOOK_ERROR', 'Error processing Stripe webhook', 'Critical', 'Critical', true, false, false, true, NOW()),

                    -- Error Log Types
                    ('SEARCH_CREATION_ERROR', 'Error creating search after payment', 'Error', 'High', true, false, false, true, NOW()),
                    ('EXPERT_ACCOUNT_VERIFICATION_FAILED', 'Expert account verification failed', 'Error', 'High', false, false, false, true, NOW()),
                    ('DATABASE_CONNECTION_ERROR', 'Database connection error', 'Error', 'High', true, false, false, true, NOW()),
                    ('EXTERNAL_API_ERROR', 'Error calling external API', 'Error', 'Medium', false, false, false, true, NOW()),

                    -- Warning Log Types
                    ('EXPERT_ACCOUNT_PENDING', 'Expert account pending verification', 'Warning', 'Medium', false, false, false, true, NOW()),
                    ('PAYMENT_RETRY_ATTEMPT', 'Payment retry attempt', 'Warning', 'Medium', false, false, false, true, NOW()),
                    ('USER_ACTION_LIMIT_EXCEEDED', 'User exceeded action limits', 'Warning', 'Low', false, false, false, true, NOW()),

                    -- Info Log Types
                    ('SERVICE_COMPLETED', 'Service completed successfully', 'Info', 'Low', false, false, false, true, NOW()),
                    ('REFUND_PROCESSED', 'Refund processed successfully', 'Info', 'Low', false, false, false, true, NOW()),
                    ('PAYMENT_SUCCESSFUL', 'Payment processed successfully', 'Info', 'Low', false, false, false, true, NOW()),
                    ('USER_LOGIN', 'User logged in', 'Info', 'Low', false, false, false, true, NOW()),
                    ('SEARCH_CREATED', 'Search created successfully', 'Info', 'Low', false, false, false, true, NOW()),
                    ('EXPERT_ACCOUNT_VERIFIED', 'Expert account verified', 'Info', 'Low', false, false, false, true, NOW())
                    ON CONFLICT (""Name"") DO NOTHING;
                ");
                return Ok(new { 
                    message = "LogType table and data created successfully!",
                    details = new {
                        tableCreated = "LogTypes",
                        columnsAdded = new[] { "AdditionalData", "LogTypeId", "RelatedEntityId", "RelatedEntityType" },
                        indexCreated = "IX_Logs_LogTypeId",
                        foreignKeyCreated = "FK_Logs_LogTypes_LogTypeId",
                        logTypesInserted = 16
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /// <summary>
        /// Método genérico para procesar refunds con diferentes porcentajes
        /// </summary>
        /// <param name="paymentIntentId">ID del PaymentIntent de Stripe</param>
        /// <param name="userId">ID del usuario</param>
        /// <param name="refundType">Tipo de refund (error_creation, expert_cancellation, etc.)</param>
        /// <param name="refundPercentage">Porcentaje a devolver (0-100)</param>
        /// <param name="reason">Razón del refund</param>
        /// <param name="metadata">Metadata adicional</param>
        private async Task<bool> ProcessGenericRefundAsync(
            string paymentIntentId, 
            int userId, 
            string refundType, 
            decimal refundPercentage, 
            string reason,
            Dictionary<string, string>? additionalMetadata = null)
        {
            try
            {
                // 🔍 Verificar si ya existe un refund para este PaymentIntent (idempotencia)
                var existingRefund = await _context.FinancialTransactions
                    .FirstOrDefaultAsync(ft => ft.StripePaymentIntentId == paymentIntentId && 
                                              ft.TransactionType == "Refund" && 
                                              ft.Amount > 0);

                if (existingRefund != null)
                {
                    return true; // ✅ Idempotencia: refund ya procesado
                }

                // 💳 Crear refund en Stripe
                // Si es un reembolso parcial, calcular el importe en céntimos a partir del PaymentIntent
                long? refundAmountInCents = null;
                if (refundPercentage < 100)
                {
                    var piService = new PaymentIntentService();
                    var paymentIntent = await piService.GetAsync(paymentIntentId);
                    // Amount está en céntimos
                    refundAmountInCents = (long)Math.Round(paymentIntent.Amount * (decimal)refundPercentage / 100m);
                }

                var refundOptions = new RefundCreateOptions
                {
                    PaymentIntent = paymentIntentId,
                    Amount = refundAmountInCents, // null = 100%
                    Reason = RefundReasons.RequestedByCustomer,
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() },
                        { "refundType", refundType },
                        { "refundPercentage", refundPercentage.ToString() },
                        { "reason", reason },
                        { "timestamp", DateTime.UtcNow.ToString("O") }
                    }
                };

                // Agregar metadata adicional si se proporciona
                if (additionalMetadata != null)
                {
                    foreach (var kvp in additionalMetadata)
                    {
                        refundOptions.Metadata[kvp.Key] = kvp.Value;
                    }
                }

                var refundService = new RefundService();
                // 🔑 IDEMPOTENCIA DETERMINISTA (A3): antes no había clave, así que un reintento/doble
                // llamada creaba un SEGUNDO refund en Stripe. La clave estable por (PaymentIntent + tipo)
                // hace que Stripe deduplique los reintentos del mismo reembolso lógico.
                var refundRequestOptions = new RequestOptions
                {
                    IdempotencyKey = $"generic-refund-{paymentIntentId}-{refundType}"
                };
                var refund = await refundService.CreateAsync(refundOptions, refundRequestOptions);
                // 💾 Registrar refund en base de datos
                var refundAmount = (decimal)refund.Amount / 100; // Convertir de céntimos a euros
                // 🌍 Round 25: heredar el currency del ServicePayment original (mismo PaymentIntent).
                // Fallback "EUR" para registros legacy donde el original no tenía currency.
                var refundCurrency = await _context.FinancialTransactions
                    .Where(ft => ft.StripePaymentIntentId == paymentIntentId
                              && ft.TransactionType == "ServicePayment")
                    .Select(ft => ft.Currency)
                    .FirstOrDefaultAsync() ?? "EUR";
                var refundTransaction = new FinancialTransaction
                {
                    UserId = userId,
                    Amount = refundAmount,
                    AmountCents = refund.Amount, // 🔧 céntimos exactos devueltos por Stripe (fuente de verdad)
                    Currency = refundCurrency,
                    TransactionType = "Refund",
                    RelatedEntityType = refundType == "automatic_error_refund" ? "ErrorRecovery" : "SearchHire",
                    RelatedEntityId = 0, // Se puede especificar si es necesario
                    StripePaymentIntentId = paymentIntentId,
                    StripeRefundId = refund.Id,
                    CreatedAt = DateTime.UtcNow
                };

                _context.FinancialTransactions.Add(refundTransaction);
                await _context.SaveChangesAsync();
                return true;
            }
            catch (StripeException stripeEx)
            {
                return false;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        [HttpPost("hire-service")]
        public async Task<IActionResult> HireService([FromBody] HireServiceDto request)
        {
            // 🚨 VALIDACIÓN DE ENTRADA
            if (request == null)
            {
                return BadRequest(new { message = "Request cannot be null" });
            }

            if (request.SearchServiceId <= 0)
            {
                return BadRequest(new { message = "Invalid service ID" });
            }

            if (request.SearchId <= 0)
            {
                return BadRequest(new { message = "Invalid search ID" });
            }
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var service = await _context.SearchServices
                    .Include(ss => ss.ExpertProfile)
                    .ThenInclude(ep => ep.User)
                    .FirstOrDefaultAsync(ss => ss.Id == request.SearchServiceId);

                if (service == null)
                {
                    return NotFound(new { message = "Service not found" });
                }

                // 🚨 FIX C9: servicio SIN experto (FK OnDelete SetNull) → sin destino de payout. Rechazar
                // ANTES de crear la Checkout Session.
                if (service.ExpertProfile == null)
                {
                    return BadRequest(new { message = "Este servicio no está disponible para contratar" });
                }

                // 🛡️ N8 FIX: servicio desactivado entre catálogo y checkout init → cliente cobrado por
                // servicio inactivo. Bloqueamos en el momento de crear la Stripe Session (mismo guard
                // que el del endpoint LoadMoneyService).
                if (!service.IsActive)
                {
                    return BadRequest(new { message = "Este servicio no está activo actualmente" });
                }

                // ✅ VALIDACIÓN CENTRALIZADA: Verificar que el experto puede recibir pagos
                if (service.ExpertProfile != null)
                {
                    var validationResult = await _stripeValidationService.ValidateExpertCanReceivePaymentsAsync(
                        service.ExpertProfile, "contratar servicio");
                    
                    if (!validationResult.IsValid)
                    {
                        return BadRequest(new { 
                            message = validationResult.ErrorMessage,
                            stripeStatus = validationResult.StripeStatus,
                            requiresStripeSetup = validationResult.RequiresStripeSetup,
                            canRetry = validationResult.CanRetry
                        });
                    }
                }

                // 🚨 VALIDACIÓN CRÍTICA: Verificar que el experto no se contrate a sí mismo
                // ✅ IMPORTANTE: Esta validación DEBE hacerse ANTES de crear el checkout session
                // para evitar perder comisiones de Stripe al hacer refunds
                if (service.ExpertProfile != null && service.ExpertProfile.UserId == userId)
                {
                    return BadRequest(new { message = "No puedes contratarte a ti mismo como experto" });
                }

                // P2-2: pre-check de Users (Role/ExpertProfile) ANTES de crear la sesión
                // Stripe. El FOR UPDATE + commit inmediato anterior no bloqueaba nada
                // útil; ninguna mutación posterior dentro de esta ruta dependía del lock
                // (la contratación se materializa en el webhook). Se reduce a lectura.
                var user = await _context.Users
                    .Include(u => u.ExpertProfile)
                    .FirstOrDefaultAsync(u => u.Id == userId);
                
                if (user == null)
                {
                    return NotFound(new { message = "User not found" });
                }

                // 🚨 VALIDACIÓN CRÍTICA: Los expertos no pueden crear contrataciones como clientes
                // ✅ IMPORTANTE: Deben usar una cuenta distinta (no registrada como experto) para contratar
                // ✅ MEJORA: Verificar explícitamente si tiene ExpertProfile en la BD (no solo en memoria)
                var hasExpertProfile = await _context.ExpertProfiles
                    .AnyAsync(ep => ep.UserId == userId);
                
                if (user.Role == UserRole.Expert || hasExpertProfile || user.ExpertProfile != null)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Expert attempted to create contract as client",
                        details: $"User {userId} (Email: {user.Email}, Role: {user.Role}, HasExpertProfile: {hasExpertProfile}) attempted to create a contract as client. Blocked.",
                        userId: userId,
                        source: "SubscriptionController.HireService",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new { 
                            UserId = userId,
                            UserEmail = user.Email,
                            UserRole = user.Role.ToString(),
                            HasExpertProfileInMemory = user.ExpertProfile != null,
                            HasExpertProfileInDb = hasExpertProfile
                        }
                    );
                    
                    return BadRequest(new { 
                        message = "Los expertos no pueden crear contrataciones. Debes usar una cuenta distinta (no registrada como experto) para contratar servicios."
                    });
                }

                // ✅ COMENTADO: Verificación de teléfono ya no es necesaria
                // 🚨 VALIDACIÓN CRÍTICA: Verificar teléfono antes del pago
                // ✅ IMPORTANTE: Esta validación DEBE hacerse ANTES de crear el checkout session
                /*
                if (!user.PhoneVerified)
                {
                    return StatusCode(403, new { message = "Phone verification required to create hires" });
                }
                */

                // 💳 NO SE NECESITA VERIFICAR BALANCE - SIEMPRE SE PAGA CON STRIPE

                // 🚨 PROTECCIÓN CONTRA CONTRATACIONES DUPLICADAS
                var pendingStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Pending.ToStringValue());
                var awaitingStatusId = await GetStatusIdByValueAsync(SearchHireStatus.AwaitingClientDecision.ToStringValue());
                var disputedStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());

                var existingHire = await _context.SearchHires
                    .FirstOrDefaultAsync(sh => sh.ClientId == userId &&
                                              sh.SearchServiceId == service.Id &&
                                              (sh.StatusId == pendingStatusId ||
                                               sh.StatusId == awaitingStatusId ||
                                               sh.StatusId == disputedStatusId));

                if (existingHire != null)
                {
                    return BadRequest(new { message = "Ya tienes una contratación activa para este servicio" });
                }

                // 🛡️ N17 FIX: límite global de hires Pending por cliente — evita DoS / spam (el
                // existingHire de arriba protege solo por (cliente,servicio), un atacante puede
                // saltarlo creando N hires a servicios distintos del mismo experto). Límite arbitrario 20.
                const int N17_MAX_PENDING_HIRES_PER_CLIENT = 20;
                var n17PendingCount = await _context.SearchHires
                    .AsNoTracking()
                    .CountAsync(sh => sh.ClientId == userId && sh.StatusId == pendingStatusId);
                if (n17PendingCount >= N17_MAX_PENDING_HIRES_PER_CLIENT)
                {
                    return BadRequest(new
                    {
                        message = $"Has alcanzado el límite de {N17_MAX_PENDING_HIRES_PER_CLIENT} contrataciones pendientes simultáneas. Completa o cancela algunas antes de crear nuevas."
                    });
                }

                // 💳 SIEMPRE PAGAR CON STRIPE - NO USAR SALDO INTERNO
                var domain = _configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com";

                // 🛡️ Round 28 MUD-6: defensa en profundidad — override del Currency contra Stripe Account
                // ANTES de crear la Session. Si el SearchService.Currency en BD no coincide con el
                // default_currency del Stripe acct del experto (caso de mudanza incompleta o servicio
                // legacy creado pre-fix), Stripe rechazaría con currency_mismatch (separate charges
                // and transfers exige coherencia entre PI.currency y destination.default_currency).
                // MUD-1 hace este override en CreateService, pero servicios legacy ya tienen Currency
                // potencialmente mal. Re-validamos aquí justo antes del cobro.
                var checkoutCurrency = (service.Currency ?? "EUR").ToLowerInvariant();
                if (!string.IsNullOrEmpty(service.ExpertProfile?.StripeAccountId))
                {
                    try
                    {
                        var stripeAcctService = new Stripe.AccountService();
                        var stripeAcct = await stripeAcctService.GetAsync(service.ExpertProfile.StripeAccountId);
                        var acctDefault = stripeAcct?.DefaultCurrency;
                        if (!string.IsNullOrEmpty(acctDefault) && !string.Equals(acctDefault, checkoutCurrency, StringComparison.OrdinalIgnoreCase))
                        {
                            await _loggingService.LogWarningAsync(
                                message: "HireService MUD-6: currency mismatch overriden to Stripe acct currency",
                                details: $"Service {service.Id}: BD says {checkoutCurrency.ToUpperInvariant()}, Stripe acct {service.ExpertProfile.StripeAccountId} default_currency is {acctDefault.ToUpperInvariant()}. Overriding to {acctDefault.ToUpperInvariant()} para evitar currency_mismatch.",
                                userId: userId,
                                source: "SubscriptionController.HireService.MUD6",
                                relatedEntityType: "SearchService",
                                relatedEntityId: service.Id);
                            checkoutCurrency = acctDefault.Trim().ToLowerInvariant();
                        }
                    }
                    catch (Stripe.StripeException stripeReadEx) when (stripeReadEx.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Stripe acct ya no existe — bloquear el checkout para evitar dejar al cliente con un PI huérfano.
                        return BadRequest(new
                        {
                            message = "Este experto no puede recibir cobros en este momento (cuenta de pagos no disponible). Por favor, contacta al soporte o vuelve a intentar más tarde.",
                            errorCode = "EXPERT_STRIPE_ACCOUNT_NOT_FOUND"
                        });
                    }
                    catch (Exception stripeReadEx)
                    {
                        // Best-effort: si Stripe no responde, usar BD pero loguear para auditoría.
                        await _loggingService.LogWarningAsync(
                            message: "HireService MUD-6: could not verify Stripe acct currency, using BD value",
                            details: $"Service {service.Id}: {stripeReadEx.Message}. Using {checkoutCurrency.ToUpperInvariant()} from BD.",
                            userId: userId,
                            source: "SubscriptionController.HireService.MUD6",
                            relatedEntityType: "SearchService",
                            relatedEntityId: service.Id);
                    }
                }

                var options = new SessionCreateOptions
                {
                    PaymentMethodTypes = new List<string> { "card" },
                    LineItems = new List<SessionLineItemOptions>
                    {
                        new SessionLineItemOptions
                        {
                            PriceData = new SessionLineItemPriceDataOptions
                            {
                                // 🌍 Round 25 FIX (M4-1): no hardcodear "eur" — Stripe rechaza el checkout
                                // si el currency del PriceData no coincide con el del Stripe Account del experto
                                // (Connect Express). Usamos el SearchService.Currency (ISO 4217 en mayúsculas)
                                // y lo pasamos a Stripe en minúsculas como exige la API.
                                // 🛡️ Round 28 Sprint 3: defensa NPE — si Currency es null en una fila legacy,
                                // caer a "eur" en vez de NullReferenceException.
                                // 🛡️ Round 28 MUD-6: usar `checkoutCurrency` que YA está validado contra Stripe acct.
                                Currency = checkoutCurrency,
                                UnitAmount = checked((long)Math.Round(service.Price * 100)),
                                ProductData = new SessionLineItemPriceDataProductDataOptions
                                {
                                    Name = $"Payment for Service {service.Id}"
                                }
                                // ✅ STRIPE TAX (Docs 2026): NO especificar TaxBehavior para que Stripe use el default automático configurado en Dashboard
                                // Si el Dashboard está en "Automático", Stripe aplicará según moneda: USD/CAD → exclusive, resto → inclusive
                                // Si se especifica, solo se permiten: "inclusive" o "exclusive" (no "unspecified" ni "automatic")
                            },
                            Quantity = 1
                        }
                    },
                    // ✅ STRIPE TAX: Habilitar cálculo automático de tax basado en ubicación del comprador
                    AutomaticTax = new SessionAutomaticTaxOptions
                    {
                        Enabled = true, // Habilita cálculo auto basado en IP, billing/shipping address
                        Liability = new SessionAutomaticTaxLiabilityOptions { Type = "self" } // 🔧 FIX: plataforma = responsable fiscal (MoR)
                    },
                    TaxIdCollection = new SessionTaxIdCollectionOptions { Enabled = true }, // 🔧 FIX: recoge NIF/VAT -> reverse charge B2B
                    BillingAddressCollection = "required", // 🔧 FIX: direccion fiable para AutomaticTax correcto por pais
                    Mode = "payment",
                    SuccessUrl = $"{domain}/success?session_id={{CHECKOUT_SESSION_ID}}&userId={userId}",
                    CancelUrl = $"{domain}/cancel",
                    CustomerEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown@example.com",
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() },
                        { "serviceId", service.Id.ToString() },
                        // 🛡️ A9 FIX: InvariantCulture (consistencia hash idempotency Stripe)
                        { "amount", service.Price.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                        { "searchId", request.SearchId.ToString() },
                        { "pendingHire", "true" }
                    },
                    // ✅ CAPTURA MANUAL: Autoriza el pago pero no lo captura hasta validar todo en el webhook
                    // Esto evita perder comisiones si algo falla después del pago
                    PaymentIntentData = new SessionPaymentIntentDataOptions
                    {
                        CaptureMethod = "manual"
                    }
                };

                var stripeService = new SessionService();
                Session session;
                try
                {
                    // 🔧 FIX #6 + regresión: searchId ya discrimina compras distintas; lo pasamos por el hash
                    // junto al precio para blindar también un cambio de precio del servicio entre dos intentos del
                    // mismo searchId (evita idempotency_error 400). Mismo (searchId,precio) => misma clave.
                    var idempotencyKey = IdempotencyKeyHelper.ForCheckout(
                        userId, service.Id,
                        request.SearchId.ToString(),
                        service.Price.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    session = await stripeService.CreateAsync(options, new RequestOptions { IdempotencyKey = idempotencyKey });
                    
                    await _loggingService.LogInfoAsync(
                        message: "Sesión de pago Stripe creada exitosamente",
                        details: $"SessionId: {session.Id}, ServiceId: {request.SearchServiceId}, Amount: {service.Price}€, UserId: {userId}",
                        userId: userId,
                        source: "SubscriptionController.HireService",
                        relatedEntityType: "Payment",
                        relatedEntityId: null,
                        additionalData: new { 
                            SessionId = session.Id,
                            ServiceId = request.SearchServiceId,
                            SearchId = request.SearchId,
                            Amount = service.Price
                        }
                    );
                }
                catch (StripeException ex)
                {
                    await _loggingService.LogErrorAsync(
                        message: "Error al crear sesión de pago Stripe",
                        details: $"StripeException al crear checkout session. ServiceId: {request.SearchServiceId}, UserId: {userId}, Error: {ex.Message}, StripeError: {ex.StripeError?.Message}",
                        userId: userId,
                        source: "SubscriptionController.HireService",
                        relatedEntityType: "Payment",
                        relatedEntityId: null,
                        additionalData: new { 
                            ServiceId = request.SearchServiceId,
                            SearchId = request.SearchId,
                            StripeErrorType = ex.StripeError?.Type,
                            StripeErrorCode = ex.StripeError?.Code,
                            StripeErrorMessage = ex.StripeError?.Message
                        }
                    );

                    return StatusCode(500, new { message = "Failed to create payment session" });
                }

                return Ok(new { url = session.Url });
            }
            catch (Exception ex)
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int? userId = null;
                if (int.TryParse(userIdClaim, out int parsedUserId))
                {
                    userId = parsedUserId;
                }

                await _loggingService.LogErrorAsync(
                    message: "Error al contratar servicio",
                    details: $"Error en HireService. ServiceId: {request?.SearchServiceId}, SearchId: {request?.SearchId}, UserId: {userId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "SubscriptionController.HireService",
                    relatedEntityType: "SearchService",
                    relatedEntityId: request?.SearchServiceId,
                    additionalData: new { 
                        ServiceId = request?.SearchServiceId,
                        SearchId = request?.SearchId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Failed to hire service" });
            }
        }

        [HttpPost("cancel-service")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> CancelService([FromBody] CancelServiceDto request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // P2-1: una sola transacción envolvente (ExecutionStrategy + BeginTransaction)
                // cubre FOR UPDATE, distribución de dinero y SaveChanges. ProcessMoneyDistributionAsync
                // detecta CurrentTransaction y reutiliza la tx ambiente (no abre otra anidada).
                // El lock pesimista se mantiene hasta el commit final, evitando el antipatrón
                // FOR UPDATE + commit inmediato que liberaba el lock antes de la mutación.
                var distributionStatusValue = SearchHireStatus.Cancelled.ToStringValue();
                bool enqueueRetry = false;
                int retryHireId = 0;
                int successHireId = 0;

                var strategy = _context.Database.CreateExecutionStrategy();
                var actionResult = await strategy.ExecuteAsync<IActionResult>(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();

                    var searchHire = await _context.SearchHires
                        .FromSqlInterpolated($"SELECT *, xmin FROM \"SearchHires\" WHERE \"Id\" = {request.SearchHireId} AND \"ExpertId\" = {userId} FOR UPDATE")
                        .Include(sh => sh.Status)
                        .Include(sh => sh.Client)
                        .Include(sh => sh.Appointment)
                        .Include(sh => sh.SearchService)
                            .ThenInclude(ss => ss.ServiceType)
                                .ThenInclude(st => st.ServiceTypeCategory)
                        .FirstOrDefaultAsync();

                    if (searchHire == null)
                    {
                        await transaction.RollbackAsync();
                        return (IActionResult)NotFound(new { message = "Service not found or unauthorized" });
                    }

                    if (searchHire.Status.StatusValue != SearchHireStatus.Pending.ToStringValue())
                    {
                        await transaction.RollbackAsync();
                        return BadRequest(new { message = "Service is not pending" });
                    }

                    var appointment = searchHire.Appointment;
                    if (appointment == null)
                    {
                        await transaction.RollbackAsync();
                        return BadRequest(new { message = "No appointment found" });
                    }

                    string statusValue = appointment.ExpertCancellationCount >= 1
                        ? "appointment_cancelled_by_expert_second"
                        : "appointment_cancelled_by_expert";

                    var cancelledStatus = await _context.SystemStatuses
                        .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && s.StatusValue == statusValue);

                    if (cancelledStatus == null)
                    {
                        await transaction.RollbackAsync();
                        return BadRequest(new { message = "Invalid cancellation status" });
                    }

                    // cancel-service es terminal: el cliente SIEMPRE se reembolsa con el estado
                    // SearchHire 'cancelled' (100/0/0). updateState:false → el hire se marca abajo
                    // dentro de esta MISMA tx; la cita se marca aquí.
                    appointment.StatusId = cancelledStatus.Id;
                    appointment.UpdatedAt = DateTime.UtcNow;

                    bool refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                        searchHire.Id,
                        distributionStatusValue,
                        $"Expert cancelled pending service {searchHire.Id}",
                        userId,
                        updateState: false);

                    if (!refundSuccess)
                    {
                        await _loggingService.LogCriticalAsync(
                            $"Failed to process money distribution - SearchHireId: {searchHire.Id}",
                            $"Money distribution failed for search hire",
                            searchHire.ClientId,
                            "SubscriptionController.CancelService",
                            "SearchHire",
                            searchHire.Id,
                            new { SearchHireId = searchHire.Id, ClientId = searchHire.ClientId, StatusValue = distributionStatusValue }
                        );
                        // Marca para encolar el retry FUERA y DESPUÉS del commit (no encolamos
                        // jobs huérfanos si el commit termina haciendo rollback).
                        enqueueRetry = true;
                        retryHireId = searchHire.Id;
                    }

                    searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                    searchHire.UpdatedAt = DateTime.UtcNow;

                    try
                    {
                        await ConcurrencyRetryHelper.SaveChangesWithRetryAsync(_context,
                            () => _context.SaveChangesAsync());
                    }
                    catch (DbUpdateConcurrencyException concEx)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: CancelService concurrency conflict after retry - state NOT advanced; money may already be moved",
                            details: $"SearchHire {searchHire.Id} concurrency conflict after retry: {concEx.Message}",
                            userId: userId,
                            source: "SubscriptionController.CancelService",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id);
                        await transaction.RollbackAsync();
                        // No encolar retry: cancelamos la marca pendiente
                        enqueueRetry = false;
                        return Conflict(new { message = "Service state changed concurrently, please retry" });
                    }

                    await transaction.CommitAsync();
                    successHireId = searchHire.Id;
                    return Ok(new { message = "Service cancelled and refunded via Stripe" });
                });

                if (enqueueRetry)
                {
                    Hangfire.BackgroundJob.Schedule<StripeRefundService>(
                        s => s.RetryMoneyDistributionJobAsync(
                            retryHireId,
                            distributionStatusValue,
                            $"Retry money distribution for cancelled service {retryHireId}",
                            userId),
                        TimeSpan.FromMinutes(2));
                }

                if (successHireId > 0)
                {
                    await _loggingService.LogInfoAsync(
                        message: "CANCEL_SERVICE",
                        details: $"Canceló servicio {successHireId} como experto con refund real de Stripe",
                        userId: userId,
                        source: "UserAction",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: successHireId
                    );
                }

                return actionResult;
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to cancel service" });
            }
        }


        [HttpPost("force-finalize")]
        public async Task<IActionResult> ForceFinalize([FromBody] ForceFinalizeDto request)
        {
            // 🔐 SEGURIDAD: Verificar rol en lugar de email
            if (!_authService.IsAdmin(User))
            {
                return Unauthorized(new { message = "Admin access required" });
            }
            try
            {
                if (!request.ResolveInFavorOfClient)
                {
                    return BadRequest(new { message = "Force finalize in favor of expert is no longer supported. Use dispute resolution instead." });
                }

                var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");

                // P2-1: ExecutionStrategy + tx envolvente. FOR UPDATE, Stripe call y
                // SaveChanges comparten transacción → el lock se mantiene hasta el commit.
                bool enqueueRetry = false;
                int retryHireId = 0;
                int successHireId = 0;

                var strategy = _context.Database.CreateExecutionStrategy();
                var actionResult = await strategy.ExecuteAsync<IActionResult>(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();

                    var searchHire = await _context.SearchHires
                        .FromSqlInterpolated($"SELECT *, xmin FROM \"SearchHires\" WHERE \"Id\" = {request.SearchHireId} FOR UPDATE")
                        .Include(sh => sh.Status)
                        .Include(sh => sh.Client)
                        .Include(sh => sh.Expert)
                        .ThenInclude(e => e.ExpertProfile)
                        .FirstOrDefaultAsync();

                    if (searchHire == null)
                    {
                        await transaction.RollbackAsync();
                        return (IActionResult)NotFound(new { message = "Service not found" });
                    }

                    // 🛡️ Round 15 — R7 FIX: bloquear ForceFinalize si el hire YA está en estado
                    // terminal. Antes: admin podía ejecutar force-finalize sobre un Completed →
                    // RefundService entraba en rama "partial distribution" + clawback al experto
                    // que ya había cobrado. Si admin quiere refund post-completed por excepción,
                    // debe usar un endpoint específico de "compensation refund" (no implementado),
                    // NO reutilizar force-finalize (pensado para casos disputados).
                    if (searchHire.Status?.IsFinalizationStatus == true)
                    {
                        await transaction.RollbackAsync();
                        return (IActionResult)BadRequest(new {
                            message = $"El servicio ya está en estado final ({searchHire.Status.StatusValue}). No se puede ForceFinalize. Para reembolsos posteriores contacta con el equipo técnico."
                        });
                    }

                    var success = await _refundService.ProcessMoneyDistributionAsync(
                        searchHire.Id,
                        "dispute_resolved_client",
                        "Force finalize in favor of client",
                        adminUserId);

                    if (!success)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Force-finalize money distribution failed - state advanced, money retry enqueued",
                            details: $"SearchHire {searchHire.Id} force-finalized in favor of client but money distribution failed; retry enqueued, needs monitoring.",
                            userId: adminUserId,
                            source: "SubscriptionController.ForceFinalize",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id);
                        enqueueRetry = true;
                        retryHireId = searchHire.Id;
                    }

                    searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.DisputeResolvedClient.ToStringValue());

                    var ffAppointment = await _context.Appointments
                        .FirstOrDefaultAsync(a => a.SearchHireId == searchHire.Id);
                    if (ffAppointment != null)
                    {
                        var ffApptCompleted = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_completed");
                        if (ffApptCompleted != null && ffAppointment.StatusId != ffApptCompleted.Id)
                        {
                            ffAppointment.StatusId = ffApptCompleted.Id;
                            ffAppointment.UpdatedAt = DateTime.UtcNow;
                        }
                    }

                    try
                    {
                        await ConcurrencyRetryHelper.SaveChangesWithRetryAsync(_context,
                            () => _context.SaveChangesAsync());
                    }
                    catch (DbUpdateConcurrencyException concEx)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: ForceFinalize concurrency conflict after retry - state NOT advanced; money may already be moved",
                            details: $"SearchHire {searchHire.Id} concurrency conflict after retry: {concEx.Message}",
                            userId: adminUserId,
                            source: "SubscriptionController.ForceFinalize",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id);
                        await transaction.RollbackAsync();
                        enqueueRetry = false;
                        return Conflict(new { message = "Service state changed concurrently, please retry" });
                    }

                    await transaction.CommitAsync();
                    successHireId = searchHire.Id;
                    return Ok(new { message = "Service finalized successfully in favor of client" });
                });

                if (enqueueRetry)
                {
                    Hangfire.BackgroundJob.Schedule<StripeRefundService>(
                        s => s.RetryMoneyDistributionJobAsync(
                            retryHireId,
                            "dispute_resolved_client",
                            "Retry force-finalize client refund (money pending)",
                            null),
                        TimeSpan.FromMinutes(2));
                }

                if (successHireId > 0)
                {
                    await _loggingService.LogWarningAsync(
                        message: "FORCE_FINALIZE_CLIENT_REFUND",
                        details: $"Finalizó forzadamente servicio {successHireId} a favor del cliente con orquestador",
                        userId: adminUserId,
                        source: "AdminAction",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: successHireId
                    );
                }

                return actionResult;
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to finalize service" });
            }
        }

        [HttpPost("resolve-dispute")]
        public async Task<IActionResult> ResolveDispute([FromBody] ResolveDisputeDto request)
        {
            // 🔐 SEGURIDAD: Verificar rol en lugar de email
            if (!_authService.IsAdmin(User))
            {
                return Unauthorized(new { message = "Admin access required" });
            }
            try
            {
                if (string.IsNullOrWhiteSpace(request.Resolution))
                {
                    return BadRequest(new { message = "Resolution reason is required" });
                }

                var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                var statusValue = request.ResolveInFavorOfClient
                    ? SearchHireStatus.DisputeResolvedClient.ToStringValue()
                    : SearchHireStatus.DisputeResolvedExpert.ToStringValue();

                // P2-1: ExecutionStrategy + tx envolvente. FOR UPDATE, mutaciones, Stripe
                // y SaveChanges en la misma transacción. ProcessMoneyDistributionAsync detecta
                // CurrentTransaction y reutiliza la tx ambiente (no abre tx anidada).
                // El A6 mantiene el orden: dispute.Status="Resolved" SÓLO al final, después
                // de que el dinero se haya intentado mover (si el dinero falla la disputa
                // queda Pending y el retry encolado lo termina).
                bool enqueueRetry = false;
                int retryHireId = 0;
                int successHireId = 0;
                // 🛡️ B3/F6: snapshot capturado dentro del strategy para enviar evidencia a Stripe
                // FUERA de la transacción (después del commit), evitando que un fallo del UpdateAsync
                // de Stripe rollee la resolución ya persistida.
                string? f6StripeDisputeId = null;
                int f6DisputeId = 0;
                string? f6Reason = null;
                string? f6ExpertResponse = null;
                string? f6ResolutionComments = null;
                bool f6ShouldSubmit = false;

                var strategy = _context.Database.CreateExecutionStrategy();
                var actionResult = await strategy.ExecuteAsync<IActionResult>(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();

                    var searchHire = await _context.SearchHires
                        .FromSqlInterpolated($"SELECT *, xmin FROM \"SearchHires\" WHERE \"Id\" = {request.SearchHireId} FOR UPDATE")
                        .Include(sh => sh.Status)
                        .Include(sh => sh.Client)
                        .Include(sh => sh.Expert)
                        .ThenInclude(e => e.ExpertProfile)
                        .FirstOrDefaultAsync();

                    if (searchHire == null)
                    {
                        await transaction.RollbackAsync();
                        return (IActionResult)NotFound(new { message = "Service not found" });
                    }

                    if (searchHire.Status.StatusValue != SearchHireStatus.Disputed.ToStringValue())
                    {
                        await transaction.RollbackAsync();
                        return BadRequest(new { message = "Service is not disputed" });
                    }

                    var dispute = await _context.Disputes
                        .FirstOrDefaultAsync(d => d.SearchHireId == searchHire.Id && d.Status == "Pending");

                    if (dispute == null)
                    {
                        await transaction.RollbackAsync();
                        return NotFound(new { message = "No pending dispute found" });
                    }

                    // 🛡️ B3 FIX: claim atómico Pending→Resolving para cerrar la race con
                    // DisputeController.ResolveDispute (otro endpoint admin que también puede
                    // resolver esta misma disputa). Solo un endpoint voltea Pending→Resolving
                    // (1 fila afectada); el segundo recibe 0 filas y se rechaza. Replicado del
                    // patrón ya probado en DisputeController línea ~530.
                    var disputeIdToClaim = dispute.Id;
                    var claimed = await _context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"Disputes\" SET \"Status\" = 'Resolving' WHERE \"Id\" = {disputeIdToClaim} AND \"Status\" = 'Pending'");
                    if (claimed == 0)
                    {
                        await transaction.RollbackAsync();
                        return BadRequest(new { message = "Dispute is already resolved or currently being resolved" });
                    }
                    dispute.Status = "Resolving"; // alinear entidad en memoria con el claim ya persistido

                    // Auto-sanación: si esta request muere tras el claim sin dejar Resolved/Pending,
                    // el job devuelve la disputa a Pending en 10 min. No-op si la resolución acaba normal.
                    Hangfire.BackgroundJob.Schedule<StripeRefundService>(
                        s => s.RescueStuckResolvingDisputeAsync(disputeIdToClaim),
                        TimeSpan.FromMinutes(10));

                    dispute.ResolutionComments = request.Resolution;

                    var success = await _refundService.ProcessMoneyDistributionAsync(
                        searchHire.Id,
                        statusValue,
                        request.ResolveInFavorOfClient
                            ? $"Dispute resolved in favor of client: {request.Resolution}"
                            : $"Dispute resolved in favor of expert: {request.Resolution}",
                        adminUserId);

                    if (!success)
                    {
                        var lastCriticalLog = await _context.Logs
                            .Include(l => l.LogType)
                            .Where(l => l.RelatedEntityType == "SearchHire" &&
                                        l.RelatedEntityId == searchHire.Id &&
                                        l.LogType != null &&
                                        l.LogType.Name == "Critical" &&
                                        l.Source != null &&
                                        l.Source.Contains("ProcessMoneyDistributionAsync"))
                            .OrderByDescending(l => l.CreatedAt)
                            .FirstOrDefaultAsync();

                        var errorMessage = lastCriticalLog != null
                            ? $"Failed to process money distribution: {lastCriticalLog.Message}. Check logs for details (LogId: {lastCriticalLog.Id})"
                            : "Failed to process money distribution. Check ProcessMoneyDistributionAsync logs for details.";

                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Dispute resolution money distribution failed - state advanced, money retry enqueued",
                            details: errorMessage,
                            userId: adminUserId,
                            source: "SubscriptionController.ResolveDispute",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id);
                        enqueueRetry = true;
                        retryHireId = searchHire.Id;
                    }

                    var rdAppointment = await _context.Appointments
                        .FirstOrDefaultAsync(a => a.SearchHireId == searchHire.Id);
                    if (rdAppointment != null)
                    {
                        var rdApptCompleted = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_completed");
                        if (rdApptCompleted != null && rdAppointment.StatusId != rdApptCompleted.Id)
                        {
                            rdAppointment.StatusId = rdApptCompleted.Id;
                            rdAppointment.UpdatedAt = DateTime.UtcNow;
                        }
                    }

                    // Sólo marcar Resolved si el dinero se movió. Si falló, la queue lo intenta
                    // de nuevo y un siguiente paso (manual o automático) lo cerrará.
                    if (success)
                    {
                        dispute.Status = "Resolved";

                        // 🛡️ B3/F6: capturar snapshot para enviar evidencia a Stripe FUERA de la tx.
                        // Solo aplica si la disputa está vinculada a Stripe y se resuelve a favor del experto
                        // (caso del chargeback: queremos defender el cargo presentando evidencia).
                        if (!request.ResolveInFavorOfClient && !string.IsNullOrEmpty(dispute.StripeDisputeId))
                        {
                            f6StripeDisputeId = dispute.StripeDisputeId;
                            f6DisputeId = dispute.Id;
                            f6Reason = dispute.Reason;
                            f6ExpertResponse = dispute.ExpertResponse;
                            f6ResolutionComments = dispute.ResolutionComments;
                            f6ShouldSubmit = true;
                        }
                    }

                    try
                    {
                        await ConcurrencyRetryHelper.SaveChangesWithRetryAsync(_context,
                            () => _context.SaveChangesAsync());
                    }
                    catch (DbUpdateConcurrencyException concEx)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: ResolveDispute concurrency conflict after retry - state NOT advanced; money may already be moved",
                            details: $"Dispute {dispute.Id} concurrency conflict after retry: {concEx.Message}",
                            userId: adminUserId,
                            source: "SubscriptionController.ResolveDispute",
                            relatedEntityType: "Dispute",
                            relatedEntityId: dispute.Id);
                        await transaction.RollbackAsync();
                        enqueueRetry = false;
                        return Conflict(new { message = "Dispute state changed concurrently, please retry" });
                    }

                    await transaction.CommitAsync();
                    successHireId = searchHire.Id;
                    return Ok(new { message = "Dispute resolved" });
                });

                if (enqueueRetry)
                {
                    Hangfire.BackgroundJob.Schedule<StripeRefundService>(
                        s => s.RetryMoneyDistributionJobAsync(
                            retryHireId,
                            statusValue,
                            "Retry dispute resolution money distribution (money pending)",
                            adminUserId),
                        TimeSpan.FromMinutes(2));
                }

                if (successHireId > 0)
                {
                    if (request.ResolveInFavorOfClient)
                    {
                        await _loggingService.LogWarningAsync(
                            message: "RESOLVE_DISPUTE_CLIENT_REFUND",
                            details: $"Resolvió disputa {successHireId} a favor del cliente con orquestador: {request.Resolution}",
                            userId: adminUserId,
                            source: "AdminAction",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: successHireId
                        );
                    }
                    else
                    {
                        await _loggingService.LogWarningAsync(
                            message: "RESOLVE_DISPUTE_EXPERT",
                            details: $"Resolvió disputa {successHireId} a favor del experto con orquestador: {request.Resolution}",
                            userId: adminUserId,
                            source: "AdminAction",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: successHireId
                        );
                    }
                }

                // 🛡️ B3/F6: enviar evidencia a Stripe FUERA de la transacción (post-commit).
                // Si falla aquí no afecta al estado interno ya persistido; solo log critical para
                // que el admin pueda reenviar manualmente desde el Dashboard.
                if (f6ShouldSubmit && !string.IsNullOrEmpty(f6StripeDisputeId))
                {
                    try
                    {
                        var combinedText = string.Join(" | ",
                            new[]
                            {
                                string.IsNullOrWhiteSpace(f6Reason) ? null : $"Reason: {f6Reason}",
                                string.IsNullOrWhiteSpace(f6ExpertResponse) ? null : $"Expert response: {f6ExpertResponse}",
                                string.IsNullOrWhiteSpace(f6ResolutionComments) ? null : $"Resolution: {f6ResolutionComments}"
                            }.Where(s => !string.IsNullOrEmpty(s)));
                        if (combinedText.Length > 150_000) combinedText = combinedText.Substring(0, 150_000);

                        await new Stripe.DisputeService().UpdateAsync(
                            f6StripeDisputeId,
                            new Stripe.DisputeUpdateOptions
                            {
                                Evidence = new Stripe.DisputeEvidenceOptions
                                {
                                    UncategorizedText = string.IsNullOrWhiteSpace(combinedText) ? null : combinedText
                                },
                                Submit = true
                            },
                            // 🛡️ R2 FIX: clave determinista por f6DisputeId (sin DateTime.UtcNow).
                            new Stripe.RequestOptions { IdempotencyKey = $"dispute-submit-{f6DisputeId}" });
                    }
                    catch (Exception submitEx)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "Failed to submit dispute evidence to Stripe on resolution",
                            details: $"DisputeId {f6DisputeId} StripeDisputeId {f6StripeDisputeId}: {submitEx.Message}. Admin debe reenviar manualmente desde Stripe Dashboard.",
                            source: "SubscriptionController.ResolveDispute",
                            relatedEntityType: "Dispute",
                            relatedEntityId: f6DisputeId);
                    }
                }

                return actionResult;
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to resolve dispute" });
            }
        }


        /// <summary>
        /// Obtiene la configuración de distribución de dinero según el estado y categorías
        /// </summary>
        /// <param name="status">Estado de la cita</param>
        /// <param name="categoryId">ID de la categoría</param>
        /// <param name="serviceTypeCategoryId">ID de la categoría del tipo de servicio</param>
        /// <returns>Configuración de distribución de dinero</returns>


        private async Task<MoneyDistributionConfigDto?> GetMoneyDistributionConfigAsync(string status, int? categoryId, int? serviceTypeCategoryId)
        {
            // 🎯 USAR NUEVO SISTEMA CENTRALIZADO DE ESTADOS
            var config = await _systemStatusService.GetMoneyDistributionAsync(status, categoryId, serviceTypeCategoryId);
            
            if (config != null)
            {
                return new MoneyDistributionConfigDto
                {
                    ClientPercentage = config.ClientPercentage,
                    ExpertPercentage = config.ExpertPercentage,
                    PlatformPercentage = config.PlatformPercentage,
                    Source = "centralized_status_system",
                    Status = status
                };
            }

            // Sistema anterior eliminado - solo usar el nuevo sistema centralizado

            // 4. NO HAY CONFIGURACIÓN - FALLAR EN LUGAR DE INVENTAR VALORES
            return null;
        }

        // ✅ REMOVED: HandleLoadMoneyCompleted method eliminated - balance system removed
        // ❌ ELIMINADO: HandleCheckoutSessionCompleted - Suscripciones periódicas ya no se usan
        // ❌ ELIMINADO: HandleSubscriptionUpdated - Suscripciones periódicas ya no se usan
        // ❌ ELIMINADO: HandleSubscriptionCanceled - Suscripciones periódicas ya no se usan
        // ❌ ELIMINADO: HandlePaymentSucceeded - Suscripciones periódicas ya no se usan (solo invoice.SubscriptionId)
        // ❌ ELIMINADO: HandlePaymentFailed - Suscripciones periódicas ya no se usan (solo invoice.SubscriptionId)

        public class LoadMoneyDto
        {
            public decimal Amount { get; set; }
        }

        public class HireServiceDto
        {
            public int SearchServiceId { get; set; }
            public int SearchId { get; set; }
        }

        public class CancelServiceDto
        {
            public int SearchHireId { get; set; }
        }
        public class LoadMoneyServiceDto
        {
            public int ServiceId { get; set; }
            public decimal Amount { get; set; }
        }

        public class ForceFinalizeDto
        {
            public int SearchHireId { get; set; }
            public bool ResolveInFavorOfClient { get; set; }
        }

        public class ResolveDisputeDto
        {
            public int SearchHireId { get; set; }
            public bool ResolveInFavorOfClient { get; set; }
            public string Resolution { get; set; }
        }

    }

    // MEJORA: Métodos auxiliares para idempotencia de webhooks
    public partial class SubscriptionController
    {
        /// <summary>
        /// Calcula si se puede reintentar el onboarding basándose en el estado y el motivo de rechazo
        /// </summary>
        private bool CalculateCanRetryOnboarding(StripeStatus stripeStatus, string? pendingStripeAccountId, string? rejectionReason)
        {
            // Permitir reintentar si no ha solicitado cuenta o está pendiente sin cuenta pendiente
            // 🔧 Round 26 DEAUTH-6: Deauthorized también permite reintento (paridad con StripeValidationService.canRetry)
            bool canRetry = stripeStatus == StripeStatus.NotRequested ||
                           (stripeStatus == StripeStatus.Pending && string.IsNullOrEmpty(pendingStripeAccountId)) ||
                           stripeStatus == StripeStatus.Deauthorized;

            // Si está Rejected, solo permitir si NO es un rechazo permanente
            if (stripeStatus == StripeStatus.Rejected && !string.IsNullOrEmpty(rejectionReason))
            {
                canRetry = !IsPermanentRejection(rejectionReason);
            }

            return canRetry;
        }

        /// <summary>
        /// Extrae el motivo del rechazo del mensaje formateado de StripeStatusDetails
        /// </summary>
        private string? ExtractRejectionReasonFromDetails(string? stripeStatusDetails)
        {
            if (string.IsNullOrEmpty(stripeStatusDetails))
                return null;

            var details = stripeStatusDetails.ToLower();
            
            // Buscar patrones conocidos en el mensaje (tanto en inglés como español)
            // Buscar variantes de "requirements.past_due" / "requisitos vencidos"
            if (details.Contains("requirements.past_due") || 
                details.Contains("requisitos vencidos") || 
                details.Contains("requisitos vencido") ||
                details.Contains("hay requisitos vencidos") ||
                details.Contains("requisitos vencido que debías"))
                return "requirements.past_due";
            else if (details.Contains("requirements.pending_verification") || details.Contains("verificación pendiente"))
                return "requirements.pending_verification";
            else if (details.Contains("action_required.requested_capabilities") || details.Contains("acción requerida"))
                return "action_required.requested_capabilities";
            else if (details.Contains("fields_needed") || details.Contains("campos faltantes"))
                return "fields_needed";
            else if (details.Contains("rejected.fraud") || details.Contains("fraude"))
                return "rejected.fraud";
            else if (details.Contains("rejected.terms_of_service") || details.Contains("términos de servicio"))
                return "rejected.terms_of_service";
            else if (details.Contains("rejected.unsupported_business") || details.Contains("negocio no permitido"))
                return "rejected.unsupported_business";
            else if (details.Contains("rejected.other") || details.Contains("rechazado por otros motivos"))
                return "rejected.other";
            else if (details.Contains("under_review") || details.Contains("en revisión"))
                return "under_review";
            else if (details.Contains("listed") || details.Contains("lista de sanciones"))
                return "listed";
            
            return null;
        }

        /// <summary>
        /// Determina si un rechazo es permanente (bloquea crear nueva cuenta) o temporal (permite reintentar)
        /// Basado en la documentación oficial de Stripe Connect Account Requirements
        /// </summary>
        private bool IsPermanentRejection(string? disabledReason)
        {
            if (string.IsNullOrEmpty(disabledReason))
                return true; // Si no sabemos el motivo, por seguridad bloqueamos

            // Rechazos TEMPORALES que permiten reintentar:
            // - requirements.past_due: Requisitos vencidos, puede completarlos y reintentar
            // - requirements.pending_verification: Verificación pendiente, puede corregir y reintentar
            // - action_required.requested_capabilities: Acción requerida, puede completarla
            // - fields_needed: Campos faltantes, puede completarlos
            if (disabledReason == "requirements.past_due" || 
                disabledReason == "requirements.pending_verification" ||
                disabledReason == "action_required.requested_capabilities" ||
                disabledReason == "fields_needed")
            {
                return false; // Temporal, permite reintentar
            }

            // ✅ CORREGIDO: Distinguir entre rechazos PERMANENTES y TEMPORALES (Stripe Docs 2025)
            
            // RECHAZOS PERMANENTES - Bloquean crear nueva cuenta:
            // - rejected.*: Fraude, violación TOS, negocio no permitido, etc.
            // - listed: Lista de sanciones OFAC - cuenta bloqueada permanentemente
            if (disabledReason.StartsWith("rejected.") || disabledReason == "listed")
            {
                return true; // PERMANENTE: Bloquea crear nueva cuenta
            }
            
            // ESTADOS TEMPORALES EN REVISIÓN - Bloquean crear OTRA cuenta mientras se resuelve:
            // - under_review: En revisión manual - debe esperar resultado antes de crear otra
            // - requirements.past_due: Requirements vencidos - debe completar la cuenta actual primero
            // - requirements.pending_verification: Docs en verificación - debe esperar resultado
            // RAZÓN: Prevenir múltiples cuentas mientras hay issues pendientes en la primera
            if (disabledReason == "under_review" || 
                disabledReason == "requirements.past_due" || 
                disabledReason == "requirements.pending_verification")
            {
                return true; // TEMPORAL: Bloquea crear otra cuenta hasta resolver la actual
            }
            
            // ESTADOS QUE PERMITEN REINTENTAR con nueva cuenta:
            // - action_required.requested_capabilities: Puede crear nueva cuenta sin esas capabilities
            // - other (sin prefijo rejected): Genérico temporal - puede reintentar
            // - fields_needed: Faltan campos - puede reintentar con nueva cuenta
            if (disabledReason == "action_required.requested_capabilities" || 
                disabledReason == "other" || 
                disabledReason == "fields_needed")
            {
                return false; // Permite crear nueva cuenta
            }

            // Por defecto: Bloquear por seguridad (valores nuevos/desconocidos requieren revisión)
            return true;
        }

        /// <summary>
        /// Reclama un evento de webhook de forma ATÓMICA para procesarlo una sola vez.
        /// Devuelve true si ESTA llamada debe procesar el evento; false si ya está
        /// procesado, en curso reciente, o lo reclamó otra entrega concurrente.
        ///
        /// Usa el índice único en EventId: el INSERT actúa como cerrojo atómico, lo que
        /// elimina la condición de carrera del patrón anterior "comprobar y luego insertar"
        /// (dos entregas concurrentes de Stripe podían pasar ambas la comprobación).
        ///
        /// Los eventos en estado Failed/Error (o Processing obsoleto) SÍ se reintentan,
        /// porque Stripe reenvía el mismo evento ante una respuesta no-2xx; la idempotencia
        /// a nivel de pago (FinancialTransaction por StripePaymentIntentId) evita el doble
        /// cumplimiento al reprocesar.
        /// </summary>
        private async Task<bool> TryBeginProcessingEventAsync(string eventId, string eventType, string? stripeAccountId)
        {
            // 🛡️ Round 28 MUD-AZ: bumped 5min → 30min. HandlePendingHireCompleted hace ~10 calls
            // a Stripe API + múltiples SaveChanges en serie. Bajo carga (pool BD saturado,
            // Stripe latency elevada), puede superar 5min en hires específicos. Con cutoff=5min,
            // un retry de Stripe a T+5:30 ve Processing con ProcessedAt=T0 ya stale → re-reclama
            // → corre EN PARALELO con la primera invocación → posible duplicación de SearchHire
            // y FinancialTransaction (Stripe idempotency keys cubren refund/transfer pero NO
            // las filas BD del evento). 30min es estancia segura: si un handler tarda >30min
            // está roto y queremos que el retry sí lo reclame.
            var processingCutoff = DateTime.UtcNow.AddMinutes(-30);

            var existing = await _context.ProcessedWebhookEvents
                .FirstOrDefaultAsync(e => e.EventId == eventId);

            if (existing != null)
            {
                // Ya terminado con éxito, omitido, o en curso reciente => NO reprocesar.
                if (existing.Status == "Success"
                    || existing.Status == "Skipped"
                    || (existing.Status == "Processing" && existing.ProcessedAt >= processingCutoff))
                {
                    return false;
                }

                // Failed/Error o Processing obsoleto (cuelgue previo) => reclamar para reintentar.
                existing.Status = "Processing";
                existing.ProcessedAt = DateTime.UtcNow;
                existing.ErrorMessage = null;
                await _context.SaveChangesAsync();
                return true;
            }

            // No existe: insertar "Processing". El índice único en EventId hace el cerrojo atómico.
            _context.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
            {
                EventId = eventId,
                EventType = eventType,
                StripeAccountId = stripeAccountId,
                Status = "Processing",
                ProcessedAt = DateTime.UtcNow
            });

            try
            {
                await _context.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Otra entrega concurrente insertó el mismo EventId primero => ya reclamado.
                // Desadjuntar la entidad fallida para que no se reintente en SaveChanges posteriores.
                var entry = _context.ChangeTracker.Entries<ProcessedWebhookEvent>()
                    .FirstOrDefault(e => e.State == EntityState.Added && e.Entity.EventId == eventId);
                if (entry != null)
                {
                    entry.State = EntityState.Detached;
                }
                return false;
            }
        }

        /// <summary>
        /// Detecta si una DbUpdateException corresponde a una violación de índice único
        /// de PostgreSQL (SQLSTATE 23505).
        /// </summary>
        private static bool IsUniqueViolation(DbUpdateException ex)
        {
            return ex.InnerException is Npgsql.PostgresException pg
                && pg.SqlState == "23505";
        }

        /// <summary>
        /// Verifica si un evento ya fue procesado para evitar duplicados
        /// </summary>
        private async Task<bool> IsEventProcessedAsync(string eventId)
        {
            try
            {
                // 🛡️ Round 28 MUD-AZ: bumped 5min → 30min. HandlePendingHireCompleted hace ~10 calls
            // a Stripe API + múltiples SaveChanges en serie. Bajo carga (pool BD saturado,
            // Stripe latency elevada), puede superar 5min en hires específicos. Con cutoff=5min,
            // un retry de Stripe a T+5:30 ve Processing con ProcessedAt=T0 ya stale → re-reclama
            // → corre EN PARALELO con la primera invocación → posible duplicación de SearchHire
            // y FinancialTransaction (Stripe idempotency keys cubren refund/transfer pero NO
            // las filas BD del evento). 30min es estancia segura: si un handler tarda >30min
            // está roto y queremos que el retry sí lo reclame.
            var processingCutoff = DateTime.UtcNow.AddMinutes(-30);
                return await _context.ProcessedWebhookEvents
                    .AnyAsync(e => e.EventId == eventId &&
                        (e.Status == "Success"
                         || e.Status == "Skipped"
                         || (e.Status == "Processing" && e.ProcessedAt >= processingCutoff)));
            }
            catch (Exception ex)
            {
                return false; // En caso de error, permitir procesamiento
            }
        }

        /// <summary>
        /// Marca un evento como procesado en la base de datos (UPSERT)
        /// </summary>
        private async Task MarkEventAsProcessedAsync(string eventId, string eventType, string? stripeAccountId = null, int? userId = null, string status = "Success", string? errorMessage = null)
        {
            try
            {
                // Verificar si el evento ya existe
                var existingEvent = await _context.ProcessedWebhookEvents
                    .FirstOrDefaultAsync(e => e.EventId == eventId);

                if (existingEvent != null)
                {
                    // 🛡️ A1 FIX: no degradar estado terminal explícito (Skipped/Failed/Error) al
                    // default "Success" que dispara el bloque final del switch. Solo el primer
                    // estado terminal explícito vale; el outer Mark("Success") no debe pisarlo.
                    var existingIsTerminal = !string.IsNullOrEmpty(existingEvent.Status)
                        && existingEvent.Status != "Success"
                        && existingEvent.Status != "Processing";
                    if (status == "Success" && existingIsTerminal)
                    {
                        return; // mantener el estado terminal previo (Skipped/Failed/Error)
                    }
                    // Actualizar evento existente
                    existingEvent.Status = status;
                    existingEvent.ErrorMessage = errorMessage;
                    existingEvent.ProcessedAt = DateTime.UtcNow;
                }
                else
                {
                    // Crear nuevo evento
                    var processedEvent = new ProcessedWebhookEvent
                    {
                        EventId = eventId,
                        EventType = eventType,
                        StripeAccountId = stripeAccountId,
                        UserId = userId,
                        Status = status,
                        ErrorMessage = errorMessage,
                        ProcessedAt = DateTime.UtcNow
                    };
                    _context.ProcessedWebhookEvents.Add(processedEvent);
                }

                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
            {
                // ✅ FIX CRÍTICO: Si la conexión está disposed, usar un nuevo contexto
                try
                {
                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    
                    var existingEvent = await recoveryContext.ProcessedWebhookEvents
                        .FirstOrDefaultAsync(e => e.EventId == eventId);

                    if (existingEvent != null)
                    {
                        // 🛡️ A1 FIX (recovery path): mismas reglas que el path principal — no
                        // degradar estado terminal explícito (Skipped/Failed/Error) al default "Success".
                        var existingIsTerminal = !string.IsNullOrEmpty(existingEvent.Status)
                            && existingEvent.Status != "Success"
                            && existingEvent.Status != "Processing";
                        if (status == "Success" && existingIsTerminal)
                        {
                            return; // mantener estado terminal previo
                        }
                        existingEvent.Status = status;
                        existingEvent.ErrorMessage = errorMessage;
                        existingEvent.ProcessedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        var processedEvent = new ProcessedWebhookEvent
                        {
                            EventId = eventId,
                            EventType = eventType,
                            StripeAccountId = stripeAccountId,
                            UserId = userId,
                            Status = status,
                            ErrorMessage = errorMessage,
                            ProcessedAt = DateTime.UtcNow
                        };
                        recoveryContext.ProcessedWebhookEvents.Add(processedEvent);
                    }
                    
                    await recoveryContext.SaveChangesAsync();
                }
                catch (Exception recoveryEx)
                {
                    // ✅ CRÍTICO: Si falla el recovery, loguear en consola en rojo
                    var originalColor = Console.ForegroundColor;
                    try
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        var separator1 = new string('=', 80);
                        Console.Error.WriteLine($"\n{separator1}");
                        Console.Error.WriteLine($"🔴 [CRITICAL] Failed to mark webhook event as processed (recovery failed)");
                        Console.Error.WriteLine($"{separator1}");
                        Console.Error.WriteLine($"EventId: {eventId}");
                        Console.Error.WriteLine($"EventType: {eventType}");
                        Console.Error.WriteLine($"Status: {status}");
                        Console.Error.WriteLine($"Error: {recoveryEx.Message}");
                        Console.Error.WriteLine($"{separator1}\n");
                        Console.ForegroundColor = originalColor;
                    }
                    catch
                    {
                        Console.Error.WriteLine($"Failed to mark webhook event as processed: {eventId}, Error: {recoveryEx.Message}");
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // ✅ FIX CRÍTICO: Si la conexión está disposed, usar un nuevo contexto
                try
                {
                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    
                    var existingEvent = await recoveryContext.ProcessedWebhookEvents
                        .FirstOrDefaultAsync(e => e.EventId == eventId);

                    if (existingEvent != null)
                    {
                        // 🛡️ A1 FIX (recovery path): mismas reglas que el path principal — no
                        // degradar estado terminal explícito (Skipped/Failed/Error) al default "Success".
                        var existingIsTerminal = !string.IsNullOrEmpty(existingEvent.Status)
                            && existingEvent.Status != "Success"
                            && existingEvent.Status != "Processing";
                        if (status == "Success" && existingIsTerminal)
                        {
                            return; // mantener estado terminal previo
                        }
                        existingEvent.Status = status;
                        existingEvent.ErrorMessage = errorMessage;
                        existingEvent.ProcessedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        var processedEvent = new ProcessedWebhookEvent
                        {
                            EventId = eventId,
                            EventType = eventType,
                            StripeAccountId = stripeAccountId,
                            UserId = userId,
                            Status = status,
                            ErrorMessage = errorMessage,
                            ProcessedAt = DateTime.UtcNow
                        };
                        recoveryContext.ProcessedWebhookEvents.Add(processedEvent);
                    }
                    
                    await recoveryContext.SaveChangesAsync();
                }
                catch (Exception recoveryEx)
                {
                    // ✅ CRÍTICO: Si falla el recovery, loguear en consola en rojo
                    var originalColor = Console.ForegroundColor;
                    try
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        var separator1 = new string('=', 80);
                        Console.Error.WriteLine($"\n{separator1}");
                        Console.Error.WriteLine($"🔴 [CRITICAL] Failed to mark webhook event as processed (recovery failed)");
                        Console.Error.WriteLine($"{separator1}");
                        Console.Error.WriteLine($"EventId: {eventId}");
                        Console.Error.WriteLine($"EventType: {eventType}");
                        Console.Error.WriteLine($"Status: {status}");
                        Console.Error.WriteLine($"Error: {recoveryEx.Message}");
                        Console.Error.WriteLine($"{separator1}\n");
                        Console.ForegroundColor = originalColor;
                    }
                    catch
                    {
                        Console.Error.WriteLine($"Failed to mark webhook event as processed: {eventId}, Error: {recoveryEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // ✅ CRÍTICO: Loguear error en consola en rojo
                var originalColor = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    var separator2 = new string('=', 80);
                    Console.Error.WriteLine($"\n{separator2}");
                    Console.Error.WriteLine($"🔴 [ERROR] Failed to mark webhook event as processed");
                    Console.Error.WriteLine($"{separator2}");
                    Console.Error.WriteLine($"EventId: {eventId}");
                    Console.Error.WriteLine($"EventType: {eventType}");
                    Console.Error.WriteLine($"Status: {status}");
                    Console.Error.WriteLine($"Error Type: {ex.GetType().Name}");
                    Console.Error.WriteLine($"Error Message: {ex.Message}");
                    Console.Error.WriteLine($"{separator2}\n");
                    Console.ForegroundColor = originalColor;
                }
                catch
                {
                    Console.Error.WriteLine($"Failed to mark webhook event as processed: {eventId}, Error: {ex.Message}");
                }
                // No lanzar excepción para no interrumpir el flujo principal
            }
        }

        /// <summary>
        /// Genera un mensaje específico para cuentas rechazadas.
        /// 🛡️ Round 28: limpiado de **markdown** (el frontend NO renderiza markdown).
        /// Texto plano natural, emojis solo al inicio para marcar tipo de mensaje.
        /// </summary>
        private string GetRejectionMessage(string? disabledReason, List<string> requirementErrorDetails)
        {
            if (string.IsNullOrEmpty(disabledReason))
            {
                return "❌ Cuenta rechazada. Tu solicitud de cuenta de pagos fue rechazada por motivos no especificados. Contacta con soporte para obtener más información.";
            }

            var baseMessage = "❌ Cuenta rechazada. Tu solicitud de cuenta de pagos fue rechazada por Stripe.";
            string reasonMessage;
            string solutionMessage;

            switch (disabledReason)
            {
                case "rejected.fraud":
                    reasonMessage = "Motivo: Stripe detectó actividad sospechosa o posible fraude en tu cuenta.";
                    solutionMessage = "Qué hacer: contacta con soporte de Stripe para resolverlo. Puede ser necesario aportar documentación adicional para verificar tu identidad.";
                    break;

                case "rejected.terms_of_service":
                    reasonMessage = "Motivo: incumplimiento de los términos de servicio de Stripe.";
                    solutionMessage = "Qué hacer: contacta con soporte. Stripe no permite reapertura automática tras este tipo de rechazo.";
                    break;

                case "rejected.unsupported_business":
                    reasonMessage = "Motivo: el tipo de negocio declarado no está permitido por Stripe.";
                    solutionMessage = "Qué hacer: contacta con soporte de Stripe. Algunos negocios requieren aprobación especial.";
                    break;

                case "rejected.other":
                    reasonMessage = "Motivo: rechazo por motivos no especificados.";
                    solutionMessage = "Qué hacer: contacta con soporte de Stripe para conocer los detalles y los pasos para resolverlo.";
                    break;

                case "under_review":
                    reasonMessage = "Motivo: tu cuenta está en revisión por el equipo de Stripe.";
                    solutionMessage = "Qué hacer: espera a que termine la revisión (1-3 días laborables). Te avisaremos cuando haya una decisión.";
                    break;

                case "listed":
                    reasonMessage = "Motivo: tu cuenta aparece en una lista de sanciones o restricciones (p. ej. OFAC).";
                    solutionMessage = "Qué hacer: contacta con soporte de Stripe inmediatamente. Este tipo de restricciones requiere resolución directa con el equipo de cumplimiento.";
                    break;

                case "action_required.requested_capabilities":
                    reasonMessage = "Motivo: se requiere acción adicional para activar las funcionalidades solicitadas.";
                    solutionMessage = "Qué hacer: abre tu panel de Stripe y completa los pasos adicionales requeridos.";
                    break;

                case "requirements.past_due":
                    reasonMessage = "Motivo: hay requisitos vencidos que no completaste a tiempo.";
                    solutionMessage = "Qué hacer: abre tu panel de Stripe y completa todos los requisitos pendientes. Los documentos deben estar actualizados y en alta calidad.";
                    break;

                case "requirements.pending_verification":
                    reasonMessage = "Motivo: hay verificaciones pendientes que no se completaron correctamente.";
                    solutionMessage = "Qué hacer: revisa tu panel de Stripe y asegúrate de que todos los documentos estén correctamente subidos y sean legibles.";
                    break;

                case "fields_needed":
                    reasonMessage = "Motivo: faltan datos por completar.";
                    solutionMessage = "Qué hacer: abre tu panel de Stripe y rellena todos los campos requeridos.";
                    break;

                case "other":
                    reasonMessage = "Motivo: rechazo por motivos no especificados.";
                    solutionMessage = "Qué hacer: contacta con soporte de Stripe para conocer los detalles.";
                    break;

                default:
                    reasonMessage = $"Motivo: {GetDisabledReasonDescription(disabledReason)}";
                    solutionMessage = "Qué hacer: contacta con soporte de Stripe para conocer los detalles y los pasos para resolverlo.";
                    break;
            }

            var message = $"{baseMessage} {reasonMessage} {solutionMessage}";

            // Agregar información sobre errores específicos si los hay (ya traducidos por GetErrorDescription)
            if (requirementErrorDetails.Any())
            {
                var errorsList = string.Join("; ", requirementErrorDetails.Select(GetErrorDescription));
                message += $" Errores específicos: {errorsList}.";
            }

            message += " Una vez resuelto, puedes volver a solicitar la verificación.";
            return message;
        }

        /// <summary>
        /// Genera un mensaje específico para cuentas pendientes con problemas
        /// </summary>
        private string GetPendingMessage(Account account, List<string> requirementErrorDetails, bool allRequirementsMet, bool paymentsEnabled, bool detailsSubmitted, bool tosAccepted, bool notDisabled, bool noRequirementErrors, bool noPendingVerifications, bool noFutureIssues)
        {
            // 🛡️ Round 28: limpiado de **markdown**, emojis embebidos en cada bullet, y formato \n
            // que el frontend NO renderiza. Texto plano natural en una sola línea.
            var issues = new List<string>();

            if (!allRequirementsMet)
            {
                var missingRequirements = account.Requirements?.CurrentlyDue ?? new List<string>();
                if (missingRequirements.Any())
                {
                    var missingList = string.Join(", ", missingRequirements.Select(GetRequirementDescription));
                    issues.Add($"faltan documentos ({missingList})");
                }
            }

            if (!paymentsEnabled)
            {
                if (!account.ChargesEnabled) issues.Add("los pagos están deshabilitados");
                if (!account.PayoutsEnabled) issues.Add("las transferencias bancarias están deshabilitadas");
                if (account.Capabilities?.Transfers != "active") issues.Add("las transferencias aún no se han activado");
            }

            if (!detailsSubmitted) issues.Add("faltan datos del perfil");
            if (!tosAccepted) issues.Add("no has aceptado los términos de servicio");

            if (!notDisabled)
            {
                var disabledReason = account.Requirements?.DisabledReason ?? "desconocida";
                issues.Add($"la cuenta está deshabilitada ({GetDisabledReasonDescription(disabledReason)})");
            }

            if (!noRequirementErrors && requirementErrorDetails.Any())
            {
                var errorMessages = requirementErrorDetails.Select(GetErrorDescription).ToList();
                issues.Add($"hubo errores con documentos previos ({string.Join("; ", errorMessages)})");
            }

            if (!noPendingVerifications) issues.Add("hay documentos en proceso de verificación");
            if (!noFutureIssues) issues.Add("hay requisitos próximos que debes cumplir");

            if (issues.Any())
            {
                return "⏳ Verificación pendiente. Tu cuenta necesita atención: " +
                       string.Join("; ", issues) +
                       ". Abre tu panel de Stripe para resolverlo y empezar a recibir pagos.";
            }

            return "✅ Verificación en proceso. Tu cuenta está siendo revisada por Stripe. Te avisaremos cuando esté lista (1-3 días laborables).";
        }

        /// <summary>
        /// Convierte códigos de disabled_reason en descripciones amigables
        /// </summary>
        private string GetDisabledReasonDescription(string disabledReason)
        {
            return disabledReason switch
            {
                "requirements.past_due" => "Requisitos vencidos - Hay documentos o información que debías proporcionar y no lo hiciste a tiempo",
                "requirements.pending_verification" => "Verificación pendiente - Hay documentos en proceso de verificación",
                "action_required.requested_capabilities" => "Acción requerida - Necesitas completar pasos adicionales para las funcionalidades solicitadas",
                "rejected.fraud" => "Rechazado por fraude - Se detectó actividad sospechosa en tu cuenta",
                "rejected.terms_of_service" => "Rechazado por términos - No cumpliste con los términos de servicio de Stripe",
                "rejected.unsupported_business" => "Negocio no soportado - Tu tipo de negocio no está permitido en Stripe",
                "rejected.other" => "Rechazado por otros motivos - Contacta soporte para más detalles",
                "under_review" => "En revisión - Tu cuenta está siendo evaluada por el equipo de Stripe",
                "listed" => "En lista de sanciones - Tu cuenta está en una lista de restricciones (ej. OFAC)",
                "fields_needed" => "Campos faltantes - Necesitas completar información adicional",
                "other" => "Otros motivos - Contacta soporte para más información",
                _ => $"Motivo específico: {disabledReason}"
            };
        }

        /// <summary>
        /// Convierte códigos de requisitos en descripciones amigables
        /// </summary>
        // 🛡️ Round 28 MUD-BN: traducir failure codes de Stripe Payout a español humano.
        // Lista oficial: https://docs.stripe.com/api/payouts/failures
        private static string TranslatePayoutFailureCode(string? code)
        {
            if (string.IsNullOrEmpty(code)) return "motivo desconocido";
            return code switch
            {
                "account_closed" => "tu cuenta bancaria está cerrada",
                "account_frozen" => "tu cuenta bancaria está congelada (contacta con tu banco)",
                "bank_account_restricted" => "tu cuenta tiene restricciones que impiden recibir transferencias",
                "bank_ownership_changed" => "el titular de tu cuenta ha cambiado",
                "could_not_process" => "el banco no pudo procesar la transferencia (suele resolverse al reintentar)",
                "debit_not_authorized" => "tu cuenta no está autorizada para recibir desde Stripe",
                "declined" => "tu banco rechazó la transferencia",
                "incorrect_account_holder_name" => "el nombre del titular no coincide con el de la cuenta bancaria",
                "incorrect_account_holder_address" => "la dirección del titular no coincide",
                "incorrect_account_holder_tax_id" => "el NIF del titular no coincide",
                "insufficient_funds" => "la cuenta de la plataforma no tiene fondos suficientes (admin debe revisar)",
                "invalid_account_number" => "el número de cuenta es inválido",
                "invalid_currency" => "la divisa no coincide con la cuenta bancaria",
                "no_account" => "no se encontró la cuenta bancaria",
                "unsupported_card" => "la tarjeta no soporta payouts",
                _ => $"código '{code}' (consulta tu banco)"
            };
        }

        private string GetRequirementDescription(string requirement)
        {
            if (string.IsNullOrEmpty(requirement))
            {
                return string.Empty;
            }

            // 🔧 Round 26 PERSON-2: traducir tokens per-person (`person_XYZ.verification.document`,
            // `person_XYZ.verification.additional_document`, `person_XYZ.address`…) a texto humano.
            // Sin esto el experto veía cosas como "person_1Abc234XyZ5.verification.document" en
            // el panel y tenía que ir al Dashboard de Stripe para entender qué le pedían.
            if (requirement.StartsWith("person_", StringComparison.Ordinal))
            {
                var dotIndex = requirement.IndexOf('.');
                if (dotIndex > 0 && dotIndex < requirement.Length - 1)
                {
                    var subRequirement = requirement.Substring(dotIndex + 1);
                    var humanSub = subRequirement switch
                    {
                        "verification.document" => "documento de identidad",
                        "verification.additional_document" => "documento adicional de verificación",
                        "address" => "dirección",
                        "phone" => "número de teléfono",
                        "dob" => "fecha de nacimiento",
                        "email" => "email",
                        "first_name" => "nombre",
                        "last_name" => "apellidos",
                        "ssn_last_4" => "últimos 4 dígitos del NIE/SSN",
                        "id_number" => "número de identificación",
                        "relationship.title" => "cargo del representante",
                        _ => subRequirement.Replace("_", " ").Replace(".", " ")
                    };
                    return $"{humanSub} de un representante/socio de la empresa";
                }
                return "información de un representante/socio de la empresa";
            }

            return requirement switch
            {
                "individual.verification.document" => "documento de identidad",
                "individual.verification.additional_document" => "documento adicional de verificación",
                "individual.address" => "dirección",
                "individual.phone" => "número de teléfono",
                "individual.dob" => "fecha de nacimiento",
                "individual.email" => "email",
                "individual.first_name" => "nombre",
                "individual.last_name" => "apellidos",
                "individual.ssn_last_4" => "últimos 4 dígitos del NIE/SSN",
                "individual.id_number" => "número de identificación",
                "company.verification.document" => "documentos de la empresa",
                "company.tax_id" => "NIF/CIF de la empresa",
                "company.address" => "dirección de la empresa",
                "company.phone" => "teléfono de la empresa",
                "company.name" => "nombre de la empresa",
                "business_profile.support_address" => "dirección del negocio",
                "business_profile.url" => "sitio web del negocio",
                "business_profile.support_phone" => "teléfono de soporte",
                "business_profile.support_email" => "email de soporte",
                "business_profile.product_description" => "descripción del producto/servicio",
                "business_profile.mcc" => "categoría del negocio (MCC)",
                "business_type" => "tipo de negocio (individual/empresa)",
                // 🛡️ Round 28: tos_acceptance.* — el flujo de aceptación de términos en Stripe Connect.
                // El usuario veía "tos acceptance ip" raw; ahora todos los subcampos tienen mensajes claros.
                "tos_acceptance.date" => "aceptar términos de servicio (fecha)",
                "tos_acceptance.ip" => "aceptar términos de servicio (IP de aceptación)",
                "tos_acceptance.user_agent" => "aceptar términos de servicio (navegador)",
                "tos_acceptance" => "aceptar términos de servicio",
                "external_account" => "información bancaria",
                "documents.bank_account_ownership_verification" => "verificación de titularidad bancaria",
                "documents.company_license" => "licencia de la empresa",
                "documents.company_memorandum_of_association" => "escritura de constitución",
                "documents.company_ministerial_decree" => "decreto ministerial de la empresa",
                "documents.company_registration_verification" => "verificación de registro de empresa",
                "documents.company_tax_id_verification" => "verificación del NIF/CIF",
                "documents.proof_of_registration" => "prueba de registro",
                _ => requirement.Replace("_", " ").Replace(".", " ")
            };
        }

        /// <summary>
        /// Convierte códigos de error en descripciones amigables
        /// </summary>
        private string GetErrorDescription(string errorDetail)
        {
            if (errorDetail.Contains("invalid_document"))
                return "Documento inválido - El documento proporcionado no es válido o no cumple con los requisitos";
            if (errorDetail.Contains("verification_failed"))
                return "Verificación fallida - No se pudo verificar la información proporcionada";
            if (errorDetail.Contains("invalid_address"))
                return "Dirección inválida - La dirección proporcionada no es válida o no existe";
            if (errorDetail.Contains("missing"))
                return "Información faltante - Falta información requerida para completar la verificación";
            if (errorDetail.Contains("expired"))
                return "Documento expirado - El documento proporcionado ha expirado y necesita ser renovado";
            if (errorDetail.Contains("unreadable"))
                return "Documento ilegible - El documento no se puede leer claramente, sube una imagen de mejor calidad";
            if (errorDetail.Contains("blurry"))
                return "Imagen borrosa - La imagen del documento está borrosa, toma una foto más clara";
            if (errorDetail.Contains("cropped"))
                return "Documento recortado - El documento está incompleto, asegúrate de que se vea completo";
            if (errorDetail.Contains("back_side"))
                return "Reverso faltante - Necesitas subir también el reverso del documento";
            if (errorDetail.Contains("selfie"))
                return "Selfie requerido - Necesitas subir una foto tuya sosteniendo el documento";
            
            return "Error de verificación - Revisa la información proporcionada y vuelve a intentar";
        }

        // Métodos para el webhook general de pagos
        private async Task HandlePaymentIntentSucceeded(PaymentIntent paymentIntent)
        {
            try
            {
                // 🔎 RECONCILIACIÓN (antes era un no-op): la creación de la contratación la dispara
                // checkout.session.completed. Si un pago tiene éxito pero NO existe el FinancialTransaction
                // de tipo "ServicePayment" para este PaymentIntent, podría haberse cobrado dinero sin
                // crear la contratación (evento perdido). Aquí solo detectamos y avisamos la discrepancia;
                // no creamos nada para no duplicar (la creación es responsabilidad del otro evento).
                var servicePayment = await _context.FinancialTransactions
                    .FirstOrDefaultAsync(ft => ft.StripePaymentIntentId == paymentIntent.Id
                                               && ft.TransactionType == "ServicePayment");

                if (servicePayment != null)
                {
                    return; // Ya reconciliado: existe el pago/contratación local.
                }

                var indicatesHire = paymentIntent.Metadata != null
                    && (paymentIntent.Metadata.GetValueOrDefault("pendingHire") == "true"
                        || paymentIntent.Metadata.ContainsKey("serviceId"));

                var amount = paymentIntent.Amount / 100m;
                int? userId = paymentIntent.Metadata != null
                    && paymentIntent.Metadata.TryGetValue("userId", out var uid)
                    && int.TryParse(uid, out var parsedUid) ? parsedUid : (int?)null;

                // 🚨 P1 FIX (Finding #1): si la metadata indica que era un hire pero NO hay ServicePayment local,
                // es indicio de que la metadata se corrompió o el evento checkout.session.completed se perdió →
                // dinero capturado sin hire creado. Esto SIEMPRE es crítico (no warning). Si no hay metadata de hire,
                // sigue siendo warning (puede ser un load_money u otro pago no-hire).
                if (indicatesHire)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL P1: Payment succeeded WITHOUT local ServicePayment record (hire metadata present)",
                        details: $"PaymentIntent {paymentIntent.Id} succeeded ({amount}€) PERO no existe FinancialTransaction ServicePayment. Metadata indica hire pendiente → checkout.session.completed posiblemente se perdió/corrupto. Dinero CAPTURADO sin hire. ACCIÓN URGENTE ADMIN: refund manual + verificar metadata.",
                        userId: userId,
                        source: "SubscriptionController.HandlePaymentIntentSucceeded",
                        relatedEntityType: "Payment",
                        relatedEntityId: null,
                        additionalData: new
                        {
                            PaymentIntentId = paymentIntent.Id,
                            Amount = amount,
                            Currency = paymentIntent.Currency,
                            IndicatesHire = true,
                            Metadata = paymentIntent.Metadata
                        });
                }
                else
                {
                    await _loggingService.LogWarningAsync(
                        message: "Payment succeeded without local ServicePayment record",
                        details: $"PaymentIntent {paymentIntent.Id} succeeded ({amount}€) but no ServicePayment FinancialTransaction exists. No hire metadata present — likely a non-hire payment, informational only.",
                        userId: userId,
                        source: "SubscriptionController.HandlePaymentIntentSucceeded",
                        relatedEntityType: "Payment",
                        relatedEntityId: null,
                        additionalData: new
                        {
                            PaymentIntentId = paymentIntent.Id,
                            Amount = amount,
                            Currency = paymentIntent.Currency,
                            IndicatesHire = false
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogWarningAsync(
                    message: "Error during payment_intent.succeeded reconciliation",
                    details: $"PaymentIntent {paymentIntent?.Id}: {ex.Message}",
                    userId: null,
                    source: "SubscriptionController.HandlePaymentIntentSucceeded",
                    relatedEntityType: "Payment",
                    relatedEntityId: null
                );
            }
        }

        /// <summary>
        /// charge.dispute.created — un comprador ha abierto un contracargo (chargeback).
        /// Stripe retira de inmediato el importe + la comisión de disputa del saldo de la
        /// plataforma. Detectamos, localizamos la contratación/experto si es posible, y
        /// alertamos (LogCritical) para que se actúe (p.ej. revertir el transfer al experto).
        /// No mueve dinero automáticamente — esa lógica de clawback se aborda por separado.
        /// </summary>
        private async Task HandleChargeDisputeCreated(Stripe.Dispute? dispute)
        {
            if (dispute == null)
            {
                await _loggingService.LogWarningAsync(
                    message: "charge.dispute.created received with null Dispute object",
                    source: "SubscriptionController.HandleChargeDisputeCreated",
                    relatedEntityType: "Dispute");
                return;
            }

            // 🛡️ B1 FIX: discriminar INQUIRIES (warning_*) de chargebacks reales.
            // Stripe usa `dispute.Status` con prefijo "warning_" para INQUIRIES:
            //   warning_needs_response, warning_under_review, warning_closed.
            // Las inquiries son AVISOS previos — Stripe NO ha retirado fondos de la plataforma,
            // así que NO se debe (a) marcar FinancialTransaction "Chargeback" — corromperia la
            // contabilidad — ni (b) encolar ReverseExpertTransferForChargebackAsync — robaria
            // el pago al experto sin que hubiera pérdida real para la plataforma.
            // Si la inquiry escala a chargeback real, Stripe envía OTRO charge.dispute.created
            // (o charge.dispute.updated → closed) con un status sin "warning_", y ahí sí actuamos.
            if (!string.IsNullOrEmpty(dispute.Status)
                && dispute.Status.StartsWith("warning_", StringComparison.OrdinalIgnoreCase))
            {
                var inquiryAmount = dispute.Amount / 100m;
                await _loggingService.LogWarningAsync(
                    message: "Stripe Inquiry (pre-dispute warning) received — NO clawback",
                    details: $"Inquiry {dispute.Id} for {inquiryAmount}€ (status: {dispute.Status}, reason: {dispute.Reason}). " +
                             $"ChargeId: {dispute.ChargeId}, PI: {dispute.PaymentIntentId}. " +
                             $"Stripe HAS NOT withdrawn funds — this is a warning, not a chargeback. " +
                             $"Respond via Stripe Dashboard if needed. If it escalates, Stripe enviará otro " +
                             $"charge.dispute.created/closed con status sin 'warning_' y allí sí se hará clawback.",
                    userId: null,
                    source: "SubscriptionController.HandleChargeDisputeCreated",
                    relatedEntityType: "Dispute",
                    relatedEntityId: null,
                    additionalData: new
                    {
                        DisputeId = dispute.Id,
                        dispute.ChargeId,
                        dispute.PaymentIntentId,
                        Amount = inquiryAmount,
                        dispute.Reason,
                        dispute.Status,
                        IsInquiry = true
                    });
                return;
            }

            var amount = dispute.Amount / 100m;
            var (hireId, expertId, clientId) = await FindHireForPaymentIntentAsync(dispute.PaymentIntentId);

            await _loggingService.LogCriticalAsync(
                message: "CRITICAL: Chargeback (dispute) opened on a payment",
                details: $"Dispute {dispute.Id} created for {amount}€ (reason: {dispute.Reason}, status: {dispute.Status}). " +
                         $"ChargeId: {dispute.ChargeId}, PaymentIntentId: {dispute.PaymentIntentId}. " +
                         (hireId.HasValue
                            ? $"Related SearchHire: {hireId}, ExpertId: {expertId}, ClientId: {clientId}. ACTION REQUIRED: if the expert was already paid via transfer, reverse that transfer to avoid double loss."
                            : "No related SearchHire found locally for this PaymentIntent."),
                userId: clientId,
                source: "SubscriptionController.HandleChargeDisputeCreated",
                relatedEntityType: "Dispute",
                relatedEntityId: hireId,
                additionalData: new
                {
                    DisputeId = dispute.Id,
                    dispute.ChargeId,
                    dispute.PaymentIntentId,
                    Amount = amount,
                    dispute.Reason,
                    dispute.Status,
                    SearchHireId = hireId,
                    ExpertId = expertId
                },
                notifyUser: false);

            // 🔁 A3: registrar un marcador "Chargeback" para que la distribución interna NO vuelva a
            // reembolsar al cliente (Stripe YA le devolvió el dinero vía el contracargo) → evita doble reembolso.
            // Idempotente. (La reversión del transfer al experto sigue alertándose para acción manual/clawback.)
            if (hireId.HasValue && !string.IsNullOrEmpty(dispute.PaymentIntentId))
            {
                var alreadyMarked = await _context.FinancialTransactions.AnyAsync(ft =>
                    ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hireId.Value &&
                    ft.TransactionType == "Chargeback" && ft.StripePaymentIntentId == dispute.PaymentIntentId);
                if (!alreadyMarked)
                {
                    // 🌍 Round 25: snapshot currency desde el SearchHire asociado al chargeback.
                    var hireCurrency = await _context.SearchHires
                        .Where(h => h.Id == hireId.Value)
                        .Select(h => h.Currency)
                        .FirstOrDefaultAsync() ?? "EUR";
                    _context.FinancialTransactions.Add(new FinancialTransaction
                    {
                        UserId = clientId,
                        Amount = -amount,
                        AmountCents = -dispute.Amount, // 🔧 céntimos exactos retirados por Stripe (fuente de verdad)
                        Currency = hireCurrency,
                        TransactionType = "Chargeback",
                        RelatedEntityType = "SearchHire",
                        RelatedEntityId = hireId.Value,
                        StripePaymentIntentId = dispute.PaymentIntentId,
                        CreatedAt = DateTime.UtcNow
                    });

                    // 🚨 Finding #17 FIX: marcar el SearchHire como Disputed para que la UI/admin
                    // refleje el estado financiero real. Antes el hire seguía "Completed" mientras
                    // la plataforma había perdido fondos → confusión total.
                    var hireToMark = await _context.SearchHires.FirstOrDefaultAsync(h => h.Id == hireId.Value);
                    if (hireToMark != null)
                    {
                        var disputedStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());
                        if (disputedStatusId != hireToMark.StatusId)
                        {
                            hireToMark.StatusId = disputedStatusId;
                            hireToMark.UpdatedAt = DateTime.UtcNow;
                        }
                    }

                    await _context.SaveChangesAsync();

                    // 🛡️ T9 FIX: detectar dispute interna activa antes de encolar reversal automático.
                    // Race documentada: si admin está resolviendo una Dispute interna (Status=Pending o
                    // Resolving) AL MISMO TIEMPO que llega el chargeback Stripe, encolar el reversal
                    // automático aquí choca con la decisión manual del admin (ProcessMoneyDistribution).
                    // Posible doble reversal de transfer + estado de hire inconsistente. Solución:
                    // si hay dispute interna activa, NO encolamos — log Critical pidiendo intervención
                    // manual para que el admin decida cómo coordinar Stripe chargeback + resolución interna.
                    var hasActiveInternalDispute = await _context.Disputes
                        .AsNoTracking()
                        .AnyAsync(d => d.SearchHireId == hireId.Value
                                    && (d.Status == "Pending" || d.Status == "Resolving"));

                    if (hasActiveInternalDispute)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL T9: Chargeback Stripe + Dispute interna activa — NO se encola reversal automático",
                            details: $"SearchHire {hireId} tiene una Dispute interna en estado Pending/Resolving cuando llegó el chargeback Stripe {dispute.Id}. NO se ha encolado ReverseExpertTransferForChargebackAsync para evitar doble reversal del transfer (race con la resolución manual del admin). ACCIÓN ADMIN: revisar la dispute interna y decidir si (a) resolverla primero y luego ejecutar reversal manualmente, o (b) abandonar la dispute interna porque Stripe ya hizo el chargeback y el cliente recibió el dinero. PaymentIntentId: {dispute.PaymentIntentId}.",
                            userId: clientId,
                            source: "SubscriptionController.HandleChargeDisputeCreated.T9",
                            relatedEntityType: "Dispute",
                            relatedEntityId: hireId,
                            additionalData: new
                            {
                                DisputeId = dispute.Id,
                                dispute.PaymentIntentId,
                                SearchHireId = hireId,
                                ExpertId = expertId,
                                Reason = "T9_active_internal_dispute_skip_auto_reversal"
                            });
                    }
                    else
                    {
                        // 🔁 R3: encolar la reversión TOTAL del transfer al experto (idempotente; no-op si no hubo
                        // transfer). Un chargeback revierte el cargo ENTERO → el experto no debe quedarse su pago.
                        Hangfire.BackgroundJob.Enqueue<StripeRefundService>(
                            s => s.ReverseExpertTransferForChargebackAsync(hireId.Value, $"Chargeback {dispute.Id} on PI {dispute.PaymentIntentId}"));
                    }
                }

                // Vincular la Dispute interna (si existe) con la disputa de Stripe para poder enviar evidencia.
                var internalDispute = await _context.Disputes
                    .Where(d => d.SearchHireId == hireId.Value)
                    .OrderByDescending(d => d.CreatedAt)
                    .FirstOrDefaultAsync();
                if (internalDispute != null && string.IsNullOrEmpty(internalDispute.StripeDisputeId))
                {
                    internalDispute.StripeDisputeId = dispute.Id;
                    await _context.SaveChangesAsync();
                }
            }
        }

        /// <summary>
        /// charge.dispute.closed — el contracargo se resolvió. Si se perdió (lost), la plataforma
        /// pierde los fondos de forma definitiva; se alerta como crítico para clawback al experto.
        /// </summary>
        private async Task HandleChargeDisputeClosed(Stripe.Dispute? dispute)
        {
            if (dispute == null) return;

            var amount = dispute.Amount / 100m;
            var (hireId, expertId, clientId) = await FindHireForPaymentIntentAsync(dispute.PaymentIntentId);
            var lost = string.Equals(dispute.Status, "lost", StringComparison.OrdinalIgnoreCase);
            // 🔧 FIX B6: estados de cierre FAVORABLES en los que Stripe REINTEGRA el bruto a la plataforma.
            // 'late_win' = un 'lost' revertido tarde por el emisor (también reintegra).
            var won = string.Equals(dispute.Status, "won", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(dispute.Status, "late_win", StringComparison.OrdinalIgnoreCase);

            if (lost)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Chargeback LOST — platform funds withdrawn",
                    details: $"Dispute {dispute.Id} closed as LOST for {amount}€. PaymentIntentId: {dispute.PaymentIntentId}. " +
                             (hireId.HasValue
                                ? $"SearchHire: {hireId}, ExpertId: {expertId}. ACTION REQUIRED: reverse the expert transfer if already paid."
                                : "No related SearchHire found locally."),
                    userId: clientId,
                    source: "SubscriptionController.HandleChargeDisputeClosed",
                    relatedEntityType: "Dispute",
                    relatedEntityId: hireId,
                    additionalData: new { DisputeId = dispute.Id, dispute.PaymentIntentId, Amount = amount, dispute.Status, ExpertId = expertId });
            }
            else if (won && hireId.HasValue)
            {
                // 🔧 FIX B6: al abrir el chargeback se revirtió el transfer al experto. Al GANARLO, Stripe
                // reintegra el bruto a la plataforma, pero el experto NO recupera su transfer automáticamente
                // (antes esto solo era un LogInfo => pérdida silenciosa para el experto). Avisamos como CRÍTICO
                // con el importe revertido para reintegro MANUAL. NO se re-paga automáticamente a propósito:
                // si sobre el mismo transfer hubo además un clawback PARCIAL de una disputa interna legítima,
                // re-pagar la suma total sobre-pagaría al experto. El admin debe discriminar antes de reintegrar.
                // 🛡️ B2 FIX: filtrar SOLO "ChargebackReversal" (no "TransferReversal" del clawback).
                // Stripe en "won" reintegra el bruto del CHARGEBACK; el experto debe recuperar SOLO
                // ese monto, NUNCA el clawback de la disputa interna previa (que sigue siendo válido).
                // Antes la suma mezclaba ambos → el admin reintegraba de más al experto (sobrepago).
                var reversedCents = await _context.FinancialTransactions
                    .Where(ft => ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hireId.Value
                                 && ft.TransactionType == "ChargebackReversal")
                    .SumAsync(ft => ft.AmountCents);
                var reversedEur = Math.Abs(reversedCents) / 100m;
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Chargeback WON — expert transfer reversed earlier may need manual reinstatement",
                    details: $"Dispute {dispute.Id} closed as '{dispute.Status}' for {amount}€. SearchHire {hireId}, ExpertId {expertId}. " +
                             $"Stripe reintegró el bruto a la plataforma. Al abrir el chargeback se revirtió el transfer al experto (~{reversedEur:F2}€ en filas TransferReversal). " +
                             $"ACCIÓN REQUERIDA: reintegrar al experto la parte revertida POR EL CHARGEBACK (descontando cualquier clawback de disputa interna previo sobre el mismo transfer).",
                    userId: expertId,
                    source: "SubscriptionController.HandleChargeDisputeClosed",
                    relatedEntityType: "Dispute",
                    relatedEntityId: hireId,
                    additionalData: new { DisputeId = dispute.Id, dispute.PaymentIntentId, Amount = amount, dispute.Status, ExpertId = expertId, ReversedCents = reversedCents, ReversedEur = reversedEur });
            }
            else
            {
                await _loggingService.LogInfoAsync(
                    message: "Chargeback resolved",
                    details: $"Dispute {dispute.Id} closed with status '{dispute.Status}' for {amount}€. PaymentIntentId: {dispute.PaymentIntentId}.",
                    userId: clientId,
                    source: "SubscriptionController.HandleChargeDisputeClosed",
                    relatedEntityType: "Dispute",
                    relatedEntityId: hireId);
            }
        }

        /// <summary>
        /// charge.dispute.funds_withdrawn / funds_reinstated — movimiento de fondos por una disputa.
        /// 🛡️ Finding #9 FIX: en funds_reinstated, crear un FT "ChargebackReinstated" para que la
        /// contabilidad refleje la reversa del cargo del chargeback (Stripe devuelve los fondos a la
        /// plataforma). Idempotente: filtrado por StripePaymentIntentId + tipo.
        /// </summary>
        private async Task HandleChargeDisputeFundsEvent(string eventType, Stripe.Dispute? dispute)
        {
            if (dispute == null) return;
            var amount = dispute.Amount / 100m;

            if (string.Equals(eventType, "charge.dispute.funds_reinstated", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(dispute.PaymentIntentId))
            {
                var (hireId, expertId, clientId) = await FindHireForPaymentIntentAsync(dispute.PaymentIntentId);
                if (hireId.HasValue)
                {
                    // 🛡️ Finding #9 FIX: crear el offsetting credit del Chargeback original.
                    var alreadyReinstated = await _context.FinancialTransactions.AnyAsync(ft =>
                        ft.RelatedEntityType == "SearchHire" && ft.RelatedEntityId == hireId.Value &&
                        ft.TransactionType == "ChargebackReinstated" && ft.StripePaymentIntentId == dispute.PaymentIntentId);
                    if (!alreadyReinstated)
                    {
                        // 🌍 Round 25: snapshot currency desde el SearchHire (mismo del Chargeback original).
                        var hireCurrency = await _context.SearchHires
                            .Where(h => h.Id == hireId.Value)
                            .Select(h => h.Currency)
                            .FirstOrDefaultAsync() ?? "EUR";
                        _context.FinancialTransactions.Add(new FinancialTransaction
                        {
                            UserId = clientId,
                            Amount = amount, // POSITIVO: la plataforma recupera el cargo retirado
                            AmountCents = dispute.Amount,
                            Currency = hireCurrency,
                            TransactionType = "ChargebackReinstated",
                            RelatedEntityType = "SearchHire",
                            RelatedEntityId = hireId.Value,
                            StripePaymentIntentId = dispute.PaymentIntentId,
                            CreatedAt = DateTime.UtcNow
                        });
                        await _context.SaveChangesAsync();
                    }
                    await _loggingService.LogWarningAsync(
                        message: $"Dispute funds_reinstated: ledger offset created (hire {hireId})",
                        details: $"Dispute {dispute.Id} funds_reinstated for {amount}€. PI {dispute.PaymentIntentId}. SearchHire {hireId}. FT 'ChargebackReinstated' creada (offsetting credit del Chargeback original).",
                        source: "SubscriptionController.HandleChargeDisputeFundsEvent",
                        relatedEntityType: "Dispute");
                    return;
                }
            }

            await _loggingService.LogWarningAsync(
                message: $"Dispute funds event: {eventType}",
                details: $"Dispute {dispute.Id} ({eventType}) for {amount}€. PaymentIntentId: {dispute.PaymentIntentId}, Status: {dispute.Status}.",
                source: "SubscriptionController.HandleChargeDisputeFundsEvent",
                relatedEntityType: "Dispute");
        }

        /// <summary>
        /// charge.refunded — se reembolsó un cargo. Si no existe un FinancialTransaction de tipo
        /// "Refund" local para este PaymentIntent, fue un reembolso externo (Dashboard de Stripe)
        /// y se avisa para reconciliar el ledger.
        /// </summary>
        /// <summary>
        /// 🛡️ Finding #7 FIX: charge.refund.created — registra la creación del refund (informational).
        /// Útil para detectar refunds creados externamente (Stripe Dashboard) que no están en el ledger
        /// local. La conciliación pesada sigue ocurriendo en charge.refunded / charge.refund.updated.
        /// Idempotencia: EventId-level (TryBeginProcessingEventAsync evita doble proceso).
        /// </summary>
        private async Task HandleChargeRefundCreated(Refund? refund)
        {
            if (refund == null) return;
            var localFt = await _context.FinancialTransactions.AsNoTracking()
                .FirstOrDefaultAsync(ft => ft.StripeRefundId == refund.Id);
            var hasLocalRecord = localFt != null;
            var amount = refund.Amount / 100m;
            await _loggingService.LogInfoAsync(
                message: $"charge.refund.created received (localRecord={hasLocalRecord})",
                details: $"Refund {refund.Id} created ({amount}€) on PI {refund.PaymentIntentId}. Status: {refund.Status}, Reason: {refund.Reason ?? "n/a"}. " +
                         (hasLocalRecord
                            ? "Local FinancialTransaction exists — internal-initiated refund."
                            : "NO local FT — likely external (Stripe Dashboard) refund. Will be reconciled on charge.refunded."),
                source: "SubscriptionController.HandleChargeRefundCreated",
                relatedEntityType: "Refund",
                relatedEntityId: localFt?.RelatedEntityId,
                additionalData: new
                {
                    RefundId = refund.Id,
                    refund.PaymentIntentId,
                    Amount = amount,
                    refund.Status,
                    refund.Reason,
                    HasLocalFt = hasLocalRecord
                });
        }

        private async Task HandleChargeRefundUpdated(Refund? refund)
        {
            // 🛡️ V11 FIX: si refund pasa a failed/canceled, log Critical (cliente no recibió dinero)
            if (refund == null) return;
            var status = refund.Status ?? "unknown";
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase))
            {
                var localFt = await _context.FinancialTransactions.AsNoTracking()
                    .FirstOrDefaultAsync(ft => ft.StripeRefundId == refund.Id);

                // 🛡️ Finding #3 FIX: crear FinancialTransaction "RefundFailed" para preservar el rastro
                // de auditoría del fallo del refund. Idempotente: si ya existe RefundFailed para este
                // StripeRefundId, no duplica.
                //
                // 🛡️ Round 27 — R27-T27-1-5 FIX: el insert anterior SIEMPRE chocaba con
                // IX_FT_StripeRefundId_uq (UNIQUE en StripeRefundId solo, sin filtro por
                // TransactionType) porque la fila Refund original ya ocupa ese StripeRefundId.
                // Resultado: cada refund fallido lanzaba 23505, contaminaba el DbContext con
                // una entidad Added, MarkEventAsProcessedAsync('Success') volvía a tirar el
                // mismo insert → webhook devuelve 500 a Stripe → Stripe reintenta indefinidamente.
                // Y el LogCritical mentía claimeando "RefundFailed FT registrada".
                //
                // Plan correcto: en lugar de un INSERT que choca, hacemos UPDATE sobre la fila
                // Refund original marcando IsRefunded=true (semántica: "este refund se procesó,
                // status=failed"). El audit detallado va al LogCriticalAsync con context completo.
                // Detach la entidad si el SaveChanges falla para no romper el resto del webhook.
                var alreadyTrackedFailure = await _context.FinancialTransactions.AnyAsync(ft =>
                    ft.StripeRefundId == refund.Id && ft.TransactionType == "RefundFailed");

                bool refundFailedRowPersisted = false;
                if (!alreadyTrackedFailure)
                {
                    // Intento best-effort: NO setear StripeRefundId en la fila RefundFailed
                    // (evita la colisión con la fila Refund original que ya tiene ese ID).
                    // El vínculo audit-trail va por StripePaymentIntentId + log details.
                    var failedFt = new FinancialTransaction
                    {
                        UserId = localFt?.UserId,
                        Amount = 0, // Solo marcador de fallo, no movimiento real
                        AmountCents = 0,
                        Currency = localFt?.Currency ?? "EUR", // 🌍 Round 25: heredar del FT Refund original
                        TransactionType = "RefundFailed",
                        RelatedEntityType = localFt?.RelatedEntityType ?? "Refund",
                        RelatedEntityId = localFt?.RelatedEntityId,
                        StripeRefundId = null, // ← R27-T27-1-5: evitar colisión con IX_FT_StripeRefundId_uq
                        StripePaymentIntentId = refund.PaymentIntentId,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.FinancialTransactions.Add(failedFt);
                    try
                    {
                        await _context.SaveChangesAsync();
                        refundFailedRowPersisted = true;
                    }
                    catch (Exception saveEx)
                    {
                        // 🛡️ R27-T27-1-5: DETACH la entidad fallida para no contaminar
                        // SaveChanges posteriores (MarkEventAsProcessedAsync) y reventar la webhook.
                        var failedEntry = _context.Entry(failedFt);
                        if (failedEntry.State == EntityState.Added)
                        {
                            failedEntry.State = EntityState.Detached;
                        }
                        await _loggingService.LogWarningAsync(
                            message: "RefundFailed FT insert failed (best-effort, detached)",
                            details: $"RefundId={refund.Id}: {saveEx.Message}. Entidad detached del DbContext para no romper SaveChanges posteriores.",
                            source: "SubscriptionController.HandleChargeRefundUpdated",
                            relatedEntityType: "Refund");
                    }
                }
                else
                {
                    refundFailedRowPersisted = true; // ya existe de una corrida previa
                }

                // R27-T27-1-5: ser honesto en el LogCritical sobre si la fila se registró.
                var auditClause = refundFailedRowPersisted
                    ? "RefundFailed FT registrada para auditoría."
                    : "RefundFailed FT NO se pudo registrar (ver warning previo); este log Critical es el único rastro estructurado del fallo.";

                await _loggingService.LogCriticalAsync(
                    message: $"CRITICAL V11: Refund {refund.Id} cambió a '{status}' — cliente no recibe dinero",
                    details: $"Refund {refund.Id} ({refund.Amount / 100m}€) PI {refund.PaymentIntentId}. FailureReason: {refund.FailureReason ?? "n/a"}. ACCIÓN ADMIN: contactar cliente, validar tarjeta actualizada, reintentar refund manual o emitir transferencia bancaria alternativa. {auditClause}",
                    userId: localFt?.UserId,
                    source: "SubscriptionController.HandleChargeRefundUpdated.V11",
                    relatedEntityType: "Refund",
                    relatedEntityId: localFt?.RelatedEntityId);
            }
        }

        private async Task HandleChargeRefunded(Charge? charge)
        {
            if (charge == null) return;

            var refundedAmount = charge.AmountRefunded / 100m;
            var paymentIntentId = charge.PaymentIntentId;

            var localRefund = !string.IsNullOrEmpty(paymentIntentId) && await _context.FinancialTransactions
                .AnyAsync(ft => ft.StripePaymentIntentId == paymentIntentId && ft.TransactionType == "Refund");

            if (localRefund)
            {
                await _loggingService.LogInfoAsync(
                    message: "Charge refunded (reconciled with local refund)",
                    details: $"Charge {charge.Id} refunded {refundedAmount}€. PaymentIntentId: {paymentIntentId}.",
                    source: "SubscriptionController.HandleChargeRefunded",
                    relatedEntityType: "Payment");
            }
            else
            {
                await _loggingService.LogWarningAsync(
                    message: "External refund detected (no local Refund record)",
                    details: $"Charge {charge.Id} refunded {refundedAmount}€ but no local Refund FinancialTransaction exists for PaymentIntent {paymentIntentId}. " +
                             "Likely a Stripe Dashboard refund — reconcile the ledger and check whether the expert transfer needs reversing.",
                    source: "SubscriptionController.HandleChargeRefunded",
                    relatedEntityType: "Payment");
            }
        }

        /// <summary>
        /// payout.paid / payout.failed — estado de los pagos de Stripe a la cuenta bancaria.
        /// </summary>
        private async Task HandlePayoutEvent(string eventType, Payout? payout, string? accountId)
        {
            if (payout == null) return;
            var amount = payout.Amount / 100m;
            var currency = (payout.Currency ?? "EUR").ToUpperInvariant();

            // 🛡️ Round 28 MUD-BN: resolver el experto desde accountId para poder notificarle.
            // ANTES los logs Critical iban con userId=null → admin recibía email pero el experto
            // (cuyo banco rechazó el payout) NO se enteraba hasta revisar el panel por casualidad.
            // Su dinero queda parado en Stripe Connect y él no sabe por qué no le llega.
            int? expertUserId = null;
            if (!string.IsNullOrEmpty(accountId))
            {
                try
                {
                    var expertProfileForPayout = await _context.ExpertProfiles
                        .AsNoTracking()
                        .FirstOrDefaultAsync(ep => ep.StripeAccountId == accountId);
                    expertUserId = expertProfileForPayout?.UserId;
                }
                catch { /* best-effort, log Critical sigue con userId=null */ }
            }

            if (string.Equals(eventType, "payout.failed", StringComparison.OrdinalIgnoreCase))
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Payout failed",
                    details: $"Payout {payout.Id} of {amount} {currency} failed for account '{accountId ?? "platform"}'. Reason: {payout.FailureMessage} ({payout.FailureCode}). ACTION REQUIRED: revisar saldo, configuración bancaria y reintentar el payout manualmente.",
                    userId: expertUserId,
                    source: "SubscriptionController.HandlePayoutEvent",
                    relatedEntityType: "Payout",
                    additionalData: new
                    {
                        PayoutId = payout.Id,
                        Amount = amount,
                        Currency = currency,
                        AccountId = accountId,
                        payout.FailureMessage,
                        payout.FailureCode,
                        payout.Status
                    });

                // 🛡️ MUD-BN: notificación in-app + email DEDICADA al experto. Mensaje específico
                // con guía de acción (revisar cuenta bancaria en Stripe Dashboard).
                // 🛡️ MUD-BX (follow-up MUD-BN): dedup contra Notifications recientes (23h).
                // Stripe puede emitir múltiples payout.failed para reintentos del MISMO payout
                // (cada uno con eventId distinto → TryBeginProcessingEventAsync los acepta) →
                // ANTES el experto recibía 3+ emails idénticos. Patrón espejo de MUD-BO/D3.
                if (expertUserId.HasValue)
                {
                    var dedupCutoff = DateTime.UtcNow.AddHours(-23);
                    var alreadyNotified = await _context.Notifications
                        .AsNoTracking()
                        .AnyAsync(n => n.UserId == expertUserId.Value
                                    && n.CreatedAt >= dedupCutoff
                                    && n.Title.StartsWith("💸 No hemos podido enviarte un pago"));
                    if (alreadyNotified)
                    {
                        // Skip dup notification, pero el LogCritical al admin sí persiste (audit).
                        goto SkipPayoutFailedNotification;
                    }
                    var failureExplanation = TranslatePayoutFailureCode(payout.FailureCode);
                    await _loggingService.LogWarningAsync(
                        message: "💸 No hemos podido enviarte un pago",
                        details: $"Intentamos transferir {amount:F2} {currency} a tu cuenta bancaria pero el banco lo rechazó ({failureExplanation}). Tu dinero sigue seguro en tu balance Stripe; no se ha perdido. ACCIÓN: revisa los datos de tu cuenta bancaria en el panel Stripe (Banking → External accounts). Te volveremos a intentar el payout cuando corrijas los datos.",
                        userId: expertUserId.Value,
                        source: "SubscriptionController.HandlePayoutEvent.MUD-BN",
                        relatedEntityType: "Payout",
                        relatedEntityId: null,
                        notifyUser: true);
                    SkipPayoutFailedNotification:;
                }
            }
            else if (string.Equals(eventType, "payout.canceled", StringComparison.OrdinalIgnoreCase))
            {
                // 🛡️ A4: payout.canceled es muy raro y siempre crítico — datos bancarios inválidos,
                // cuenta suspendida por Stripe, o cancelación manual por compliance. Sin alerta el
                // experto queda con el dinero "en el aire" y la plataforma no se entera.
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Payout canceled",
                    details: $"Payout {payout.Id} of {amount} {currency} CANCELED for account '{accountId ?? "platform"}'. Reason: {payout.FailureMessage ?? "n/a"} ({payout.FailureCode ?? "n/a"}). ACTION REQUIRED: contactar al experto, revisar cuenta bancaria/KYC y reagendar el payout.",
                    userId: expertUserId,
                    source: "SubscriptionController.HandlePayoutEvent",
                    relatedEntityType: "Payout",
                    additionalData: new
                    {
                        PayoutId = payout.Id,
                        Amount = amount,
                        Currency = currency,
                        AccountId = accountId,
                        payout.FailureMessage,
                        payout.FailureCode,
                        payout.Status
                    });

                // 🛡️ MUD-BN: notificar al experto también para payout.canceled.
                if (expertUserId.HasValue)
                {
                    await _loggingService.LogWarningAsync(
                        message: "⏸️ Un pago a tu cuenta ha sido cancelado",
                        details: $"Stripe canceló el envío de {amount:F2} {currency} a tu cuenta bancaria. Suele ocurrir cuando los datos bancarios no son válidos o la verificación KYC necesita actualizarse. Tu dinero sigue en tu balance Stripe (no se ha perdido). Revisa los datos de tu cuenta bancaria en el panel Stripe; un administrador puede contactarte si necesita más información.",
                        userId: expertUserId.Value,
                        source: "SubscriptionController.HandlePayoutEvent.MUD-BN",
                        relatedEntityType: "Payout",
                        relatedEntityId: null,
                        notifyUser: true);
                }
            }
            else
            {
                await _loggingService.LogInfoAsync(
                    message: "Payout paid",
                    details: $"Payout {payout.Id} of {amount}€ paid for account '{accountId ?? "platform"}'.",
                    source: "SubscriptionController.HandlePayoutEvent",
                    relatedEntityType: "Payout");

                // 🌍 Round 21: actualizar LastPayoutDate del ExpertProfile asociado a la cuenta Connect.
                // En "separate charges & transfers" payout.Destination es el external account (banco/tarjeta),
                // así que la cuenta Connect del experto se identifica por stripeEvent.Account (= accountId).
                // Para payouts de la plataforma (accountId == null) no hay experto que actualizar.
                if (!string.IsNullOrEmpty(accountId))
                {
                    var expertProfile = await _context.ExpertProfiles
                        .FirstOrDefaultAsync(ep => ep.StripeAccountId == accountId);

                    if (expertProfile != null)
                    {
                        // Stripe.Payout.ArrivalDate es DateTime no-nullable; si llega por defecto (MinValue) usar UtcNow.
                        expertProfile.LastPayoutDate = payout.ArrivalDate != default ? payout.ArrivalDate : DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                    }
                    else
                    {
                        await _loggingService.LogWarningAsync(
                            message: "payout.paid for unknown ExpertProfile",
                            details: $"No ExpertProfile found with StripeAccountId='{accountId}' for Payout {payout.Id}. Cannot update LastPayoutDate.",
                            source: "SubscriptionController.HandlePayoutEvent",
                            relatedEntityType: "Payout");
                    }
                }
            }
        }

        /// <summary>
        /// 🛡️ A4: charge.dispute.updated — Stripe actualiza el Dispute cuando el cliente añade
        /// evidencia, la plataforma sube counter-evidence, o cambia el estado intermedio. Antes
        /// caía en default y se perdía visibilidad. Aquí solo se actualizan campos no-críticos
        /// (Reason, evidence) si el Dispute ya existe localmente. La resolución final se sigue
        /// manejando en charge.dispute.closed (no aquí).
        /// </summary>
        private async Task HandleChargeDisputeUpdated(Stripe.Dispute? dispute)
        {
            if (dispute == null) return;

            var local = await _context.Disputes
                .FirstOrDefaultAsync(d => d.StripeDisputeId == dispute.Id);

            if (local == null)
            {
                // Dispute no local todavía — probablemente llegará el .created en otra entrega.
                // Sólo log info, no alerta.
                await _loggingService.LogInfoAsync(
                    message: "charge.dispute.updated for unknown local dispute",
                    details: $"StripeDisputeId={dispute.Id} status={dispute.Status} reason={dispute.Reason}",
                    source: "SubscriptionController.HandleChargeDisputeUpdated",
                    relatedEntityType: "Dispute");
                return;
            }

            // Refrescar campos del Dispute (motivo puede cambiar; estado intermedio también).
            // NO tocamos el StatusId interno — eso lo hace charge.dispute.closed.
            var changed = false;
            if (!string.IsNullOrEmpty(dispute.Reason) && local.Reason != dispute.Reason)
            {
                local.Reason = dispute.Reason;
                changed = true;
            }
            if (changed)
            {
                await _context.SaveChangesAsync();
            }

            await _loggingService.LogInfoAsync(
                message: "charge.dispute.updated processed",
                details: $"DisputeId={local.Id} StripeDisputeId={dispute.Id} stripeStatus={dispute.Status} reason={dispute.Reason} changed={changed}",
                source: "SubscriptionController.HandleChargeDisputeUpdated",
                relatedEntityType: "Dispute",
                relatedEntityId: local.Id);
        }

        /// <summary>
        /// 🛡️ A4: charge.failed — el cargo en sí falló (no el payment_intent). Es raro porque
        /// payment_intent.payment_failed cubre el caso normal, pero ocurre p.ej. con fraude
        /// detectado post-auth. Solo log + alerta — el flujo de hire ya se aborta por el
        /// payment_intent fallido. Sin handler antes caía a default = silencio.
        /// </summary>
        private async Task HandleChargeFailed(Charge? charge)
        {
            if (charge == null) return;
            var amount = charge.Amount / 100m;

            await _loggingService.LogWarningAsync(
                message: "Stripe charge.failed",
                details: $"ChargeId={charge.Id} amount={amount}€ failure_code={charge.FailureCode ?? "n/a"} failure_message={charge.FailureMessage ?? "n/a"} outcome={charge.Outcome?.Reason ?? "n/a"} risk={charge.Outcome?.RiskLevel ?? "n/a"} PI={charge.PaymentIntentId ?? "n/a"}",
                userId: null,
                source: "SubscriptionController.HandleChargeFailed",
                relatedEntityType: "Charge",
                relatedEntityId: null);
        }

        /// <summary>
        /// Localiza la SearchHire (y experto/cliente) asociada a un PaymentIntent, vía el
        /// FinancialTransaction de tipo "ServicePayment". Devuelve nulls si no se encuentra.
        /// </summary>
        private async Task<(int? hireId, int? expertId, int? clientId)> FindHireForPaymentIntentAsync(string? paymentIntentId)
        {
            if (string.IsNullOrEmpty(paymentIntentId))
            {
                return (null, null, null);
            }

            var ft = await _context.FinancialTransactions
                .FirstOrDefaultAsync(t => t.StripePaymentIntentId == paymentIntentId
                                          && t.TransactionType == "ServicePayment");
            if (ft == null || ft.RelatedEntityId == null)
            {
                return (null, null, null);
            }

            var hire = await _context.SearchHires
                .FirstOrDefaultAsync(h => h.Id == ft.RelatedEntityId);
            if (hire == null)
            {
                return (ft.RelatedEntityId, null, null);
            }

            return (hire.Id, hire.ExpertId, hire.ClientId);
        }

        private async Task HandlePaymentIntentFailed(PaymentIntent paymentIntent)
        {
            try
            {
                // 🚨 LOG CRÍTICO: Pago fallido - afecta dinero
                var amount = paymentIntent.Amount / 100m; // Convertir de céntimos a euros
                var userId = paymentIntent.Metadata?.ContainsKey("userId") == true &&
                            int.TryParse(paymentIntent.Metadata["userId"], out int parsedUserId)
                            ? parsedUserId : (int?)null;

                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Payment intent failed",
                    // 🛡️ MUD-AH: solo ISO currency (antes mezclaba € + ISO contradictorio).
                    details: $"Payment intent {paymentIntent.Id} failed. Amount: {amount} {paymentIntent.Currency?.ToUpper() ?? "EUR"}. Error: {paymentIntent.LastPaymentError?.Message ?? "No error details"}, Code: {paymentIntent.LastPaymentError?.Code}, Type: {paymentIntent.LastPaymentError?.Type}, DeclineCode: {paymentIntent.LastPaymentError?.DeclineCode}",
                    userId: userId,
                    source: "SubscriptionController.HandlePaymentIntentFailed",
                    relatedEntityType: "Payment",
                    relatedEntityId: null,
                    additionalData: new {
                        event_type = "payment_intent_failed",
                        PaymentIntentId = paymentIntent.Id,
                        Amount = amount,
                        Currency = paymentIntent.Currency,
                        Status = paymentIntent.Status,
                        LastPaymentError = paymentIntent.LastPaymentError != null ? new {
                            Message = paymentIntent.LastPaymentError.Message,
                            Code = paymentIntent.LastPaymentError.Code,
                            Type = paymentIntent.LastPaymentError.Type,
                            DeclineCode = paymentIntent.LastPaymentError.DeclineCode
                        } : null,
                        Metadata = paymentIntent.Metadata
                    }
                );

                // P0-6: localizar el SearchHire asociado y, si no está finalizado, cancelarlo
                // (incluyendo Appointment y timers Hangfire). No se emite refund: no hubo cobro.
                var hireFt = await _context.FinancialTransactions
                    .FirstOrDefaultAsync(t => t.StripePaymentIntentId == paymentIntent.Id
                                              && t.TransactionType == "ServicePayment");

                if (hireFt == null || hireFt.RelatedEntityId == null)
                {
                    // Sin hire local: mantener sólo el log crítico ya emitido. El webhook
                    // devolverá 200 al caller para no provocar reintentos infinitos de Stripe.
                    return;
                }

                int hireId = hireFt.RelatedEntityId.Value;

                var cancelledHireStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                var pendingHireStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Pending.ToStringValue());
                var awaitingHireStatusId = await GetStatusIdByValueAsync(SearchHireStatus.AwaitingClientDecision.ToStringValue());
                var disputedHireStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());

                var appointmentCancelledStatusId = (await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus"
                                              && s.StatusValue == "appointment_cancelled_by_client"))?.Id;

                var strategy = _context.Database.CreateExecutionStrategy();
                int? expertIdForNotif = null;
                int? clientIdForNotif = null;
                bool hireWasCancelledNow = false;

                await strategy.ExecuteAsync(async () =>
                {
                    await using var tx = await _context.Database.BeginTransactionAsync();

                    var hire = await _context.SearchHires
                        .Include(h => h.Appointment)
                        .FirstOrDefaultAsync(h => h.Id == hireId);

                    if (hire == null)
                    {
                        await tx.CommitAsync();
                        return;
                    }

                    expertIdForNotif = hire.ExpertId;
                    clientIdForNotif = hire.ClientId;

                    bool isActive = hire.StatusId == pendingHireStatusId
                                    || hire.StatusId == awaitingHireStatusId
                                    || hire.StatusId == disputedHireStatusId;

                    if (!isActive)
                    {
                        await tx.CommitAsync();
                        return;
                    }

                    hire.StatusId = cancelledHireStatusId;
                    hire.UpdatedAt = DateTime.UtcNow;
                    // 🛡️ Round 28 MUD-AS: marcar CaptureStatus="Failed" para que el watchdog
                    // (PlatformMaintenanceService.ProcessExpiringPaymentIntentsAsync) NO
                    // reintente cancelar un PI ya muerto. Evita gastos en Stripe API + log
                    // spam por hire afectado cada tick del cron.
                    hire.CaptureStatus = "Failed";
                    hireWasCancelledNow = true;

                    if (hire.Appointment != null && appointmentCancelledStatusId.HasValue)
                    {
                        hire.Appointment.StatusId = appointmentCancelledStatusId.Value;
                        hire.Appointment.UpdatedAt = DateTime.UtcNow;

                        var activeTimers = await _context.AppointmentTimers
                            .Where(t => t.AppointmentId == hire.Appointment.Id
                                        && !t.IsExpired
                                        && t.HangfireJobId != null)
                            .ToListAsync();

                        foreach (var timer in activeTimers)
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(timer.HangfireJobId))
                                {
                                    BackgroundJob.Delete(timer.HangfireJobId);
                                }
                            }
                            catch (Exception delEx)
                            {
                                await _loggingService.LogWarningAsync(
                                    message: "Failed to cancel Hangfire job after payment_intent.payment_failed",
                                    details: $"AppointmentTimerId: {timer.Id}, HangfireJobId: {timer.HangfireJobId}, Error: {delEx.Message}",
                                    source: "SubscriptionController.HandlePaymentIntentFailed",
                                    relatedEntityType: "AppointmentTimer",
                                    relatedEntityId: timer.Id);
                            }

                            timer.IsExpired = true;
                            timer.ExpiredAt = DateTime.UtcNow;
                            timer.Notes = (timer.Notes ?? string.Empty)
                                + $" | Cancelled by HandlePaymentIntentFailed (PI: {paymentIntent.Id})";
                        }
                    }

                    await _context.SaveChangesAsync();
                    await tx.CommitAsync();
                });

                if (hireWasCancelledNow)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: SearchHire cancelled due to payment_intent.payment_failed (deferred capture)",
                        details: $"SearchHire {hireId} cancelled because PaymentIntent {paymentIntent.Id} failed at capture time. No refund issued (no funds captured). Hangfire timers cancelled and Appointment marked as cancelled_by_client when present.",
                        userId: clientIdForNotif,
                        source: "SubscriptionController.HandlePaymentIntentFailed",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hireId,
                        additionalData: new {
                            // P3-11: tags estructurados para dashboard de métricas (Prometheus/AppInsights pendiente).
                            metric_name = "payment_intent_failed_hire_cancelled_total",
                            metric_kind = "counter",
                            event_type = "payment_intent_failed_hire_cancelled",
                            severity = "critical",
                            PaymentIntentId = paymentIntent.Id,
                            SearchHireId = hireId,
                            ExpertId = expertIdForNotif,
                            ClientId = clientIdForNotif,
                            LastPaymentErrorCode = paymentIntent.LastPaymentError?.Code,
                            LastPaymentErrorType = paymentIntent.LastPaymentError?.Type,
                            TimestampUtc = DateTime.UtcNow
                        });

                    var stripeErrorMsg = paymentIntent.LastPaymentError?.Message ?? "el cobro no pudo completarse";
                    var notifications = new List<Notification>();

                    if (clientIdForNotif.HasValue)
                    {
                        notifications.Add(new Notification
                        {
                            Id = Guid.NewGuid(),
                            Title = "Pago no completado",
                            Message = $"Tu pago para la contratación #{hireId} no pudo completarse ({stripeErrorMsg}). La contratación ha sido cancelada y no se te ha cobrado. Puedes intentarlo de nuevo desde tu panel.",
                            Type = "payment_failed",
                            UserId = clientIdForNotif.Value,
                            Read = false,
                            CreatedAt = DateTime.UtcNow
                        });
                    }

                    if (expertIdForNotif.HasValue)
                    {
                        notifications.Add(new Notification
                        {
                            Id = Guid.NewGuid(),
                            Title = "Contratación cancelada por pago fallido",
                            Message = $"La contratación #{hireId} se ha cancelado porque el pago del cliente no pudo completarse. No es necesario que realices ninguna acción.",
                            Type = "hire_cancelled_payment_failed",
                            UserId = expertIdForNotif.Value,
                            Read = false,
                            CreatedAt = DateTime.UtcNow
                        });
                    }

                    if (notifications.Count > 0)
                    {
                        try
                        {
                            _context.Notifications.AddRange(notifications);
                            await _context.SaveChangesAsync();
                        }
                        catch (Exception notifEx)
                        {
                            await _loggingService.LogWarningAsync(
                                message: "Failed to persist in-app notifications after payment_intent.payment_failed",
                                details: $"SearchHireId: {hireId}, Error: {notifEx.Message}",
                                source: "SubscriptionController.HandlePaymentIntentFailed",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: hireId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 🚨 LOG CRÍTICO: Error al procesar pago fallido
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Error processing failed payment intent",
                    details: $"Error processing failed payment intent {paymentIntent.Id}: {ex.Message}",
                    source: "SubscriptionController.HandlePaymentIntentFailed",
                    relatedEntityType: "Payment",
                    relatedEntityId: null,
                    additionalData: new {
                        PaymentIntentId = paymentIntent.Id,
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                // 🔁 FIX B7: relanzar (mismo motivo que HandlePaymentIntentCanceled) → 500 → Stripe reintenta.
                // Handler idempotente (guard isActive + notifs solo si hireWasCancelledNow).
                throw;
            }
        }

        /// <summary>
        /// transfer.failed (FIX B4) — el transfer al experto falló. Marca el SearchHire como TransferFailed,
        /// registra el fallo (sin devolver al cliente: el servicio se prestó) y alerta para revisión manual.
        /// Idempotente. Invocable desde /webhook (Connect) y /webhook-general (plataforma): en separate charges
        /// &amp; transfers el Transfer lo crea la PLATAFORMA (sin StripeAccount), así que Stripe entrega transfer.*
        /// al endpoint de "Your account" (plataforma) — por eso se maneja en ambos switches.
        /// </summary>
        private async Task HandleTransferFailed(Transfer? transfer)
        {
            if (transfer == null)
            {
                await _loggingService.LogWarningAsync(
                    message: "transfer.failed received with null Transfer object",
                    source: "SubscriptionController.transfer.failed",
                    relatedEntityType: "Transfer");
                return;
            }

            // El ID del transfer se persiste en FinancialTransaction.StripeTransferId; resolvemos el Payout y de
            // ahí el SearchHire.
            var payoutTx = await _context.FinancialTransactions
                .FirstOrDefaultAsync(ft => ft.StripeTransferId == transfer.Id
                                        && ft.TransactionType == "Payout"
                                        && ft.RelatedEntityType == "SearchHire");

            SearchHire? searchHire = null;
            if (payoutTx != null && payoutTx.RelatedEntityId.HasValue)
            {
                searchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .FirstOrDefaultAsync(sh => sh.Id == payoutTx.RelatedEntityId.Value);
            }

            if (searchHire == null)
            {
                // Si no localizamos el Payout/SearchHire por StripeTransferId, lo registramos como crítico para
                // revisión manual en vez de perder el fallo.
                await _loggingService.LogCriticalAsync(
                    message: "transfer.failed sin Payout/SearchHire asociado",
                    details: $"Transfer {transfer.Id} falló pero no se encontró FinancialTransaction Payout con ese StripeTransferId. Requiere revisión manual del pago al experto.",
                    userId: null,
                    source: "SubscriptionController.transfer.failed",
                    relatedEntityType: "Transfer",
                    relatedEntityId: null,
                    additionalData: new { TransferId = transfer.Id });
                return;
            }

            // 🛡️ Round 28 TX-2: envuelto en CreateExecutionStrategy().ExecuteAsync.
            // Antes: BeginTransactionAsync sin strategy era incompatible con EnableRetryOnFailure.
            // Si Stripe enviaba transfer.failed y ocurría un retry, lanzaba InvalidOperationException
            // y el FT "TransferFailed" no se registraba → contabilidad rota + sin alerta admin.
            var transferFailedStrategy = _context.Database.CreateExecutionStrategy();
            await transferFailedStrategy.ExecuteAsync(async () =>
            {
                await using var transferFailedTransaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // Solo REGISTRAR el fallo + marcar el hire para revisión. NO devolver al cliente (el experto hizo
                    // el trabajo); el pago al experto se reintenta/gestiona manualmente.
                    var failedTransaction = await _context.FinancialTransactions
                        .FirstOrDefaultAsync(ft => ft.RelatedEntityId == searchHire.Id &&
                                                   ft.TransactionType == "Payout");
                if (failedTransaction != null)
                {
                    _context.FinancialTransactions.Add(new FinancialTransaction
                    {
                        UserId = failedTransaction.UserId,
                        Amount = 0,
                        AmountCents = 0,
                        Currency = searchHire.Currency, // 🌍 Round 25: snapshot ISO 4217 desde el hire asociado
                        TransactionType = "TransferFailed",
                        RelatedEntityType = "SearchHire",
                        RelatedEntityId = searchHire.Id,
                        // 🛡️ C5 FIX: transfer.Id es un ID de Transfer (tr_XXX), NO un PaymentIntent.
                        // Antes se grababa en StripePaymentIntentId, donde las queries que filtran
                        // por StripePaymentIntentId == "pi_XXX" nunca lo encontraban → reportería rota.
                        // Para conservar el PI original (útil en trazabilidad), también lo copiamos
                        // de failedTransaction.StripePaymentIntentId si está presente.
                        StripeTransferId = transfer.Id,
                        StripePaymentIntentId = failedTransaction.StripePaymentIntentId,
                        CreatedAt = DateTime.UtcNow
                    });
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Transfer to expert failed - requires admin action",
                        details: $"TransferId: {transfer.Id}, SearchHireId: {searchHire.Id}, ExpertId: {failedTransaction.UserId}, Amount: {failedTransaction.Amount}€. Marcado para revisión manual.",
                        userId: failedTransaction.UserId,
                        source: "SubscriptionController.transfer.failed",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id,
                        additionalData: new { TransferId = transfer.Id, ExpertId = failedTransaction.UserId, Amount = failedTransaction.Amount });
                }

                    searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.TransferFailed.ToStringValue());
                    searchHire.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    await transferFailedTransaction.CommitAsync();
                }
                catch
                {
                    try { await transferFailedTransaction.RollbackAsync(); } catch { }
                    throw; // re-lanzar → el endpoint devuelve 500 y Stripe reintenta
                }
            });

            // 🛡️ Notificaciones FUERA del lambda: side-effects no-idempotentes (no deben re-ejecutarse en retries).
            if (searchHire.ExpertId.HasValue)
            {
                await _loggingService.LogWarningAsync(
                    message: "Transferencia pendiente con error",
                    details: $"La transferencia de tu servicio #{searchHire.Id} falló. El equipo de pagos la reintentará y te avisaremos.",
                    userId: searchHire.ExpertId.Value,
                    source: "SubscriptionController.transfer.failed",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHire.Id,
                    notifyUser: true);
            }
            await _loggingService.LogWarningAsync(
                message: "Pago al experto en revisión",
                details: $"La transferencia al experto del servicio #{searchHire.Id} falló. Un administrador está revisando el caso.",
                userId: searchHire.ClientId,
                source: "SubscriptionController.transfer.failed",
                relatedEntityType: "SearchHire",
                relatedEntityId: searchHire.Id,
                notifyUser: true);
        }

        /// <summary>
        /// 🛡️ Finding #19 FIX: transfer.created — informational. Confirma que Stripe aceptó un transfer
        /// que la plataforma creó (ProcessMoneyDistribution → TransferCreateOptions). Útil para detectar
        /// transfers externos (Dashboard) sin contraparte local en FT "Payout".
        /// </summary>
        private async Task HandleTransferCreated(Transfer? transfer)
        {
            if (transfer == null) return;
            var amount = transfer.Amount / 100m;
            var localPayout = await _context.FinancialTransactions.AsNoTracking()
                .FirstOrDefaultAsync(ft => ft.StripeTransferId == transfer.Id && ft.TransactionType == "Payout");
            var hasLocal = localPayout != null;

            if (!hasLocal)
            {
                await _loggingService.LogWarningAsync(
                    message: "transfer.created sin FT Payout local",
                    details: $"Transfer {transfer.Id} ({amount}€ → {transfer.Destination}) creado en Stripe pero no hay FT 'Payout' local con ese StripeTransferId. Posible transfer externo (Dashboard). ACCIÓN: reconciliar el ledger.",
                    source: "SubscriptionController.HandleTransferCreated",
                    relatedEntityType: "Transfer",
                    additionalData: new
                    {
                        TransferId = transfer.Id,
                        Amount = amount,
                        Destination = transfer.Destination,
                        Currency = transfer.Currency
                    });
            }
            else
            {
                await _loggingService.LogInfoAsync(
                    message: "transfer.created reconciled with local Payout",
                    details: $"Transfer {transfer.Id} ({amount}€) confirmed by Stripe. Local Payout FT exists (SearchHire {localPayout?.RelatedEntityId}).",
                    source: "SubscriptionController.HandleTransferCreated",
                    relatedEntityType: "Transfer",
                    relatedEntityId: localPayout?.RelatedEntityId);
            }
        }

        /// <summary>
        /// 🛡️ Finding #20 FIX: balance.available — observabilidad del saldo de plataforma para detectar
        /// caídas (chargebacks/disputas/payouts fallidos). Si algún saldo se vuelve negativo, log CRITICAL.
        /// Usa StripeEntity como tipo base (Balance hereda) y reflexión para evitar dependencia rígida del
        /// nombre exacto del tipo amount en la versión actual de Stripe.NET (cambia entre versiones).
        /// </summary>
        private async Task HandleBalanceAvailable(object? balanceObj)
        {
            if (balanceObj == null) return;
            try
            {
                // Reflexión para leer Available/Pending sin acoplarnos al tipo concreto BalanceAmount,
                // cuyo nombre/namespace varía entre versiones de Stripe.NET (v50 reestructuró el subtree).
                var type = balanceObj.GetType();
                var availableProp = type.GetProperty("Available");
                var pendingProp = type.GetProperty("Pending");

                var available = availableProp?.GetValue(balanceObj) as System.Collections.IEnumerable;
                var pending = pendingProp?.GetValue(balanceObj) as System.Collections.IEnumerable;

                long minAvailable = 0, minPending = 0;
                var availableSummaryParts = new List<string>();
                var pendingSummaryParts = new List<string>();

                if (available != null)
                {
                    foreach (var item in available)
                    {
                        var amountObj = item.GetType().GetProperty("Amount")?.GetValue(item);
                        var amount = amountObj == null ? 0L : Convert.ToInt64(amountObj);
                        var currency = item.GetType().GetProperty("Currency")?.GetValue(item)?.ToString() ?? "?";
                        availableSummaryParts.Add($"{currency}:{amount / 100m:F2}");
                        if (amount < minAvailable) minAvailable = amount;
                    }
                }
                if (pending != null)
                {
                    foreach (var item in pending)
                    {
                        var amountObj = item.GetType().GetProperty("Amount")?.GetValue(item);
                        var amount = amountObj == null ? 0L : Convert.ToInt64(amountObj);
                        var currency = item.GetType().GetProperty("Currency")?.GetValue(item)?.ToString() ?? "?";
                        pendingSummaryParts.Add($"{currency}:{amount / 100m:F2}");
                        if (amount < minPending) minPending = amount;
                    }
                }

                var availableSummary = string.Join(", ", availableSummaryParts);
                var pendingSummary = string.Join(", ", pendingSummaryParts);
                var anyNegative = minAvailable < 0 || minPending < 0;

                if (anyNegative)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Platform balance has NEGATIVE values",
                        details: $"Stripe balance.available event reports negative amount(s). Available=[{availableSummary}] Pending=[{pendingSummary}]. Posibles pérdidas no reconciliadas (chargebacks, disputas, payouts fallidos). REVISIÓN URGENTE.",
                        source: "SubscriptionController.HandleBalanceAvailable",
                        relatedEntityType: "Balance",
                        additionalData: new { Available = availableSummary, Pending = pendingSummary });
                }
                else
                {
                    await _loggingService.LogInfoAsync(
                        message: "Platform balance snapshot",
                        details: $"Available=[{availableSummary}] Pending=[{pendingSummary}]",
                        source: "SubscriptionController.HandleBalanceAvailable",
                        relatedEntityType: "Balance");
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogWarningAsync(
                    message: "balance.available handler failed",
                    details: ex.Message,
                    source: "SubscriptionController.HandleBalanceAvailable",
                    relatedEntityType: "Balance");
            }
        }

        /// <summary>
        /// transfer.reversed (P6) — Stripe confirma que un transfer al experto fue revertido (total o parcial).
        /// Casa el transfer revertido con su Payout/SearchHire por StripeTransferId (o metadata searchHireId) y
        /// con la fila "TransferReversal" del clawback. Confirma que la reversión esperada se aplicó; ALERTA como
        /// crítico si el importe revertido ≠ esperado o si no se localiza el hire/registro. Idempotente (solo concilia).
        /// </summary>
        private async Task HandleTransferReversed(Transfer? transfer)
        {
            if (transfer == null)
            {
                await _loggingService.LogWarningAsync(
                    message: "transfer.reversed received with null Transfer object",
                    source: "SubscriptionController.HandleTransferReversed",
                    relatedEntityType: "Transfer");
                return;
            }

            var reversedAmount = transfer.AmountReversed / 100m;
            var transferAmount = transfer.Amount / 100m;

            // 1) Localizar el Payout/SearchHire por el StripeTransferId persistido.
            var payoutTx = await _context.FinancialTransactions
                .FirstOrDefaultAsync(ft => ft.StripeTransferId == transfer.Id
                                        && ft.TransactionType == "Payout"
                                        && ft.RelatedEntityType == "SearchHire");

            // Fallback: la reversión por chargeback escribe searchHireId en la metadata del transfer.
            int? hireId = payoutTx?.RelatedEntityId;
            if (!hireId.HasValue
                && transfer.Metadata != null
                && transfer.Metadata.TryGetValue("searchHireId", out var metaHireId)
                && int.TryParse(metaHireId, out var parsedHireId))
            {
                hireId = parsedHireId;
            }

            if (!hireId.HasValue)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: transfer.reversed without matching Payout/SearchHire",
                    details: $"Transfer {transfer.Id} reportado como revertido ({reversedAmount:F2}€ de {transferAmount:F2}€) pero no se encontró FinancialTransaction Payout con ese StripeTransferId ni searchHireId en metadata. Requiere conciliación manual del clawback al experto.",
                    userId: null,
                    source: "SubscriptionController.HandleTransferReversed",
                    relatedEntityType: "Transfer",
                    relatedEntityId: null,
                    additionalData: new { TransferId = transfer.Id, AmountReversed = reversedAmount, TransferAmount = transferAmount, transfer.Reversed });
                return;
            }

            // 2) ¿Existe ya el registro de reversión? (consistencia).
            // 🛡️ B2 FIX: transfer.reversed se dispara para ambos casos (clawback y chargeback),
            // así que aceptamos cualquiera de los dos TransactionTypes. Antes solo buscaba
            // "TransferReversal" → para reversales de chargeback (ahora "ChargebackReversal")
            // siempre era null, y el mismatch check usaba el fallback de payoutTx.Amount.
            var reversalTx = await _context.FinancialTransactions
                .FirstOrDefaultAsync(ft => ft.RelatedEntityType == "SearchHire"
                                        && ft.RelatedEntityId == hireId.Value
                                        && (ft.TransactionType == "TransferReversal" || ft.TransactionType == "ChargebackReversal")
                                        && ft.StripeTransferId == transfer.Id);

            var expectedReversal = reversalTx != null
                ? Math.Abs(reversalTx.Amount)
                : (payoutTx != null ? Math.Abs(payoutTx.Amount) : reversedAmount);

            var mismatch = Math.Abs(reversedAmount - expectedReversal) > 0.01m;

            if (mismatch)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: transfer.reversed amount mismatch",
                    details: $"Transfer {transfer.Id} (SearchHire {hireId}) revertido por {reversedAmount:F2}€ pero se esperaba {expectedReversal:F2}€ (transfer original {transferAmount:F2}€). Posible reversión PARCIAL o doble — el experto podría quedarse fondos de un cargo revertido. Revisión manual.",
                    userId: payoutTx?.UserId,
                    source: "SubscriptionController.HandleTransferReversed",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: hireId.Value,
                    additionalData: new { TransferId = transfer.Id, AmountReversed = reversedAmount, ExpectedReversal = expectedReversal, TransferAmount = transferAmount, transfer.Reversed });
            }
            else
            {
                await _loggingService.LogInfoAsync(
                    message: "Expert transfer reversal confirmed by Stripe",
                    details: $"Transfer {transfer.Id} (SearchHire {hireId}) confirmado como revertido por {reversedAmount:F2}€ (esperado {expectedReversal:F2}€). " +
                             (reversalTx != null
                                ? "Casa con el TransferReversal local registrado por el clawback."
                                : "Sin TransferReversal local previo (reversión iniciada fuera del flujo automático); conciliar el ledger."),
                    userId: payoutTx?.UserId,
                    source: "SubscriptionController.HandleTransferReversed",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: hireId.Value);
            }
        }

        /// <summary>
        /// payment_intent.canceled (A-v) — con CAPTURA MANUAL, una autorización no capturada expira a ~7 días y
        /// Stripe la cancela. Si el SearchHire asociado sigue en un estado no-final esperando captura, se marca
        /// Cancelado y se notifica. NO hubo cobro → NO se emite refund. Idempotente; seguro si el PI no tiene hire.
        /// Clon del patrón probado de HandlePaymentIntentFailed.
        /// </summary>
        private async Task HandlePaymentIntentCanceled(PaymentIntent paymentIntent)
        {
            try
            {
                var amount = paymentIntent.Amount / 100m;
                var userId = paymentIntent.Metadata?.ContainsKey("userId") == true &&
                            int.TryParse(paymentIntent.Metadata["userId"], out int parsedUserId)
                            ? parsedUserId : (int?)null;

                // 🛡️ N23 FIX: Stripe usa el mismo evento payment_intent.canceled tanto para PI
                // que expiró sin captura (caso normal con CaptureMethod=manual) como para PI
                // que SÍ fue capturado y luego cancelado manualmente desde Dashboard (raro pero
                // posible). En el segundo caso el cliente fue cobrado y SE NECESITA refund.
                // Stripe.net v50+ eliminó pi.Charges → usamos pi.LatestChargeId (o GetAsync con
                // expand=latest_charge) y comprobamos el status del último charge.
                try
                {
                    if (!string.IsNullOrEmpty(paymentIntent.LatestChargeId))
                    {
                        var n23ChargeSvc = new Stripe.ChargeService();
                        var latestCharge = await n23ChargeSvc.GetAsync(paymentIntent.LatestChargeId);
                        if (latestCharge != null && string.Equals(latestCharge.Status, "succeeded", StringComparison.OrdinalIgnoreCase) && !latestCharge.Refunded)
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL N23: payment_intent.canceled con cargo CAPTURADO — refund manual requerido",
                                details: $"PI {paymentIntent.Id} ({amount}€) llegó como canceled pero el latest charge {latestCharge.Id} está succeeded y NO refunded. " +
                                         $"El dinero SÍ se cobró al cliente. ACCIÓN ADMIN: emitir refund manual desde Stripe Dashboard " +
                                         $"(Refunds → {paymentIntent.Id}) y revertir cualquier transfer al experto si ya se hizo. " +
                                         $"El handler sigue cancelando el hire local — la reconciliación de dinero es manual.",
                                userId: userId,
                                source: "SubscriptionController.HandlePaymentIntentCanceled.N23",
                                relatedEntityType: "PaymentIntent",
                                relatedEntityId: null,
                                additionalData: new { PaymentIntentId = paymentIntent.Id, Amount = amount, ChargeId = latestCharge.Id, ChargeStatus = latestCharge.Status, latestCharge.Refunded });
                        }
                    }
                }
                catch (Exception n23Ex)
                {
                    // Si no podemos comprobar el charge, log warning y continuar (no bloquear el flow del cancel).
                    await _loggingService.LogWarningAsync(
                        message: "N23: failed to check latest charge status on PI canceled",
                        details: $"PI {paymentIntent.Id}: no se pudo verificar el charge para detectar refund pendiente. Verificar manualmente en Stripe Dashboard. Error: {n23Ex.Message}",
                        userId: userId,
                        source: "SubscriptionController.HandlePaymentIntentCanceled.N23",
                        relatedEntityType: "PaymentIntent",
                        relatedEntityId: null);
                }

                await _loggingService.LogWarningAsync(
                    message: "Payment intent canceled (deferred capture expired or voided)",
                    // 🛡️ MUD-AH: solo ISO currency.
                    details: $"PaymentIntent {paymentIntent.Id} canceled. Amount: {amount} {paymentIntent.Currency?.ToUpper() ?? "EUR"}. CancellationReason: {paymentIntent.CancellationReason ?? "n/a"}, Status: {paymentIntent.Status}.",
                    userId: userId,
                    source: "SubscriptionController.HandlePaymentIntentCanceled",
                    relatedEntityType: "Payment",
                    relatedEntityId: null,
                    notifyUser: false);

                var hireFt = await _context.FinancialTransactions
                    .FirstOrDefaultAsync(t => t.StripePaymentIntentId == paymentIntent.Id
                                              && t.TransactionType == "ServicePayment");

                if (hireFt == null || hireFt.RelatedEntityId == null)
                {
                    return;
                }

                int hireId = hireFt.RelatedEntityId.Value;

                var cancelledHireStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                var pendingHireStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Pending.ToStringValue());
                var awaitingHireStatusId = await GetStatusIdByValueAsync(SearchHireStatus.AwaitingClientDecision.ToStringValue());
                var disputedHireStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());

                var appointmentCancelledStatusId = (await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus"
                                              && s.StatusValue == "appointment_cancelled_by_client"))?.Id;

                var strategy = _context.Database.CreateExecutionStrategy();
                int? expertIdForNotif = null;
                int? clientIdForNotif = null;
                bool hireWasCancelledNow = false;

                await strategy.ExecuteAsync(async () =>
                {
                    await using var tx = await _context.Database.BeginTransactionAsync();

                    var hire = await _context.SearchHires
                        .Include(h => h.Appointment)
                        .FirstOrDefaultAsync(h => h.Id == hireId);

                    if (hire == null)
                    {
                        await tx.CommitAsync();
                        return;
                    }

                    expertIdForNotif = hire.ExpertId;
                    clientIdForNotif = hire.ClientId;

                    bool isActive = hire.StatusId == pendingHireStatusId
                                    || hire.StatusId == awaitingHireStatusId
                                    || hire.StatusId == disputedHireStatusId;

                    if (!isActive)
                    {
                        await tx.CommitAsync();
                        return;
                    }

                    hire.StatusId = cancelledHireStatusId;
                    hire.UpdatedAt = DateTime.UtcNow;
                    // 🛡️ Round 28 MUD-AS: marcar CaptureStatus="Failed" para que el watchdog
                    // (PlatformMaintenanceService.ProcessExpiringPaymentIntentsAsync) NO
                    // reintente cancelar un PI ya muerto. Evita gastos en Stripe API + log
                    // spam por hire afectado cada tick del cron.
                    hire.CaptureStatus = "Failed";
                    hireWasCancelledNow = true;

                    if (hire.Appointment != null && appointmentCancelledStatusId.HasValue)
                    {
                        hire.Appointment.StatusId = appointmentCancelledStatusId.Value;
                        hire.Appointment.UpdatedAt = DateTime.UtcNow;

                        var activeTimers = await _context.AppointmentTimers
                            .Where(t => t.AppointmentId == hire.Appointment.Id
                                        && !t.IsExpired
                                        && t.HangfireJobId != null)
                            .ToListAsync();

                        foreach (var timer in activeTimers)
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(timer.HangfireJobId))
                                {
                                    BackgroundJob.Delete(timer.HangfireJobId);
                                }
                            }
                            catch (Exception delEx)
                            {
                                await _loggingService.LogWarningAsync(
                                    message: "Failed to cancel Hangfire job after payment_intent.canceled",
                                    details: $"AppointmentTimerId: {timer.Id}, HangfireJobId: {timer.HangfireJobId}, Error: {delEx.Message}",
                                    source: "SubscriptionController.HandlePaymentIntentCanceled",
                                    relatedEntityType: "AppointmentTimer",
                                    relatedEntityId: timer.Id);
                            }

                            timer.IsExpired = true;
                            timer.ExpiredAt = DateTime.UtcNow;
                            timer.Notes = (timer.Notes ?? string.Empty)
                                + $" | Cancelled by HandlePaymentIntentCanceled (PI: {paymentIntent.Id})";
                        }
                    }

                    await _context.SaveChangesAsync();
                    await tx.CommitAsync();
                });

                if (hireWasCancelledNow)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: SearchHire cancelled due to payment_intent.canceled (deferred capture expired)",
                        details: $"SearchHire {hireId} cancelled because PaymentIntent {paymentIntent.Id} was canceled (reason: {paymentIntent.CancellationReason ?? "n/a"}) before capture. No refund issued (no funds captured). Hangfire timers cancelled and Appointment marked as cancelled_by_client when present.",
                        userId: clientIdForNotif,
                        source: "SubscriptionController.HandlePaymentIntentCanceled",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: hireId,
                        additionalData: new {
                            metric_name = "payment_intent_canceled_hire_cancelled_total",
                            metric_kind = "counter",
                            event_type = "payment_intent_canceled_hire_cancelled",
                            severity = "critical",
                            PaymentIntentId = paymentIntent.Id,
                            SearchHireId = hireId,
                            ExpertId = expertIdForNotif,
                            ClientId = clientIdForNotif,
                            CancellationReason = paymentIntent.CancellationReason,
                            TimestampUtc = DateTime.UtcNow
                        });

                    var notifications = new List<Notification>();

                    if (clientIdForNotif.HasValue)
                    {
                        notifications.Add(new Notification
                        {
                            Id = Guid.NewGuid(),
                            Title = "Contratación cancelada",
                            Message = $"La contratación #{hireId} se ha cancelado porque la autorización del pago expiró antes de completarse. No se te ha cobrado. Puedes intentarlo de nuevo desde tu panel.",
                            Type = "payment_canceled",
                            UserId = clientIdForNotif.Value,
                            Read = false,
                            CreatedAt = DateTime.UtcNow
                        });
                    }

                    if (expertIdForNotif.HasValue)
                    {
                        notifications.Add(new Notification
                        {
                            Id = Guid.NewGuid(),
                            Title = "Contratación cancelada por pago expirado",
                            Message = $"La contratación #{hireId} se ha cancelado porque la autorización de pago del cliente expiró. No es necesario que realices ninguna acción.",
                            Type = "hire_cancelled_payment_canceled",
                            UserId = expertIdForNotif.Value,
                            Read = false,
                            CreatedAt = DateTime.UtcNow
                        });
                    }

                    if (notifications.Count > 0)
                    {
                        try
                        {
                            _context.Notifications.AddRange(notifications);
                            await _context.SaveChangesAsync();
                        }
                        catch (Exception notifEx)
                        {
                            await _loggingService.LogWarningAsync(
                                message: "Failed to persist in-app notifications after payment_intent.canceled",
                                details: $"SearchHireId: {hireId}, Error: {notifEx.Message}",
                                source: "SubscriptionController.HandlePaymentIntentCanceled",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: hireId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Error processing canceled payment intent",
                    details: $"Error processing canceled payment intent {paymentIntent.Id}: {ex.Message}",
                    source: "SubscriptionController.HandlePaymentIntentCanceled",
                    relatedEntityType: "Payment",
                    relatedEntityId: null,
                    additionalData: new {
                        PaymentIntentId = paymentIntent.Id,
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                // 🔁 FIX B7: RELANZAR (no tragar). Al propagar, el catch del endpoint marca el evento "Failed"
                // y devuelve 500 => Stripe REINTENTA y TryBeginProcessingEventAsync re-reclama el evento. El
                // handler es idempotente (guard isActive + notifs solo si hireWasCancelledNow), así que el
                // reintento no duplica. El "Success" del switch queda inalcanzable porque está tras esta llamada.
                throw;
            }
        }

        [HttpGet("all-money-distribution-configs")]
        public async Task<IActionResult> GetAllMoneyDistributionConfigs([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                // 🔐 SEGURIDAD: Verificar rol en lugar de email
                if (!_authService.IsAdmin(User))
                {
                    return Unauthorized(new { message = "Admin access required" });
                }

                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                var query = _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .Where(c => c.IsActive)
                    .Select(c => new
                    {
                        StatusId = c.StatusId,
                        StatusValue = c.Status.StatusValue,
                        StatusDisplayName = c.Status.DisplayName,
                        CategoryId = c.CategoryId,
                        CategoryName = c.Category != null ? c.Category.Name : null,
                        ServiceTypeCategoryId = c.ServiceTypeCategoryId,
                        ServiceTypeCategoryName = c.ServiceTypeCategory != null ? c.ServiceTypeCategory.Name : null,
                        ClientPercentage = c.ClientPercentage,
                        ExpertPercentage = c.ExpertPercentage,
                        PlatformPercentage = c.PlatformPercentage,
                        IsActive = c.IsActive,
                        CreatedAt = c.CreatedAt,
                        UpdatedAt = c.UpdatedAt
                    });

                var totalCount = await query.CountAsync();

                var configs = await query
                    .OrderBy(c => c.CategoryId)
                    .ThenBy(c => c.ServiceTypeCategoryId)
                    .ThenBy(c => c.StatusValue)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                return Ok(new { 
                    message = "All money distribution configurations",
                    configs,
                    pagination = new
                    {
                        page,
                        pageSize,
                        totalCount,
                        totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                        hasNextPage = page * pageSize < totalCount,
                        hasPreviousPage = page > 1
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Maneja el rechazo de una cuenta de Stripe, notificando tanto al admin como al experto
        /// </summary>
        private async Task HandleAccountRejection(int expertId, string rejectionReason)
        {
            try
            {
                // 1. Verificar si el experto tiene contrataciones activas
                var activeHires = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Where(sh => sh.ExpertId == expertId && 
                                sh.Status.StatusValue == "pending")
                    .CountAsync();
                // 2. Crear log crítico (esto automáticamente notifica al admin)
                await _loggingService.LogCriticalAsync(
                    $"Expert account rejected - ExpertId: {expertId}",
                    $"Stripe account rejected for expert {expertId}. Reason: {rejectionReason}. Active hires: {activeHires}",
                    expertId,
                    "SubscriptionController.HandleAccountRejection",
                    "ExpertProfile",
                    expertId,
                    new { 
                        ExpertId = expertId, 
                        RejectionReason = rejectionReason, 
                        ActiveHiresCount = activeHires,
                        Timestamp = DateTime.UtcNow
                    }
                );
                
                // 3. Crear notificación para el experto
                await NotifyExpertOfAccountRejection(expertId, rejectionReason, activeHires);
            }
            catch (Exception ex)
            {
                // P0-4: no tragar el error en silencio. Se preserva el comportamiento (no se relanza)
                // pero se deja traza para detectar fallos de notificación/persistencia.
                try
                {
                    await _loggingService.LogWarningAsync(
                        message: "Excepción silenciada en helper de notificación de cuenta Stripe",
                        details: $"Exception: {ex.GetType().Name}: {ex.Message}. Stack: {(ex.StackTrace ?? string.Empty).Substring(0, Math.Min(800, (ex.StackTrace ?? string.Empty).Length))}",
                        source: "SubscriptionController." + nameof(HandleAccountRejection));
                }
                catch
                {
                    Console.Error.WriteLine($"[CATCH-P0-4] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Crea una notificación para el experto cuando su cuenta es rechazada
        /// </summary>
        private async Task NotifyExpertOfAccountRejection(int expertId, string rejectionReason, int activeHiresCount)
        {
            try
            {
                var expert = await _context.Users.FindAsync(expertId);
                if (expert == null) 
                {
                    return;
                }
                
                // Crear notificación para el experto
                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    Title = "🚨 Cuenta de Pagos Rechazada",
                    Message = $"Tu cuenta de pagos fue rechazada por Stripe. Motivo: {rejectionReason}. Tienes {activeHiresCount} contrataciones activas que pueden verse afectadas. Contacta al soporte para más información.",
                    Type = "account_rejected",
                    UserId = expertId,
                    Read = false,
                    CreatedAt = DateTime.UtcNow
                };
                
                _context.Notifications.Add(notification);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // P0-4: no tragar el error en silencio. Se preserva el comportamiento (no se relanza)
                // pero se deja traza para detectar fallos de notificación/persistencia.
                try
                {
                    await _loggingService.LogWarningAsync(
                        message: "Excepción silenciada en helper de notificación de cuenta Stripe",
                        details: $"Exception: {ex.GetType().Name}: {ex.Message}. Stack: {(ex.StackTrace ?? string.Empty).Substring(0, Math.Min(800, (ex.StackTrace ?? string.Empty).Length))}",
                        source: "SubscriptionController." + nameof(NotifyExpertOfAccountRejection));
                }
                catch
                {
                    Console.Error.WriteLine($"[CATCH-P0-4] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Maneja la desautorización de una cuenta de Stripe, notificando tanto al admin como al experto
        /// (porque puede tener contrataciones activas)
        /// </summary>
        private async Task HandleAccountDeauthorization(int expertId, string deauthorizationReason)
        {
            try
            {
                // 1. Verificar si el experto tiene contrataciones activas
                // 🛡️ Round 28 MUD-BI: incluir TransferFailed. Si un hire ya prestó el servicio
                // pero el transfer al experto falló (status=TransferFailed esperando revisión),
                // y luego el experto se desautoriza/desactiva, ANTES quedaba zombi: el cliente
                // recibió servicio + dinero atascado en plataforma indefinidamente porque el
                // scan NO lo encontraba. Ahora lo recogemos: HandleApprovedAccountRejection
                // lo marcará RequiresManualReview=true + log Critical para admin.
                var activeStatusValues = new[]
                {
                    SearchHireStatus.Pending.ToStringValue(),
                    SearchHireStatus.AwaitingClientDecision.ToStringValue(),
                    SearchHireStatus.Disputed.ToStringValue(),
                    SearchHireStatus.TransferFailed.ToStringValue()
                };
                var activeHiresList = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Appointment)
                        .ThenInclude(a => a.Status)
                    .Where(sh => sh.ExpertId == expertId && activeStatusValues.Contains(sh.Status.StatusValue))
                    .ToListAsync();
                var activeHires = activeHiresList.Count;

                // 2. Crear log crítico (esto automáticamente notifica al admin)
                await _loggingService.LogCriticalAsync(
                    $"Expert account deauthorized - ExpertId: {expertId}",
                    $"Stripe account deauthorized for expert {expertId}. Reason: {deauthorizationReason}. Active hires: {activeHires}",
                    expertId,
                    "SubscriptionController.HandleAccountDeauthorization",
                    "ExpertProfile",
                    expertId,
                    new { 
                        ExpertId = expertId, 
                        DeauthorizationReason = deauthorizationReason, 
                        ActiveHiresCount = activeHires,
                        Timestamp = DateTime.UtcNow
                    }
                );

                // 3. Cancelar (con refund total) los hires futuros aún no prestados.
                var servicedAppointmentStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "appointment_awaiting_report",
                    "appointment_report_sent",
                    "appointment_completed",
                    "appointment_completed_without_client_approval"
                };
                // 🛡️ Round 28 MUD-BL: cargar disputes internas Pending/Resolving una sola vez
                // para chequear race condition contra refund automático. Si admin está
                // arbitrando una Dispute interna AHORA, NO debemos refund 100% al cliente
                // por nuestra cuenta — eso atropellaría la decisión admin → posible doble
                // refund + estado inconsistente. En ese caso, marcamos RequiresManualReview
                // y dejamos que el admin termine la dispute antes de procesar money flow.
                var hireIdsInDeauth = activeHiresList.Select(h => h.Id).ToList();
                var hiresWithActiveDispute = await _context.Disputes
                    .AsNoTracking()
                    .Where(d => hireIdsInDeauth.Contains(d.SearchHireId)
                             && (d.Status == "Pending" || d.Status == "Resolving"))
                    .Select(d => d.SearchHireId)
                    .ToListAsync();
                var hasActiveDisputeSet = new HashSet<int>(hiresWithActiveDispute);

                foreach (var hire in activeHiresList)
                {
                    var appointmentStatus = hire.Appointment?.Status?.StatusValue;
                    var alreadyServiced = appointmentStatus != null && servicedAppointmentStatuses.Contains(appointmentStatus);

                    // 🛡️ MUD-BL: si tiene dispute interna activa, NO refund automático.
                    if (hasActiveDisputeSet.Contains(hire.Id))
                    {
                        hire.RequiresManualReview = true;
                        hire.UpdatedAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL MUD-BL: deauth con Dispute interna activa — refund automático bloqueado",
                            details: $"SearchHire #{hire.Id}: experto desautorizó cuenta Stripe pero hay Dispute Pending/Resolving en curso. NO refund automático para no atropellar arbitraje admin. Admin debe resolver dispute primero, luego decidir refund/transfer manualmente. ExpertId={expertId}.",
                            userId: expertId,
                            source: "SubscriptionController.HandleAccountDeauthorization.MUD-BL",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: hire.Id);
                        if (hire.ClientId.HasValue)
                        {
                            try
                            {
                                await _loggingService.LogWarningAsync(
                                    message: "Tu disputa requiere revisión adicional",
                                    details: $"El experto involucrado en tu disputa desconectó su cuenta. Un administrador revisará el caso completo antes de decidir el reembolso. (SearchHire #{hire.Id})",
                                    userId: hire.ClientId.Value,
                                    source: "SubscriptionController.HandleAccountDeauthorization.MUD-BL",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: hire.Id,
                                    notifyUser: true);
                            }
                            catch { /* best-effort */ }
                        }
                        continue;
                    }

                    if (alreadyServiced)
                    {
                        hire.RequiresManualReview = true;
                        hire.UpdatedAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();

                        // 🛡️ Round 11 — S-E FIX: notificar al cliente también cuando su hire queda
                        // bloqueada en revisión manual. Antes el cliente no se enteraba.
                        if (hire.ClientId.HasValue)
                        {
                            try
                            {
                                await _loggingService.LogWarningAsync(
                                    message: "Tu servicio requiere revisión manual",
                                    details: $"El experto que estaba prestando tu servicio ha desconectado su cuenta de pagos de la plataforma. Tu servicio ya se había prestado, así que un administrador revisará el caso para resolverlo (refund o pago al experto). Te avisaremos cuando esté resuelto. (SearchHire #{hire.Id})",
                                    userId: hire.ClientId.Value,
                                    source: "SubscriptionController.HandleAccountDeauthorization",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: hire.Id,
                                    notifyUser: true);
                            }
                            catch { /* swallow — la notificación es best-effort */ }
                        }
                        continue;
                    }
                    try
                    {
                        await _refundService.ProcessMoneyDistributionAsync(
                            hire.Id,
                            SearchHireStatus.Cancelled.ToStringValue(), // 🔧 FIX #2: el granular *_account_delete NO existe en SystemStatuses -> config==null -> el reembolso al cliente quedaba BLOQUEADO. Cancelled (100/0/0) existe y reembolsa íntegro.
                            $"Stripe account deauthorized ({deauthorizationReason}); appointment not yet served.",
                            initiatedByUserId: -1,
                            updateState: true);

                        // 🛡️ Round 11 — S-E FIX: notificar al cliente del refund.
                        // Equivalente a lo que HandleApprovedAccountRejection ya hace.
                        if (hire.ClientId.HasValue)
                        {
                            try
                            {
                                await _loggingService.LogInfoAsync(
                                    message: "Tu servicio se ha cancelado y reembolsado",
                                    details: $"El experto que ibas a contratar ha desconectado su cuenta de pagos de la plataforma antes de prestar el servicio. Hemos cancelado tu contratación y te hemos reembolsado el importe íntegro. Puedes buscar otro experto si lo deseas. (SearchHire #{hire.Id})",
                                    userId: hire.ClientId.Value,
                                    source: "SubscriptionController.HandleAccountDeauthorization",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: hire.Id,
                                    notifyUser: true);
                            }
                            catch { /* swallow — la notificación es best-effort */ }
                        }
                    }
                    catch (Exception refundEx)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Failed to refund after deauthorization",
                            details: $"SearchHire {hire.Id}, ExpertId {expertId}, Error: {refundEx.Message}",
                            userId: expertId,
                            source: "SubscriptionController.HandleAccountDeauthorization",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: hire.Id);
                    }
                }

                // 4. Crear notificación para el experto
                await NotifyExpertOfAccountDeauthorization(expertId, deauthorizationReason, activeHires);
            }
            catch (Exception ex)
            {
                // P0-4: no tragar el error en silencio. Se preserva el comportamiento (no se relanza)
                // pero se deja traza para detectar fallos de notificación/persistencia.
                try
                {
                    await _loggingService.LogWarningAsync(
                        message: "Excepción silenciada en helper de notificación de cuenta Stripe",
                        details: $"Exception: {ex.GetType().Name}: {ex.Message}. Stack: {(ex.StackTrace ?? string.Empty).Substring(0, Math.Min(800, (ex.StackTrace ?? string.Empty).Length))}",
                        source: "SubscriptionController." + nameof(HandleAccountDeauthorization));
                }
                catch
                {
                    Console.Error.WriteLine($"[CATCH-P0-4] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Maneja la transición Approved -> Rejected de una cuenta de experto con hires activos.
        /// - Hires cuya cita ya se prestó (appointment_awaiting_report / appointment_report_sent / appointment_completed_*):
        ///   marca RequiresManualReview = true y emite LogCriticalAsync para que el admin lo revise.
        /// - Hires cuya cita aún no se prestó: cancela y emite refund total al cliente via _refundService.
        /// - Notifica al experto y a los clientes afectados.
        /// </summary>
        private async Task HandleApprovedAccountRejection(int expertProfileId, string disabledReason)
        {
            try
            {
                var expertProfile = await _context.ExpertProfiles
                    .Include(ep => ep.User)
                    .FirstOrDefaultAsync(ep => ep.Id == expertProfileId);
                if (expertProfile == null)
                {
                    await _loggingService.LogWarningAsync(
                        message: "HandleApprovedAccountRejection: expert profile not found",
                        details: $"expertProfileId={expertProfileId}",
                        source: "SubscriptionController.HandleApprovedAccountRejection",
                        relatedEntityType: "ExpertProfile");
                    return;
                }

                var expertId = expertProfile.UserId;

                // 🛡️ Round 28 MUD-BI: incluir TransferFailed. Si un hire ya prestó el servicio
                // pero el transfer al experto falló (status=TransferFailed esperando revisión),
                // y luego el experto se desautoriza/desactiva, ANTES quedaba zombi: el cliente
                // recibió servicio + dinero atascado en plataforma indefinidamente porque el
                // scan NO lo encontraba. Ahora lo recogemos: HandleApprovedAccountRejection
                // lo marcará RequiresManualReview=true + log Critical para admin.
                var activeStatusValues = new[]
                {
                    SearchHireStatus.Pending.ToStringValue(),
                    SearchHireStatus.AwaitingClientDecision.ToStringValue(),
                    SearchHireStatus.Disputed.ToStringValue(),
                    SearchHireStatus.TransferFailed.ToStringValue()
                };

                var activeHires = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Appointment)
                        .ThenInclude(a => a.Status)
                    .Where(sh => sh.ExpertId == expertId && activeStatusValues.Contains(sh.Status.StatusValue))
                    .ToListAsync();

                if (activeHires.Count == 0)
                {
                    await _loggingService.LogInfoAsync(
                        message: "Expert account rejected with no active hires",
                        details: $"ExpertId={expertId}, Reason={disabledReason}",
                        userId: expertId,
                        source: "SubscriptionController.HandleApprovedAccountRejection",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfile.Id);
                    return;
                }

                var servicedAppointmentStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "appointment_awaiting_report",
                    "appointment_report_sent",
                    "appointment_completed",
                    "appointment_completed_without_client_approval"
                };

                // 🛡️ Round 28 MUD-BW (follow-up MUD-BL): mismo guard de Dispute Pending también
                // aquí. Race: admin arbitrando Dispute interna AHORA + Stripe envía account.updated
                // degradando a Rejected/Disabled/Restricted → refund 100% automático atropella
                // decisión admin → posible doble refund + estado inconsistente. MUD-BL solo
                // protegía deauth; este path es idéntico.
                var hireIdsInRejection = activeHires.Select(h => h.Id).ToList();
                var hireIdsWithActiveDispute = await _context.Disputes
                    .AsNoTracking()
                    .Where(d => hireIdsInRejection.Contains(d.SearchHireId)
                             && (d.Status == "Pending" || d.Status == "Resolving"))
                    .Select(d => d.SearchHireId)
                    .ToListAsync();
                var rejectionDisputeSet = new HashSet<int>(hireIdsWithActiveDispute);

                foreach (var hire in activeHires)
                {
                    var appointmentStatus = hire.Appointment?.Status?.StatusValue;
                    var alreadyServiced = appointmentStatus != null && servicedAppointmentStatuses.Contains(appointmentStatus);

                    // 🛡️ MUD-BW: si tiene dispute interna activa, NO refund automático.
                    if (rejectionDisputeSet.Contains(hire.Id))
                    {
                        hire.RequiresManualReview = true;
                        hire.UpdatedAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL MUD-BW: rejection con Dispute interna activa — refund automático bloqueado",
                            details: $"SearchHire #{hire.Id}: cuenta Stripe rechazada/disabled/restricted ({disabledReason}) pero hay Dispute Pending/Resolving en curso. NO refund automático para no atropellar arbitraje admin. ExpertId={expertId}.",
                            userId: expertId,
                            source: "SubscriptionController.HandleApprovedAccountRejection.MUD-BW",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: hire.Id);
                        if (hire.ClientId.HasValue)
                        {
                            try
                            {
                                await _loggingService.LogWarningAsync(
                                    message: "Tu disputa requiere revisión adicional",
                                    details: $"El experto involucrado en tu disputa perdió temporalmente su capacidad de cobro. Un administrador revisará el caso completo antes de decidir el reembolso. (SearchHire #{hire.Id})",
                                    userId: hire.ClientId.Value,
                                    source: "SubscriptionController.HandleApprovedAccountRejection.MUD-BW",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: hire.Id,
                                    notifyUser: true);
                            }
                            catch { /* best-effort */ }
                        }
                        continue;
                    }

                    if (alreadyServiced)
                    {
                        hire.RequiresManualReview = true;
                        hire.UpdatedAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();

                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Expert account rejected after service performed",
                            details: $"SearchHire {hire.Id} flagged for manual review. Expert {expertId} account rejected ({disabledReason}). Appointment status: {appointmentStatus}.",
                            userId: expertId,
                            source: "SubscriptionController.HandleApprovedAccountRejection",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: hire.Id,
                            additionalData: new
                            {
                                SearchHireId = hire.Id,
                                ExpertId = expertId,
                                ClientId = hire.ClientId,
                                AppointmentStatus = appointmentStatus,
                                DisabledReason = disabledReason,
                                RequiresManualReview = true
                            });
                    }
                    else
                    {
                        try
                        {
                            var refundReason = $"Expert account rejected by Stripe ({disabledReason}); appointment not yet served.";
                            await _refundService.ProcessMoneyDistributionAsync(
                                hire.Id,
                                SearchHireStatus.Cancelled.ToStringValue(), // 🔧 FIX #2: el granular *_account_delete NO existe en SystemStatuses -> config==null -> el reembolso al cliente quedaba BLOQUEADO. Cancelled (100/0/0) existe y reembolsa íntegro.
                                refundReason,
                                initiatedByUserId: -1,
                                updateState: true);
                        }
                        catch (Exception refundEx)
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Failed to refund client after expert account rejection",
                                details: $"SearchHire {hire.Id}, ExpertId {expertId}, Error: {refundEx.Message}",
                                userId: expertId,
                                source: "SubscriptionController.HandleApprovedAccountRejection",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: hire.Id);
                        }
                    }

                    if (hire.ClientId.HasValue)
                    {
                        await _loggingService.LogWarningAsync(
                            message: "Servicio afectado por cierre de cuenta del experto",
                            details: $"El experto del servicio #{hire.Id} ya no puede operar. Si la cita no se prestó, se procesará un reembolso completo automáticamente.",
                            userId: hire.ClientId.Value,
                            source: "SubscriptionController.HandleApprovedAccountRejection",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: hire.Id,
                            notifyUser: true);
                    }
                }

                await _loggingService.LogErrorAsync(
                    message: "Tu cuenta de pagos fue rechazada por Stripe",
                    details: $"Stripe rechazó tu cuenta. Motivo: {disabledReason}. {activeHires.Count} servicios activos han sido procesados (reembolso o revisión manual).",
                    userId: expertId,
                    source: "SubscriptionController.HandleApprovedAccountRejection",
                    relatedEntityType: "ExpertProfile",
                    relatedEntityId: expertProfile.Id,
                    notifyUser: true);
            }
            catch (Exception ex)
            {
                try
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Exception in HandleApprovedAccountRejection",
                        details: $"ExpertProfileId {expertProfileId}, DisabledReason {disabledReason}: {ex.GetType().Name}: {ex.Message}",
                        source: "SubscriptionController.HandleApprovedAccountRejection",
                        relatedEntityType: "ExpertProfile",
                        relatedEntityId: expertProfileId);
                }
                catch
                {
                    Console.Error.WriteLine($"[HandleApprovedAccountRejection] {ex.GetType().Name}: {ex.Message}");
                }
                // 🛡️ A2 FIX: relanzar para que el caller (account.updated try-catch línea ~2369)
                // marque el webhook como "Failed" y devuelva 500. Sin throw, Stripe creía que el
                // webhook fue OK y NO reintentaba → hires zombi (refund pendiente, sin manual review).
                throw;
            }
        }

        /// <summary>
        /// Solo notifica al experto cuando su cuenta es rechazada (no puede tener contrataciones activas)
        /// </summary>
        private async Task NotifyExpertOnly(int expertId, string rejectionReason)
        {
            try
            {
                var expert = await _context.Users.FindAsync(expertId);
                if (expert == null) 
                {
                    return;
                }
                
                // Crear notificación para el experto
                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    Title = "❌ Cuenta de Pagos Rechazada",
                    Message = $"Tu cuenta de pagos fue rechazada por Stripe. Motivo: {rejectionReason}. Puedes intentar configurar una nueva cuenta de pagos.",
                    Type = "account_rejected",
                    UserId = expertId,
                    Read = false,
                    CreatedAt = DateTime.UtcNow
                };
                
                _context.Notifications.Add(notification);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // P0-4: no tragar el error en silencio. Se preserva el comportamiento (no se relanza)
                // pero se deja traza para detectar fallos de notificación/persistencia.
                try
                {
                    await _loggingService.LogWarningAsync(
                        message: "Excepción silenciada en helper de notificación de cuenta Stripe",
                        details: $"Exception: {ex.GetType().Name}: {ex.Message}. Stack: {(ex.StackTrace ?? string.Empty).Substring(0, Math.Min(800, (ex.StackTrace ?? string.Empty).Length))}",
                        source: "SubscriptionController." + nameof(NotifyExpertOnly));
                }
                catch
                {
                    Console.Error.WriteLine($"[CATCH-P0-4] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Crea una notificación para el experto cuando su cuenta es desautorizada
        /// </summary>
        private async Task NotifyExpertOfAccountDeauthorization(int expertId, string deauthorizationReason, int activeHiresCount)
        {
            try
            {
                var expert = await _context.Users.FindAsync(expertId);
                if (expert == null) 
                {
                    return;
                }
                
                // Crear notificación para el experto
                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    Title = "🚫 Cuenta de Pagos Desautorizada",
                    Message = $"Tu cuenta de pagos fue desautorizada por Stripe. Motivo: {deauthorizationReason}. Tienes {activeHiresCount} contrataciones activas que pueden verse afectadas. Contacta al soporte para más información.",
                    Type = "account_deauthorized",
                    UserId = expertId,
                    Read = false,
                    CreatedAt = DateTime.UtcNow
                };
                
                _context.Notifications.Add(notification);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // P0-4: no tragar el error en silencio. Se preserva el comportamiento (no se relanza)
                // pero se deja traza para detectar fallos de notificación/persistencia.
                try
                {
                    await _loggingService.LogWarningAsync(
                        message: "Excepción silenciada en helper de notificación de cuenta Stripe",
                        details: $"Exception: {ex.GetType().Name}: {ex.Message}. Stack: {(ex.StackTrace ?? string.Empty).Substring(0, Math.Min(800, (ex.StackTrace ?? string.Empty).Length))}",
                        source: "SubscriptionController." + nameof(NotifyExpertOfAccountDeauthorization));
                }
                catch
                {
                    Console.Error.WriteLine($"[CATCH-P0-4] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Método auxiliar para extraer el tipo de evento desde el JSON del webhook
        /// </summary>
        private string GetEventTypeFromJson(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json))
                    return "unknown";
                
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("type", out var typeElement))
                {
                    return typeElement.GetString() ?? "unknown";
                }
                return "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private string? GetAccountIdFromJson(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json))
                    return null;
                
                using var doc = JsonDocument.Parse(json);
                // Intentar obtener account del nivel raíz (para eventos de Connect)
                if (doc.RootElement.TryGetProperty("account", out var accountElement))
                {
                    return accountElement.GetString();
                }
                // Intentar obtener account de data.object.account
                if (doc.RootElement.TryGetProperty("data", out var dataElement) &&
                    dataElement.TryGetProperty("object", out var objectElement) &&
                    objectElement.TryGetProperty("account", out var nestedAccountElement))
                {
                    return nestedAccountElement.GetString();
                }
                return null;
            }
            catch
            {
                return null;
            }
        }


    }
}