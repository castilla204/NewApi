using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    public partial class SubscriptionController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<SubscriptionController> _logger;
        private readonly ISubscriptionService _subscriptionService;
        private readonly IConfiguration _configuration;
        private readonly StorageClient _storageClient;
        private readonly string? _webhookSecret;
        private readonly string? _generalWebhookSecret;
        private readonly IUserActionLoggingService _userActionLogging;
        private readonly SystemStatusService _systemStatusService;
        private readonly StripeRefundService _refundService;
        private readonly IAuthorizationServices _authService;
        private readonly ILoggingService _loggingService;
        private readonly IStripeValidationService _stripeValidationService;

        public SubscriptionController(AppDbContext context, ILogger<SubscriptionController> logger, IConfiguration configuration, ISubscriptionService subscriptionService, StorageClient storageClient, IUserActionLoggingService userActionLogging, SystemStatusService systemStatusService, IAuthorizationServices authService, ILoggingService loggingService, StripeRefundService refundService, IStripeValidationService stripeValidationService)
        {
            _logger = logger;
            _logger.LogInformation("Initializing SubscriptionController");
            _context = context;
            _userActionLogging = userActionLogging;
            _systemStatusService = systemStatusService;
            _subscriptionService = subscriptionService;
            _configuration = configuration;
            _authService = authService;
            _storageClient = storageClient;
            _loggingService = loggingService;
            _refundService = refundService;
            _stripeValidationService = stripeValidationService;
            _webhookSecret = _configuration["Stripe:WebhookSecret"];
            _generalWebhookSecret = _configuration["Stripe:GeneralWebhookSecret"];
            StripeConfiguration.ApiKey = _configuration["Stripe:SecretKey"];
            _logger.LogInformation("Stripe API Key and Webhook Secrets configured");
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
                _logger.LogWarning("SystemStatus not found for StatusValue: {StatusValue}", statusValue);
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
                StripeStatus.Pending => "⏳ **Verificación en Proceso**: Tu cuenta de pagos está siendo revisada por Stripe. Este proceso puede tomar entre 1-3 días hábiles. Te notificaremos cuando esté lista. Mientras tanto, puedes preparar tus servicios.",
                StripeStatus.Approved => "✅ **Cuenta Aprobada**: ¡Excelente! Tu cuenta de pagos está completamente verificada y lista para recibir pagos. Ya puedes crear servicios y comenzar a ganar dinero.",
                StripeStatus.Rejected => "❌ **Cuenta Rechazada**: Tu solicitud de cuenta de pagos fue rechazada. Esto puede deberse a información incompleta o incorrecta. Revisa los detalles específicos y vuelve a intentar con información actualizada.",
                StripeStatus.Deauthorized => "🚫 **Cuenta Desautorizada**: Tu cuenta de pagos ha sido desautorizada. Esto puede ocurrir por violaciones de términos o problemas de seguridad. Contacta al soporte técnico para resolver esta situación.",
                _ => "❓ **Estado Desconocido**: No se pudo determinar el estado de tu cuenta de pagos. Por favor, contacta al soporte técnico para obtener ayuda."
            };
        }

        [HttpPost("cancel")]
        public async Task<IActionResult> CancelSubscription()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                _logger.LogInformation("Processing cancellation request for user {UserId}", userId);

                var subscription = await _context.UserSubscriptions
                    .Include(us => us.User)
                    .FirstOrDefaultAsync(us => us.UserId == userId && us.Status == "active");

                if (subscription == null)
                {
                    _logger.LogInformation("No active subscription found for user {UserId}", userId);
                    return NotFound(new { message = "No active subscription found" });
                }

                if (subscription.Status == "pending_cancellation")
                {
                    _logger.LogInformation("Subscription already pending cancellation for user {UserId}", userId);
                    return Ok(new
                    {
                        message = "Subscription is already set to cancel at the end of the billing period",
                        status = subscription.Status,
                        endDate = subscription.EndDate
                    });
                }

                if (!string.IsNullOrEmpty(subscription.StripeSubscriptionId))
                {
                    var stripeService = new SubscriptionService();
                    try
                    {
                        var updateOptions = new SubscriptionUpdateOptions
                        {
                            CancelAtPeriodEnd = true
                        };
                        var updatedSubscription = await stripeService.UpdateAsync(subscription.StripeSubscriptionId, updateOptions);
                        _logger.LogInformation("Subscription {StripeSubscriptionId} marked to cancel at period end for user {UserId}", subscription.StripeSubscriptionId, userId);

                        subscription.Status = "pending_cancellation";
                        subscription.UpdatedAt = DateTime.UtcNow;
                    }
                    catch (StripeException ex)
                    {
                        _logger.LogError(ex, "Stripe error marking subscription {StripeSubscriptionId} for cancellation: {StripeError}", subscription.StripeSubscriptionId, ex.StripeError?.Message);
                        return BadRequest(new { message = "Failed to process cancellation in Stripe" });
                    }
                }

                var strategy = _context.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Database error saving cancellation for user {UserId}", userId);
                    throw;
                }
                });

                _logger.LogInformation("Subscription cancellation scheduled successfully for user {UserId}, endDate={EndDate}", userId, subscription.EndDate);
                return Ok(new
                {
                    message = "Subscription will cancel at the end of the billing period",
                    status = subscription.Status,
                    endDate = subscription.EndDate
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing cancellation for user {UserId}");
                return StatusCode(500, new { message = "Failed to process subscription cancellation" });
            }
        }

        [HttpPost("create-checkout-session")]
        public async Task<IActionResult> CreateCheckoutSession([FromBody] CreateSubscriptionDto request)
        {
            // 🚨 VALIDACIÓN DE ENTRADA
            if (request == null)
            {
                _logger.LogError("Request is null");
                return BadRequest(new { message = "Request cannot be null" });
            }

            if (request.PlanId <= 0)
            {
                _logger.LogError("Invalid PlanId: {PlanId}", request.PlanId);
                return BadRequest(new { message = "Invalid plan ID" });
            }

            _logger.LogInformation("CreateCheckoutSession endpoint invoked with request: {Request}", JsonSerializer.Serialize(request));

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                _logger.LogInformation("Authenticated user: userId={UserId}", userId);

                var plan = await _context.SubscriptionPlans.FindAsync(request.PlanId);
                if (plan == null)
                {
                    _logger.LogError("Subscription plan not found for planId={PlanId}", request.PlanId);
                    return NotFound(new { message = "Subscription plan not found" });
                }

                _logger.LogInformation("Found subscription plan: planId={PlanId}, name={PlanName}", plan.Id, plan.Name);

                var priceId = request.IsYearly ? plan.StripePriceIdYearly : plan.StripePriceIdMonthly;
                var domain = "https://atrapo.io";

                if (string.IsNullOrEmpty(priceId))
                {
                    _logger.LogError("Stripe price ID not configured for planId={PlanId}, isYearly={IsYearly}", plan.Id, request.IsYearly);
                    return BadRequest(new { message = "Stripe price ID not configured for this plan" });
                }

                _logger.LogInformation("Using Stripe priceId={PriceId} for planId={PlanId}, isYearly={IsYearly}", priceId, plan.Id, request.IsYearly);

                var options = new SessionCreateOptions
                {
                    LineItems = new List<SessionLineItemOptions>
                    {
                        new SessionLineItemOptions
                        {
                            Price = priceId,
                            Quantity = 1,
                        },
                    },
                    Mode = "subscription",
                    SuccessUrl = domain + "/success",
                    CancelUrl = domain + "/cancel",
                    CustomerEmail = User.FindFirst(ClaimTypes.Email)?.Value,
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() },
                        { "planId", plan.Id.ToString() },
                        { "isYearly", request.IsYearly.ToString() }
                    }
                };

                _logger.LogInformation("Creating Stripe Checkout session for userId={UserId}, planId={PlanId}, isYearly={IsYearly}", userId, plan.Id, request.IsYearly);

                var service = new SessionService();
                Session session;
                try
                {
                    session = await service.CreateAsync(options);
                    _logger.LogInformation("Stripe Checkout session created successfully: sessionId={SessionId}, url={SessionUrl}", session.Id, session.Url);
                }
                catch (StripeException e)
                {
                    _logger.LogError(e, "Stripe error creating checkout session: {ErrorMessage}", e.Message);
                    return StatusCode(500, new { message = e.Message });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating checkout session: {ErrorMessage}", ex.Message);
                    return StatusCode(500, new { message = "Failed to create checkout session" });
                }

                return Ok(new { url = session.Url });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating checkout session: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpGet("plans")]
        public async Task<IActionResult> GetSubscriptionPlans()
        {
            _logger.LogInformation("GetSubscriptionPlans endpoint invoked");

            try
            {
                var plans = await _context.SubscriptionPlans
                    .Where(p => p.IsActive)
                    .Select(p => new SubscriptionPlanDto
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Description = p.Description,
                        PriceMonthly = p.PriceMonthly,
                        PriceYearly = p.PriceYearly,
                        MaxSearches = p.MaxSearches,
                        MinSearchInterval = p.MinSearchInterval,
                        IsActive = p.IsActive
                    })
                    .ToListAsync();

                _logger.LogInformation("Retrieved {PlanCount} active subscription plans", plans.Count);
                return Ok(plans);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving subscription plans: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to retrieve subscription plans" });
            }
        }

        [HttpGet("details")]
        public async Task<IActionResult> GetSubscriptionDetails()
        {
            _logger.LogInformation("GetSubscriptionDetails endpoint invoked");

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                _logger.LogInformation("Authenticated user: userId={UserId}", userId);

                var subscription = await _context.UserSubscriptions
                    .Include(us => us.SubscriptionPlan)
                    .Where(us => us.UserId == userId && us.Status == "active")
                    .OrderByDescending(us => us.CreatedAt)
                    .FirstOrDefaultAsync();

                if (subscription == null)
                {
                    _logger.LogInformation("No active subscription found for userId={UserId}", userId);
                    return Ok(new SubscriptionDetailsDto
                    {
                        IsYearly = false,
                        Status = "none",
                        BillingPeriod = "none"
                    });
                }

                _logger.LogInformation("Found active subscription for userId={UserId}: subscriptionId={SubscriptionId}", userId, subscription.Id);

                return Ok(new SubscriptionDetailsDto
                {
                    IsYearly = subscription.IsYearly,
                    StartDate = subscription.StartDate,
                    EndDate = subscription.EndDate,
                    Status = subscription.Status,
                    Price = subscription.IsYearly ? subscription.SubscriptionPlan.PriceYearly : subscription.SubscriptionPlan.PriceMonthly,
                    BillingPeriod = subscription.IsYearly ? "yearly" : "monthly",
                    NextBillingDate = subscription.EndDate
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving subscription details: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to retrieve subscription details" });
            }
        }

        [HttpGet("current")]
        public async Task<IActionResult> GetCurrentSubscription()
        {
            _logger.LogInformation("GetCurrentSubscription endpoint invoked");

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                _logger.LogInformation("Authenticated user: userId={UserId}", userId);

                var user = await _context.Users
                    .Include(u => u.SubscriptionPlan)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user == null)
                {
                    _logger.LogError("User not found for userId={UserId}", userId);
                    return NotFound(new { message = "User not found" });
                }

                _logger.LogInformation("Found user: userId={UserId}, email={Email}", user.Id, user.Email);

                if (user.SubscriptionPlan == null)
                {
                    _logger.LogInformation("No subscription plan assigned to userId={UserId}, retrieving free plan", userId);

                    var freePlan = await _context.SubscriptionPlans
                        .FirstOrDefaultAsync(p => p.PriceMonthly == 0);

                    if (freePlan == null)
                    {
                        _logger.LogError("No free plan found for userId={UserId}", userId);
                        return NotFound(new { message = "No free plan available" });
                    }

                    _logger.LogInformation("Returning free plan for userId={UserId}: planId={PlanId}", userId, freePlan.Id);

                    return Ok(new SubscriptionPlanDto
                    {
                        Id = freePlan.Id,
                        Name = freePlan.Name,
                        Description = freePlan.Description,
                        PriceMonthly = freePlan.PriceMonthly,
                        PriceYearly = freePlan.PriceYearly,
                        MaxSearches = freePlan.MaxSearches,
                        MinSearchInterval = freePlan.MinSearchInterval,
                        IsActive = freePlan.IsActive
                    });
                }

                _logger.LogInformation("Returning current subscription plan for userId={UserId}: planId={PlanId}", userId, user.SubscriptionPlan.Id);

                return Ok(new SubscriptionPlanDto
                {
                    Id = user.SubscriptionPlan.Id,
                    Name = user.SubscriptionPlan.Name,
                    Description = user.SubscriptionPlan.Description,
                    PriceMonthly = user.SubscriptionPlan.PriceMonthly,
                    PriceYearly = user.SubscriptionPlan.PriceYearly,
                    MaxSearches = user.SubscriptionPlan.MaxSearches,
                    MinSearchInterval = user.SubscriptionPlan.MinSearchInterval,
                    IsActive = user.SubscriptionPlan.IsActive
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving current subscription: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to retrieve current subscription" });
            }
        }

        [HttpPost("expert-onboarding")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> CreateExpertOnboarding()
        {
            _logger.LogInformation("CreateExpertOnboarding endpoint invoked");

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    _logger.LogError("Expert profile not found for userId={UserId}", userId);
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
                            _logger.LogWarning(ex, "Could not retrieve rejection reason from Stripe for userId={UserId}: {ErrorMessage}", userId, ex.Message);
                        }
                    }
                    
                    // Si no se pudo obtener de Stripe, intentar extraer del StripeStatusDetails
                    if (string.IsNullOrEmpty(disabledReason) && !string.IsNullOrEmpty(expertProfile.StripeStatusDetails))
                    {
                        disabledReason = ExtractRejectionReasonFromDetails(expertProfile.StripeStatusDetails);
                        if (!string.IsNullOrEmpty(disabledReason))
                        {
                            _logger.LogInformation("✅ Extracted rejection reason from StripeStatusDetails in restart-onboarding for userId={UserId}: {Reason}", userId, disabledReason);
                        }
                        else
                        {
                            _logger.LogWarning("⚠️ Could not extract rejection reason from StripeStatusDetails for userId={UserId}. Details: {Details}", userId, expertProfile.StripeStatusDetails);
                        }
                    }
                    else if (string.IsNullOrEmpty(disabledReason))
                    {
                        _logger.LogWarning("⚠️ No rejection reason available (neither from Stripe nor from StripeStatusDetails) for userId={UserId}", userId);
                    }
                    
                    // Si es un rechazo permanente, bloquear
                    _logger.LogInformation("🔍 Checking if rejection is permanent for userId={UserId}, disabledReason={Reason}, isPermanent={IsPermanent}", 
                        userId, disabledReason ?? "null", IsPermanentRejection(disabledReason));
                    if (IsPermanentRejection(disabledReason))
                    {
                        _logger.LogWarning("🚫 BLOCKED: Cannot create onboarding link for permanently rejected account - userId={UserId}, accountId={AccountId}, reason={Reason}", 
                            userId, expertProfile.StripeAccountId, disabledReason);
                        
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
                        _logger.LogInformation("✅ ALLOWED: Temporary rejection allows retry - userId={UserId}, reason={Reason}, cleaning up account", userId, disabledReason);
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
                    _logger.LogInformation("Expert already has a Stripe account: userId={UserId}, stripeAccountId={StripeAccountId}", userId, expertProfile.StripeAccountId);
                    
                    // Clean up PendingStripeAccountId if it exists (shouldn't happen but just in case)
                    if (!string.IsNullOrEmpty(expertProfile.PendingStripeAccountId))
                    {
                        _logger.LogInformation("Clearing stale PendingStripeAccountId for expert with completed account: userId={UserId}", userId);
                        expertProfile.PendingStripeAccountId = null;
                        expertProfile.OnboardingCompleted = true;
                        await _context.SaveChangesAsync();
                    }
                    
                    // If expert already has a completed Stripe account, create a login link instead
                    var linkOptions = new AccountLinkCreateOptions
                    {
                        Account = expertProfile.StripeAccountId,
                        RefreshUrl = "https://atrapo.io/refresh-onboarding",
                        ReturnUrl = "https://atrapo.io/complete-onboarding",
                        Type = "account_onboarding"
                    };
                    
                    var linkService = new AccountLinkService();
                    
                    try
                    {
                        var accountLink = await linkService.CreateAsync(linkOptions);
                        _logger.LogInformation("Stripe account link created for existing account: userId={UserId}, url={Url}", userId, accountLink.Url);
                        return Ok(new { url = accountLink.Url, isLoginLink = true });
                    }
                    catch (StripeException ex)
                    {
                        _logger.LogError(ex, "Stripe error creating account link for userId={UserId}: {ErrorMessage}", userId, ex.Message);
                        return StatusCode(500, new { message = "Failed to create Stripe account link" });
                    }
                }

                if (!string.IsNullOrEmpty(expertProfile.PendingStripeAccountId))
                {
                    _logger.LogInformation("Expert already has a pending Stripe account: userId={UserId}, pendingStripeAccountId={PendingStripeAccountId}", userId, expertProfile.PendingStripeAccountId);
                    
                    // Si tiene cuenta pendiente pero no completó onboarding, crear nuevo link para continuar
                    var linkOptions = new AccountLinkCreateOptions
                    {
                        Account = expertProfile.PendingStripeAccountId,
                        RefreshUrl = "https://atrapo.io/refresh-onboarding",
                        ReturnUrl = "https://atrapo.io/complete-onboarding",
                        Type = "account_onboarding",
                        Collect = "eventually_due"
                    };
                    
                    var linkService = new AccountLinkService();
                    
                    try
                    {
                        var accountLink = await linkService.CreateAsync(linkOptions);
                        _logger.LogInformation("Onboarding link created for pending account: userId={UserId}, url={Url}", userId, accountLink.Url);
                        return Ok(new { url = accountLink.Url, isLoginLink = false });
                    }
                    catch (StripeException ex)
                    {
                        _logger.LogError(ex, "Stripe error creating onboarding link for pending account userId={UserId}: {ErrorMessage}", userId, ex.Message);
                        return StatusCode(500, new { message = "Failed to create onboarding link" });
                    }
                }

                // Limpiar cualquier PendingStripeAccountId anterior antes de crear nueva cuenta
                if (!string.IsNullOrEmpty(expertProfile.PendingStripeAccountId))
                {
                    _logger.LogWarning("Clearing existing PendingStripeAccountId before creating new account: userId={UserId}, oldPendingId={OldPendingId}", 
                        userId, expertProfile.PendingStripeAccountId);
                    expertProfile.PendingStripeAccountId = null;
                }

                // Marcar como pendiente antes de crear la cuenta
                expertProfile.StripeStatus = StripeStatus.Pending;
                await _context.SaveChangesAsync();
                _logger.LogInformation("Set StripeStatus to Pending for new account creation: userId={UserId}", userId);

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
                    _logger.LogInformation("Stripe account created for userId={UserId}, accountId={AccountId}", userId, account.Id);
                }
                catch (StripeException ex)
                {
                    _logger.LogError(ex, "Stripe error creating account for userId={UserId}: {ErrorMessage}", userId, ex.Message);
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
                        
                        _logger.LogInformation("Attempting to save Stripe account: userId={UserId}, accountId={AccountId}, pendingAccountId={PendingAccountId}", 
                            userId, expertProfile.StripeAccountId, account.Id);
                        
                    await _context.SaveChangesAsync();
                        _logger.LogInformation("Successfully saved Stripe account to database: userId={UserId}", userId);

                    var linkOptions = new AccountLinkCreateOptions
                    {
                        Account = account.Id,
                        RefreshUrl = "https://atrapo.io/refresh-onboarding",
                        ReturnUrl = "https://atrapo.io/complete-onboarding",
                        Type = "account_onboarding",
                        Collect = "eventually_due"
                    };

                    var linkService = new AccountLinkService();
                    AccountLink accountLink;
                    try
                    {
                        accountLink = await linkService.CreateAsync(linkOptions);
                        _logger.LogInformation("Onboarding link created for userId={UserId}, url={Url}", userId, accountLink.Url);
                    }
                    catch (StripeException ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Stripe error creating onboarding link for userId={UserId}: {ErrorMessage}", userId, ex.Message);
                        return StatusCode(500, new { message = "Failed to create onboarding link" });
                    }

                    await transaction.CommitAsync();
                    return Ok(new { url = accountLink.Url });
                }
                    catch (DbUpdateException dbEx)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(dbEx, "Database error saving Stripe account for userId={UserId}: {ErrorMessage}, InnerException={InnerException}", 
                            userId, dbEx.Message, dbEx.InnerException?.Message);
                        return StatusCode(500, new { message = "Failed to save Stripe account", details = dbEx.InnerException?.Message ?? dbEx.Message });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                        _logger.LogError(ex, "Unexpected error saving Stripe account for userId={UserId}: {ErrorMessage}, StackTrace={StackTrace}", 
                            userId, ex.Message, ex.StackTrace);
                        return StatusCode(500, new { message = "Failed to save Stripe account", details = ex.Message });
                }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating expert onboarding: {ErrorMessage}", ex.Message);
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
            _logger.LogInformation("CreateAccountLink endpoint invoked");

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    _logger.LogError("Expert profile not found for userId={UserId}", userId);
                    return NotFound(new { message = "Expert profile not found" });
                }

                if (string.IsNullOrEmpty(expertProfile.StripeAccountId))
                {
                    _logger.LogError("Stripe account ID not found for expert userId={UserId}", userId);
                    return BadRequest(new { message = "Stripe account not found. Please complete onboarding first." });
                }

                if (expertProfile.StripeStatus == StripeStatus.Rejected)
                {
                    _logger.LogWarning("Cannot create account link for rejected account: userId={UserId}, accountId={AccountId}", userId, expertProfile.StripeAccountId);
                    return BadRequest(new { message = "La cuenta de pagos fue rechazada por Stripe. No se puede abrir el panel. Reinicia el onboarding para crear una cuenta nueva." });
                }

                // Crear un enlace de cuenta de Stripe Connect para actualizar datos bancarios
                var accountLinkService = new Stripe.AccountLinkService();
                var accountLinkOptions = new Stripe.AccountLinkCreateOptions
                {
                    Account = expertProfile.StripeAccountId,
                    RefreshUrl = "https://atrapo.io/expert-panel?refresh=true", // URL si necesita refrescar
                    ReturnUrl = "https://atrapo.io/expert-panel", // URL de retorno después de actualizar datos
                    Type = "account_onboarding" // Tipo de enlace para completar/actualizar información de la cuenta
                };

                var accountLink = await accountLinkService.CreateAsync(accountLinkOptions);

                _logger.LogInformation("Account link created successfully for expert userId={UserId}, accountLinkUrl={AccountLinkUrl}", 
                    userId, accountLink.Url);

                return Ok(new { 
                    message = "Enlace de cuenta creado exitosamente",
                    accountLinkUrl = accountLink.Url 
                });
            }
            catch (StripeException stripeEx)
            {
                _logger.LogError(stripeEx, "Stripe error creating account link: {StripeError}", stripeEx.Message);
                return StatusCode(500, new { message = "Error de Stripe al crear el enlace de cuenta", error = stripeEx.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating account link: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Error interno del servidor" });
            }
        }

        [HttpGet("onboarding-status")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> GetOnboardingStatus()
        {
            _logger.LogInformation("GetOnboardingStatus endpoint invoked");

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    _logger.LogError("Expert profile not found for userId={UserId}", userId);
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
                    CanAccessStripe =
                        (expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted)
                        || ((expertProfile.StripeStatus == StripeStatus.Deauthorized || expertProfile.StripeStatus == StripeStatus.Rejected) && hasActiveHires)
                };

                _logger.LogInformation("Onboarding status for userId={UserId}: {Status}", userId, System.Text.Json.JsonSerializer.Serialize(status));
                return Ok(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting onboarding status: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to get onboarding status" });
            }
        }

        [HttpGet("expert-status")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> GetExpertStatus()
        {
            _logger.LogInformation("GetExpertStatus endpoint invoked");

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    _logger.LogError("Expert profile not found for userId={UserId}", userId);
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
                            _logger.LogWarning(ex, "Could not retrieve rejection reason from Stripe for userId={UserId}: {ErrorMessage}", userId, ex.Message);
                        }
                    }
                    
                    // Si no se pudo obtener de Stripe, intentar extraer del StripeStatusDetails
                    if (string.IsNullOrEmpty(rejectionReason) && !string.IsNullOrEmpty(expertProfile.StripeStatusDetails))
                    {
                        rejectionReason = ExtractRejectionReasonFromDetails(expertProfile.StripeStatusDetails);
                        if (!string.IsNullOrEmpty(rejectionReason))
                        {
                            _logger.LogInformation("Extracted rejection reason from StripeStatusDetails for userId={UserId}: {Reason}", userId, rejectionReason);
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
                    CanAccessStripe = (expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted)
                        || ((expertProfile.StripeStatus == StripeStatus.Deauthorized || expertProfile.StripeStatus == StripeStatus.Rejected) && hasActiveHires),
                    // Solo permitir creación/cobro cuando realmente está aprobado
                    CanCreateServices = expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted,
                    CanReceivePayments = expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted,
                    StatusMessage = GetStatusMessage(expertProfile.StripeStatus),
                    // Permitir reintentar si:
                    // - No ha solicitado cuenta (NotRequested)
                    // - Está Pending sin cuenta pendiente
                    // - Está Rejected PERO es un rechazo temporal (requirements.past_due, etc.)
                    CanRetryOnboarding = CalculateCanRetryOnboarding(expertProfile.StripeStatus, expertProfile.PendingStripeAccountId, rejectionReason),
                    RejectionReason = rejectionReason
                };

                _logger.LogInformation("Expert status for userId={UserId}: {Status}", userId, System.Text.Json.JsonSerializer.Serialize(status));
                return Ok(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting expert status: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to get expert status" });
            }
        }



        [HttpPost("sync-stripe-status")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> SyncStripeStatus()
        {
            _logger.LogInformation("SyncStripeStatus endpoint invoked");

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    _logger.LogError("Expert profile not found for userId={UserId}", userId);
                    return NotFound(new { message = "Expert profile not found" });
                }

                if (string.IsNullOrEmpty(expertProfile.StripeAccountId))
                {
                    _logger.LogInformation("Expert has no Stripe account to sync: userId={UserId}", userId);
                    return BadRequest(new { message = "No Stripe account found to sync" });
                }

                // Verificar el estado actual en Stripe
                var accountService = new AccountService();
                Account account;
                try
                {
                    account = await accountService.GetAsync(expertProfile.StripeAccountId);
                    _logger.LogInformation("Retrieved Stripe account status for userId={UserId}, accountId={AccountId}", userId, account.Id);
                }
                catch (StripeException ex)
                {
                    _logger.LogError(ex, "Stripe error retrieving account for userId={UserId}: {ErrorMessage}", userId, ex.Message);
                    return StatusCode(500, new { message = "Failed to retrieve Stripe account status" });
                }

                // Actualizar el estado basado en la información de Stripe usando la lógica correcta
                bool isAccountApproved = account.Requirements?.CurrentlyDue?.Count == 0;
                bool canReceivePayments = account.ChargesEnabled && account.PayoutsEnabled;
                bool onboardingCompleted = isAccountApproved && canReceivePayments;

                // Verificar si la cuenta ha sido rechazada o desactivada
                string disabledReason = account.Requirements?.DisabledReason;
                bool isAccountRejected = !string.IsNullOrEmpty(disabledReason) && disabledReason.StartsWith("rejected");
                bool isAccountDisabled = !account.ChargesEnabled || !account.PayoutsEnabled;

                _logger.LogInformation("🔍 DEBUG: Syncing Stripe account status for userId={UserId}, isApproved={IsApproved}, canReceivePayments={CanReceivePayments}, disabledReason={DisabledReason}, isRejected={IsRejected}",
                    userId, isAccountApproved, canReceivePayments, disabledReason, isAccountRejected);

                // Actualizar el StripeStatus basado en el estado real
                // PRIORIDAD 1: Verificar si la cuenta está aprobada y puede recibir pagos
                if (onboardingCompleted)
                {
                    var previousStatus = expertProfile.StripeStatus;
                    expertProfile.StripeStatus = StripeStatus.Approved;
                    expertProfile.OnboardingCompleted = true;
                    _logger.LogInformation("✅ DEBUG: Account approved and ready for payments for userId={UserId}, previousStatus={PreviousStatus}", userId, previousStatus);
                }
                // PRIORIDAD 2: Verificar si la cuenta ha sido rechazada o desactivada
                else if (isAccountRejected || (isAccountDisabled && !string.IsNullOrEmpty(disabledReason)))
                {
                    // La cuenta ha sido rechazada por Stripe
                    var previousStatus = expertProfile.StripeStatus;
                    expertProfile.StripeStatus = StripeStatus.Rejected;
                    expertProfile.OnboardingCompleted = false;
                    _logger.LogWarning("❌ DEBUG: Account rejected by Stripe for userId={UserId}, reason={DisabledReason}, previousStatus={PreviousStatus}", userId, disabledReason, previousStatus);
                }
                // PRIORIDAD 3: La cuenta aún está pendiente de verificación
                else if (!isAccountApproved)
                {
                    var previousStatus = expertProfile.StripeStatus;
                    expertProfile.StripeStatus = StripeStatus.Pending;
                    expertProfile.OnboardingCompleted = false;
                    _logger.LogInformation("⏳ DEBUG: Account still pending verification for userId={UserId}, previousStatus={PreviousStatus}", userId, previousStatus);
                }
                else
                {
                    // La cuenta está aprobada pero no puede recibir pagos (caso raro)
                    var previousStatus = expertProfile.StripeStatus;
                    expertProfile.StripeStatus = StripeStatus.Pending;
                    expertProfile.OnboardingCompleted = false;
                    _logger.LogWarning("⚠️ DEBUG: Account approved but cannot receive payments for userId={UserId}, previousStatus={PreviousStatus}", userId, previousStatus);
                }
                
                // Limpiar PendingStripeAccountId si existe
                if (!string.IsNullOrEmpty(expertProfile.PendingStripeAccountId))
                {
                    _logger.LogInformation("Clearing PendingStripeAccountId for synced account: userId={UserId}", userId);
                    expertProfile.PendingStripeAccountId = null;
                }

                await _context.SaveChangesAsync();

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

                _logger.LogInformation("Stripe status synced for userId={UserId}: {Status}", userId, System.Text.Json.JsonSerializer.Serialize(status));
                return Ok(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing Stripe status: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to sync Stripe status" });
            }
        }

        [HttpPost("restart-onboarding")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> RestartOnboarding()
        {
            _logger.LogInformation("RestartOnboarding endpoint invoked");

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    _logger.LogError("Expert profile not found for userId={UserId}", userId);
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
                            _logger.LogWarning(ex, "Could not retrieve rejection reason from Stripe for userId={UserId}: {ErrorMessage}", userId, ex.Message);
                        }
                    }
                    
                    // Si no se pudo obtener de Stripe, intentar extraer del StripeStatusDetails
                    if (string.IsNullOrEmpty(disabledReason) && !string.IsNullOrEmpty(expertProfile.StripeStatusDetails))
                    {
                        disabledReason = ExtractRejectionReasonFromDetails(expertProfile.StripeStatusDetails);
                        if (!string.IsNullOrEmpty(disabledReason))
                        {
                            _logger.LogInformation("✅ Extracted rejection reason from StripeStatusDetails in restart-onboarding for userId={UserId}: {Reason}", userId, disabledReason);
                        }
                        else
                        {
                            _logger.LogWarning("⚠️ Could not extract rejection reason from StripeStatusDetails for userId={UserId}. Details: {Details}", userId, expertProfile.StripeStatusDetails);
                        }
                    }
                    else if (string.IsNullOrEmpty(disabledReason))
                    {
                        _logger.LogWarning("⚠️ No rejection reason available (neither from Stripe nor from StripeStatusDetails) for userId={UserId}", userId);
                    }
                    
                    // Si es un rechazo permanente, bloquear
                    _logger.LogInformation("🔍 Checking if rejection is permanent for userId={UserId}, disabledReason={Reason}, isPermanent={IsPermanent}", 
                        userId, disabledReason ?? "null", IsPermanentRejection(disabledReason));
                    if (IsPermanentRejection(disabledReason))
                    {
                        _logger.LogWarning("🚫 BLOCKED: Cannot restart onboarding for permanently rejected account - userId={UserId}, accountId={AccountId}, reason={Reason}", 
                            userId, expertProfile.StripeAccountId, disabledReason);
                        
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
                        _logger.LogInformation("✅ ALLOWED: Temporary rejection allows retry - userId={UserId}, reason={Reason}, cleaning up and resetting", userId, disabledReason);
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
                    _logger.LogInformation("Expert already has completed onboarding: userId={UserId}, creating account link", userId);
                    
                    var restartLinkOptions = new AccountLinkCreateOptions
                    {
                        Account = expertProfile.StripeAccountId,
                        RefreshUrl = "https://atrapo.io/refresh-onboarding",
                        ReturnUrl = "https://atrapo.io/complete-onboarding",
                        Type = "account_onboarding"
                    };
                    
                    var restartLinkService = new AccountLinkService();
                    
                    try
                    {
                        var restartAccountLink = await restartLinkService.CreateAsync(restartLinkOptions);
                        _logger.LogInformation("Stripe account link created for completed account: userId={UserId}, url={Url}", userId, restartAccountLink.Url);
                        return Ok(new { url = restartAccountLink.Url, isLoginLink = true });
                    }
                    catch (StripeException ex)
                    {
                        _logger.LogError(ex, "Stripe error creating account link for userId={UserId}: {ErrorMessage}", userId, ex.Message);
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
                    RefreshUrl = "https://atrapo.io/refresh-onboarding",
                    ReturnUrl = "https://atrapo.io/complete-onboarding",
                    Type = "account_onboarding",
                    Collect = "eventually_due"
                };

                var pendingLinkService = new AccountLinkService();
                AccountLink pendingAccountLink;
                try
                {
                    pendingAccountLink = await pendingLinkService.CreateAsync(pendingLinkOptions);
                    _logger.LogInformation("New onboarding link created for userId={UserId}, url={Url}", userId, pendingAccountLink.Url);
                }
                catch (StripeException ex)
                {
                    _logger.LogError(ex, "Stripe error creating new onboarding link for userId={UserId}: {ErrorMessage}", userId, ex.Message);
                    return StatusCode(500, new { message = "Failed to create new onboarding link" });
                }

                return Ok(new { url = pendingAccountLink.Url });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restarting onboarding: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to restart onboarding" });
            }
        }

        [HttpPost("load-money")]
        public async Task<IActionResult> LoadMoney([FromBody] LoadMoneyDto request)
        {
            _logger.LogInformation("LoadMoney endpoint invoked with amount: {Amount}", request.Amount);

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                if (request.Amount <= 0 || request.Amount > 1000)
                {
                    _logger.LogError("Invalid amount: {Amount}", request.Amount);
                    return BadRequest(new { message = "Amount must be between 0.01 and 1000" });
                }

                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var user = await _context.Users
                    .FromSqlRaw("SELECT * FROM \"Users\" WHERE \"Id\" = {0} FOR UPDATE", userId)
                    .FirstOrDefaultAsync();
                if (user == null)
                {
                    _logger.LogError("User not found for userId={UserId}", userId);
                    return NotFound(new { message = "User not found" });
                }

                var domain = "https://atrapo.io";
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
                                UnitAmount = (long)(request.Amount * 100),
                                ProductData = new SessionLineItemPriceDataProductDataOptions
                                {
                                    Name = "Load Money"
                                }
                            },
                            Quantity = 1
                        }
                    },
                    Mode = "payment",
                    SuccessUrl = domain + "/success",
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
                    _logger.LogInformation("Stripe Checkout session created: sessionId={SessionId}, url={SessionUrl}", session.Id, session.Url);
                }
                catch (StripeException e)
                {
                    _logger.LogError(e, "Stripe error creating checkout session: {ErrorMessage}", e.Message);
                    return StatusCode(500, new { message = e.Message });
                }

                return Ok(new { url = session.Url });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating load money session: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to create load money session" });
            }
        }



        [HttpPost("load-money-service")]
        public async Task<IActionResult> LoadMoneyService([FromBody] LoadMoneyServiceDto request)
        {
            // 🚨 VALIDACIÓN DE ENTRADA
            if (request == null)
            {
                _logger.LogError("Request is null");
                return BadRequest(new { message = "Request cannot be null" });
            }

            if (request.ServiceId <= 0)
            {
                _logger.LogError("Invalid ServiceId: {ServiceId}", request.ServiceId);
                return BadRequest(new { message = "Invalid service ID" });
            }

            if (request.Amount <= 0)
            {
                _logger.LogError("Invalid Amount: {Amount}", request.Amount);
                return BadRequest(new { message = "Amount must be greater than 0" });
            }

            _logger.LogInformation("LoadMoneyService endpoint invoked with serviceId: {ServiceId}, amount: {Amount}", request.ServiceId, request.Amount);

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim ?? "null");
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var service = await _context.SearchServices
                    .Include(ss => ss.ExpertProfile)
                    .FirstOrDefaultAsync(ss => ss.Id == request.ServiceId);
                if (service == null)
                {
                    _logger.LogError("Service not found for serviceId={ServiceId}", request.ServiceId);
                    return NotFound(new { message = "Service not found" });
                }

                if (service.Price != request.Amount || service.Price <= 0 || service.Price > 1000)
                {
                    _logger.LogError("Invalid service price: expected={Expected}, received={Received} for serviceId={ServiceId}", service.Price, request.Amount, request.ServiceId);
                    return BadRequest(new { message = "Service price mismatch or invalid amount (must be between 0.01 and 1000.00)" });
                }

                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var user = await _context.Users
                    .FromSqlRaw("SELECT * FROM \"Users\" WHERE \"Id\" = {0} FOR UPDATE", userId)
                    .FirstOrDefaultAsync();
                if (user == null)
                {
                    _logger.LogError("User not found for userId={UserId}", userId);
                    return NotFound(new { message = "User not found" });
                }

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
                    _logger.LogError("User already has an active hire for this service: userId={UserId}, serviceId={ServiceId}, existingHireId={ExistingHireId}", 
                        userId, service.Id, existingHire.Id);
                    return BadRequest(new { message = "Ya tienes una contratación activa para este servicio" });
                }

                // 💳 SIEMPRE PAGAR CON STRIPE - NO USAR SALDO INTERNO
                var amountToCharge = service.Price;

                var domain = "https://atrapo.io";
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
                                UnitAmount = (long)(amountToCharge * 100),
                                ProductData = new SessionLineItemPriceDataProductDataOptions
                                {
                                    Name = $"Payment for Service {service.Id}"
                                }
                            },
                            Quantity = 1
                        }
                    },
                    Mode = "payment",
                    SuccessUrl = $"{domain}/success?userId={userId}",
                    CancelUrl = $"{domain}/cancel",
                    CustomerEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown@example.com",
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() },
                        { "serviceId", request.ServiceId.ToString() },
                        { "amount", amountToCharge.ToString() },
                        { "pendingHire", "true" }
                    }
                };

                var stripeService = new SessionService();
                Session session;
                try
                {
                    session = await stripeService.CreateAsync(options);
                    _logger.LogInformation("Stripe Checkout session created: sessionId={SessionId}, url={SessionUrl}", session.Id, session.Url);
                }
                catch (StripeException ex)
                {
                    _logger.LogError(ex, "Stripe error creating checkout session for userId={UserId}, serviceId={ServiceId}: {ErrorMessage}", userId, request.ServiceId, ex.Message);
                    return StatusCode(500, new { message = ex.Message });
                }

                return Ok(new { url = session.Url });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating load money session for serviceId={ServiceId}: {ErrorMessage}", request.ServiceId, ex.Message);
                return StatusCode(500, new { message = "Failed to create load money session" });
            }
        }

        [HttpPost("webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> HandleStripeWebhook()
        {
            // ✅ SEGURIDAD CRÍTICA: Habilitar buffering para permitir múltiples lecturas del body
            Request.EnableBuffering();
            Request.Body.Position = 0;
            
            var json = await new StreamReader(Request.Body).ReadToEndAsync();
            var signatureHeader = Request.Headers["Stripe-Signature"];
            _logger.LogInformation("🔔 WEBHOOK RECEIVED: signature={SignatureHeader}, payload={Payload}", signatureHeader, json);

            try
            {
                _logger.LogInformation("🔐 DEBUG: Validating webhook signature with secret: {WebhookSecret}", _webhookSecret?.Substring(0, 10) + "...");
                var stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, _webhookSecret);
                _logger.LogInformation("✅ WEBHOOK EVENT CONSTRUCTED: type={EventType}, id={EventId}", stripeEvent.Type, stripeEvent.Id);

                // 🔒 IDEMPOTENCIA COMPLETA: Verificar si el evento ya fue procesado
                if (await IsEventProcessedAsync(stripeEvent.Id))
                {
                    _logger.LogInformation("🔄 DEBUG: Evento principal ya procesado (eventId={EventId}), ignorando", stripeEvent.Id);
                    return Ok(new { message = "Event already processed" });
                }

                switch (stripeEvent.Type)
                {
                    // Los eventos de pago se manejan en el webhook general

                    case "account.application.authorized":
                        // Este evento indica que el usuario autorizó la aplicación (OAuth)
                        // Solo actualizar el ID, pero NO marcar como aprobado hasta que llegue account.updated
                        var authorizedApp = stripeEvent.Data.Object as Application;
                        if (authorizedApp != null)
                        {
                            _logger.LogInformation("🔗 DEBUG: Application authorized: appId={AppId}, accountId={AccountId}", authorizedApp.Id, stripeEvent.Account);
                            
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
                                _logger.LogInformation("✅ Updated account ID after authorization (status remains Pending): userId={UserId}, stripeStatus={StripeStatus}", 
                                    authorizedExpertProfile.UserId, authorizedExpertProfile.StripeStatus);
                            }
                        }
                        break;

                    case "account.application.deauthorized":
                        var deauthorizedAccount = stripeEvent.Data.Object as Account;
                        if (deauthorizedAccount != null)
                        {
                            _logger.LogInformation("❌ DEBUG: Account application deauthorized for accountId={AccountId}", deauthorizedAccount.Id);
                            
                            // Buscar por StripeAccountId o PendingStripeAccountId
                            var deauthorizedExpertProfile = await _context.ExpertProfiles
                                .FirstOrDefaultAsync(ep => ep.StripeAccountId == deauthorizedAccount.Id || ep.PendingStripeAccountId == deauthorizedAccount.Id);
                            
                            if (deauthorizedExpertProfile != null)
                            {
                                _logger.LogInformation("⚠️ DEBUG: Found expert profile for account.application.deauthorized: userId={UserId}, accountId={AccountId}", deauthorizedExpertProfile.UserId, deauthorizedAccount.Id);
                                
                                // Marcar como rechazado cuando la aplicación es desautorizada
                                deauthorizedExpertProfile.StripeStatus = StripeStatus.Rejected;
                                deauthorizedExpertProfile.OnboardingCompleted = false;
                                
                                // ✅ NUEVO: Notificar al admin y experto (porque puede tener contrataciones activas)
                                await HandleAccountDeauthorization(deauthorizedExpertProfile.UserId, "Account deauthorized by Stripe");
                                
                                // Limpiar PendingStripeAccountId si existe
                                if (!string.IsNullOrEmpty(deauthorizedExpertProfile.PendingStripeAccountId))
                                {
                                    _logger.LogInformation("🧹 DEBUG: Clearing PendingStripeAccountId for rejected account: userId={UserId}", deauthorizedExpertProfile.UserId);
                                    deauthorizedExpertProfile.PendingStripeAccountId = null;
                                }
                                
                                // Opcional: También limpiar StripeAccountId si fue rechazado
                                // deauthorizedExpertProfile.StripeAccountId = null;
                                
                                await _context.SaveChangesAsync();
                                _logger.LogInformation("❌ DEBUG: Account application deauthorized - status set to Rejected for userId={UserId}", deauthorizedExpertProfile.UserId);
                            }
                            else
                            {
                                _logger.LogWarning("❌ DEBUG: No expert profile found for account.application.deauthorized accountId={AccountId}", deauthorizedAccount.Id);
                            }
                        }
                        break;

                    case "account.updated":
                        var account = stripeEvent.Data.Object as Account;
                        if (account == null)
                        {
                            _logger.LogWarning("account.updated webhook received but account data is null");
                            break;
                        }

                        // 🔒 IDEMPOTENCIA COMPLETA: Verificar tanto idempotency_key como stripeEvent.Id
                        var idempotencyKey = stripeEvent.Request?.IdempotencyKey;
                        _logger.LogInformation("🔑 DEBUG: Idempotency key: {IdempotencyKey} (exists: {Exists}), EventId: {EventId}", 
                            idempotencyKey ?? "null", idempotencyKey != null ? "Yes" : "No", stripeEvent.Id);
                        
                        // 🚨 VERIFICAR IDEMPOTENCIA COMPLETA: Usar idempotency_key si existe, sino usar stripeEvent.Id
                        var eventIdToCheck = !string.IsNullOrEmpty(idempotencyKey) ? idempotencyKey : stripeEvent.Id;
                        if (await IsEventProcessedAsync(eventIdToCheck))
                        {
                            _logger.LogInformation("🔄 DEBUG: Evento ya procesado (eventId={EventId}), ignorando", eventIdToCheck);
                            break;
                        }

                            _logger.LogInformation("🔍 DEBUG: Processing account.updated webhook for accountId={AccountId}, ChargesEnabled={ChargesEnabled}, PayoutsEnabled={PayoutsEnabled}, DetailsSubmitted={DetailsSubmitted}, RequirementsCurrentlyDue={RequirementsCurrentlyDue}", 
                                account.Id, account.ChargesEnabled, account.PayoutsEnabled, account.DetailsSubmitted, 
                                account.Requirements?.CurrentlyDue?.Count ?? 0);
                            
                            // Log metadata para debugging
                            if (account.Metadata != null)
                            {
                                _logger.LogInformation("📋 DEBUG: Account metadata: {Metadata}", string.Join(", ", account.Metadata.Select(kv => $"{kv.Key}={kv.Value}")));
                            }
                            else
                            {
                                _logger.LogWarning("⚠️ DEBUG: No metadata found in account");
                            }
                            
                            // Buscar por StripeAccountId, PendingStripeAccountId, o por userId en metadata
                            _logger.LogInformation("🔍 DEBUG: Searching for expert profile with accountId={AccountId}", account.Id);
                            var expertProfile = await _context.ExpertProfiles
                                .FirstOrDefaultAsync(ep => ep.StripeAccountId == account.Id || ep.PendingStripeAccountId == account.Id);
                            
                            if (expertProfile != null)
                            {
                                _logger.LogInformation("✅ DEBUG: Found expert profile by account ID: userId={UserId}, stripeAccountId={StripeAccountId}, pendingStripeAccountId={PendingStripeAccountId}", 
                                    expertProfile.UserId, expertProfile.StripeAccountId, expertProfile.PendingStripeAccountId);
                            }
                            else
                            {
                                _logger.LogWarning("❌ DEBUG: No expert profile found by account ID, trying metadata search...");
                                
                                // Si no se encuentra por account ID, buscar por userId en metadata
                                if (account.Metadata != null && account.Metadata.ContainsKey("userId"))
                                {
                                    if (int.TryParse(account.Metadata["userId"], out int userIdFromMetadata))
                                    {
                                        _logger.LogInformation("🔍 DEBUG: Searching by userId from metadata: {UserId}", userIdFromMetadata);
                                        expertProfile = await _context.ExpertProfiles
                                            .FirstOrDefaultAsync(ep => ep.UserId == userIdFromMetadata);
                                        
                                        if (expertProfile != null)
                                        {
                                            _logger.LogInformation("✅ DEBUG: Found expert profile by userId from metadata: userId={UserId}, accountId={AccountId}, stripeAccountId={StripeAccountId}, pendingStripeAccountId={PendingStripeAccountId}", 
                                                userIdFromMetadata, account.Id, expertProfile.StripeAccountId, expertProfile.PendingStripeAccountId);
                                        }
                                        else
                                        {
                                            _logger.LogError("❌ DEBUG: No expert profile found even by userId from metadata: {UserId}", userIdFromMetadata);
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogError("❌ DEBUG: Could not parse userId from metadata: {UserId}", account.Metadata["userId"]);
                                    }
                                }
                                else
                                {
                                    _logger.LogError("❌ DEBUG: No metadata or userId in metadata found");
                                }
                            }
                            
                            if (expertProfile != null)
                            {
                                _logger.LogInformation("✅ DEBUG: Expert profile found: userId={UserId}, stripeAccountId={StripeAccountId}, pendingStripeAccountId={PendingStripeAccountId}", 
                                    expertProfile.UserId, expertProfile.StripeAccountId, expertProfile.PendingStripeAccountId);
                                
                                // CORRECCIÓN: Try-catch interno para capturar errores en lógica de verificación
                                try
                                {
                                    // LÓGICA DE VERIFICACIÓN 100% ALINEADA CON STRIPE DOCS
                                // Requirements críticos: currently_due, past_due, errors, pending_verification deben estar VACÍOS
                                bool noCurrentlyDue = (account.Requirements?.CurrentlyDue?.Count ?? 0) == 0;
                                bool noPastDue = (account.Requirements?.PastDue?.Count ?? 0) == 0;
                                bool noErrors = (account.Requirements?.Errors?.Count ?? 0) == 0;
                                bool noPendingVerification = (account.Requirements?.PendingVerification?.Count ?? 0) == 0;  // CLAVE: Debe ser 0 para full verification
                                bool allCriticalRequirementsMet = noCurrentlyDue && noPastDue && noErrors && noPendingVerification;
                                // eventually_due: Ignorar para aprobación (docs confirman no bloquea)

                                // Future requirements: Solo para alertas
                                bool hasFutureIssues = (account.FutureRequirements?.CurrentlyDue?.Count ?? 0) > 0 ||
                                                       (account.FutureRequirements?.PastDue?.Count ?? 0) > 0 ||
                                                       (account.FutureRequirements?.EventuallyDue?.Count ?? 0) > 0;

                                // Capabilities y Enabled Flags
                                bool chargesEnabled = account.ChargesEnabled;
                                bool payoutsEnabled = account.PayoutsEnabled;
                                
                                // ✅ MEJORA: Verificación explícita de capabilities
                                bool transfersActive = account.Capabilities?.Transfers == "active";
                                bool paymentsEnabled = chargesEnabled && payoutsEnabled && transfersActive;
                                
                                // ✅ MEJORA: Logging de capabilities para debugging
                                _logger.LogInformation("🔍 DEBUG: Capabilities - Transfers={Transfers}, Charges={Charges}, Payouts={Payouts}", 
                                    account.Capabilities?.Transfers, chargesEnabled, payoutsEnabled);

                                bool detailsSubmitted = account.DetailsSubmitted;
                                
                                // Log para debug ToS IP
                                string tosIp = account.TosAcceptance?.Ip ?? "null";
                                _logger.LogInformation("🔍 DEBUG: ToS Acceptance - Date: {Date}, IP: {Ip}", 
                                    account.TosAcceptance?.Date, tosIp);
                                bool tosAccepted = account.TosAcceptance?.Date != null && !string.IsNullOrEmpty(tosIp);
                                
                                string disabledReason = account.Requirements?.DisabledReason ?? "";
                                bool notDisabled = string.IsNullOrEmpty(disabledReason);

                                // Condición FINAL para Verified (exacta de docs): Requirements críticos met + enabled + details/tos + no disabled
                                bool isAccountVerified = allCriticalRequirementsMet && paymentsEnabled && detailsSubmitted && tosAccepted && notDisabled;
                                
                                // Errores details
                                List<string> errorDetails = new List<string>();
                                if (account.Requirements?.Errors != null && account.Requirements.Errors.Any())
                                {
                                    foreach (var error in account.Requirements.Errors)
                                    {
                                        errorDetails.Add($"Code: {error.Code}, Reason: {error.Reason}, Requirement: {error.Requirement}");
                                    }
                                    _logger.LogWarning("⚠️ DEBUG: Requirements errors for accountId={AccountId}: {Errors}", account.Id, string.Join("; ", errorDetails));
                                }
                                
                                // Rejected: Si disabled_reason indica rechazo (docs: startsWith "rejected.", etc.)
                                bool isRejected = !string.IsNullOrEmpty(disabledReason) &&
                                                  (disabledReason.StartsWith("rejected.") || disabledReason == "under_review" || disabledReason == "listed" ||
                                                   disabledReason == "requirements.past_due" || disabledReason == "requirements.pending_verification" ||
                                                   disabledReason == "other" || disabledReason == "action_required.requested_capabilities");
                                
                                // Logging Detallado (agregado para debug)
                                _logger.LogInformation("📊 DEBUG: Requirements - currentlyDue={CurrentlyDue}, pastDue={PastDue}, eventuallyDue={EventuallyDue}, errors={Errors}, pendingVerification={PendingVerification}",
                                    account.Requirements?.CurrentlyDue?.Count ?? 0, account.Requirements?.PastDue?.Count ?? 0,
                                    account.Requirements?.EventuallyDue?.Count ?? 0, account.Requirements?.Errors?.Count ?? 0,
                                    account.Requirements?.PendingVerification?.Count ?? 0);
                                _logger.LogInformation("🔍 DEBUG: Payments - chargesEnabled={ChargesEnabled}, payoutsEnabled={PayoutsEnabled}, paymentsEnabled={PaymentsEnabled}",
                                    chargesEnabled, payoutsEnabled, paymentsEnabled);
                                _logger.LogInformation("📋 DEBUG: Verification - allCriticalRequirementsMet={AllCritical}, detailsSubmitted={Details}, tosAccepted={Tos}, notDisabled={NotDisabled}, isVerified={IsVerified}, hasFutureIssues={FutureIssues}",
                                    allCriticalRequirementsMet, detailsSubmitted, tosAccepted, notDisabled, isAccountVerified, hasFutureIssues);
                                _logger.LogInformation("✅ DEBUG: Final - verified={Verified}, rejected={Rejected}, disabledReason={DisabledReason}, errorDetails={Errors}",
                                    isAccountVerified, isRejected, disabledReason, string.Join("; ", errorDetails));
                                
                                // MEJORA: Usar transacción para actualizaciones atómicas con ExecutionStrategy (NpgsqlRetryingExecutionStrategy)
                                var previousStatus = expertProfile.StripeStatus;
                                var strategy = _context.Database.CreateExecutionStrategy();
                                await strategy.ExecuteAsync(async () =>
                                {
                                    await using var transaction = await _context.Database.BeginTransactionAsync();
                                    try
                                    {
                                        if (isAccountVerified)
                                        {
                                            expertProfile.StripeStatus = StripeStatus.Approved;
                                            expertProfile.OnboardingCompleted = true;
                                            expertProfile.StripeAccountId ??= account.Id;  // Set si vacío
                                            expertProfile.PendingStripeAccountId = null;  // Clear pending
                                            string details = "✅ **Cuenta Aprobada**: ¡Excelente! Tu cuenta de pagos está completamente verificada y lista para recibir pagos.";
                                            if (hasFutureIssues || (account.Requirements?.EventuallyDue?.Count ?? 0) > 0)
                                            {
                                                details += " Nota: Verifica requirements futuros para mantener el estado.";
                                            }
                                            expertProfile.StripeStatusDetails = details;
                                            _logger.LogInformation("🎉 DEBUG: Account verified and approved for userId={UserId} (prev: {PreviousStatus})", expertProfile.UserId, previousStatus);
                                        }
                                        else if (isRejected)
                                        {
                                            expertProfile.StripeStatus = StripeStatus.Rejected;
                                            expertProfile.OnboardingCompleted = false;
                                            expertProfile.StripeStatusDetails = GetRejectionMessage(disabledReason, errorDetails);
                                            
                                            // ✅ Notificar solo en transición real a Rejected para evitar duplicados
                                            if (previousStatus != StripeStatus.Rejected)
                                            {
                                                await NotifyExpertOnly(expertProfile.UserId, disabledReason);
                                            }
                                            
                                            _logger.LogWarning("❌ DEBUG: Account rejected for userId={UserId}, reason={Reason}", expertProfile.UserId, disabledReason);
                                        }
                                        else
                                        {
                                            // Pending: Incluye critical issues o en proceso
                                            expertProfile.StripeStatus = StripeStatus.Pending;
                                            expertProfile.OnboardingCompleted = false;
                                            string pendingMsg = GetPendingMessage(account, errorDetails, allCriticalRequirementsMet, paymentsEnabled, detailsSubmitted, tosAccepted, notDisabled, noErrors, noPendingVerification, !hasFutureIssues);
                                            if (!noPendingVerification) pendingMsg += " En revisión asíncrona por Stripe (pending_verification).";
                                            if (hasFutureIssues) pendingMsg += " Prepara para requirements futuros.";
                                            expertProfile.StripeStatusDetails = pendingMsg;
                                            _logger.LogWarning("⏳ DEBUG: Account pending for userId={UserId} (prev: {PreviousStatus}), pendingVerification={PendingVerif}", 
                                                expertProfile.UserId, previousStatus, account.Requirements?.PendingVerification?.Count ?? 0);
                                        }

                                        await _context.SaveChangesAsync();
                                        await transaction.CommitAsync();

                                        // 🚨 MARCAR EVENTO COMO PROCESADO usando el mismo ID que se verificó
                                        await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, expertProfile.UserId);

                                        _logger.LogInformation("✅ DEBUG: Updated profile: userId={UserId}, status={Status}, completed={Completed}", 
                                            expertProfile.UserId, expertProfile.StripeStatus, expertProfile.OnboardingCompleted);
                                    }
                                    catch (Exception ex)
                                    {
                                        await transaction.RollbackAsync();
                                        _logger.LogError(ex, "❌ ERROR: Processing account.updated for {AccountId}", account.Id);
                                        // 🚨 MARCAR EVENTO COMO PROCESADO (FALLIDO) usando el mismo ID que se verificó
                                        await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, expertProfile.UserId, "Failed", ex.Message);
                                        throw;  // Retry por Stripe
                                    }
                                });
                                }
                                catch (Exception logicEx)
                                {
                                    _logger.LogError(logicEx, "❌ ERROR: En lógica de verificación para account.updated accountId={AccountId}. Verificar Capabilities o ToS.", account.Id);
                                    
                                    // ✅ MEJORA: Marcar evento como procesado con error
                                    await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, null, "Error", logicEx.Message);
                                    
                                    // ✅ MEJORA: No hacer throw para evitar retry, pero registrar el error
                                    return Ok(new { message = "Event processed with errors" });
                                }
                            }
                            else
                            {
                                _logger.LogWarning("❌ DEBUG: No expert profile found for account.updated accountId={AccountId}", account.Id);
                                
                                // Log all expert profiles for debugging
                                var allProfiles = await _context.ExpertProfiles.ToListAsync();
                                _logger.LogWarning("❌ DEBUG: Total expert profiles in database: {Count}", allProfiles.Count);
                                
                                foreach (var profile in allProfiles)
                                {
                                    _logger.LogInformation("📋 DEBUG: Expert profile: userId={UserId}, StripeAccountId={StripeAccountId}, PendingStripeAccountId={PendingStripeAccountId}", 
                                        profile.UserId, profile.StripeAccountId, profile.PendingStripeAccountId);
                                }
                                
                                // Intentar encontrar por userId en metadata si existe
                                if (account.Metadata != null && account.Metadata.ContainsKey("userId"))
                                {
                                    if (int.TryParse(account.Metadata["userId"], out int userIdFromMetadata))
                                    {
                                        _logger.LogWarning("🔍 DEBUG: Trying to find expert profile by userId from metadata: {UserId}", userIdFromMetadata);
                                        var profileByUserId = await _context.ExpertProfiles
                                            .FirstOrDefaultAsync(ep => ep.UserId == userIdFromMetadata);
                                        
                                        if (profileByUserId != null)
                                        {
                                            _logger.LogInformation("✅ DEBUG: Found expert profile by userId! Updating with new account info...");
                                            
                                            // ✅ CORRECCIÓN CRÍTICA: Usar transacción para consistencia
                                            using (var fallbackTransaction = await _context.Database.BeginTransactionAsync())
                                            {
                                                try
                                                {
                                            // Actualizar el perfil con la nueva información de la cuenta
                                            profileByUserId.StripeAccountId = account.Id;
                                            if (!string.IsNullOrEmpty(profileByUserId.PendingStripeAccountId))
                                            {
                                                profileByUserId.PendingStripeAccountId = null;
                                            }
                                            
                                                    // ✅ CORRECCIÓN CRÍTICA: Aplicar la misma lógica completa de verificación
                                            bool noPendingRequirements = (account.Requirements?.CurrentlyDue?.Count ?? 0) == 0;
                                            bool noPastDueRequirements = (account.Requirements?.PastDue?.Count ?? 0) == 0;
                                            bool noRequirementErrors = (account.Requirements?.Errors?.Count ?? 0) == 0;
                                            bool noPendingVerifications = (account.Requirements?.PendingVerification?.Count ?? 0) == 0;
                                            bool allRequirementsMet = noPendingRequirements && noPastDueRequirements && noRequirementErrors && noPendingVerifications;

                                            bool canProcessPayments = account.ChargesEnabled;
                                            bool canReceivePayments = account.PayoutsEnabled;
                                            bool transfersActive = account.Capabilities?.Transfers == "active";
                                            bool paymentsEnabled = canProcessPayments && canReceivePayments && transfersActive;

                                            bool detailsSubmitted = account.DetailsSubmitted;
                                            bool tosAccepted = account.TosAcceptance?.Date != null && !string.IsNullOrEmpty(account.TosAcceptance?.Ip);
                                            bool notDisabled = string.IsNullOrEmpty(account.Requirements?.DisabledReason);

                                            bool isAccountApproved = allRequirementsMet && paymentsEnabled && detailsSubmitted && tosAccepted && notDisabled;
                                            
                                                    // ✅ CORRECCIÓN: Manejar todos los estados, no solo aprobado
                                                    string disabledReason = account.Requirements?.DisabledReason ?? "";
                                                    bool isRejected = !string.IsNullOrEmpty(disabledReason) &&
                                                                      (disabledReason.StartsWith("rejected.") || disabledReason == "under_review" || disabledReason == "listed" ||
                                                                       disabledReason == "requirements.past_due" || disabledReason == "requirements.pending_verification" ||
                                                                       disabledReason == "other" || disabledReason == "action_required.requested_capabilities");
                                            
                                            if (isAccountApproved)
                                            {
                                                profileByUserId.StripeStatus = StripeStatus.Approved;
                                                profileByUserId.OnboardingCompleted = true;
                                                profileByUserId.StripeStatusDetails = "✅ **Cuenta Aprobada**: ¡Excelente! Tu cuenta de pagos está completamente verificada y lista para recibir pagos. Ya puedes crear servicios y comenzar a ganar dinero.";
                                                _logger.LogInformation("🎉 DEBUG: Account approved and profile updated for userId={UserId}", userIdFromMetadata);
                                            }
                                                    else if (isRejected)
                                                    {
                                                        profileByUserId.StripeStatus = StripeStatus.Rejected;
                                                        profileByUserId.OnboardingCompleted = false;
                                                        profileByUserId.StripeStatusDetails = GetRejectionMessage(disabledReason, new List<string>());
                                                        _logger.LogWarning("❌ DEBUG: Account rejected for userId={UserId}, reason={Reason}", userIdFromMetadata, disabledReason);
                                                    }
                                                    else
                                                    {
                                                        profileByUserId.StripeStatus = StripeStatus.Pending;
                                                        profileByUserId.OnboardingCompleted = false;
                                                        profileByUserId.StripeStatusDetails = "⏳ **Cuenta Pendiente**: Tu cuenta está siendo procesada. Completa todos los requisitos para continuar.";
                                                        _logger.LogWarning("⏳ DEBUG: Account pending for userId={UserId}", userIdFromMetadata);
                                            }
                                            
                                            await _context.SaveChangesAsync();
                                                    await fallbackTransaction.CommitAsync();
                                                    
                                            // 🚨 MARCAR EVENTO COMO PROCESADO usando el mismo ID que se verificó
                                            await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, userIdFromMetadata);
                                                    
                                                    _logger.LogInformation("✅ DEBUG: Fallback profile updated successfully: userId={UserId}, status={Status}", userIdFromMetadata, profileByUserId.StripeStatus);
                                                }
                                                catch (Exception fallbackEx)
                                                {
                                                    await fallbackTransaction.RollbackAsync();
                                                    _logger.LogError(fallbackEx, "❌ ERROR: Fallback profile update failed for userId={UserId}", userIdFromMetadata);
                                                    // Marcar evento como procesado con error
                                                    await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, userIdFromMetadata, "Failed", fallbackEx.Message);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            _logger.LogError("❌ DEBUG: No expert profile found even by userId from metadata: {UserId}", userIdFromMetadata);
                                        }
                                    }
                                }
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
                                        
                                        _logger.LogCritical("🚨 CRITICAL TRANSFER FAILURE - SearchHireId: {SearchHireId}, TransferId: {TransferId}, ExpertId: {ExpertId}, Amount: {Amount}€", 
                                            searchHire.Id, transfer.Id, failedTransaction.UserId, failedTransaction.Amount);
                                        
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
                                    }
                                    
                                    // ✅ ACTUALIZAR ESTADO A COMPLETED - El servicio está completado, solo falló el transfer
                                    searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Completed.ToStringValue());
                                searchHire.UpdatedAt = DateTime.UtcNow;
                                    
                                await _context.SaveChangesAsync();
                                    await transaction.CommitAsync();
                                    
                                    _logger.LogCritical("🚨 TRANSFER FAILED BUT SERVICE COMPLETED - SearchHireId: {SearchHireId}, TransferId: {TransferId}, Status: {Status}, ExpertId: {ExpertId}, Amount: {Amount}€ - REQUIRES ADMIN INTERVENTION", 
                                        searchHire.Id, transfer.Id, searchHire.Status?.StatusValue, failedTransaction?.UserId, failedTransaction?.Amount);
                                }
                                catch (Exception ex)
                                {
                                    await transaction.RollbackAsync();
                                    _logger.LogError(ex, "Error reverting failed transfer for searchHireId={SearchHireId}, transferId={TransferId}",
                                        searchHire.Id, transfer.Id);
                                }
                                });
                            }
                            else
                            {
                                _logger.LogWarning("No SearchHire found for transferId={TransferId}", transfer.Id);
                            }
                        }
                        break;

                    // Los eventos de suscripción y facturas se manejan en el webhook general

                    default:
                        _logger.LogWarning("Unhandled event type: {EventType}", stripeEvent.Type);
                        break;
                }

                // 🚨 MARCAR EVENTO PRINCIPAL COMO PROCESADO
                await MarkEventAsProcessedAsync(stripeEvent.Id, stripeEvent.Type);

                _logger.LogInformation("✅ WEBHOOK PROCESSED SUCCESSFULLY: returning 200 OK");
                return Ok();
            }
            catch (StripeException e)
            {
                _logger.LogError(e, "Stripe webhook error: {ErrorMessage}, payload: {Payload}", e.Message, json);
                return BadRequest(new { error = e.Message });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "General webhook error: {ErrorMessage}, payload: {Payload}", e.Message, json);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        [HttpPost("webhook-general")]
        [AllowAnonymous]
        public async Task<IActionResult> HandleGeneralStripeWebhook()
        {
            // ✅ SEGURIDAD CRÍTICA: Habilitar buffering para permitir múltiples lecturas del body
            Request.EnableBuffering();
            Request.Body.Position = 0;
            
            var json = await new StreamReader(Request.Body).ReadToEndAsync();
            var signatureHeader = Request.Headers["Stripe-Signature"];
            _logger.LogInformation("🔔 GENERAL WEBHOOK RECEIVED: signature={SignatureHeader}, payload={Payload}", signatureHeader, json);

            try
            {
                _logger.LogInformation("🔐 DEBUG: Validating general webhook signature with secret: {WebhookSecret}", _generalWebhookSecret?.Substring(0, 10) + "...");
                _logger.LogInformation("🔐 DEBUG: Full signature header: {FullSignature}", signatureHeader);
                _logger.LogInformation("🔐 DEBUG: Webhook secret length: {SecretLength}", _generalWebhookSecret?.Length ?? 0);
                
                if (string.IsNullOrEmpty(_generalWebhookSecret))
                {
                    _logger.LogError("❌ GENERAL WEBHOOK SECRET IS NULL OR EMPTY!");
                    return BadRequest(new { error = "Webhook secret not configured" });
                }
                
                if (string.IsNullOrEmpty(signatureHeader))
                {
                    _logger.LogError("❌ STRIPE SIGNATURE HEADER IS NULL OR EMPTY!");
                    return BadRequest(new { error = "Stripe signature header missing" });
                }
                
                var stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, _generalWebhookSecret);
                _logger.LogInformation("✅ GENERAL WEBHOOK EVENT CONSTRUCTED: type={EventType}, id={EventId}", stripeEvent.Type, stripeEvent.Id);

                // 🔒 IDEMPOTENCIA COMPLETA: Verificar si el evento ya fue procesado
                if (await IsEventProcessedAsync(stripeEvent.Id))
                {
                    _logger.LogInformation("🔄 DEBUG: Evento general ya procesado (eventId={EventId}), ignorando", stripeEvent.Id);
                    return Ok(new { message = "Event already processed" });
                }

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
                            _logger.LogWarning("No payment intent data in payment_intent.succeeded event");
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
                            _logger.LogWarning("No payment intent data in payment_intent.payment_failed event");
                        }
                        break;

                    case "checkout.session.completed":
                        var session = stripeEvent.Data.Object as Session;
                        _logger.LogInformation("🔔 CHECKOUT SESSION COMPLETED: sessionId={SessionId}, mode={Mode}, metadata={Metadata}", 
                            session?.Id, session?.Mode, JsonSerializer.Serialize(session?.Metadata));
                        if (session != null && session.Mode == "payment")
                        {
                            // 🔍 IDEMPOTENCIA: Verificar si ya se procesó este evento
                            var existingTransaction = await _context.FinancialTransactions
                                .FirstOrDefaultAsync(ft => ft.StripePaymentIntentId == session.PaymentIntentId && 
                                                          ft.TransactionType == "ServicePayment");

                            if (existingTransaction != null)
                            {
                                _logger.LogWarning("⚠️ WEBHOOK ALREADY PROCESSED - PaymentIntentId: {PaymentIntentId}, ExistingTransactionId: {TransactionId}", 
                                    session.PaymentIntentId, existingTransaction.Id);
                                return Ok(new { message = "Event already processed" }); // ✅ Idempotencia
                            }

                            _logger.LogInformation("Processing payment session: sessionId={SessionId}, metadata={Metadata}", session.Id, JsonSerializer.Serialize(session.Metadata));
                            if (int.TryParse(session.Metadata.GetValueOrDefault("userId", "0"), out int userId) &&
                                decimal.TryParse(session.Metadata.GetValueOrDefault("amount", "0"), out decimal amount) &&
                                bool.TryParse(session.Metadata.GetValueOrDefault("pendingHire", "false"), out bool pendingHire))
                            {
                                if (pendingHire && int.TryParse(session.Metadata.GetValueOrDefault("serviceId", "0"), out int serviceId))
                                {
                                    _logger.LogInformation("Processing pending hire for userId={UserId}, serviceId={ServiceId}, amount={Amount}", userId, serviceId, amount);
                                    await HandlePendingHireCompleted(userId, amount, serviceId, session.Metadata, session);
                                }
                                else
                                {
                                    _logger.LogInformation("Processing load money for userId={UserId}, amount={Amount}, paymentIntentId={PaymentIntentId}", userId, amount, session.PaymentIntentId);
                                    // ✅ REMOVED: Load money functionality eliminated - all payments are direct Stripe
                                }
                            }
                            else
                            {
                                _logger.LogError("Invalid metadata for payment session: sessionId={SessionId}, metadata={Metadata}", session.Id, JsonSerializer.Serialize(session.Metadata));
                                return BadRequest(new { error = "Invalid metadata format" });
                            }
                        }
                        else if (session != null && session.Mode == "subscription")
                        {
                            if (!int.TryParse(session.Metadata.GetValueOrDefault("userId", "0"), out int userId) ||
                                !int.TryParse(session.Metadata.GetValueOrDefault("planId", "0"), out int planId) ||
                                !bool.TryParse(session.Metadata.GetValueOrDefault("isYearly", "false"), out bool isYearly))
                            {
                                _logger.LogError("Invalid metadata in subscription session: sessionId={SessionId}, metadata={Metadata}", session.Id, JsonSerializer.Serialize(session.Metadata));
                                return BadRequest(new { error = "Invalid metadata format" });
                            }
                            await HandleCheckoutSessionCompleted(userId, planId, isYearly, session.SubscriptionId);
                        }
                        else
                        {
                            _logger.LogWarning("No session data in checkout.session.completed event");
                        }
                        break;

                    case "invoice.payment_succeeded":
                        var invoiceSucceeded = stripeEvent.Data.Object as Invoice;
                        if (invoiceSucceeded != null)
                        {
                            await HandlePaymentSucceeded(invoiceSucceeded);
                        }
                        else
                        {
                            _logger.LogWarning("No invoice data in invoice.payment_succeeded event");
                        }
                        break;

                    case "invoice.payment_failed":
                        var invoiceFailed = stripeEvent.Data.Object as Invoice;
                        if (invoiceFailed != null)
                        {
                            await HandlePaymentFailed(invoiceFailed);
                        }
                        else
                        {
                            _logger.LogWarning("No invoice data in invoice.payment_failed event");
                        }
                        break;

                    case "customer.subscription.created":
                        var subscriptionCreated = stripeEvent.Data.Object as Subscription;
                        if (subscriptionCreated != null)
                        {
                            _logger.LogInformation("🆕 Subscription Created: {SubscriptionId}, Status: {Status}, Customer: {CustomerId}", 
                                subscriptionCreated.Id, subscriptionCreated.Status, subscriptionCreated.CustomerId);
                            // No hay método específico para created, solo logueamos
                        }
                        else
                        {
                            _logger.LogWarning("No subscription data in customer.subscription.created event");
                        }
                        break;

                    case "customer.subscription.updated":
                        var subscriptionUpdated = stripeEvent.Data.Object as Subscription;
                        if (subscriptionUpdated != null)
                        {
                            await HandleSubscriptionUpdated(subscriptionUpdated);
                        }
                        else
                        {
                            _logger.LogWarning("No subscription data in customer.subscription.updated event");
                        }
                        break;

                    case "customer.subscription.deleted":
                        var subscriptionDeleted = stripeEvent.Data.Object as Subscription;
                        if (subscriptionDeleted != null)
                        {
                            await HandleSubscriptionCanceled(subscriptionDeleted);
                        }
                        else
                        {
                            _logger.LogWarning("No subscription data in customer.subscription.deleted event");
                        }
                        break;

                    default:
                        _logger.LogWarning("Unhandled general webhook event type: {EventType}", stripeEvent.Type);
                        break;
                }

                // 🚨 MARCAR EVENTO GENERAL COMO PROCESADO
                await MarkEventAsProcessedAsync(stripeEvent.Id, stripeEvent.Type);

                _logger.LogInformation("✅ GENERAL WEBHOOK PROCESSED SUCCESSFULLY: returning 200 OK");
                return Ok();
            }
            catch (StripeException e)
            {
                _logger.LogError(e, "Stripe general webhook error: {ErrorMessage}, payload: {Payload}", e.Message, json);
                return BadRequest(new { error = e.Message });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "General webhook error: {ErrorMessage}, payload: {Payload}", e.Message, json);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        private async Task HandlePendingHireCompleted(int userId, decimal amount, int serviceId, Dictionary<string, string> metadata, Session session)
        {
            _logger.LogInformation("Handling pending hire completed for userId={UserId}, serviceId={ServiceId}, amount={Amount}", userId, serviceId, amount);

            // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
            var user = await _context.Users
                .FromSqlRaw("SELECT * FROM \"Users\" WHERE \"Id\" = {0} FOR UPDATE", userId)
                .FirstOrDefaultAsync();
            if (user == null)
            {
                _logger.LogError("User not found for userId={UserId}", userId);
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }

            var service = await _context.SearchServices.FindAsync(serviceId);
            if (service == null)
            {
                _logger.LogError("Service not found for serviceId={ServiceId}", serviceId);
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }

            if (!metadata.TryGetValue("searchData", out var searchDataJson) || !metadata.TryGetValue("parameters", out var parametersJson))
            {
                _logger.LogError("Missing searchData or parameters in metadata for userId={UserId}, serviceId={ServiceId}", userId, serviceId);
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
                _logger.LogError(ex, "Error deserializing search data or parameters for userId={UserId}", userId);
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }

            if (searchDto == null || parameterDto == null)
            {
                _logger.LogError("Deserialized searchDto or parameterDto is null for userId={UserId}", userId);
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

            if (!user.PhoneVerified)
            {
                _logger.LogError("Phone verification required for userId={UserId}", userId);
                return; // ✅ CORRECTO: Salir silenciosamente en método async Task
            }

            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
            using var transaction = await _context.Database.BeginTransactionAsync();
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
                    _logger.LogError("Expert cannot hire themselves: expertUserId={ExpertUserId}, userId={UserId}", expertuserid, userId);
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

                // Create search hire
                var searchHire = new SearchHire
                {
                    ClientId = userId,
                    ExpertId = expertuserid,
                    SearchServiceId = service.Id,
                    SearchId = search.Id,
                        StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Pending.ToStringValue()),
                    Amount = service.Price,
                    CreatedAt = DateTime.UtcNow,
                    CompletionDeadline = DateTime.UtcNow.AddDays(7),
                    ExpertAvailabilityId = currentAvailabilityId // Guardar la disponibilidad usada
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
                await transaction.CommitAsync();

                _logger.LogInformation("Pending hire completed successfully for userId={UserId}, searchId={SearchId}, searchHireId={SearchHireId}", userId, search.Id, searchHire.Id);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                    _logger.LogError(ex, "❌ ERROR PROCESSING PENDING HIRE for userId={UserId}, serviceId={ServiceId}", userId, serviceId);
                    
                    // 🚨 CRÍTICO: Refund automático si falla la creación de búsqueda
                    await ProcessAutomaticRefundOnError(session.PaymentIntentId, ex, userId, serviceId);
                    
                throw;
            }
            });
        }

        /// <summary>
        /// Procesa refund automático cuando falla la creación de búsqueda después del pago
        /// Siempre devuelve 100% porque no se creó nada
        /// </summary>
        private async Task ProcessAutomaticRefundOnError(string paymentIntentId, Exception originalError, int userId, int serviceId)
        {
            _logger.LogInformation("🚨 PROCESSING AUTOMATIC REFUND ON ERROR - PaymentIntentId: {PaymentIntentId}, UserId: {UserId}, ServiceId: {ServiceId}", 
                paymentIntentId, userId, serviceId);

            try
            {
                // 🔍 Verificar si ya existe un refund para este PaymentIntent (idempotencia)
                var existingRefund = await _context.FinancialTransactions
                    .FirstOrDefaultAsync(ft => ft.StripePaymentIntentId == paymentIntentId && 
                                              ft.TransactionType == "Refund" && 
                                              ft.Amount > 0);

                if (existingRefund != null)
                {
                    _logger.LogWarning("⚠️ REFUND ALREADY EXISTS - PaymentIntentId: {PaymentIntentId}, ExistingRefundId: {RefundId}", 
                        paymentIntentId, existingRefund.Id);
                    return; // ✅ Idempotencia: no procesar refund duplicado
                }

                // ✅ USAR MÉTODO DIRECTO: 100% porque no se creó nada (no hay SearchHire)
                var additionalMetadata = new Dictionary<string, string>
                {
                    { "serviceId", serviceId.ToString() },
                    { "originalError", originalError.Message }
                };

                var refundSuccess = await ProcessGenericRefundAsync(
                    paymentIntentId, 
                    userId, 
                    "automatic_error_refund", 
                    100m, // ✅ 100% para errores de creación
                    $"Error creating search after payment: {originalError.Message}",
                    additionalMetadata);

                if (!refundSuccess)
                {
                    _logger.LogError("❌ FAILED TO PROCESS ERROR REFUND - PaymentIntentId: {PaymentIntentId}, UserId: {UserId}", 
                        paymentIntentId, userId);
                    
                    // 🚨 Registrar fallo crítico de refund
                    await _loggingService.LogCriticalAsync(
                        $"Failed to process automatic refund - PaymentIntentId: {paymentIntentId}",
                        $"UserId: {userId}, ServiceId: {serviceId}, OriginalError: {originalError.Message}",
                        userId,
                        "SubscriptionController.ProcessAutomaticRefundOnError",
                        "Payment",
                        serviceId,
                        new { PaymentIntentId = paymentIntentId, ServiceId = serviceId, OriginalError = originalError.Message }
                    );
                    
                    await LogCriticalRefundFailure(paymentIntentId, userId, serviceId, originalError);
                }

                // 📧 TODO: Enviar notificación al usuario sobre el refund
                // await _notificationService.SendRefundNotificationAsync(userId, refundAmount, "Error creating search after payment");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ ERROR IN PROCESS AUTOMATIC REFUND ON ERROR - PaymentIntentId: {PaymentIntentId}, Error: {Error}", 
                    paymentIntentId, ex.Message);
                
                // 🚨 CRÍTICO: Si falla el refund, alertar a administradores
                await LogCriticalRefundFailure(paymentIntentId, userId, serviceId, ex);
            }
        }

        /// <summary>
        /// Registra fallos críticos de refund para alertar a administradores
        /// </summary>
        private async Task LogCriticalRefundFailure(string paymentIntentId, int userId, int serviceId, Exception error)
        {
            _logger.LogCritical("🚨 CRITICAL REFUND FAILURE - PaymentIntentId: {PaymentIntentId}, UserId: {UserId}, ServiceId: {ServiceId}, Error: {Error}", 
                paymentIntentId, userId, serviceId, error.Message);

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

                _logger.LogInformation("🔄 Creating LogType table and inserting data...");

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

                _logger.LogInformation("✅ LogTypes table created successfully");

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

                _logger.LogInformation("✅ Logs table columns added successfully");

                // Crear índice si no existe
                await _context.Database.ExecuteSqlRawAsync(@"
                    CREATE INDEX IF NOT EXISTS ""IX_Logs_LogTypeId"" ON ""Logs"" (""LogTypeId"");
                ");

                _logger.LogInformation("✅ Index created successfully");

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

                _logger.LogInformation("✅ Foreign key created successfully");

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

                _logger.LogInformation("✅ Default log types inserted successfully");

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
                _logger.LogError(ex, "❌ Error creating LogType table");
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
            _logger.LogInformation("🔄 PROCESSING GENERIC REFUND - PaymentIntentId: {PaymentIntentId}, UserId: {UserId}, Type: {Type}, Percentage: {Percentage}%", 
                paymentIntentId, userId, refundType, refundPercentage);

            try
            {
                // 🔍 Verificar si ya existe un refund para este PaymentIntent (idempotencia)
                var existingRefund = await _context.FinancialTransactions
                    .FirstOrDefaultAsync(ft => ft.StripePaymentIntentId == paymentIntentId && 
                                              ft.TransactionType == "Refund" && 
                                              ft.Amount > 0);

                if (existingRefund != null)
                {
                    _logger.LogWarning("⚠️ REFUND ALREADY EXISTS - PaymentIntentId: {PaymentIntentId}, ExistingRefundId: {RefundId}", 
                        paymentIntentId, existingRefund.Id);
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

                _logger.LogInformation("✅ STRIPE REFUND CREATED - RefundId: {RefundId}, PaymentIntentId: {PaymentIntentId}, Percentage: {Percentage}%", 
                    refund.Id, paymentIntentId, refundPercentage);

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

                _logger.LogInformation("✅ GENERIC REFUND COMPLETED - RefundId: {RefundId}, UserId: {UserId}, Amount: {Amount}€, Percentage: {Percentage}%", 
                    refund.Id, userId, refundAmount, refundPercentage);

                return true;
            }
            catch (StripeException stripeEx)
            {
                _logger.LogError(stripeEx, "❌ STRIPE ERROR PROCESSING GENERIC REFUND - PaymentIntentId: {PaymentIntentId}, Error: {Error}", 
                    paymentIntentId, stripeEx.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ GENERAL ERROR PROCESSING GENERIC REFUND - PaymentIntentId: {PaymentIntentId}, Error: {Error}", 
                    paymentIntentId, ex.Message);
                return false;
            }
        }

        [HttpPost("hire-service")]
        public async Task<IActionResult> HireService([FromBody] HireServiceDto request)
        {
            // 🚨 VALIDACIÓN DE ENTRADA
            if (request == null)
            {
                _logger.LogError("Request is null");
                return BadRequest(new { message = "Request cannot be null" });
            }

            if (request.SearchServiceId <= 0)
            {
                _logger.LogError("Invalid SearchServiceId: {SearchServiceId}", request.SearchServiceId);
                return BadRequest(new { message = "Invalid service ID" });
            }

            if (request.SearchId <= 0)
            {
                _logger.LogError("Invalid SearchId: {SearchId}", request.SearchId);
                return BadRequest(new { message = "Invalid search ID" });
            }

            _logger.LogInformation("HireService endpoint invoked for searchServiceId={SearchServiceId}", request.SearchServiceId);

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var service = await _context.SearchServices
                    .Include(ss => ss.ExpertProfile)
                    .ThenInclude(ep => ep.User)
                    .FirstOrDefaultAsync(ss => ss.Id == request.SearchServiceId);

                if (service == null)
                {
                    _logger.LogError("Service not found for searchServiceId={SearchServiceId}", request.SearchServiceId);
                    return NotFound(new { message = "Service not found" });
                }

                // ✅ VALIDACIÓN CENTRALIZADA: Verificar que el experto puede recibir pagos
                if (service.ExpertProfile != null)
                {
                    var validationResult = await _stripeValidationService.ValidateExpertCanReceivePaymentsAsync(
                        service.ExpertProfile, "contratar servicio");
                    
                    if (!validationResult.IsValid)
                    {
                        _logger.LogWarning("Service hire blocked due to expert Stripe status: serviceId={ServiceId}, expertId={ExpertId}, stripeStatus={StripeStatus}", 
                            service.Id, service.ExpertProfile.UserId, validationResult.StripeStatus);
                        
                        return BadRequest(new { 
                            message = validationResult.ErrorMessage,
                            stripeStatus = validationResult.StripeStatus,
                            requiresStripeSetup = validationResult.RequiresStripeSetup,
                            canRetry = validationResult.CanRetry
                        });
                    }
                }

                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var user = await _context.Users
                    .FromSqlRaw("SELECT * FROM \"Users\" WHERE \"Id\" = {0} FOR UPDATE", userId)
                    .FirstOrDefaultAsync();
                if (user == null)
                {
                    _logger.LogError("User not found for userId={UserId}", userId);
                    return NotFound(new { message = "User not found" });
                }

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
                    _logger.LogError("User already has an active hire for this service: userId={UserId}, serviceId={ServiceId}, existingHireId={ExistingHireId}", 
                        userId, service.Id, existingHire.Id);
                    return BadRequest(new { message = "Ya tienes una contratación activa para este servicio" });
                }

                // 💳 SIEMPRE PAGAR CON STRIPE - NO USAR SALDO INTERNO
                var domain = "https://atrapo.io";
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
                                UnitAmount = (long)(service.Price * 100),
                                ProductData = new SessionLineItemPriceDataProductDataOptions
                                {
                                    Name = $"Payment for Service {service.Id}"
                                }
                            },
                            Quantity = 1
                        }
                    },
                    Mode = "payment",
                    SuccessUrl = $"{domain}/success?userId={userId}",
                    CancelUrl = $"{domain}/cancel",
                    CustomerEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown@example.com",
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() },
                        { "serviceId", service.Id.ToString() },
                        { "amount", service.Price.ToString() },
                        { "searchId", request.SearchId.ToString() },
                        { "pendingHire", "true" }
                    }
                };

                var stripeService = new SessionService();
                Session session;
                try
                {
                    session = await stripeService.CreateAsync(options);
                    _logger.LogInformation("Stripe Checkout session created: sessionId={SessionId}, url={SessionUrl}", session.Id, session.Url);
                }
                catch (StripeException ex)
                {
                    _logger.LogError(ex, "Stripe error creating checkout session for userId={UserId}, serviceId={ServiceId}: {ErrorMessage}", userId, service.Id, ex.Message);
                    return StatusCode(500, new { message = "Failed to create payment session" });
                }

                return Ok(new { url = session.Url });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error hiring service: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to hire service" });
            }
        }

        [HttpPost("complete-service")]
        public async Task<IActionResult> CompleteService([FromBody] CompleteServiceDto request)
        {
            _logger.LogInformation("CompleteService endpoint invoked for searchHireId={SearchHireId}", request.SearchHireId);

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", request.SearchHireId)
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .Include(sh => sh.Client)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", request.SearchHireId);
                    return NotFound(new { message = "Service not found" });
                }

                if (searchHire.ClientId != userId)
                {
                    _logger.LogError("User is not the client for searchHireId={SearchHireId}, userId={UserId}", searchHire.Id, userId);
                    return Unauthorized(new { message = "Unauthorized to complete this service" });
                }

                if (searchHire.Status.StatusValue != SearchHireStatus.Pending.ToStringValue() && searchHire.Status.StatusValue != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                {
                    _logger.LogError("Service cannot be approved in status: searchHireId={SearchHireId}, status={Status}", searchHire.Id, searchHire.Status);
                    return BadRequest(new { message = "Service cannot be approved in current state" });
                }

                if (request.ClientApproved == null)
                {
                    _logger.LogError("ClientApproved is required for client action: searchHireId={SearchHireId}", searchHire.Id);
                    return BadRequest(new { error = "ClientApproved is required" });
                }

                // 🔄 USAR EXECUTION STRATEGY para compatibilidad con NpgsqlRetryingExecutionStrategy
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        searchHire.ClientApproved = request.ClientApproved.Value;
                        if (!searchHire.ClientApproved.Value)
                        {
                            // 🛡️ DISPUTA: Cliente rechaza servicio → Abrir disputa para revisión admin
                            searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());
                            searchHire.UpdatedAt = DateTime.UtcNow;
                            _logger.LogInformation("Client opened dispute for searchHireId={SearchHireId}", searchHire.Id);
                            
                            // 📝 LOGGING: Registrar acción de disputa
                            await _userActionLogging.LogUserActionAsync(userId, "DISPUTE_SERVICE", 
                                $"Abrió disputa para servicio {searchHire.Id}", 
                                "SearchHire", searchHire.Id);
                        }
                        else
                        {
                            var ok = await _refundService.ProcessMoneyDistributionAsync(
                                searchHire.Id,
                                "completed",
                                "Client approved service",
                                userId);
                            if (!ok)
                            {
                                await transaction.RollbackAsync();
                                _logger.LogError("Money distribution failed for searchHireId={SearchHireId}", searchHire.Id);
                                return StatusCode(500, new { message = "Failed to process payment to expert" });
                            }

                            searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Completed.ToStringValue());
                            searchHire.UpdatedAt = DateTime.UtcNow;
                            _logger.LogInformation("Client approved service, distribution completed for searchHireId={SearchHireId}", searchHire.Id);
                            
                            // 📝 LOGGING: Registrar acción de aprobación
                            await _userActionLogging.LogUserActionAsync(userId, "APPROVE_SERVICE", 
                                $"Aprobó servicio {searchHire.Id} y se completó distribución", 
                                "SearchHire", searchHire.Id);
                        }

                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();

                        return Ok(new { message = searchHire.ClientApproved.Value ? "Service completed" : "Dispute opened" });
                    }
                    catch (StripeException ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Stripe error processing transfer for searchHireId={SearchHireId}: {ErrorMessage}", searchHire.Id, ex.Message);
                        return StatusCode(500, new { message = "Failed to process payment to expert" });
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Database error completing service for searchHireId={SearchHireId}", searchHire.Id);
                        return StatusCode(500, new { message = "Failed to complete service" });
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing service: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to complete service" });
            }
        }

        [HttpPost("cancel-service")]
        [Authorize(Roles = "Expert")]
        public async Task<IActionResult> CancelService([FromBody] CancelServiceDto request)
        {
            _logger.LogInformation("CancelService endpoint invoked for searchHireId={SearchHireId}", request.SearchHireId);

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} AND \"ExpertId\" = {1} FOR UPDATE", request.SearchHireId, userId)
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Appointment)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                            .ThenInclude(st => st.ServiceTypeCategory)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found or user is not the expert for searchHireId={SearchHireId}, userId={UserId}", request.SearchHireId, userId);
                    return NotFound(new { message = "Service not found or unauthorized" });
                }

                if (searchHire.Status.StatusValue != SearchHireStatus.Pending.ToStringValue())
                {
                    _logger.LogError("Service is not in pending status: searchHireId={SearchHireId}, status={Status}", searchHire.Id, searchHire.Status);
                    return BadRequest(new { message = "Service is not pending" });
                }

                // Verificar contador de cancelaciones del experto
                var appointment = searchHire.Appointment;
                if (appointment == null)
                {
                    _logger.LogError("No appointment found for searchHireId={SearchHireId}", searchHire.Id);
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
                    _logger.LogError("Appointment status not found: {StatusValue}", statusValue);
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
                    _logger.LogInformation("Skipping money distribution for non-finalization status: {StatusValue}", statusValue);
                }

                if (!refundSuccess)
                {
                    _logger.LogError("Failed to process money distribution for searchHireId={SearchHireId}", searchHire.Id);
                    
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
                    await _userActionLogging.LogUserActionAsync(userId, "CANCEL_SERVICE", 
                    $"Canceló servicio {searchHire.Id} como experto con refund real de Stripe", 
                        "SearchHire", searchHire.Id);

                _logger.LogInformation("Service cancelled with central refund for searchHireId={SearchHireId}, clientId={ClientId}", searchHire.Id, searchHire.ClientId);
                return Ok(new { message = "Service cancelled and refunded via Stripe" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling service: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to cancel service" });
            }
        }


        [HttpPost("force-finalize")]
        public async Task<IActionResult> ForceFinalize([FromBody] ForceFinalizeDto request)
        {
            // 🔐 SEGURIDAD: Verificar rol en lugar de email
            if (!_authService.IsAdmin(User))
            {
                _logger.LogError("Unauthorized access attempt to force-finalize endpoint by user={UserId}", User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
                return Unauthorized(new { message = "Admin access required" });
            }

            _logger.LogInformation("ForceFinalize endpoint invoked for searchHireId={SearchHireId}, resolveInFavorOfClient={ResolveInFavorOfClient}",
                request.SearchHireId, request.ResolveInFavorOfClient);

            try
            {
                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", request.SearchHireId)
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", request.SearchHireId);
                    return NotFound(new { message = "Service not found" });
                }

                _logger.LogInformation("Processing force-finalize for searchHireId={SearchHireId}, current status={Status}",
                    searchHire.Id, searchHire.Status);

                if (request.ResolveInFavorOfClient)
                {
                    var success = await _refundService.ProcessMoneyDistributionAsync(
                        searchHire.Id,
                        "dispute_resolved_client",
                        "Force finalize in favor of client",
                        int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0"));

                    if (!success)
                    {
                        _logger.LogError("Failed to process client refund via orchestrator for force-finalize searchHireId={SearchHireId}", searchHire.Id);
                        return StatusCode(500, new { message = "Failed to process client refund" });
                    }

                    searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.DisputeResolvedClient.ToStringValue());
                    _logger.LogInformation("Force-finalized in favor of client via orchestrator for searchHireId={SearchHireId}", searchHire.Id);

                    await _userActionLogging.LogAdminActionAsync(int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0"), "FORCE_FINALIZE_CLIENT_REFUND", 
                        $"Finalizó forzadamente servicio {searchHire.Id} a favor del cliente con orquestador", 
                        "SearchHire", searchHire.Id);

                    return Ok(new { message = "Service finalized successfully in favor of client" });
                }
                else
                {
                    _logger.LogWarning("Force finalize in favor of expert is no longer supported for searchHireId={SearchHireId}", searchHire.Id);
                    return BadRequest(new { message = "Force finalize in favor of expert is no longer supported. Use dispute resolution instead." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finalizing service: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to finalize service" });
            }
        }

        [HttpPost("resolve-dispute")]
        public async Task<IActionResult> ResolveDispute([FromBody] ResolveDisputeDto request)
        {
            // 🔐 SEGURIDAD: Verificar rol en lugar de email
            if (!_authService.IsAdmin(User))
            {
                _logger.LogError("Unauthorized access attempt to resolve-dispute endpoint by user={UserId}", User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
                return Unauthorized(new { message = "Admin access required" });
            }

            _logger.LogInformation("ResolveDispute endpoint invoked for searchHireId={SearchHireId}", request.SearchHireId);

            try
            {
                if (string.IsNullOrWhiteSpace(request.Resolution))
                {
                    _logger.LogError("Resolution reason is required for searchHireId={SearchHireId}", request.SearchHireId);
                    return BadRequest(new { message = "Resolution reason is required" });
                }

                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", request.SearchHireId)
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", request.SearchHireId);
                    return NotFound(new { message = "Service not found" });
                }

                if (searchHire.Status.StatusValue != SearchHireStatus.Disputed.ToStringValue())
                {
                    _logger.LogError("Service is not in disputed status: searchHireId={SearchHireId}, status={Status}", searchHire.Id, searchHire.Status);
                    return BadRequest(new { message = "Service is not disputed" });
                }

                var dispute = await _context.Disputes
                    .FirstOrDefaultAsync(d => d.SearchHireId == searchHire.Id && d.Status == "Pending");

                if (dispute == null)
                {
                    _logger.LogError("No pending dispute found for searchHireId={SearchHireId}", searchHire.Id);
                    return NotFound(new { message = "No pending dispute found" });
                }

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    dispute.Status = "Resolved";
                    dispute.ResolutionComments = request.Resolution;

                if (request.ResolveInFavorOfClient)
                {
                    // Orquestador: 100% a cliente según configuración de disputa
                    var success = await _refundService.ProcessMoneyDistributionAsync(
                        searchHire.Id,
                        "dispute_resolved_client",
                        $"Dispute resolved in favor of client: {request.Resolution}",
                        int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0"));

                    if (!success)
                    {
                        _logger.LogError("Failed to process client refund via orchestrator for dispute searchHireId={SearchHireId}", searchHire.Id);
                        await transaction.RollbackAsync();
                        return StatusCode(500, new { message = "Failed to process client refund" });
                    }

                    searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.DisputeResolvedClient.ToStringValue());
                    _logger.LogInformation("Dispute resolved in favor of client via orchestrator for searchHireId={SearchHireId}", searchHire.Id);

                    // 📝 LOGGING
                    var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                    await _userActionLogging.LogAdminActionAsync(adminUserId, "RESOLVE_DISPUTE_CLIENT_REFUND", 
                        $"Resolvió disputa {searchHire.Id} a favor del cliente con orquestador: {request.Resolution}", 
                        "SearchHire", searchHire.Id);
                }
                    else
                    {
                    // Orquestador: 100% a experto según configuración de disputa
                    var success = await _refundService.ProcessMoneyDistributionAsync(
                        searchHire.Id,
                        "dispute_resolved_expert",
                        $"Dispute resolved in favor of expert: {request.Resolution}",
                        int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0"));

                    if (!success)
                    {
                        _logger.LogError("Failed to process expert payout via orchestrator for dispute searchHireId={SearchHireId}", searchHire.Id);
                        await transaction.RollbackAsync();
                        return StatusCode(500, new { message = "Failed to process expert payout" });
                    }

                    searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.DisputeResolvedExpert.ToStringValue());
                    _logger.LogInformation("Dispute resolved in favor of expert via orchestrator for searchHireId={SearchHireId}", searchHire.Id);

                    // 📝 LOGGING
                    var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                    await _userActionLogging.LogAdminActionAsync(adminUserId, "RESOLVE_DISPUTE_EXPERT", 
                        $"Resolvió disputa {searchHire.Id} a favor del experto con orquestador: {request.Resolution}", 
                        "SearchHire", searchHire.Id);
                    }
                    searchHire.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    return Ok(new { message = "Dispute resolved" });
                }
                catch (StripeException ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Stripe error resolving dispute for searchHireId={SearchHireId}: {ErrorMessage}", searchHire.Id, ex.Message);
                    return StatusCode(500, new { message = "Failed to process dispute resolution" });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Database error resolving dispute for searchHireId={SearchHireId}", searchHire.Id);
                    return StatusCode(500, new { message = "Failed to resolve dispute" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving dispute: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to resolve dispute" });
            }
        }


        [HttpPost("process-expired-services")]
        public async Task<IActionResult> ProcessExpiredServices()
        {
            // 🔐 SEGURIDAD: Verificar rol en lugar de email
            if (!_authService.IsAdmin(User))
            {
                _logger.LogError("Unauthorized access attempt by user={UserId}", User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
                return Unauthorized(new { message = "Admin access required" });
            }

            _logger.LogInformation("ProcessExpiredServices endpoint invoked");

            try
            {
                await _subscriptionService.ProcessExpiredServicesAsync();
                return Ok(new { message = "Processed expired services" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing expired services: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to process expired services" });
            }
        }


        /// <summary>
        /// Obtiene la configuración de distribución de dinero según el estado y categorías
        /// </summary>
        /// <param name="status">Estado de la cita</param>
        /// <param name="categoryId">ID de la categoría</param>
        /// <param name="serviceTypeCategoryId">ID de la categoría del tipo de servicio</param>
        /// <returns>Configuración de distribución de dinero</returns>


        /// <summary>
        /// Verifica si el experto ha respondido en las primeras 24 horas
        /// </summary>
        /// <param name="searchHireId">ID del servicio contratado</param>
        public async Task CheckExpertResponseAsync(int searchHireId)
        {
            _logger.LogInformation("Checking expert response for searchHireId={SearchHireId}", searchHireId);

            try
            {
                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", searchHireId)
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                    return;
                }

                // Verificar que el servicio esté activo
                if (searchHire.Status.StatusValue != SearchHireStatus.Pending.ToStringValue())
                {
                    _logger.LogInformation("SearchHire is not active for searchHireId={SearchHireId}, current status={Status}", 
                        searchHireId, searchHire.Status);
                    return;
                }

                // Calcular si han pasado 24 horas desde la contratación
                var timeSinceHire = DateTime.UtcNow - searchHire.CreatedAt;
                if (timeSinceHire.TotalHours < 24)
                {
                    _logger.LogInformation("Less than 24 hours have passed for searchHireId={SearchHireId}, hours={Hours}", 
                        searchHireId, timeSinceHire.TotalHours);
                    return;
                }

                // Verificar si el experto ha enviado algún mensaje
                var hasExpertMessage = await _context.Messages
                    .AnyAsync(m => m.Conversation.SearchHireId == searchHireId && 
                                   m.SenderId == searchHire.ExpertId && 
                                   m.SentAt > searchHire.CreatedAt);

                if (!hasExpertMessage)
                {
                    _logger.LogWarning("Expert has not responded within 24 hours for searchHireId={SearchHireId}, processing automatic refund via orchestrator", searchHireId);

                    var success = await _refundService.ProcessMoneyDistributionAsync(
                        searchHireId,
                        "appointment_cancelled_by_no_response",
                        "Automatic refund: expert did not respond within 24 hours",
                        searchHire.ClientId);

                    if (success)
                    {
                        _logger.LogInformation("Automatic refund (no response) processed successfully for searchHireId={SearchHireId}", searchHireId);
                    }
                    else
                    {
                        _logger.LogError("Failed to process automatic refund (no response) for searchHireId={SearchHireId}", searchHireId);
                    }
                }
                else
                {
                    _logger.LogInformation("Expert has responded for searchHireId={SearchHireId}, no action needed", searchHireId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CheckExpertResponseAsync for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
            }
        }


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
            _logger.LogError("No money distribution configuration found for status: {Status}, categoryId: {CategoryId}, serviceTypeCategoryId: {ServiceTypeCategoryId}. Configuration must be created by admin.", 
                status, categoryId, serviceTypeCategoryId);
            return null;
        }

        // ✅ REMOVED: HandleLoadMoneyCompleted method eliminated - balance system removed

        private async Task HandleCheckoutSessionCompleted(int userId, int planId, bool isYearly, string subscriptionId)
        {
            _logger.LogInformation("Handling checkout.session.completed for userId={UserId}, planId={PlanId}, isYearly={IsYearly}, subscriptionId={SubscriptionId}", userId, planId, isYearly, subscriptionId);

            // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
            var user = await _context.Users
                .FromSqlRaw("SELECT * FROM \"Users\" WHERE \"Id\" = {0} FOR UPDATE", userId)
                .FirstOrDefaultAsync();
            if (user == null)
            {
                _logger.LogError("User not found for userId={UserId}", userId);
                return;
            }

            _logger.LogInformation("Found user: userId={UserId}, email={Email}", user.Id, user.Email);

            user.SubscriptionPlanId = planId;
            _logger.LogInformation("Updated user's subscription plan to planId={PlanId}", planId);

            var userSubscription = new UserSubscription
            {
                UserId = userId,
                SubscriptionPlanId = planId,
                IsYearly = isYearly,
                StartDate = DateTime.UtcNow,
                EndDate = DateTime.UtcNow.AddMonths(isYearly ? 12 : 1),
                Status = "active",
                StripeSubscriptionId = subscriptionId
            };

            _logger.LogInformation("Creating new UserSubscription: userId={UserId}, planId={PlanId}, isYearly={IsYearly}, subscriptionId={SubscriptionId}", userId, planId, isYearly, subscriptionId);

            _context.UserSubscriptions.Add(userSubscription);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Successfully saved UserSubscription for userId={UserId}", userId);
        }

        private async Task HandleSubscriptionUpdated(Subscription subscription)
        {
            _logger.LogInformation("Handling subscription updated for subscriptionId={SubscriptionId}", subscription.Id);

            var userSubscription = await _context.UserSubscriptions
                .FirstOrDefaultAsync(us => us.StripeSubscriptionId == subscription.Id);

            if (userSubscription != null)
            {
                _logger.LogInformation("Found UserSubscription: id={Id}, userId={UserId}", userSubscription.Id, userSubscription.UserId);

                userSubscription.Status = subscription.Status;
                userSubscription.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                _logger.LogInformation("Updated UserSubscription: id={Id}, new status={Status}", userSubscription.Id, subscription.Status);
            }
            else
            {
                _logger.LogWarning("No UserSubscription found for subscriptionId={SubscriptionId}", subscription.Id);
            }
        }

        private async Task HandleSubscriptionCanceled(Subscription subscription)
        {
            _logger.LogInformation("Handling subscription canceled for subscriptionId={SubscriptionId}", subscription.Id);

            var userSubscription = await _context.UserSubscriptions
                .Include(us => us.User)
                .FirstOrDefaultAsync(us => us.StripeSubscriptionId == subscription.Id);

            if (userSubscription != null)
            {
                _logger.LogInformation("Found UserSubscription: id={Id}, userId={UserId}", userSubscription.Id, userSubscription.UserId);

                userSubscription.Status = "cancelled";
                userSubscription.EndDate = DateTime.UtcNow;
                userSubscription.UpdatedAt = DateTime.UtcNow;

                var freePlan = await _context.SubscriptionPlans
                    .FirstOrDefaultAsync(p => p.PriceMonthly == 0);

                if (freePlan != null)
                {
                    userSubscription.User.SubscriptionPlanId = freePlan.Id;
                    _logger.LogInformation("Reverted user to free plan: planId={PlanId}", freePlan.Id);
                }
                else
                {
                    _logger.LogWarning("No free plan found to revert user");
                }

                await _context.SaveChangesAsync();
                _logger.LogInformation("Updated UserSubscription to cancelled status: id={Id}", userSubscription.Id);
            }
            else
            {
                _logger.LogWarning("No UserSubscription found for subscriptionId={SubscriptionId}", subscription.Id);
            }
        }

        private async Task HandlePaymentSucceeded(Invoice invoice)
        {
            if (string.IsNullOrEmpty(invoice.SubscriptionId))
            {
                _logger.LogWarning("Invoice has no subscriptionId, skipping payment succeeded handling");
                return;
            }

            _logger.LogInformation("Handling payment succeeded for invoiceId={InvoiceId}, subscriptionId={SubscriptionId}", invoice.Id, invoice.SubscriptionId);

            var userSubscription = await _context.UserSubscriptions
                .FirstOrDefaultAsync(us => us.StripeSubscriptionId == invoice.SubscriptionId);

            if (userSubscription != null)
            {
                _logger.LogInformation("Found UserSubscription: id={Id}, userId={UserId}", userSubscription.Id, userSubscription.UserId);

                userSubscription.EndDate = userSubscription.IsYearly ?
                    userSubscription.EndDate.AddYears(1) :
                    userSubscription.EndDate.AddMonths(1);
                userSubscription.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                _logger.LogInformation("Updated UserSubscription end date: id={Id}, new endDate={EndDate}", userSubscription.Id, userSubscription.EndDate);
            }
            else
            {
                _logger.LogWarning("No UserSubscription found for subscriptionId={SubscriptionId}", invoice.SubscriptionId);
            }
        }

        private async Task HandlePaymentFailed(Invoice invoice)
        {
            if (string.IsNullOrEmpty(invoice.SubscriptionId))
            {
                _logger.LogWarning("Invoice has no subscriptionId, skipping payment failed handling");
                return;
            }

            _logger.LogInformation("Handling payment failed for invoiceId={InvoiceId}, subscriptionId={SubscriptionId}", invoice.Id, invoice.SubscriptionId);

            var userSubscription = await _context.UserSubscriptions
                .Include(us => us.User)
                .FirstOrDefaultAsync(us => us.StripeSubscriptionId == invoice.SubscriptionId);

            if (userSubscription != null)
            {
                _logger.LogInformation("Found UserSubscription: id={Id}, userId={UserId}", userSubscription.Id, userSubscription.UserId);

                userSubscription.Status = "payment_failed";
                userSubscription.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                _logger.LogInformation("Updated UserSubscription to payment_failed status: id={Id}", userSubscription.Id);
            }
            else
            {
                _logger.LogWarning("No UserSubscription found for subscriptionId={SubscriptionId}", invoice.SubscriptionId);
            }
        }

        public class LoadMoneyDto
        {
            public decimal Amount { get; set; }
        }

        public class HireServiceDto
        {
            public int SearchServiceId { get; set; }
            public int SearchId { get; set; }
        }

        public class CompleteServiceDto
        {
            public int SearchHireId { get; set; }
            public bool? ClientApproved { get; set; }
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

            // Rechazos PERMANENTES que bloquean crear nueva cuenta:
            // - rejected.fraud: Fraude detectado - NO permitir crear otra cuenta
            // - rejected.terms_of_service: Violación de términos - NO permitir crear otra cuenta
            // - rejected.unsupported_business: Negocio no permitido - NO permitir crear otra cuenta
            // - rejected.other: Otros motivos graves - NO permitir crear otra cuenta
            // - under_review: En revisión manual - NO permitir crear otra mientras se revisa
            // - listed: Lista de sanciones (OFAC, etc.) - NO permitir crear otra cuenta
            // - other: Motivo desconocido genérico - Por seguridad, bloquear hasta revisar
            if (disabledReason.StartsWith("rejected.") || 
                disabledReason == "under_review" || 
                disabledReason == "listed" ||
                disabledReason == "other")
            {
                return true; // Permanente, bloquea
            }

            // Por defecto, si no conocemos el tipo, bloqueamos por seguridad
            // Esto previene que valores nuevos o desconocidos permitan crear cuentas sin revisar
            return true;
        }

        /// <summary>
        /// Verifica si un evento ya fue procesado para evitar duplicados
        /// </summary>
        private async Task<bool> IsEventProcessedAsync(string eventId)
        {
            try
            {
                return await _context.ProcessedWebhookEvents
                    .AnyAsync(e => e.EventId == eventId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ ERROR: Fallo al verificar idempotencia para eventId={EventId}", eventId);
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
                    _logger.LogInformation("🔄 DEBUG: Evento actualizado: eventId={EventId}, status={Status}", eventId, status);
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
                    _logger.LogInformation("✅ DEBUG: Evento creado: eventId={EventId}, status={Status}", eventId, status);
                }
                
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ ERROR: Fallo al marcar evento como procesado: eventId={EventId}", eventId);
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
            _logger.LogInformation("💳 Payment Intent Succeeded: {PaymentIntentId}, Amount: {Amount}, Currency: {Currency}", 
                paymentIntent.Id, paymentIntent.Amount, paymentIntent.Currency);

            try
            {
                // Aquí puedes agregar lógica específica para cuando un pago se completa exitosamente
                // Por ejemplo, actualizar el estado de una orden, enviar confirmación por email, etc.
                
                if (paymentIntent.Metadata != null && paymentIntent.Metadata.Count > 0)
                {
                    _logger.LogInformation("Payment Intent metadata: {Metadata}", 
                        string.Join(", ", paymentIntent.Metadata.Select(kv => $"{kv.Key}={kv.Value}")));
                }

                // Si tienes un sistema de órdenes, podrías actualizar el estado aquí
                // await UpdateOrderStatus(paymentIntent.Metadata["order_id"], "paid");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling payment intent succeeded: {PaymentIntentId}", paymentIntent.Id);
            }
        }

        private async Task HandlePaymentIntentFailed(PaymentIntent paymentIntent)
        {
            _logger.LogWarning("❌ Payment Intent Failed: {PaymentIntentId}, Amount: {Amount}, Currency: {Currency}, LastPaymentError: {LastPaymentError}", 
                paymentIntent.Id, paymentIntent.Amount, paymentIntent.Currency, 
                paymentIntent.LastPaymentError?.Message ?? "No error details");

            try
            {
                // Aquí puedes agregar lógica para manejar pagos fallidos
                // Por ejemplo, notificar al usuario, actualizar el estado de la orden, etc.
                
                if (paymentIntent.Metadata != null && paymentIntent.Metadata.Count > 0)
                {
                    _logger.LogInformation("Failed Payment Intent metadata: {Metadata}", 
                        string.Join(", ", paymentIntent.Metadata.Select(kv => $"{kv.Key}={kv.Value}")));
                }

                // Si tienes un sistema de órdenes, podrías actualizar el estado aquí
                // await UpdateOrderStatus(paymentIntent.Metadata["order_id"], "payment_failed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling payment intent failed: {PaymentIntentId}", paymentIntent.Id);
            }
        }

        [HttpGet("all-money-distribution-configs")]
        public async Task<IActionResult> GetAllMoneyDistributionConfigs()
        {
            try
            {
                // 🔐 SEGURIDAD: Verificar rol en lugar de email
                if (!_authService.IsAdmin(User))
                {
                    _logger.LogError("Unauthorized access attempt to money distribution configs by user={UserId}", User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
                    return Unauthorized(new { message = "Admin access required" });
                }

                var configs = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Include(sc => sc.Category)
                    .Include(sc => sc.ServiceTypeCategory)
                    .Where(c => c.IsActive)
                    .OrderBy(c => c.CategoryId)
                    .ThenBy(c => c.ServiceTypeCategoryId)
                    .ThenBy(c => c.Status.StatusValue)
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
                    })
                    .ToListAsync();

                return Ok(new { 
                    message = "All money distribution configurations",
                    count = configs.Count,
                    configs 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all money distribution configs");
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Maneja el rechazo de una cuenta de Stripe, notificando tanto al admin como al experto
        /// </summary>
        private async Task HandleAccountRejection(int expertId, string rejectionReason)
        {
            _logger.LogWarning("🚨 CRITICAL: Account rejected for expertId={ExpertId}, reason={Reason}", expertId, rejectionReason);
            
            try
            {
                // 1. Verificar si el experto tiene contrataciones activas
                var activeHires = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Where(sh => sh.ExpertId == expertId && 
                                sh.Status.StatusValue == "pending")
                    .CountAsync();
                
                _logger.LogInformation("Found {Count} active hires for rejected expert {ExpertId}", activeHires, expertId);
                
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
                
                _logger.LogInformation("✅ Account rejection handled for expert {ExpertId} with {Count} active hires", expertId, activeHires);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle account rejection for expert {ExpertId}", expertId);
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
                    _logger.LogWarning("Expert not found for notification - ExpertId: {ExpertId}", expertId);
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
                
                _logger.LogInformation("✅ Expert notification created for account rejection - ExpertId: {ExpertId}", expertId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create expert notification for account rejection - ExpertId: {ExpertId}", expertId);
            }
        }

        /// <summary>
        /// Maneja la desautorización de una cuenta de Stripe, notificando tanto al admin como al experto
        /// (porque puede tener contrataciones activas)
        /// </summary>
        private async Task HandleAccountDeauthorization(int expertId, string deauthorizationReason)
        {
            _logger.LogWarning("🚨 CRITICAL: Account deauthorized for expertId={ExpertId}, reason={Reason}", expertId, deauthorizationReason);
            
            try
            {
                // 1. Verificar si el experto tiene contrataciones activas
                var activeHires = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Where(sh => sh.ExpertId == expertId && 
                                sh.Status.StatusValue == "pending")
                    .CountAsync();
                
                _logger.LogInformation("Found {Count} active hires for deauthorized expert {ExpertId}", activeHires, expertId);
                
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
                
                _logger.LogInformation("✅ Account deauthorization handled for expert {ExpertId} with {Count} active hires", expertId, activeHires);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle account deauthorization for expert {ExpertId}", expertId);
            }
        }

        /// <summary>
        /// Solo notifica al experto cuando su cuenta es rechazada (no puede tener contrataciones activas)
        /// </summary>
        private async Task NotifyExpertOnly(int expertId, string rejectionReason)
        {
            _logger.LogInformation("📧 Notifying expert only for account rejection - ExpertId: {ExpertId}, reason: {Reason}", expertId, rejectionReason);
            
            try
            {
                var expert = await _context.Users.FindAsync(expertId);
                if (expert == null) 
                {
                    _logger.LogWarning("Expert not found for notification - ExpertId: {ExpertId}", expertId);
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
                
                _logger.LogInformation("✅ Expert-only notification created for account rejection - ExpertId: {ExpertId}", expertId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create expert-only notification for account rejection - ExpertId: {ExpertId}", expertId);
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
                    _logger.LogWarning("Expert not found for notification - ExpertId: {ExpertId}", expertId);
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
                
                _logger.LogInformation("✅ Expert notification created for account deauthorization - ExpertId: {ExpertId}", expertId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create expert notification for account deauthorization - ExpertId: {ExpertId}", expertId);
            }
        }


    }
}