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
        private readonly IAuthorizationServices _authService;

        public SubscriptionController(AppDbContext context, ILogger<SubscriptionController> logger, IConfiguration configuration, ISubscriptionService subscriptionService, StorageClient storageClient, IUserActionLoggingService userActionLogging, SystemStatusService systemStatusService, IAuthorizationServices authService)
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

                using var transaction = await _context.Database.BeginTransactionAsync();
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

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // Guardar temporalmente el account ID hasta que se complete el onboarding
                    expertProfile.PendingStripeAccountId = account.Id;
                    expertProfile.OnboardingCompleted = false;
                    await _context.SaveChangesAsync();

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
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Database error saving Stripe account for userId={UserId}", userId);
                    return StatusCode(500, new { message = "Failed to save Stripe account" });
                }
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

                var status = new OnboardingStatusDto
                {
                    HasStripeAccount = !string.IsNullOrEmpty(expertProfile.StripeAccountId),
                    HasPendingOnboarding = !string.IsNullOrEmpty(expertProfile.PendingStripeAccountId),
                    OnboardingCompleted = expertProfile.OnboardingCompleted,
                    StripeAccountId = expertProfile.StripeAccountId,
                    StripeStatus = expertProfile.StripeStatus.ToString(),
                    StripeStatusDetails = expertProfile.StripeStatusDetails,
                    CanAccessStripe = !string.IsNullOrEmpty(expertProfile.StripeAccountId) && expertProfile.OnboardingCompleted
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
                if (expertProfile.StripeStatus == StripeStatus.Rejected && !string.IsNullOrEmpty(expertProfile.StripeAccountId))
                {
                    try
                    {
                        var accountService = new AccountService();
                        var account = await accountService.GetAsync(expertProfile.StripeAccountId);
                        rejectionReason = account.Requirements?.DisabledReason;
                    }
                    catch (StripeException ex)
                    {
                        _logger.LogWarning(ex, "Could not retrieve rejection reason for userId={UserId}: {ErrorMessage}", userId, ex.Message);
                    }
                }

                var status = new ExpertStatusDto
                {
                    HasStripeAccount = !string.IsNullOrEmpty(expertProfile.StripeAccountId),
                    HasPendingOnboarding = !string.IsNullOrEmpty(expertProfile.PendingStripeAccountId),
                    OnboardingCompleted = expertProfile.OnboardingCompleted,
                    StripeStatus = expertProfile.StripeStatus.ToString(),
                    StripeStatusDetails = expertProfile.StripeStatusDetails,
                    StripeAccountId = expertProfile.StripeAccountId,
                    CanAccessStripe = expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted,
                    CanCreateServices = expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted,
                    CanReceivePayments = expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted,
                    StatusMessage = GetStatusMessage(expertProfile.StripeStatus),
                    CanRetryOnboarding = expertProfile.StripeStatus == StripeStatus.Rejected || expertProfile.StripeStatus == StripeStatus.NotRequested,
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

                // Si ya tiene cuenta completada, crear login link en lugar de reiniciar
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
                        // Este evento solo indica que el usuario autorizó la aplicación (OAuth)
                        // NO indica que la cuenta esté aprobada o que el onboarding esté completo
                        var authorizedApp = stripeEvent.Data.Object as Application;
                        if (authorizedApp != null)
                        {
                            _logger.LogInformation("🔗 DEBUG: Application authorized: appId={AppId}, accountId={AccountId}", authorizedApp.Id, stripeEvent.Account);
                            // No actualizamos el estado del experto aquí, solo registramos la autorización
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
                                
                                // FIX: Simplificado - usar solo flags básicos que sabemos que funcionan
                                // Para Express accounts, si charges_enabled y payouts_enabled son true,
                                // significa que todas las capabilities necesarias están activas
                                bool paymentsEnabled = chargesEnabled && payoutsEnabled;
                                
                                // Log de capabilities para debug (sin acceder a .Status)
                                _logger.LogInformation("🔍 DEBUG: Capabilities object exists: {Exists}, Transfers exists: {TransfersExists}", 
                                    account.Capabilities != null, account.Capabilities?.Transfers != null);

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
                                
                                // MEJORA: Usar transacción para actualizaciones atómicas
                                var previousStatus = expertProfile.StripeStatus;
                                using (var transaction = await _context.Database.BeginTransactionAsync())
                                {
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
                                }
                                }
                                catch (Exception logicEx)
                                {
                                    _logger.LogError(logicEx, "❌ ERROR: En lógica de verificación para account.updated accountId={AccountId}. Verificar Capabilities o ToS.", account.Id);
                                    // No throw; evita retry innecesario, pero loguea para debug
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
                                            
                                            // Actualizar el perfil con la nueva información de la cuenta
                                            profileByUserId.StripeAccountId = account.Id;
                                            if (!string.IsNullOrEmpty(profileByUserId.PendingStripeAccountId))
                                            {
                                                profileByUserId.PendingStripeAccountId = null;
                                            }
                                            
                                            // Aplicar la misma lógica de aprobación
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
                                            
                                            if (isAccountApproved)
                                            {
                                                profileByUserId.StripeStatus = StripeStatus.Approved;
                                                profileByUserId.OnboardingCompleted = true;
                                                profileByUserId.StripeStatusDetails = "✅ **Cuenta Aprobada**: ¡Excelente! Tu cuenta de pagos está completamente verificada y lista para recibir pagos. Ya puedes crear servicios y comenzar a ganar dinero.";
                                                _logger.LogInformation("🎉 DEBUG: Account approved and profile updated for userId={UserId}", userIdFromMetadata);
                                            }
                                            
                                            await _context.SaveChangesAsync();
                                            // 🚨 MARCAR EVENTO COMO PROCESADO usando el mismo ID que se verificó
                                            await MarkEventAsProcessedAsync(eventIdToCheck, stripeEvent.Type, account.Id, userIdFromMetadata);
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
                                using var transaction = await _context.Database.BeginTransactionAsync();
                                try
                                {
                                    // 🚨 REVERTIR TRANSACCIÓN FINANCIERA
                                    var failedTransaction = await _context.FinancialTransactions
                                        .FirstOrDefaultAsync(ft => ft.RelatedEntityId == searchHire.Id && 
                                                                   ft.TransactionType == "Payout");
                                    
                                    if (failedTransaction != null)
                                    {
                                        // Revertir la transacción de pago al experto
                                        var reversalTransaction = new FinancialTransaction
                                        {
                                            UserId = failedTransaction.UserId,
                                            Amount = -failedTransaction.Amount,
                                            TransactionType = "TransferReversal",
                                            RelatedEntityType = "SearchHire",
                                            RelatedEntityId = searchHire.Id,
                                            CreatedAt = DateTime.UtcNow
                                        };
                                        _context.FinancialTransactions.Add(reversalTransaction);
                                        
                                        // 💳 PROCESAR REFUND REAL EN STRIPE para transferencia fallida
                                        var refundReason = "Transfer to expert failed - automatic refund";
                                        var refundSuccess = await ProcessAutomaticClientRefundAsync(searchHire.Id, refundReason);
                                        
                                        if (!refundSuccess)
                                        {
                                            _logger.LogError("Failed to process Stripe refund for failed transfer searchHireId={SearchHireId}", searchHire.Id);
                                        }
                                    }
                                    
                                    // Actualizar estado
                                searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.TransferFailed.ToStringValue());
                                searchHire.UpdatedAt = DateTime.UtcNow;
                                    
                                await _context.SaveChangesAsync();
                                    await transaction.CommitAsync();
                                    
                                    _logger.LogError("Transfer failed and reverted for searchHireId={SearchHireId}, transferId={TransferId}",
                                    searchHire.Id, transfer.Id);
                                }
                                catch (Exception ex)
                                {
                                    await transaction.RollbackAsync();
                                    _logger.LogError(ex, "Error reverting failed transfer for searchHireId={SearchHireId}, transferId={TransferId}",
                                        searchHire.Id, transfer.Id);
                                }
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
                        CompletionDeadline = DateTime.UtcNow.AddDays(7)
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
                    _logger.LogError(ex, "Error processing pending hire for userId={UserId}, serviceId={ServiceId}", userId, serviceId);
                    throw;
                }
            });
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
                            await ProcessTransferToExpert(searchHire.Id);
                            searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Completed.ToStringValue());
                            searchHire.UpdatedAt = DateTime.UtcNow;
                            _logger.LogInformation("Client approved service, transfer completed for searchHireId={SearchHireId}", searchHire.Id);
                            
                            // 📝 LOGGING: Registrar acción de aprobación
                            await _userActionLogging.LogUserActionAsync(userId, "APPROVE_SERVICE", 
                                $"Aprobó servicio {searchHire.Id} y se completó transferencia", 
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

                // 💳 PROCESAR REFUND REAL EN STRIPE usando el método existente
                var refundReason = $"Expert cancelled service {searchHire.Id}";
                var refundSuccess = await ProcessAutomaticClientRefundAsync(searchHire.Id, refundReason);
                
                if (!refundSuccess)
                {
                    _logger.LogError("Failed to process Stripe refund for searchHireId={SearchHireId}", searchHire.Id);
                    return StatusCode(500, new { message = "Failed to process refund" });
                }

                // Actualizar estado del SearchHire a cancelado
                searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                searchHire.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                // 📝 LOGGING: Registrar acción de cancelación
                await _userActionLogging.LogUserActionAsync(userId, "CANCEL_SERVICE", 
                    $"Canceló servicio {searchHire.Id} como experto con refund real de Stripe", 
                    "SearchHire", searchHire.Id);

                _logger.LogInformation("Service cancelled with real Stripe refund for searchHireId={SearchHireId}, clientId={ClientId}", searchHire.Id, searchHire.ClientId);
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
                    // 💸 REFUND AUTOMÁTICO: Procesar reembolso real a Stripe a favor del cliente
                    var success = await ProcessAutomaticClientRefundAsync(searchHire.Id, "Admin force-finalized in favor of client");
                    
                    if (success)
                    {
                        searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.DisputeResolvedClient.ToStringValue());
                        _logger.LogInformation("Force-finalized in favor of client with automatic refund for searchHireId={SearchHireId}, refunded amount={Amount}",
searchHire.Id, searchHire.Amount);
                        
                        // 📝 LOGGING: Registrar finalización forzada a favor del cliente con refund
                        await _userActionLogging.LogAdminActionAsync(int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0"), "FORCE_FINALIZE_CLIENT_REFUND", 
                            $"Finalizó forzadamente servicio {searchHire.Id} a favor del cliente con refund automático", 
                            "SearchHire", searchHire.Id);
                        
                        return Ok(new { message = "Service finalized successfully in favor of client with automatic refund" });
                    }
                    else
                    {
                        _logger.LogError("Failed to process automatic client refund for searchHireId={SearchHireId}", searchHire.Id);
                        return StatusCode(500, new { message = "Failed to process client refund" });
                    }
                }
                else
                {
                    // Verificar que el experto tenga Stripe configurado antes de transferir
                    if (string.IsNullOrEmpty(searchHire.Expert.ExpertProfile?.StripeAccountId))
                    {
                        _logger.LogError("Expert has no Stripe account configured for searchHireId={SearchHireId}, expertId={ExpertId}", 
                            searchHire.Id, searchHire.ExpertId);
                        return BadRequest(new { message = "Expert has no Stripe account configured" });
                    }
                    
                    // Procesar transferencia al experto
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        await ProcessTransferToExpert(searchHire.Id);
                        // Actualizar estado a disputa resuelta a favor del experto
                        searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.DisputeResolvedExpert.ToStringValue());
                        
                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();
                        
                        // 📝 LOGGING: Registrar finalización forzada a favor del experto
                        await _userActionLogging.LogAdminActionAsync(int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0"), "FORCE_FINALIZE_EXPERT", 
                            $"Finalizó forzadamente servicio {searchHire.Id} a favor del experto", 
                            "SearchHire", searchHire.Id);
                        
                        // 🎯 USAR CONFIGURACIÓN REAL EN LUGAR DE COMISIÓN HARDCODEADA
                        var config = await GetMoneyDistributionConfigAsync("completed", 
                            searchHire.SearchService?.CategoryId, 
                            searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);
                        
                        var actualAmount = config != null ? 
                            searchHire.Amount * (config.ExpertPercentage / 100) : 
                            searchHire.Amount * 0.9m; // Fallback si no hay configuración
                        
                        _logger.LogInformation("Force-finalized in favor of expert for searchHireId={SearchHireId}, transferred amount={Amount}",
                            searchHire.Id, actualAmount);
                        
                        return Ok(new { message = "Service finalized successfully in favor of expert" });
                    }
                    catch (StripeException ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Stripe error finalizing service for searchHireId={SearchHireId}: {ErrorMessage}",
                            searchHire.Id, ex.Message);
                        return StatusCode(500, new { message = "Failed to process service finalization" });
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Database error finalizing service for searchHireId={SearchHireId}", searchHire.Id);
                        return StatusCode(500, new { message = "Failed to finalize service" });
                    }
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
                        // 💸 REFUND AUTOMÁTICO: Procesar reembolso real a Stripe a favor del cliente
                        var success = await ProcessAutomaticClientRefundAsync(searchHire.Id, $"Dispute resolved in favor of client: {request.Resolution}");
                        
                        if (!success)
                        {
                            _logger.LogError("Failed to process automatic client refund for dispute searchHireId={SearchHireId}", searchHire.Id);
                            await transaction.RollbackAsync();
                            return StatusCode(500, new { message = "Failed to process client refund" });
                        }
                        
                        searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.DisputeResolvedClient.ToStringValue());
                        _logger.LogInformation("Dispute resolved in favor of client with automatic refund for searchHireId={SearchHireId}", searchHire.Id);
                        
                        // 📝 LOGGING: Registrar resolución de disputa a favor del cliente con refund
                        var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                        await _userActionLogging.LogAdminActionAsync(adminUserId, "RESOLVE_DISPUTE_CLIENT_REFUND", 
                            $"Resolvió disputa {searchHire.Id} a favor del cliente con refund automático: {request.Resolution}", 
                            "SearchHire", searchHire.Id);
                    }
                    else
                    {
                        // Verificar que el experto tenga Stripe configurado antes de transferir
                        if (string.IsNullOrEmpty(searchHire.Expert.ExpertProfile?.StripeAccountId))
                        {
                            _logger.LogError("Expert has no Stripe account configured for searchHireId={SearchHireId}, expertId={ExpertId}", 
                                searchHire.Id, searchHire.ExpertId);
                            await transaction.RollbackAsync();
                            return BadRequest(new { message = "Expert has no Stripe account configured" });
                        }
                        
                        await ProcessTransferToExpert(searchHire.Id);
                        searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.DisputeResolvedExpert.ToStringValue());
                        _logger.LogInformation("Dispute resolved in favor of expert for searchHireId={SearchHireId}", searchHire.Id);
                        
                        // 📝 LOGGING: Registrar resolución de disputa a favor del experto
                        var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                        await _userActionLogging.LogAdminActionAsync(adminUserId, "RESOLVE_DISPUTE_EXPERT", 
                            $"Resolvió disputa {searchHire.Id} a favor del experto: {request.Resolution}", 
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
        /// Procesa refund automático real a Stripe cuando el admin da la razón al cliente en una disputa
        /// </summary>
        /// <param name="searchHireId">ID del servicio contratado</param>
        /// <param name="reason">Razón del reembolso</param>
        /// <returns>True si se procesó correctamente, false en caso contrario</returns>
        public async Task<bool> ProcessAutomaticClientRefundAsync(int searchHireId, string reason)
        {
            _logger.LogInformation("Processing automatic client refund for searchHireId={SearchHireId}, reason={Reason}", searchHireId, reason);

            try
            {
                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", searchHireId)
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.SearchService)
                    .ThenInclude(ss => ss.ServiceType)
                    .ThenInclude(st => st.ServiceTypeCategory)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                    return false;
                }

                // Obtener configuración de distribución de dinero para cancelación de experto
                var config = await GetMoneyDistributionConfigAsync("appointment_cancelled_by_expert", 
                    searchHire.SearchService?.CategoryId, 
                    searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);
                
                if (config == null)
                {
                    _logger.LogError("No money distribution configuration found for appointment_cancelled_by_expert status for searchHireId={SearchHireId}", searchHireId);
                    return false;
                }

                // Calcular montos según porcentajes de la base de datos
                var clientRefundAmount = searchHire.Amount * (config.ClientPercentage / 100);
                var expertAmount = searchHire.Amount * (config.ExpertPercentage / 100);
                var platformAmount = searchHire.Amount * (config.PlatformPercentage / 100);

                _logger.LogInformation("Money distribution for searchHireId={SearchHireId}: Client={ClientAmount}€ ({ClientPercentage}%), Expert={ExpertAmount}€ ({ExpertPercentage}%), Platform={PlatformAmount}€ ({PlatformPercentage}%)",
                    searchHireId, clientRefundAmount, config.ClientPercentage, expertAmount, config.ExpertPercentage, platformAmount, config.PlatformPercentage);

                // Buscar la transacción de pago original del servicio
                var servicePayment = await _context.FinancialTransactions
                    .Where(ft => ft.UserId == searchHire.ClientId 
                              && ft.TransactionType == "ServicePayment"
                              && ft.RelatedEntityType == "SearchHire"
                              && ft.RelatedEntityId == searchHireId
                              && !string.IsNullOrEmpty(ft.StripePaymentIntentId))
                    .FirstOrDefaultAsync();

                if (servicePayment == null)
                {
                    _logger.LogError("No service payment found for searchHireId={SearchHireId}", searchHireId);
                    return false;
                }

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // 💳 CREAR REFUND REAL EN STRIPE (solo el porcentaje del cliente)
                    var refundOptions = new RefundCreateOptions
                    {
                        PaymentIntent = servicePayment.StripePaymentIntentId,
                        Amount = (long)(clientRefundAmount * 100), // Solo el porcentaje del cliente en céntimos
                        Reason = RefundReasons.RequestedByCustomer,
                        Metadata = new Dictionary<string, string>
                        {
                            { "userId", searchHire.ClientId.ToString() },
                            { "searchHireId", searchHireId.ToString() },
                            { "refundType", "expert_cancellation" },
                            { "reason", reason },
                            { "originalTransactionId", servicePayment.Id.ToString() },
                            { "clientPercentage", config.ClientPercentage.ToString() },
                            { "expertPercentage", config.ExpertPercentage.ToString() },
                            { "platformPercentage", config.PlatformPercentage.ToString() }
                        }
                    };

                    var refundService = new RefundService();
                    var refund = await refundService.CreateAsync(refundOptions);

                    // Actualizar transacción original como refundada
                    servicePayment.IsRefunded = true;
                    servicePayment.StripeRefundId = refund.Id;

                    // Crear transacción de refund para el cliente
                    var refundTransaction = new FinancialTransaction
                    {
                        UserId = searchHire.ClientId,
                        Amount = clientRefundAmount, // Solo el porcentaje del cliente
                        TransactionType = "Refund",
                        RelatedEntityType = "SearchHire",
                        RelatedEntityId = searchHireId,
                        StripePaymentIntentId = servicePayment.StripePaymentIntentId,
                        StripeRefundId = refund.Id,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.FinancialTransactions.Add(refundTransaction);

                    // Si el experto debe recibir algo, crear transacción de pago al experto
                    if (expertAmount > 0 && searchHire.ExpertId.HasValue)
                    {
                        var expertTransaction = new FinancialTransaction
                        {
                            UserId = searchHire.ExpertId.Value,
                            Amount = expertAmount,
                            TransactionType = "Payout",
                            RelatedEntityType = "SearchHire",
                            RelatedEntityId = searchHireId,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.FinancialTransactions.Add(expertTransaction);
                    }

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    _logger.LogInformation("Successfully processed automatic client refund for searchHireId={SearchHireId}, refundId={RefundId}, clientRefund={ClientRefund}€, expertAmount={ExpertAmount}€, platformAmount={PlatformAmount}€, reason={Reason}",
                        searchHireId, refund.Id, clientRefundAmount, expertAmount, platformAmount, reason);

                    return true;
                }
                catch (StripeException ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Stripe error processing automatic client refund for searchHireId={SearchHireId}: {ErrorMessage}", 
                        searchHireId, ex.Message);
                    return false;
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Error processing automatic client refund for searchHireId={SearchHireId}: {ErrorMessage}", 
                        searchHireId, ex.Message);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProcessAutomaticClientRefundAsync for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
                return false;
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
        /// Función centralizada para dar la razón al cliente y procesar el reembolso (balance interno)
        /// </summary>
        /// <param name="searchHireId">ID del servicio contratado</param>
        /// <param name="reason">Razón del reembolso</param>
        /// <returns>True si se procesó correctamente, false en caso contrario</returns>
        public async Task<bool> ProcessClientRefundAsync(int searchHireId, string reason)
        {
            _logger.LogInformation("Processing client refund for searchHireId={SearchHireId}, reason={Reason}", searchHireId, reason);

            try
            {
                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", searchHireId)
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                    return false;
                }

                // Verificar que el servicio esté en estado activo
                if (searchHire.Status.StatusValue != SearchHireStatus.Pending.ToStringValue())
                {
                    _logger.LogWarning("SearchHire is not in active status for searchHireId={SearchHireId}, current status={Status}", 
                        searchHireId, searchHire.Status);
                    return false;
                }

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // 🎯 USAR CONFIGURACIÓN DE DISTRIBUCIÓN DE DINERO
                    var config = await GetMoneyDistributionConfigAsync("cancelled", 
                        searchHire.SearchService?.CategoryId, 
                        searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);
                    
                    var refundAmount = config != null 
                        ? searchHire.Amount * (config.ClientPercentage / 100)
                        : searchHire.Amount; // Si no hay configuración, reembolsar el 100%
                    
                    _logger.LogInformation("Using money distribution config for refund: Client={ClientPercentage}%, Expert={ExpertPercentage}%, Platform={PlatformPercentage}%, Source={Source} for searchHireId={SearchHireId}", 
                        config?.ClientPercentage ?? 100, config?.ExpertPercentage ?? 0, config?.PlatformPercentage ?? 0, config?.Source ?? "default", searchHireId);
                    
                    // 💳 PROCESAR REFUND REAL EN STRIPE usando el método existente
                    var refundReason = "Force finalize - refund to client";
                    var refundSuccess = await ProcessAutomaticClientRefundAsync(searchHireId, refundReason);
                    
                    if (!refundSuccess)
                    {
                        _logger.LogError("Failed to process Stripe refund for force finalize searchHireId={SearchHireId}", searchHireId);
                        await transaction.RollbackAsync();
                        return false;
                    }
                    
                    // Actualizar estado del servicio
                    searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                    searchHire.UpdatedAt = DateTime.UtcNow;
                    
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    _logger.LogInformation("Successfully processed client refund for searchHireId={SearchHireId}, refunded amount={Amount}, original amount={OriginalAmount}, reason={Reason}",
                        searchHire.Id, refundAmount, searchHire.Amount, reason);

                    return true;
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Error processing client refund for searchHireId={SearchHireId}: {ErrorMessage}", 
                        searchHireId, ex.Message);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProcessClientRefundAsync for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
                return false;
            }
        }

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
                    _logger.LogWarning("Expert has not responded within 24 hours for searchHireId={SearchHireId}, processing automatic refund", searchHireId);
                    
                    // Procesar reembolso automático
                    var success = await ProcessClientRefundAsync(searchHireId, "Expert did not respond within 24 hours - automatic refund");
                    
                    if (success)
                    {
                        _logger.LogInformation("Automatic refund processed successfully for searchHireId={SearchHireId}", searchHireId);
                    }
                    else
                    {
                        _logger.LogError("Failed to process automatic refund for searchHireId={SearchHireId}", searchHireId);
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

        public async Task ProcessTransferToExpert(int searchHireId)
        {
            _logger.LogInformation("Processing transfer to expert for searchHireId={SearchHireId}", searchHireId);

            // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
            var searchHire = await _context.SearchHires
                .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", searchHireId)
                .Include(sh => sh.Status)
                .Include(sh => sh.Expert)
                .ThenInclude(e => e.ExpertProfile)
                .Include(sh => sh.SearchService)
                .ThenInclude(ss => ss.ServiceType)
                .FirstOrDefaultAsync();

            if (searchHire == null)
            {
                _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                throw new Exception("SearchHire not found");
            }

            // Verificar que el servicio esté en estado válido para transferencia
            if (searchHire.Status.StatusValue != SearchHireStatus.Pending.ToStringValue() && 
                searchHire.Status.StatusValue != SearchHireStatus.AwaitingClientDecision.ToStringValue())
            {
                _logger.LogWarning("SearchHire is not in valid status for transfer for searchHireId={SearchHireId}, current status={Status}", 
                    searchHireId, searchHire.Status);
                throw new Exception($"SearchHire is not in valid status for transfer: {searchHire.Status}");
            }

            // 🚨 PROTECCIÓN CONTRA TRANSFERENCIAS DUPLICADAS
            if (!string.IsNullOrEmpty(searchHire.ExpertTransferId))
            {
                _logger.LogWarning("Transfer already exists for searchHireId={SearchHireId}, transferId={TransferId}", 
                    searchHireId, searchHire.ExpertTransferId);
                throw new Exception($"Transfer already exists for this SearchHire: {searchHire.ExpertTransferId}");
            }

            // 🎯 USAR SISTEMA DE CONFIGURACIONES EN LUGAR DE COMISIÓN FIJA
            var config = await GetMoneyDistributionConfigAsync("completed", 
                searchHire.SearchService?.CategoryId, 
                searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);
            
            if (config == null)
            {
                _logger.LogError("No money distribution configuration found for searchHireId={SearchHireId}", searchHireId);
                throw new Exception("No money distribution configuration found");
            }
            
            var amountToExpert = searchHire.Amount * (config.ExpertPercentage / 100);
            var amountInCents = (long)(amountToExpert * 100);
            
            _logger.LogInformation("Using money distribution config: Expert={ExpertPercentage}%, Platform={PlatformPercentage}%, Source={Source} for searchHireId={SearchHireId}", 
                config.ExpertPercentage, config.PlatformPercentage, config.Source, searchHireId);

            var expertStripeAccountId = searchHire.Expert.ExpertProfile?.StripeAccountId;
            if (string.IsNullOrEmpty(expertStripeAccountId))
            {
                _logger.LogError("Expert has no Stripe account for searchHireId={SearchHireId}, expertId={ExpertId}", searchHireId, searchHire.ExpertId);
                throw new Exception("Expert has no Stripe account configured");
            }

            try
            {
                var transferOptions = new TransferCreateOptions
                {
                    Amount = amountInCents,
                    Currency = "eur",
                    Destination = expertStripeAccountId,
                    Metadata = new Dictionary<string, string>
                    {
                        { "searchHireId", searchHireId.ToString() }
                    }
                };

                var transferService = new TransferService();
                var transfer = await transferService.CreateAsync(transferOptions);
                searchHire.ExpertTransferId = transfer.Id;
                
                // NO actualizar el estado aquí - se hace en el código que llama
                // searchHire.Status = SearchHireStatus.Completed.ToStringValue();
                // searchHire.UpdatedAt = DateTime.UtcNow;
                
                _logger.LogInformation("Transfer created for searchHireId={SearchHireId}, transferId={TransferId}, amount={Amount}", searchHireId, transfer.Id, amountToExpert);

                // Crear transacción financiera para el pago al experto
                var expertTransaction = new FinancialTransaction
                {
                    UserId = searchHire.ExpertId.Value,
                    Amount = amountToExpert,
                    TransactionType = "Payout",
                    RelatedEntityType = "SearchHire",
                    RelatedEntityId = searchHireId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.FinancialTransactions.Add(expertTransaction);

                // 🚨 ACTUALIZAR BALANCE DEL CLIENTE (dinero ya se retiró al contratar)
                // El balance del cliente ya se redujo cuando contrató el servicio
                // Aquí solo registramos la transacción de pago al experto

                // NO hacer SaveChanges aquí - se hace en el código que llama
                // await _context.SaveChangesAsync();
                
                _logger.LogInformation("Successfully processed transfer to expert for searchHireId={SearchHireId}, amount={Amount}", 
                    searchHireId, amountToExpert);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Stripe error processing transfer for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
                throw new Exception($"Stripe transfer failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing transfer for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
                throw;
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
        /// Marca un evento como procesado en la base de datos
        /// </summary>
        private async Task MarkEventAsProcessedAsync(string eventId, string eventType, string? stripeAccountId = null, int? userId = null, string status = "Success", string? errorMessage = null)
        {
            try
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

                _context.ProcessedWebhookEvents.Add(processedEvent);
                await _context.SaveChangesAsync();
                
                _logger.LogInformation("✅ DEBUG: Evento marcado como procesado: eventId={EventId}, status={Status}", eventId, status);
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


    }
}