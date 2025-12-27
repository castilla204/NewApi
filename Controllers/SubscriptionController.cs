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

        // ✅ Propiedades para leer claves dinámicamente desde configuración
        private string? WebhookSecret => _configuration["Stripe:WebhookSecret"];
        private string? GeneralWebhookSecret => _configuration["Stripe:GeneralWebhookSecret"];
        private string? StripeSecretKey => _configuration["Stripe:SecretKey"];

        public SubscriptionController(AppDbContext context, IConfiguration configuration, ISubscriptionService subscriptionService, StorageClient storageClient, SystemStatusService systemStatusService, IAuthorizationServices authService, ILoggingService loggingService, StripeRefundService refundService, IStripeValidationService stripeValidationService, IInvoiceService invoiceService, IAppointmentService appointmentService)
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
                // Default to "pending" (ID = 1)
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

            // ✅ FIX: Permitir PendingVerification si charges_enabled: true (Stripe permite operar)
            // Necesitamos verificar la cuenta de Stripe para saber si charges_enabled
            // Por ahora, permitimos PendingVerification si no hay otros problemas
            if (expertProfile.StripeStatus == StripeStatus.PendingVerification)
            {
                // ✅ PendingVerification es informativo, no bloqueante si Stripe permite operar
                // Verificamos si realmente está bloqueado consultando Stripe
                try
                {
                    if (!string.IsNullOrEmpty(expertProfile.StripeAccountId))
                    {
                        var accountService = new AccountService();
                        var account = await accountService.GetAsync(expertProfile.StripeAccountId);
                        
                        // Si charges_enabled y payouts_enabled, permitir operar
                        if (account.ChargesEnabled && account.PayoutsEnabled)
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
            return status switch
            {
                StripeStatus.NotRequested => "🔧 **Configuración Pendiente**: No has configurado tu cuenta de pagos de Stripe. Para ofrecer servicios y recibir pagos, necesitas completar el proceso de verificación. Haz clic en 'Configurar Pagos' para comenzar.",
                StripeStatus.Pending => "⏳ **Onboarding en Proceso**: Estamos creando tu cuenta en Stripe Connect. Completa el flujo de onboarding para pasar a verificación.",
                StripeStatus.ActionRequired => "📝 **Información Faltante**: Stripe te pide información/archivos inmediatos (requirements.currently_due). Abre el panel de Stripe y completa los campos pendientes para evitar restricciones.",
                StripeStatus.PendingVerification => "🔍 **Verificación de Documentos**: Stripe está revisando la documentación enviada. Puedes seguir operando normalmente mientras se completa la verificación.",
                StripeStatus.RequirementsDue => "⏰ **Requisitos por Vencer**: Stripe marcó requisitos con fecha límite próxima (future requirements). Completa la información solicitada cuanto antes para evitar bloqueos.",
                StripeStatus.RequirementsPastDue => "❗ **Requisitos Vencidos**: Algunos requisitos vencieron y Stripe bloqueó pagos. Actualiza tus datos en el panel de Stripe para reactivar la cuenta.",
                StripeStatus.RestrictedSoon => "⚠️ **Restricción Inminente**: Stripe restringirá tu cuenta si no atiendes los requisitos indicados. Ingresa al panel de Stripe y resuélvelos hoy mismo.",
                StripeStatus.Restricted => "🚫 **Cuenta Restringida**: Stripe limitó temporalmente cobros/pagos hasta que completes las acciones indicadas. Revisa los detalles en el panel de Stripe.",
                StripeStatus.Disabled => "⛔ **Pagos Deshabilitados**: Stripe deshabilitó los pagos por un incidente o incumplimiento. Debes resolverlo con Stripe para recuperar la cuenta.",
                StripeStatus.Approved => "✅ **Cuenta Aprobada**: ¡Excelente! Tu cuenta de pagos está completamente verificada y lista para recibir pagos. Ya puedes crear servicios y comenzar a ganar dinero.",
                StripeStatus.Rejected => "❌ **Cuenta Rechazada**: Stripe rechazó tu cuenta de forma definitiva. Revisa el motivo y contacta a soporte para crear una nueva solicitud solo si Stripe lo permite.",
                StripeStatus.Deauthorized => "🚫 **Cuenta Desautorizada**: Tu cuenta de pagos fue desconectada manualmente. Vuelve a iniciar el onboarding para reconectar Stripe.",
                _ => "❓ **Estado Desconocido**: No se pudo determinar el estado de tu cuenta de pagos. Por favor, contacta al soporte técnico para obtener ayuda."
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
                    requirementErrors.Add($"Code: {error.Code}, Reason: {error.Reason}, Requirement: {error.Requirement}");
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
                FutureRequirementsPastDue = futurePastDue.Any()
            };

            if (state.FutureRequirementsPastDue)
            {
                state.FutureRequirementsDueAt = DateTime.UtcNow.AddDays(-1);
            }
            else if (state.FutureRequirementsCurrentlyDue)
            {
                state.FutureRequirementsDueAt = DateTime.UtcNow.AddDays(7);
            }
            else if (futureEventuallyDue.Any())
            {
                state.FutureRequirementsDueAt = DateTime.UtcNow.AddDays(30);
            }

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
                
                state.Status = disabledReason switch
                {
                    "requirements.past_due" => StripeStatus.RequirementsPastDue,
                    "requirements.pending_verification" or "requirements.pending_review" => StripeStatus.PendingVerification,
                    "requirements.missing" or "requirements.currently_due" => StripeStatus.ActionRequired,
                    "platform_paused" or "platform_disabled" or "platform_suspended" => StripeStatus.Disabled,
                    _ when disabledReason.StartsWith("requirements.") => StripeStatus.Restricted,
                    _ when disabledReason.StartsWith("rejected.") => StripeStatus.Rejected,
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
            var details = new List<string> { GetStatusMessage(state.Status) };

            if (state.BlockingRequirements.Any() &&
                (state.Status == StripeStatus.ActionRequired || state.Status == StripeStatus.RequirementsPastDue))
            {
                details.Add($"Requisitos pendientes: {string.Join(", ", state.BlockingRequirements)}.");
            }

            if (state.FutureRequirements.Any() &&
                (state.Status == StripeStatus.RequirementsDue ||
                 state.Status == StripeStatus.RestrictedSoon ||
                 state.Status == StripeStatus.RequirementsPastDue))
            {
                var label = state.FutureRequirementsPastDue ? "requisitos vencidos" : "requisitos futuros";
                details.Add($"Stripe indicó {label}: {string.Join(", ", state.FutureRequirements)}.");
            }

            if (!string.IsNullOrWhiteSpace(state.DisabledReason) &&
                (state.Status == StripeStatus.Disabled || state.Status == StripeStatus.Restricted || state.Status == StripeStatus.Rejected))
            {
                details.Add($"Código Stripe: {state.DisabledReason}.");
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
            expertProfile.StripeFutureRequirements = state.FutureRequirements.Any()
                ? string.Join(", ", state.FutureRequirements)
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
                return profile;
            }

            if (account.Metadata != null && account.Metadata.TryGetValue("userId", out var userIdValue) && int.TryParse(userIdValue, out var userId))
            {
                return await _context.ExpertProfiles.FirstOrDefaultAsync(ep => ep.UserId == userId);
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
                        // Continuar con el flujo normal de creación de cuenta
                    }
                }

                if (!string.IsNullOrEmpty(expertProfile.StripeAccountId))
                {
                    // Clean up PendingStripeAccountId if it exists (shouldn't happen but just in case)
                    if (!string.IsNullOrEmpty(expertProfile.PendingStripeAccountId))
                    {
                        expertProfile.PendingStripeAccountId = null;
                        expertProfile.OnboardingCompleted = true;
                        await _context.SaveChangesAsync();
                    }
                    
                    // If expert already has a completed Stripe account, create a login link instead
                    var linkOptions = new AccountLinkCreateOptions
                    {
                        Account = expertProfile.StripeAccountId,
                        RefreshUrl = "https://inspecciono.com/refresh-onboarding",
                        ReturnUrl = "https://inspecciono.com/complete-onboarding",
                        Type = "account_onboarding"
                    };
                    
                    var linkService = new AccountLinkService();
                    
                    try
                    {
                        var accountLink = await linkService.CreateAsync(linkOptions);
                        return Ok(new { url = accountLink.Url, isLoginLink = true });
                    }
                    catch (StripeException ex)
                    {
                        return StatusCode(500, new { message = "Failed to create Stripe account link" });
                    }
                }

                if (!string.IsNullOrEmpty(expertProfile.PendingStripeAccountId))
                {
                    // Si tiene cuenta pendiente pero no completó onboarding, crear nuevo link para continuar
                    var linkOptions = new AccountLinkCreateOptions
                    {
                        Account = expertProfile.PendingStripeAccountId,
                        RefreshUrl = "https://inspecciono.com/refresh-onboarding",
                        ReturnUrl = "https://inspecciono.com/complete-onboarding",
                        Type = "account_onboarding",
                        Collect = "eventually_due"
                    };
                    
                    var linkService = new AccountLinkService();
                    
                    try
                    {
                        var accountLink = await linkService.CreateAsync(linkOptions);
                        return Ok(new { url = accountLink.Url, isLoginLink = false });
                    }
                    catch (StripeException ex)
                    {
                        return StatusCode(500, new { message = "Failed to create onboarding link" });
                    }
                }

                // Limpiar cualquier PendingStripeAccountId anterior antes de crear nueva cuenta
                if (!string.IsNullOrEmpty(expertProfile.PendingStripeAccountId))
                {
                    expertProfile.PendingStripeAccountId = null;
                }

                // Marcar como pendiente antes de crear la cuenta
                expertProfile.StripeStatus = StripeStatus.Pending;
                await _context.SaveChangesAsync();
                var accountOptions = new AccountCreateOptions
                {
                    Type = "express",
                    Country = "ES",
                    Email = User.FindFirst(ClaimTypes.Email)?.Value,
                    Capabilities = new AccountCapabilitiesOptions
                    {
                        Transfers = new AccountCapabilitiesTransfersOptions { Requested = true }
                    },
                    BusinessType = "individual",
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() }
                    }
                };

                var accountService = new AccountService();
                Account account;
                try
                {
                    account = await accountService.CreateAsync(accountOptions);
                }
                catch (StripeException ex)
                {
                    return StatusCode(500, new { message = "Failed to create Stripe account" });
                }

                // Usar la estrategia de ejecución para manejar transacciones con reintentos
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // Guardar temporalmente el account ID hasta que se complete el onboarding
                    expertProfile.PendingStripeAccountId = account.Id;
                    expertProfile.OnboardingCompleted = false;
                        expertProfile.StripeStatus = StripeStatus.Pending;
                    await _context.SaveChangesAsync();
                    var linkOptions = new AccountLinkCreateOptions
                    {
                        Account = account.Id,
                        RefreshUrl = "https://inspecciono.com/refresh-onboarding",
                        ReturnUrl = "https://inspecciono.com/complete-onboarding",
                        Type = "account_onboarding",
                        Collect = "eventually_due"
                    };

                    var linkService = new AccountLinkService();
                    AccountLink accountLink;
                    try
                    {
                        accountLink = await linkService.CreateAsync(linkOptions);
                    }
                    catch (StripeException ex)
                    {
                        await transaction.RollbackAsync();
                        return StatusCode(500, new { message = "Failed to create onboarding link" });
                    }

                    await transaction.CommitAsync();
                    return Ok(new { url = accountLink.Url });
                }
                    catch (DbUpdateException dbEx)
                    {
                        await transaction.RollbackAsync();
                        return StatusCode(500, new { message = "Failed to save Stripe account", details = dbEx.InnerException?.Message ?? dbEx.Message });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                        return StatusCode(500, new { message = "Failed to save Stripe account", details = ex.Message });
                }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to process expert onboarding" });
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
                var accountLinkOptions = new Stripe.AccountLinkCreateOptions
                {
                    Account = expertProfile.StripeAccountId,
                    RefreshUrl = "https://inspecciono.com/expert-panel?refresh=true", // URL si necesita refrescar
                    ReturnUrl = "https://inspecciono.com/expert-panel", // URL de retorno después de actualizar datos
                    Type = linkType // ✅ account_update para cuentas aprobadas, account_onboarding para requirements pendientes
                };

                var accountLink = await accountLinkService.CreateAsync(accountLinkOptions);
                return Ok(new { 
                    message = "Enlace de cuenta creado exitosamente",
                    accountLinkUrl = accountLink.Url 
                });
            }
            catch (StripeException stripeEx)
            {
                return StatusCode(500, new { message = "Error de Stripe al crear el enlace de cuenta", error = stripeEx.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error interno del servidor" });
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
                    return StatusCode(500, new { message = "Failed to retrieve Stripe account status" });
                }

                var previousStatus = expertProfile.StripeStatus;
                var accountState = EvaluateStripeAccount(account);

                ApplyStripeAccountState(expertProfile, accountState, account.Id);

                await _context.SaveChangesAsync();
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
                    var restartLinkOptions = new AccountLinkCreateOptions
                    {
                        Account = expertProfile.StripeAccountId,
                        RefreshUrl = "https://inspecciono.com/refresh-onboarding",
                        ReturnUrl = "https://inspecciono.com/complete-onboarding",
                        Type = "account_onboarding"
                    };
                    
                    var restartLinkService = new AccountLinkService();
                    
                    try
                    {
                        var restartAccountLink = await restartLinkService.CreateAsync(restartLinkOptions);
                        return Ok(new { url = restartAccountLink.Url, isLoginLink = true });
                    }
                    catch (StripeException ex)
                    {
                        return StatusCode(500, new { message = "Failed to create Stripe account link" });
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
                    RefreshUrl = "https://inspecciono.com/refresh-onboarding",
                    ReturnUrl = "https://inspecciono.com/complete-onboarding",
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
                    return StatusCode(500, new { message = "Failed to create new onboarding link" });
                }

                return Ok(new { url = pendingAccountLink.Url });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to restart onboarding" });
            }
        }

        [HttpPost("load-money")]
        public async Task<IActionResult> LoadMoney([FromBody] LoadMoneyDto request)
        {
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

                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var user = await _context.Users
                    .FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE")
                    .FirstOrDefaultAsync();
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

                var domain = "https://inspecciono.com";
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
                                },
                                // ✅ STRIPE TAX: Configurar tax como inclusivo
                                TaxBehavior = "inclusive"
                            },
                            Quantity = 1
                        }
                    },
                    // ✅ STRIPE TAX: Habilitar cálculo automático de tax
                    AutomaticTax = new SessionAutomaticTaxOptions
                    {
                        Enabled = true
                    },
                    Mode = "payment",
                    SuccessUrl = $"{domain}/success?session_id={{CHECKOUT_SESSION_ID}}",
                    CancelUrl = domain + "/cancel",
                    CustomerEmail = User.FindFirst(ClaimTypes.Email)?.Value,
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() },
                        { "amount", request.Amount.ToString() }
                    }
                };

                var service = new SessionService();
                Session session;
                try
                {
                    session = await service.CreateAsync(options);
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

                // 🚨 VALIDACIÓN CRÍTICA: Verificar que el experto no se contrate a sí mismo
                // ✅ IMPORTANTE: Esta validación DEBE hacerse ANTES de crear el checkout session
                // para evitar perder comisiones de Stripe al hacer refunds
                if (service.ExpertProfile != null && service.ExpertProfile.UserId == userId)
                {
                    return BadRequest(new { message = "No puedes contratarte a ti mismo como experto" });
                }

                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var user = await _context.Users
                    .FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE")
                    .FirstOrDefaultAsync();
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

                // 💳 SIEMPRE PAGAR CON STRIPE - NO USAR SALDO INTERNO
                var amountToCharge = service.Price;

                var domain = "https://inspecciono.com";
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
                                UnitAmount = checked((long)Math.Round(amountToCharge * 100)),
                                ProductData = new SessionLineItemPriceDataProductDataOptions
                                {
                                    Name = $"Payment for Service {service.Id}"
                                },
                                // ✅ STRIPE TAX: Configurar tax como inclusivo (el precio ya incluye IVA)
                                TaxBehavior = "inclusive" // Stripe hace reverse calc automático
                            },
                            Quantity = 1
                        }
                    },
                    // ✅ STRIPE TAX: Habilitar cálculo automático de tax basado en ubicación del comprador
                    AutomaticTax = new SessionAutomaticTaxOptions
                    {
                        Enabled = true // Habilita cálculo auto basado en IP, billing/shipping address
                    },
                    Mode = "payment",
                    SuccessUrl = $"{domain}/success?session_id={{CHECKOUT_SESSION_ID}}&userId={userId}",
                    CancelUrl = $"{domain}/cancel",
                    CustomerEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown@example.com",
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() },
                        { "serviceId", request.ServiceId.ToString() },
                        { "amount", amountToCharge.ToString() },
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
                    session = await stripeService.CreateAsync(options);
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
        public async Task<IActionResult> HandleStripeWebhook()
        {
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
                
                // ✅ FALLBACK: Si el webhook secret principal está vacío, intentar usar el general
                // Esto es útil en desarrollo cuando solo se configura un webhook secret
                if (string.IsNullOrEmpty(webhookSecretToUse))
                {
                    webhookSecretToUse = GeneralWebhookSecret;
                    if (!string.IsNullOrEmpty(webhookSecretToUse))
                    {
                        await _loggingService.LogWarningAsync(
                            message: "Using general webhook secret as fallback for Connect webhook",
                            details: "Webhook secret for Connect events is not configured, using general webhook secret as fallback. This should be fixed in production.",
                            userId: null,
                            source: "SubscriptionController.HandleStripeWebhook",
                            relatedEntityType: "Webhook"
                        );
                    }
                }
                
                if (string.IsNullOrEmpty(webhookSecretToUse))
                {
                    // 🚨 LOG CRÍTICO: Webhook secret no configurado
                    var eventType = GetEventTypeFromJson(json);
                    var accountId = GetAccountIdFromJson(json);
                    
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Webhook secret not configured for Connect events",
                        details: $"Both WebhookSecret and GeneralWebhookSecret are empty. Event type: {eventType}, Account: {accountId}. " +
                                $"INSTRUCCIONES: 1) Ve al Dashboard de Stripe → Developers → Webhooks → Tu endpoint → Signing secret. " +
                                $"2) Copia el secret (whsec_...). " +
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
                            Instructions = "Configure webhook secret from Stripe Dashboard → Developers → Webhooks → Your endpoint → Signing secret"
                        }
                    );
                    return BadRequest(new { 
                        error = "Webhook secret not configured",
                        instructions = "Configure Stripe:WebhookSecret from Stripe Dashboard → Developers → Webhooks → Your endpoint → Signing secret",
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
                        await _loggingService.LogWarningAsync(
                            message: "Stripe webhook API version mismatch",
                            details: $"Webhook event received with API version '{stripeEvent.ApiVersion}', but SDK expects '{expectedVersion}'. " +
                                    $"Consider updating the webhook endpoint in Stripe Dashboard to use API version '{expectedVersion}' for better compatibility.",
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

                // 🔒 IDEMPOTENCIA COMPLETA: Verificar si el evento ya fue procesado
                if (await IsEventProcessedAsync(stripeEvent.Id))
                {
                    return Ok(new { message = "Event already processed" });
                }

                // ✅ CORRECCIÓN CRÍTICA: Marcar idempotencia ANTES de procesar (Stripe Best Practices)
                // Esto previene procesamiento duplicado si hay error durante el procesamiento
                await MarkEventAsProcessedAsync(stripeEvent.Id, stripeEvent.Type, stripeEvent.Account, null, "Processing");
                eventMarkedProcessing = true;

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

                        var deauthStrategy = _context.Database.CreateExecutionStrategy();
                        await deauthStrategy.ExecuteAsync(async () =>
                        {
                            await using var transaction = await _context.Database.BeginTransactionAsync();
                            try
                            {
                                ApplyStripeAccountState(deauthorizedExpertProfile, deauthorizedState);

                                // Stripe recomienda desvincular por completo la cuenta
                                deauthorizedExpertProfile.StripeAccountId = null;
                                deauthorizedExpertProfile.PendingStripeAccountId = null;

                                await _context.SaveChangesAsync();
                                await transaction.CommitAsync();
                            }
                            catch
                            {
                                await transaction.RollbackAsync();
                                throw;
                            }
                        });

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
                        var account = stripeEvent.Data.Object as Account;
                        if (account == null)
                        {
                            break;
                        }

                        var idempotencyKey = stripeEvent.Request?.IdempotencyKey;
                        var eventIdToCheck = !string.IsNullOrEmpty(idempotencyKey) ? idempotencyKey : stripeEvent.Id;
                        if (await IsEventProcessedAsync(eventIdToCheck))
                        {
                            break;
                        }

                        var profileToUpdate = await FindExpertProfileForAccountAsync(account);
                        if (profileToUpdate == null)
                        {
                            await _loggingService.LogWarningAsync(
                                message: "Stripe account updated without matching expert profile",
                                details: $"account_id={account.Id}",
                                userId: null,
                                source: "SubscriptionController.account.updated",
                                relatedEntityType: "StripeAccount",
                                relatedEntityId: null);

                            await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, null, "Skipped", "Expert profile not found");
                            break;
                        }

                        try
                        {
                            var strategy = _context.Database.CreateExecutionStrategy();
                            await strategy.ExecuteAsync(async () =>
                            {
                                await using var transaction = await _context.Database.BeginTransactionAsync();
                                try
                                {
                                    var previousStatus = profileToUpdate.StripeStatus;
                                    var state = EvaluateStripeAccount(account);

                                    ApplyStripeAccountState(profileToUpdate, state, account.Id);

                                    await _context.SaveChangesAsync();
                                    await transaction.CommitAsync();

                                    if (previousStatus != state.Status)
                                    {
                                        await NotifyStripeStatusTransitionAsync(
                                            profileToUpdate,
                                            previousStatus,
                                            state,
                                            "SubscriptionController.account.updated");
                                    }

                                    await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, profileToUpdate.UserId);
                                }
                                catch (Exception ex)
                                {
                                    await transaction.RollbackAsync();
                                    await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, profileToUpdate.UserId, "Failed", ex.Message);
                                    throw;
                                }
                            });
                        }
                        catch (Exception logicEx)
                        {
                            await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, profileToUpdate.UserId, "Error", logicEx.Message);
                            if (eventMarkedProcessing && currentEventId != null)
                            {
                                await MarkEventAsProcessedAsync(
                                    currentEventId,
                                    currentEventType ?? stripeEvent.Type,
                                    currentAccountId,
                                    null,
                                    "Error",
                                    logicEx.Message);
                                eventMarkedProcessing = false;
                            }
                            return Ok(new { message = "Event processed with errors" });
                        }

                        break;
                    case "transfer.failed":
                        var transfer = stripeEvent.Data.Object as Transfer;
                        if (transfer != null)
                        {
                            var searchHire = await _context.SearchHires
                                .Include(sh => sh.Status)
                                .Include(sh => sh.Client)
                                .FirstOrDefaultAsync(sh => sh.ExpertTransferId == transfer.Id);
                            if (searchHire != null)
                            {
                                var transferFailedStrategy = _context.Database.CreateExecutionStrategy();
                                await transferFailedStrategy.ExecuteAsync(async () =>
                                {
                                    await using var transaction = await _context.Database.BeginTransactionAsync();
                                try
                                {
                                    // 🚨 REGISTRAR FALLO DE TRANSFER - NO DEVOLVER AL CLIENTE
                                    // El cliente ya aprobó el servicio, el experto ya hizo el trabajo
                                    // Solo registrar el error y alertar a administradores
                                    
                                    var failedTransaction = await _context.FinancialTransactions
                                        .FirstOrDefaultAsync(ft => ft.RelatedEntityId == searchHire.Id && 
                                                                   ft.TransactionType == "Payout");
                                    
                                    if (failedTransaction != null)
                                    {
                                        // ✅ SOLO REGISTRAR EL FALLO - NO REVERTIR NI REFUND
                                        var failureRecord = new FinancialTransaction
                                        {
                                            UserId = failedTransaction.UserId,
                                            Amount = 0, // No hay monto en caso de fallo
                                            TransactionType = "TransferFailed",
                                            RelatedEntityType = "SearchHire",
                                            RelatedEntityId = searchHire.Id,
                                            StripePaymentIntentId = transfer.Id, // ID del transfer fallido
                                            CreatedAt = DateTime.UtcNow
                                        };
                                        _context.FinancialTransactions.Add(failureRecord);
                                        // 🚨 Registrar en sistema de logs con tipo específico
                                        await _loggingService.LogCriticalAsync(
                                            $"Transfer to expert failed - SearchHireId: {searchHire.Id}",
                                            $"TransferId: {transfer.Id}, ExpertId: {failedTransaction.UserId}, Amount: {failedTransaction.Amount}€",
                                            failedTransaction.UserId,
                                            "SubscriptionController.transfer.failed",
                                            "SearchHire",
                                            searchHire.Id,
                                            new { TransferId = transfer.Id, ExpertId = failedTransaction.UserId, Amount = failedTransaction.Amount }
                                        );
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: Transfer failure requires admin action",
                                            details: $"Transfer {transfer.Id} for SearchHire {searchHire.Id} failed and was marked for manual review.",
                                            userId: null,
                                            source: "SubscriptionController.transfer.failed",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHire.Id,
                                            additionalData: new { TransferId = transfer.Id, ExpertId = failedTransaction.UserId, Amount = failedTransaction.Amount }
                                        );
                                    }
                                    
                                    var transferFailedStatusId = await GetStatusIdByValueAsync(SearchHireStatus.TransferFailed.ToStringValue());
                                    searchHire.StatusId = transferFailedStatusId;
                                    searchHire.UpdatedAt = DateTime.UtcNow;
                                    
                                await _context.SaveChangesAsync();
                                    await transaction.CommitAsync();

                                    if (searchHire.ExpertId.HasValue)
                                    {
                                        await _loggingService.LogWarningAsync(
                                            message: "Transferencia pendiente con error",
                                            details: $"La transferencia de tu servicio #{searchHire.Id} falló. El equipo de pagos la reintentará y te avisaremos.",
                                            userId: searchHire.ExpertId.Value,
                                            source: "SubscriptionController.transfer.failed",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHire.Id,
                                            notifyUser: true
                                        );
                                    }

                                    await _loggingService.LogWarningAsync(
                                        message: "Pago al experto en revisión",
                                        details: $"La transferencia al experto del servicio #{searchHire.Id} falló. Un administrador está revisando el caso para garantizar que el dinero esté seguro.",
                                        userId: searchHire.ClientId,
                                        source: "SubscriptionController.transfer.failed",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHire.Id,
                                        notifyUser: true
                                    );
                                }
                                catch (Exception ex)
                                {
                                    await transaction.RollbackAsync();
                                }
                                });
                            }
                            else
                            {
                            }
                        }
                        break;

                    // Los eventos de suscripción y facturas se manejan en el webhook general

                    default:
                        break;
                }

                if (eventMarkedProcessing)
                {
                    await MarkEventAsProcessedAsync(stripeEvent.Id, stripeEvent.Type, stripeEvent.Account, null, "Success");
                    eventMarkedProcessing = false;
                }
                return Ok();
            }
            catch (StripeException e)
            {
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
                return BadRequest(new { error = e.Message });
            }
            catch (Exception e)
            {
                // 🚨 LOG CRÍTICO: Error general en webhook (puede afectar dinero)
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: General webhook error",
                    details: $"General exception in webhook handler: {e.Message}",
                    source: "SubscriptionController.HandleStripeWebhook",
                    relatedEntityType: "Webhook",
                    additionalData: new { 
                        Action = "StripeWebhook",
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

        [HttpPost("webhook-general")]
        [AllowAnonymous]
        public async Task<IActionResult> HandleGeneralStripeWebhook()
        {
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
                        await _loggingService.LogWarningAsync(
                            message: "Stripe webhook API version mismatch",
                            details: $"Webhook event received with API version '{stripeEvent.ApiVersion}', but SDK expects '{expectedVersion}'. " +
                                    $"Consider updating the webhook endpoint in Stripe Dashboard to use API version '{expectedVersion}' for better compatibility.",
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
                // 🔒 IDEMPOTENCIA COMPLETA: Verificar si el evento ya fue procesado
                if (await IsEventProcessedAsync(stripeEvent.Id))
                {
                    return Ok(new { message = "Event already processed" });
                }

                await MarkEventAsProcessedAsync(stripeEvent.Id, stripeEvent.Type, stripeEvent.Account, null, "Processing");
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
                                return BadRequest(new { error = "PaymentIntentId is missing from session" });
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
                                if (pendingHire && int.TryParse(session.Metadata.GetValueOrDefault("serviceId", "0"), out int serviceId))
                                {
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
                                return BadRequest(new { error = "Invalid metadata format" });
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

                    case "invoice.payment_succeeded":
                    case "invoice.payment_failed":
                    case "customer.subscription.created":
                    case "customer.subscription.updated":
                    case "customer.subscription.deleted":
                        // ✅ IGNORAR: Suscripciones periódicas ya no se usan
                        break;

                    default:
                        break;
                }

                if (eventMarkedProcessing)
                {
                    await MarkEventAsProcessedAsync(stripeEvent.Id, stripeEvent.Type, stripeEvent.Account, null, "Success");
                    eventMarkedProcessing = false;
                }
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
                return BadRequest(new { error = e.Message });
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
            // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
            var user = await _context.Users
                .FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE")
                .FirstOrDefaultAsync();
            if (user == null)
            {
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }

            var service = await _context.SearchServices.FindAsync(serviceId);
            if (service == null)
            {
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }

            if (!metadata.TryGetValue("searchData", out var searchDataJson) || !metadata.TryGetValue("parameters", out var parametersJson))
            {
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }

            CreateSearchDto searchDto;
            CreateSearchParameterDto parameterDto;
            try
            {
                searchDto = JsonSerializer.Deserialize<CreateSearchDto>(searchDataJson);
                parameterDto = JsonSerializer.Deserialize<CreateSearchParameterDto>(parametersJson);
            }
            catch (JsonException ex)
            {
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

            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
            using var transaction = await _context.Database.BeginTransactionAsync();
            SearchHire? searchHire = null;
            try
            {
                    // ✅ REMOVED: Balance system eliminated - all payments are direct Stripe
                
                await _context.SaveChangesAsync();

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
                await _context.SaveChangesAsync();

                // Create platform associations
                if (parameterDto.PlatformIds != null && parameterDto.PlatformIds.Any())
                {
                    var platforms = await _context.Platforms
                        .Where(p => parameterDto.PlatformIds.Contains(p.Id))
                        .ToListAsync();
                    if (platforms.Count != parameterDto.PlatformIds.Count)
                    {
                        throw new Exception("Some platform IDs are invalid");
                    }
                    foreach (var platform in platforms)
                    {
                        _context.SearchParameterPlatforms.Add(new SearchParameterPlatform
                        {
                            SearchParameterId = searchParameter.SearchParameterId,
                            PlatformId = platform.Id
                        });
                    }
                }

                var expertProfile = await _context.ExpertProfiles
                       .FirstOrDefaultAsync(z => z.Id == service.ExpertProfileId);

                var expertuserid = expertProfile?.UserId ?? 0;

                // Validar que el experto no se contrate a sí mismo
                if (expertuserid == userId)
                {
                    throw new InvalidOperationException("No puedes contratarte a ti mismo como experto");
                }

                // Obtener la disponibilidad actual del experto al momento de la contratación
                int? currentAvailabilityId = null;
                if (expertProfile != null)
                {
                    var currentAvailability = await _context.ExpertAvailabilities
                        .Where(ea => ea.ExpertId == expertProfile.Id && ea.IsActive && ea.EffectiveTo == null)
                        .OrderByDescending(ea => ea.EffectiveFrom)
                        .FirstOrDefaultAsync();
                    currentAvailabilityId = currentAvailability?.Id;
                }

                // ✅ INTERNACIONALIZACIÓN: Obtener timezone y country del experto al momento de crear la contratación
                // Esto crea un snapshot que protege las contrataciones activas si el experto cambia de ubicación
                var expertTimezone = expertProfile?.Timezone ?? "UTC";
                var expertCountry = expertProfile?.Country;

                // ✅ STRIPE TAX: Obtener tax breakdown de la Checkout Session (NO PaymentIntent)
                // El tax breakdown está en la Session, no en el PaymentIntent
                decimal totalAmount = service.Price;
                decimal? taxAmount = null;
                decimal? baseAmount = null;
                
                try
                {
                    var sessionService = new SessionService();
                    var sessionGetOptions = new SessionGetOptions
                    {
                        Expand = new List<string> { "total_details.breakdown" } // Opcional pero recomendado para breakdown detallado
                    };
                    var sessionWithTax = await sessionService.GetAsync(session.Id, sessionGetOptions);
                    
                    if (sessionWithTax.AmountTotal.HasValue)
                    {
                        totalAmount = sessionWithTax.AmountTotal.Value / 100m; // Total pagado (en centavos, dividir por 100)
                        taxAmount = (sessionWithTax.TotalDetails?.AmountTax ?? 0) / 100m; // IVA (en centavos, dividir por 100)
                        baseAmount = totalAmount - taxAmount; // Base pre-tax
                        
                        // ✅ VALIDACIÓN: Si AutomaticTax no aplicó (ej. exención B2B), AmountTax será 0
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
                        }
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

                // Create search hire
                searchHire = new SearchHire
                {
                    ClientId = userId,
                    ExpertId = expertuserid,
                    SearchServiceId = service.Id,
                    SearchId = search.Id,
                        StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Pending.ToStringValue()),
                    Amount = totalAmount, // Total con IVA (€110)
                    BaseAmount = baseAmount, // Base sin IVA (€90.91) ✅ STRIPE TAX
                    TaxAmount = taxAmount, // IVA (€19.09) ✅ STRIPE TAX
                    CreatedAt = DateTime.UtcNow,
                    CompletionDeadline = DateTime.UtcNow.AddDays(7),
                    ExpertAvailabilityId = currentAvailabilityId, // Guardar la disponibilidad usada
                    ExpertTimezone = expertTimezone, // ✅ INTERNACIONALIZACIÓN: Snapshot del timezone del lugar de contratación
                    ExpertCountry = expertCountry // ✅ INTERNACIONALIZACIÓN: Snapshot del país del lugar de contratación
                };
                    // ✅ REMOVED: Balance verification eliminated - all payments are direct Stripe

                    // ✅ REMOVED: No restrictions on multiple service hires - users can contract the same service multiple times

                    // ✅ REMOVED: Balance deduction eliminated - all payments are direct Stripe
                
                _context.SearchHires.Add(searchHire);
                    await _context.SaveChangesAsync(); // ✅ SAVE FIRST to get the real ID

                var paymentTransaction = new FinancialTransaction
                {
                    UserId = userId,
                    Amount = -service.Price,
                    TransactionType = "ServicePayment",
                    RelatedEntityType = "SearchHire",
                        RelatedEntityId = searchHire.Id, // ✅ NOW searchHire.Id has the real ID
                        StripePaymentIntentId = session.PaymentIntentId, // ✅ ADDED: Track Stripe payment intent
                    CreatedAt = DateTime.UtcNow
                };
                _context.FinancialTransactions.Add(paymentTransaction);

                await _context.SaveChangesAsync();

                if (string.IsNullOrEmpty(session.PaymentIntentId))
                {
                    await LogPaymentCaptureFailureAsync(
                        paymentIntentId: "missing",
                        userId: userId,
                        serviceId: serviceId,
                        failureReason: "Stripe checkout session did not include a PaymentIntentId.",
                        searchHireId: searchHire.Id);
                    throw new InvalidOperationException("PaymentIntentId is missing from checkout session.");
                }

                await EnsurePaymentCapturedAsync(session.PaymentIntentId, userId, serviceId, searchHire.Id);

                await transaction.CommitAsync();

                // ✅ Crear automáticamente la cita en estado "awaiting_appointment" con timer de 24h
                // Esto asegura que el cliente tenga 24 horas para proponer una fecha/hora
                try
                {
                    // Verificar que no exista ya una cita (por si acaso)
                    var existingAppointment = await _context.Appointments
                        .FirstOrDefaultAsync(a => a.SearchHireId == searchHire.Id);
                    
                    if (existingAppointment == null)
                    {
                        // Obtener el estado "awaiting_appointment"
                        var awaitingStatus = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                      s.StatusValue == "awaiting_appointment");
                        
                        if (awaitingStatus != null)
                        {
                            var appointment = new Appointment
                            {
                                SearchHireId = searchHire.Id,
                                StatusId = awaitingStatus.Id,
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
                            var jobId = BackgroundJob.Schedule<IAppointmentService>(
                                service => service.ProcessAppointmentTimerAsync(proposalTimer.Id),
                                proposalTimer.EndTime - DateTime.UtcNow
                            );

                            // Guardar el JobId en el timer
                            proposalTimer.HangfireJobId = jobId;
                            await _context.SaveChangesAsync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 🚨 LOG CRÍTICO: Error al crear cita automática y timer inicial
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Failed to create automatic appointment and initial timer",
                        details: $"Error creating automatic appointment for SearchHire {searchHire.Id} in HandlePendingHireCompleted. " +
                                $"The SearchHire was confirmed but the Appointment/Timer flow failed. " +
                                $"Error: {ex.Message}. StackTrace: {ex.StackTrace}",
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id,
                        additionalData: new { 
                            Action = "CreateAutomaticAppointment",
                            SearchHireId = searchHire.Id,
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
                    details: $"Tu pago se procesó correctamente. La contratación #{searchHire.Id} está activa y el experto ha sido notificado.",
                    userId: userId,
                    source: "SubscriptionController.HandlePendingHireCompleted",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHire.Id,
                    notifyUser: true
                );

                // ✅ Enviar factura por email al cliente (en segundo plano con Hangfire)
                if (!string.IsNullOrEmpty(user.Email))
                {
                    Hangfire.BackgroundJob.Enqueue<IInvoiceService>(service => 
                        service.SendInvoiceByEmailBackgroundJob(searchHire.Id, user.Email));
                    Console.WriteLine($"[SUBSCRIPTION CONTROLLER] [INVOICE] Factura encolada para envío. SearchHireId: {searchHire.Id}, Email: {user.Email}");
                }

                // ✅ Notificar al experto sobre la nueva contratación
                if (expertuserid > 0)
                {
                    await _loggingService.LogInfoAsync(
                        message: "Nueva contratación recibida",
                        details: $"Has recibido una nueva contratación #{searchHire.Id} por {service.Price}€. Revisa los detalles y contacta con el cliente.",
                        userId: expertuserid,
                        source: "SubscriptionController.HandlePendingHireCompleted",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id,
                        notifyUser: true
                    );
                }

            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();

                if (!string.IsNullOrEmpty(session?.PaymentIntentId))
                {
                    await LogPaymentCaptureFailureAsync(
                        paymentIntentId: session.PaymentIntentId,
                        userId: userId,
                        serviceId: serviceId,
                        failureReason: ex.Message,
                        searchHireId: searchHire?.Id);

                    await _loggingService.LogWarningAsync(
                        message: "Intento de pago no capturado",
                        details: $"No pudimos completar el cobro del servicio {serviceId}. El cargo no se realizó y el cliente debe reintentar el pago.",
                        userId: userId,
                        source: "SubscriptionController.HandlePendingHireCompleted",
                        relatedEntityType: "Payment",
                        relatedEntityId: serviceId,
                        additionalData: new { PaymentIntentId = session.PaymentIntentId, SearchHireId = searchHire?.Id },
                        notifyUser: true
                    );
                }

                throw;
            }
            });
        }

        // ❌ ELIMINADO: ProcessAutomaticRefundOnError - No se usa (reemplazado por captura manual)

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
                    await paymentIntentService.CaptureAsync(paymentIntentId);
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

        private async Task LogPaymentCaptureFailureAsync(string paymentIntentId, int userId, int serviceId, string failureReason, int? searchHireId = null, Exception? exception = null)
        {
            await _loggingService.LogCriticalAsync(
                message: "CRITICAL: Payment capture failure",
                details: $"Failed to capture PaymentIntent {paymentIntentId}. Reason: {failureReason}",
                userId: null,
                source: "SubscriptionController.HandlePendingHireCompleted",
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
        private async Task LogCriticalRefundFailure(string paymentIntentId, int userId, int serviceId, Exception error)
        {
            // 💾 Registrar fallo crítico en base de datos para seguimiento
            var criticalError = new FinancialTransaction
            {
                UserId = userId,
                Amount = 0, // No hay monto en caso de error
                TransactionType = "CriticalRefundFailure",
                RelatedEntityType = "ErrorRecovery",
                RelatedEntityId = 0,
                StripePaymentIntentId = paymentIntentId,
                CreatedAt = DateTime.UtcNow
            };

            _context.FinancialTransactions.Add(criticalError);
            await _context.SaveChangesAsync();

            // 🚨 Registrar en sistema de logs con tipo específico
            await _loggingService.LogCriticalAsync(
                $"Critical refund failure - PaymentIntentId: {paymentIntentId}",
                error.Message,
                userId,
                "SubscriptionController.LogCriticalRefundFailure",
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
                var refund = await refundService.CreateAsync(refundOptions);
                // 💾 Registrar refund en base de datos
                var refundAmount = (decimal)refund.Amount / 100; // Convertir de céntimos a euros
                var refundTransaction = new FinancialTransaction
                {
                    UserId = userId,
                    Amount = refundAmount,
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

                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var user = await _context.Users
                    .FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE")
                    .Include(u => u.ExpertProfile)
                    .FirstOrDefaultAsync();
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

                // 💳 SIEMPRE PAGAR CON STRIPE - NO USAR SALDO INTERNO
                var domain = "https://inspecciono.com";
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
                                UnitAmount = checked((long)Math.Round(service.Price * 100)),
                                ProductData = new SessionLineItemPriceDataProductDataOptions
                                {
                                    Name = $"Payment for Service {service.Id}"
                                },
                                // ✅ STRIPE TAX: Configurar tax como inclusivo (el precio ya incluye IVA)
                                TaxBehavior = "inclusive" // Stripe hace reverse calc automático
                            },
                            Quantity = 1
                        }
                    },
                    // ✅ STRIPE TAX: Habilitar cálculo automático de tax basado en ubicación del comprador
                    AutomaticTax = new SessionAutomaticTaxOptions
                    {
                        Enabled = true // Habilita cálculo auto basado en IP, billing/shipping address
                    },
                    Mode = "payment",
                    SuccessUrl = $"{domain}/success?session_id={{CHECKOUT_SESSION_ID}}&userId={userId}",
                    CancelUrl = $"{domain}/cancel",
                    CustomerEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown@example.com",
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() },
                        { "serviceId", service.Id.ToString() },
                        { "amount", service.Price.ToString() },
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
                    session = await stripeService.CreateAsync(options);
                }
                catch (StripeException ex)
                {
                    return StatusCode(500, new { message = "Failed to create payment session" });
                }

                return Ok(new { url = session.Url });
            }
            catch (Exception ex)
            {
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

                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlInterpolated($"SELECT * FROM \"SearchHires\" WHERE \"Id\" = {request.SearchHireId} AND \"ExpertId\" = {userId} FOR UPDATE")
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Appointment)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                            .ThenInclude(st => st.ServiceTypeCategory)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    return NotFound(new { message = "Service not found or unauthorized" });
                }

                if (searchHire.Status.StatusValue != SearchHireStatus.Pending.ToStringValue())
                {
                    return BadRequest(new { message = "Service is not pending" });
                }

                // Verificar contador de cancelaciones del experto
                var appointment = searchHire.Appointment;
                if (appointment == null)
                {
                    return BadRequest(new { message = "No appointment found" });
                }

                // Determinar si es primera o segunda cancelación del experto
                string statusValue;
                if (appointment.ExpertCancellationCount >= 1)
                {
                    statusValue = "appointment_cancelled_by_expert_second";
                }
                else
                {
                    statusValue = "appointment_cancelled_by_expert";
                }

                // Obtener información del estado para verificar si es de finalización
                var cancelledStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && s.StatusValue == statusValue);

                if (cancelledStatus == null)
                {
                    return BadRequest(new { message = "Invalid cancellation status" });
                }

                // Solo procesar distribución de dinero si es estado de finalización
                bool refundSuccess = true;
                if (cancelledStatus.IsFinalizationStatus)
                {
                    // 💳 Orquestador central: refund/transfer según configuración
                    refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                        searchHire.Id,
                        statusValue,
                        $"Expert cancelled service {searchHire.Id}",
                        userId);
                }
                else
                {
                }

                if (!refundSuccess)
                {
                    // 🚨 Registrar fallo crítico de distribución
                    await _loggingService.LogCriticalAsync(
                        $"Failed to process money distribution - SearchHireId: {searchHire.Id}",
                        $"Money distribution failed for search hire",
                        searchHire.ClientId,
                        "SubscriptionController.CancelService",
                        "SearchHire",
                        searchHire.Id,
                        new { SearchHireId = searchHire.Id, ClientId = searchHire.ClientId, StatusValue = statusValue }
                    );
                    
                    return StatusCode(500, new { message = "Failed to process money distribution" });
                }

                // Actualizar estado del SearchHire a cancelado
                searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                    searchHire.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();

                    // 📝 LOGGING: Registrar acción de cancelación
                    await _loggingService.LogInfoAsync(
                        message: "CANCEL_SERVICE",
                        details: $"Canceló servicio {searchHire.Id} como experto con refund real de Stripe",
                        userId: userId,
                        source: "UserAction",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id
                    );
                return Ok(new { message = "Service cancelled and refunded via Stripe" });
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
                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlInterpolated($"SELECT * FROM \"SearchHires\" WHERE \"Id\" = {request.SearchHireId} FOR UPDATE")
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    return NotFound(new { message = "Service not found" });
                }
                if (request.ResolveInFavorOfClient)
                {
                    var success = await _refundService.ProcessMoneyDistributionAsync(
                        searchHire.Id,
                        "dispute_resolved_client",
                        "Force finalize in favor of client",
                        int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0"));

                    if (!success)
                    {
                        return StatusCode(500, new { message = "Failed to process client refund" });
                    }

                    searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.DisputeResolvedClient.ToStringValue());
                    await _loggingService.LogWarningAsync(
                        message: "FORCE_FINALIZE_CLIENT_REFUND",
                        details: $"Finalizó forzadamente servicio {searchHire.Id} a favor del cliente con orquestador",
                        userId: int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0"),
                        source: "AdminAction",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id
                    );

                    return Ok(new { message = "Service finalized successfully in favor of client" });
                }
                else
                {
                    return BadRequest(new { message = "Force finalize in favor of expert is no longer supported. Use dispute resolution instead." });
                }
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

                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlInterpolated($"SELECT * FROM \"SearchHires\" WHERE \"Id\" = {request.SearchHireId} FOR UPDATE")
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    return NotFound(new { message = "Service not found" });
                }

                if (searchHire.Status.StatusValue != SearchHireStatus.Disputed.ToStringValue())
                {
                    return BadRequest(new { message = "Service is not disputed" });
                }

                var dispute = await _context.Disputes
                    .FirstOrDefaultAsync(d => d.SearchHireId == searchHire.Id && d.Status == "Pending");

                if (dispute == null)
                {
                    return NotFound(new { message = "No pending dispute found" });
                }

                dispute.Status = "Resolved";
                dispute.ResolutionComments = request.Resolution;
                await _context.SaveChangesAsync();

                var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                var statusValue = request.ResolveInFavorOfClient
                    ? SearchHireStatus.DisputeResolvedClient.ToStringValue()
                    : SearchHireStatus.DisputeResolvedExpert.ToStringValue();

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

                    return StatusCode(500, new
                    {
                        message = errorMessage,
                        searchHireId = searchHire.Id,
                        status = statusValue,
                        logId = lastCriticalLog?.Id
                    });
                }

                if (request.ResolveInFavorOfClient)
                {
                    await _loggingService.LogWarningAsync(
                        message: "RESOLVE_DISPUTE_CLIENT_REFUND",
                        details: $"Resolvió disputa {searchHire.Id} a favor del cliente con orquestador: {request.Resolution}",
                        userId: adminUserId,
                        source: "AdminAction",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id
                    );
                }
                else
                {
                    await _loggingService.LogWarningAsync(
                        message: "RESOLVE_DISPUTE_EXPERT",
                        details: $"Resolvió disputa {searchHire.Id} a favor del experto con orquestador: {request.Resolution}",
                        userId: adminUserId,
                        source: "AdminAction",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id
                    );
                }

                return Ok(new { message = "Dispute resolved" });
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

        public class CreateSubscriptionDto
        {
            public int PlanId { get; set; }
            public bool IsYearly { get; set; }
        }

        public class SubscriptionPlanDto
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public decimal PriceMonthly { get; set; }
            public decimal PriceYearly { get; set; }
            public int MaxSearches { get; set; }
            public int MinSearchInterval { get; set; }
            public bool IsActive { get; set; }
        }

        public class SubscriptionDetailsDto
        {
            public bool IsYearly { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public string Status { get; set; }
            public decimal Price { get; set; }
            public string BillingPeriod { get; set; }
            public DateTime? NextBillingDate { get; set; }
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
            bool canRetry = stripeStatus == StripeStatus.NotRequested || 
                           (stripeStatus == StripeStatus.Pending && string.IsNullOrEmpty(pendingStripeAccountId));
            
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
        /// Verifica si un evento ya fue procesado para evitar duplicados
        /// </summary>
        private async Task<bool> IsEventProcessedAsync(string eventId)
        {
            try
            {
                var processingCutoff = DateTime.UtcNow.AddMinutes(-5);
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
            catch (Exception ex)
            {
                // No lanzar excepción para no interrumpir el flujo principal
            }
        }

        /// <summary>
        /// Genera un mensaje específico para cuentas rechazadas
        /// </summary>
        private string GetRejectionMessage(string? disabledReason, List<string> requirementErrorDetails)
        {
            if (string.IsNullOrEmpty(disabledReason))
            {
                return "❌ **Cuenta Rechazada**: Tu solicitud de cuenta de pagos fue rechazada por motivos no especificados. Por favor, contacta al soporte técnico para obtener más información y resolver esta situación.";
            }

            var baseMessage = "❌ **Cuenta Rechazada** - Tu solicitud de cuenta de pagos fue rechazada.\n\n";
            var reasonMessage = "";
            var solutionMessage = "";

            switch (disabledReason)
            {
                case "rejected.fraud":
                    reasonMessage = "🚨 **Motivo**: Se detectó actividad sospechosa o posible fraude en tu cuenta.";
                    solutionMessage = "**Solución**: Contacta inmediatamente al soporte de Stripe para resolver este problema. Puede ser necesario proporcionar documentación adicional para verificar tu identidad.";
                    break;

                case "rejected.terms_of_service":
                    reasonMessage = "📜 **Motivo**: No aceptaste los términos de servicio de Stripe o los violaste.";
                    solutionMessage = "**Solución**: Ve a tu cuenta de Stripe y acepta los términos de servicio. Si ya los aceptaste, revisa que no hayas violado ninguna política.";
                    break;

                case "rejected.unsupported_business":
                    reasonMessage = "🏢 **Motivo**: Tu tipo de negocio no está permitido en la plataforma de Stripe.";
                    solutionMessage = "**Solución**: Contacta al soporte de Stripe para verificar si tu negocio puede ser aceptado. Algunos tipos de negocio requieren aprobación especial.";
                    break;

                case "rejected.other":
                    reasonMessage = "⚠️ **Motivo**: Rechazo por otros motivos no especificados.";
                    solutionMessage = "**Solución**: Contacta al soporte de Stripe para obtener detalles específicos sobre el rechazo y los pasos para resolverlo.";
                    break;

                case "under_review":
                    reasonMessage = "🔍 **Motivo**: Tu cuenta está siendo revisada por el equipo de Stripe.";
                    solutionMessage = "**Solución**: Espera a que se complete la revisión (1-3 días hábiles). Te notificaremos cuando tengamos una decisión.";
                    break;

                case "listed":
                    reasonMessage = "🚫 **Motivo**: Tu cuenta está en una lista de sanciones o restricciones (ej. OFAC).";
                    solutionMessage = "**Solución**: Contacta al soporte de Stripe inmediatamente. Este tipo de restricciones requiere resolución directa con el equipo de cumplimiento.";
                    break;

                case "action_required.requested_capabilities":
                    reasonMessage = "⚡ **Motivo**: Se requiere acción adicional para activar las funcionalidades solicitadas.";
                    solutionMessage = "**Solución**: Ve a tu panel de Stripe y completa los pasos adicionales requeridos para activar las capacidades de tu cuenta.";
                    break;

                case "requirements.past_due":
                    reasonMessage = "⏰ **Motivo**: Hay requisitos vencidos que debías completar y no lo hiciste a tiempo.";
                    solutionMessage = "**Solución**: Ve a tu panel de Stripe y completa inmediatamente todos los requisitos pendientes. Los documentos deben estar actualizados y en alta calidad.";
                    break;

                case "requirements.pending_verification":
                    reasonMessage = "🔍 **Motivo**: Hay verificaciones pendientes que no se completaron correctamente.";
                    solutionMessage = "**Solución**: Revisa tu panel de Stripe y asegúrate de que todos los documentos estén correctamente subidos y sean legibles. Vuelve a enviar cualquier documento que haya sido rechazado.";
                    break;

                case "fields_needed":
                    reasonMessage = "📝 **Motivo**: Faltan campos de información que debes completar.";
                    solutionMessage = "**Solución**: Ve a tu panel de Stripe y completa todos los campos requeridos. Una vez completados, podrás reintentar la verificación.";
                    break;

                case "other":
                    reasonMessage = "⚠️ **Motivo**: Rechazo por motivos no especificados.";
                    solutionMessage = "**Solución**: Contacta al soporte de Stripe para obtener información específica sobre este rechazo y los pasos para resolverlo.";
                    break;

                default:
                    reasonMessage = $"⚠️ **Motivo**: {GetDisabledReasonDescription(disabledReason)}";
                    solutionMessage = "**Solución**: Contacta al soporte de Stripe para obtener información específica sobre este rechazo y los pasos para resolverlo.";
                    break;
            }

            var message = baseMessage + reasonMessage + "\n\n" + solutionMessage;

            // Agregar información sobre errores específicos si los hay
            if (requirementErrorDetails.Any())
            {
                message += "\n\n**Errores específicos encontrados:**\n";
                foreach (var error in requirementErrorDetails)
                {
                    message += $"• {GetErrorDescription(error)}\n";
                }
                message += "\n**Acción requerida**: Corrige estos errores específicos en tu cuenta de Stripe.";
            }

            message += "\n\n💡 **Consejo**: Una vez resuelto el problema, puedes volver a solicitar la verificación de tu cuenta.";

            return message;
        }

        /// <summary>
        /// Genera un mensaje específico para cuentas pendientes con problemas
        /// </summary>
        private string GetPendingMessage(Account account, List<string> requirementErrorDetails, bool allRequirementsMet, bool paymentsEnabled, bool detailsSubmitted, bool tosAccepted, bool notDisabled, bool noRequirementErrors, bool noPendingVerifications, bool noFutureIssues)
        {
            var issues = new List<string>();
            var solutions = new List<string>();

            if (!allRequirementsMet)
            {
                var missingRequirements = account.Requirements?.CurrentlyDue ?? new List<string>();
                if (missingRequirements.Any())
                {
                    var missingList = string.Join(", ", missingRequirements.Select(GetRequirementDescription));
                    issues.Add($"📋 **Documentos Faltantes**: {missingList}");
                    solutions.Add("Ve a tu panel de Stripe y sube los documentos requeridos en alta calidad");
                }
            }

            if (!paymentsEnabled)
            {
                if (!account.ChargesEnabled)
                {
                    issues.Add("💳 **Pagos Deshabilitados**: No puedes procesar pagos de clientes");
                    solutions.Add("Completa la verificación de identidad en tu cuenta de Stripe");
                }
                if (!account.PayoutsEnabled)
                {
                    issues.Add("💰 **Transferencias Deshabilitadas**: No puedes recibir pagos");
                    solutions.Add("Agrega y verifica una cuenta bancaria en tu perfil de Stripe");
                }
                if (account.Capabilities?.Transfers != "active")
                {
                    issues.Add("🔄 **Transferencias Inactivas**: Las transferencias no están disponibles");
                    solutions.Add("Espera a que Stripe active esta funcionalidad o contacta soporte");
                }
            }

            if (!detailsSubmitted)
            {
                issues.Add("📝 **Información Incompleta**: Los detalles de tu cuenta no han sido enviados");
                solutions.Add("Completa todos los campos requeridos en tu perfil de Stripe");
            }

            if (!tosAccepted)
            {
                issues.Add("📜 **Términos No Aceptados**: Debes aceptar los términos de servicio");
                solutions.Add("Ve a tu cuenta de Stripe y acepta los términos de servicio");
            }

            if (!notDisabled)
            {
                var disabledReason = account.Requirements?.DisabledReason ?? "desconocida";
                issues.Add($"🚫 **Cuenta Deshabilitada**: Razón: {GetDisabledReasonDescription(disabledReason)}");
                solutions.Add("Contacta al soporte de Stripe para resolver este problema");
            }

            if (!noRequirementErrors && requirementErrorDetails.Any())
            {
                var errorMessages = requirementErrorDetails.Select(GetErrorDescription).ToList();
                issues.Add($"⚠️ **Errores Detectados**: {string.Join(", ", errorMessages)}");
                solutions.Add("Revisa y corrige la información según los errores mostrados");
            }

            if (!noPendingVerifications)
            {
                issues.Add("🔍 **Verificaciones Pendientes**: Hay documentos en proceso de verificación");
                solutions.Add("Espera a que Stripe complete la verificación (1-3 días hábiles)");
            }

            if (!noFutureIssues)
            {
                issues.Add("⏰ **Requisitos Futuros**: Hay requisitos que deben cumplirse próximamente");
                solutions.Add("Revisa tu panel de Stripe para ver los requisitos pendientes");
            }

            if (issues.Any())
            {
                var message = "⏳ **Verificación Pendiente** - Tu cuenta de pagos necesita atención:\n\n";
                message += "**Problemas encontrados:**\n";
                foreach (var issue in issues)
                {
                    message += $"• {issue}\n";
                }
                
                message += "\n**Cómo solucionarlo:**\n";
                foreach (var solution in solutions)
                {
                    message += $"• {solution}\n";
                }
                
                message += "\n💡 **Consejo**: Accede a tu panel de Stripe para completar estos pasos. Una vez resuelto, podrás recibir pagos y ofrecer servicios.";
                
                return message;
            }

            return "✅ **Verificación en Proceso**: Tu cuenta está siendo revisada por Stripe. Te notificaremos cuando esté lista (1-3 días hábiles).";
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
        private string GetRequirementDescription(string requirement)
        {
            return requirement switch
            {
                "individual.verification.document" => "documento de identidad",
                "individual.address" => "dirección",
                "individual.phone" => "número de teléfono",
                "individual.dob" => "fecha de nacimiento",
                "individual.email" => "email",
                "company.verification.document" => "documentos de la empresa",
                "business_profile.support_address" => "dirección del negocio",
                "business_profile.url" => "sitio web del negocio",
                "business_profile.support_phone" => "teléfono de soporte",
                "business_profile.support_email" => "email de soporte",
                "tos_acceptance.date" => "aceptar términos de servicio",
                "external_account" => "información bancaria",
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
                // Aquí puedes agregar lógica específica para cuando un pago se completa exitosamente
                // Por ejemplo, actualizar el estado de una orden, enviar confirmación por email, etc.
                
                if (paymentIntent.Metadata != null && paymentIntent.Metadata.Count > 0)
                {
                }

                // Si tienes un sistema de órdenes, podrías actualizar el estado aquí
                // await UpdateOrderStatus(paymentIntent.Metadata["order_id"], "paid");
            }
            catch (Exception ex)
            {
            }
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
                    details: $"Payment intent {paymentIntent.Id} failed. Amount: {amount}€ {paymentIntent.Currency?.ToUpper()}. Error: {paymentIntent.LastPaymentError?.Message ?? "No error details"}, Code: {paymentIntent.LastPaymentError?.Code}, Type: {paymentIntent.LastPaymentError?.Type}, DeclineCode: {paymentIntent.LastPaymentError?.DeclineCode}",
                    userId: userId,
                    source: "SubscriptionController.HandlePaymentIntentFailed",
                    relatedEntityType: "Payment",
                    relatedEntityId: null,
                    additionalData: new { 
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
                
                if (paymentIntent.Metadata != null && paymentIntent.Metadata.Count > 0)
                {
                }

                // Si tienes un sistema de órdenes, podrías actualizar el estado aquí
                // await UpdateOrderStatus(paymentIntent.Metadata["order_id"], "payment_failed");
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
                var activeHires = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Where(sh => sh.ExpertId == expertId && 
                                sh.Status.StatusValue == "pending")
                    .CountAsync();
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
                
                // 3. Crear notificación para el experto
                await NotifyExpertOfAccountDeauthorization(expertId, deauthorizationReason, activeHires);
            }
            catch (Exception ex)
            {
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