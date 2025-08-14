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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using newApi.Services;
using SubscriptionService = Stripe.SubscriptionService;
using newApi.Common;
using Google.Api;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SubscriptionController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<SubscriptionController> _logger;
        private readonly ISubscriptionService _subscriptionService;
        private readonly IConfiguration _configuration;
        private readonly string _webhookSecret;

        public SubscriptionController(AppDbContext context, ILogger<SubscriptionController> logger, IConfiguration configuration, ISubscriptionService subscriptionService)
        {
            _logger = logger;
            _logger.LogInformation("Initializing SubscriptionController");
            _context = context;
            _subscriptionService = subscriptionService;
            _configuration = configuration;
            _webhookSecret = _configuration["Stripe:WebhookSecret"];
            StripeConfiguration.ApiKey = _configuration["Stripe:SecretKey"];
            _logger.LogInformation("Stripe API Key and Webhook Secret configured");
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
                    return BadRequest(new { message = "Expert already registered with Stripe" });
                }

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
                    expertProfile.StripeAccountId = account.Id;
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

                var user = await _context.Users.FindAsync(userId);
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

        [Authorize]
        [HttpPost("load-money-service")]
        public async Task<IActionResult> LoadMoneyService([FromBody] LoadMoneyServiceDto request)
        {
            _logger.LogInformation("LoadMoneyService endpoint invoked with serviceId: {ServiceId}, amount: {Amount}", request.ServiceId, request.Amount);

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim ?? "null");
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var service = await _context.SearchServices.FindAsync(request.ServiceId);
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

                var user = await _context.Users.FindAsync(userId);
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
                        { "serviceId", request.ServiceId.ToString() },
                        { "amount", request.Amount.ToString() },
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
            _logger.LogInformation("Received webhook with signature: {SignatureHeader}, payload: {Payload}", signatureHeader, json);

            try
            {
                var stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, _webhookSecret);
                _logger.LogInformation("Event constructed successfully: type={EventType}, id={EventId}", stripeEvent.Type, stripeEvent.Id);

                switch (stripeEvent.Type)
                {
                    case "checkout.session.completed":
                        var session = stripeEvent.Data.Object as Session;
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
                                    await HandlePendingHireCompleted(userId, amount, serviceId, session.Metadata);
                                }
                                else
                                {
                                    _logger.LogInformation("Processing load money for userId={UserId}, amount={Amount}", userId, amount);
                                    await HandleLoadMoneyCompleted(userId, amount);
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
                        break;

                    case "account.updated":
                        var account = stripeEvent.Data.Object as Account;
                        if (account != null)
                        {
                            var expertProfile = await _context.ExpertProfiles
                                .FirstOrDefaultAsync(ep => ep.StripeAccountId == account.Id);
                            if (expertProfile != null)
                            {
                                bool isAccountEnabled = account.ChargesEnabled && account.PayoutsEnabled;
                                _logger.LogInformation("Account updated for expert userId={UserId}, accountId={AccountId}, enabled={Enabled}",
                                    expertProfile.UserId, account.Id, isAccountEnabled);
                                await _context.SaveChangesAsync();
                            }
                            else
                            {
                                _logger.LogWarning("No expert profile found for accountId={AccountId}", account.Id);
                            }
                        }
                        break;

                    case "transfer.failed":
                        var transfer = stripeEvent.Data.Object as Transfer;
                        if (transfer != null)
                        {
                            var searchHire = await _context.SearchHires
                                .FirstOrDefaultAsync(sh => sh.ExpertTransferId == transfer.Id);
                            if (searchHire != null)
                            {
                                searchHire.Status = SearchHireStatus.TransferFailed.ToStringValue();
                                searchHire.UpdatedAt = DateTime.UtcNow;
                                await _context.SaveChangesAsync();
                                _logger.LogError("Transfer failed for searchHireId={SearchHireId}, transferId={TransferId}",
                                    searchHire.Id, transfer.Id);
                            }
                            else
                            {
                                _logger.LogWarning("No SearchHire found for transferId={TransferId}", transfer.Id);
                            }
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

                    default:
                        _logger.LogWarning("Unhandled event type: {EventType}", stripeEvent.Type);
                        break;
                }

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

        private async Task HandlePendingHireCompleted(int userId, decimal amount, int serviceId, Dictionary<string, string> metadata)
        {
            _logger.LogInformation("Handling pending hire completed for userId={UserId}, serviceId={ServiceId}, amount={Amount}", userId, serviceId, amount);

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                _logger.LogError("User not found for userId={UserId}", userId);
                throw new Exception("User not found");
            }

            var service = await _context.SearchServices.FindAsync(serviceId);
            if (service == null)
            {
                _logger.LogError("Service not found for serviceId={ServiceId}", serviceId);
                throw new Exception("Service not found");
            }

            if (!metadata.TryGetValue("searchData", out var searchDataJson) || !metadata.TryGetValue("parameters", out var parametersJson))
            {
                _logger.LogError("Missing searchData or parameters in metadata for userId={UserId}, serviceId={ServiceId}", userId, serviceId);
                throw new Exception("Missing search data or parameters in metadata");
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
                throw new Exception("Invalid search data or parameters format");
            }

            if (searchDto == null || parameterDto == null)
            {
                _logger.LogError("Deserialized searchDto or parameterDto is null for userId={UserId}", userId);
                throw new Exception("Invalid search data or parameters");
            }

            var activeSearchCount = await _context.Searches.CountAsync(s => s.UserId == userId && s.IsActive);
            var subscriptionLimits = await _subscriptionService.GetUserSubscriptionLimits(userId);
            if (activeSearchCount >= subscriptionLimits.MaxSearches)
            {
                _logger.LogError("User has reached max searches: userId={UserId}, maxSearches={MaxSearches}", userId, subscriptionLimits.MaxSearches);
                throw new Exception($"User has reached the limit of {subscriptionLimits.MaxSearches} active searches");
            }
            if (searchDto.Frequency < subscriptionLimits.MinSearchInterval)
            {
                _logger.LogError("Search frequency below minimum: userId={UserId}, frequency={Frequency}, minInterval={MinInterval}", userId, searchDto.Frequency, subscriptionLimits.MinSearchInterval);
                throw new Exception($"Minimum search interval is {subscriptionLimits.MinSearchInterval} hours");
            }

            if (!user.PhoneVerified)
            {
                _logger.LogError("Phone verification required for userId={UserId}", userId);
                throw new Exception("Phone verification required");
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Update user balance
                user.Balance += amount;
                var depositTransaction = new FinancialTransaction
                {
                    UserId = userId,
                    Amount = amount,
                    TransactionType = "Deposit",
                    CreatedAt = DateTime.UtcNow
                };
                _context.FinancialTransactions.Add(depositTransaction);
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

                // Create search hire
                var searchHire = new SearchHire
                {
                    ClientId = userId,
                    ExpertId = service.ExpertProfileId,
                    SearchServiceId = service.Id,
                    SearchId = search.Id,
                    Status = SearchHireStatus.Pending.ToStringValue(),
                    Amount = service.Price,
                    CreatedAt = DateTime.UtcNow,
                    CompletionDeadline = DateTime.UtcNow.AddDays(7)
                };
                _context.SearchHires.Add(searchHire);

                // Deduct service price from balance
                user.Balance -= service.Price;
                var paymentTransaction = new FinancialTransaction
                {
                    UserId = userId,
                    Amount = -service.Price,
                    TransactionType = "ServicePayment",
                    RelatedEntityType = "SearchHire",
                    RelatedEntityId = searchHire.Id,
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
        }

        [HttpPost("hire-service")]
        public async Task<IActionResult> HireService([FromBody] HireServiceDto request)
        {
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

                var user = await _context.Users.FindAsync(userId);
                if (user == null)
                {
                    _logger.LogError("User not found for userId={UserId}", userId);
                    return NotFound(new { message = "User not found" });
                }

                if (user.Balance < service.Price)
                {
                    _logger.LogError("Insufficient balance for userId={UserId}, balance={Balance}, required={Price}", userId, user.Balance, service.Price);
                    return BadRequest(new { message = "Insufficient balance" });
                }

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    user.Balance -= service.Price;
                    var financialTransaction = new FinancialTransaction
                    {
                        UserId = userId,
                        Amount = -service.Price,
                        TransactionType = "ServicePayment",
                        RelatedEntityType = "SearchHire",
                        RelatedEntityId = null,
                        CreatedAt = DateTime.UtcNow
                    };

                    var searchHire = new SearchHire
                    {
                        ClientId = userId,
                        ExpertId = service.ExpertProfile.UserId,
                        SearchServiceId = service.Id,
                        SearchId = request.SearchId,
                        Status = SearchHireStatus.Pending.ToStringValue(),
                        Amount = service.Price,
                        CreatedAt = DateTime.UtcNow,
                        CompletionDeadline = DateTime.UtcNow.AddDays(7)
                    };

                    _context.SearchHires.Add(searchHire);
                    await _context.SaveChangesAsync();

                    financialTransaction.RelatedEntityId = searchHire.Id;
                    _context.FinancialTransactions.Add(financialTransaction);
                    await _context.SaveChangesAsync();

                    await transaction.CommitAsync();
                    _logger.LogInformation("Service hired successfully: searchHireId={SearchHireId}, userId={UserId}", searchHire.Id, userId);

                    return Ok(new { message = "Service hired successfully", searchHireId = searchHire.Id });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Database error hiring service for userId={UserId}", userId);
                    return StatusCode(500, new { message = "Failed to hire service" });
                }
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

                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .Include(sh => sh.Client)
                    .FirstOrDefaultAsync(sh => sh.Id == request.SearchHireId);

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

                if (searchHire.Status != SearchHireStatus.Pending.ToStringValue() && searchHire.Status != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                {
                    _logger.LogError("Service cannot be approved in status: searchHireId={SearchHireId}, status={Status}", searchHire.Id, searchHire.Status);
                    return BadRequest(new { message = "Service cannot be approved in current state" });
                }

                if (request.ClientApproved == null)
                {
                    _logger.LogError("ClientApproved is required for client action: searchHireId={SearchHireId}", searchHire.Id);
                    return BadRequest(new { error = "ClientApproved is required" });
                }

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    searchHire.ClientApproved = request.ClientApproved.Value;
                    if (!searchHire.ClientApproved.Value)
                    {
                        searchHire.Status = SearchHireStatus.Disputed.ToStringValue();
                        searchHire.UpdatedAt = DateTime.UtcNow;
                        _logger.LogInformation("Client opened dispute for searchHireId={SearchHireId}", searchHire.Id);
                    }
                    else
                    {
                        await ProcessTransferToExpert(searchHire.Id);
                        searchHire.Status = SearchHireStatus.Completed.ToStringValue();
                        searchHire.UpdatedAt = DateTime.UtcNow;
                        _logger.LogInformation("Client approved service, transfer completed for searchHireId={SearchHireId}", searchHire.Id);
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

                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Client)
                    .FirstOrDefaultAsync(sh => sh.Id == request.SearchHireId && sh.ExpertId == userId);

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found or user is not the expert for searchHireId={SearchHireId}, userId={UserId}", request.SearchHireId, userId);
                    return NotFound(new { message = "Service not found or unauthorized" });
                }

                if (searchHire.Status != SearchHireStatus.Pending.ToStringValue())
                {
                    _logger.LogError("Service is not in pending status: searchHireId={SearchHireId}, status={Status}", searchHire.Id, searchHire.Status);
                    return BadRequest(new { message = "Service is not pending" });
                }

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    searchHire.Client.Balance += searchHire.Amount;
                    var financialTransaction = new FinancialTransaction
                    {
                        UserId = searchHire.ClientId,
                        Amount = searchHire.Amount,
                        TransactionType = "Refund",
                        RelatedEntityType = "SearchHire",
                        RelatedEntityId = searchHire.Id,
                        CreatedAt = DateTime.UtcNow
                    };

                    searchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
                    searchHire.UpdatedAt = DateTime.UtcNow;

                    _context.FinancialTransactions.Add(financialTransaction);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    _logger.LogInformation("Service cancelled and refunded for searchHireId={SearchHireId}, clientId={ClientId}", searchHire.Id, searchHire.ClientId);
                    return Ok(new { message = "Service cancelled and refunded" });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Database error cancelling service for searchHireId={SearchHireId}", searchHire.Id);
                    return StatusCode(500, new { message = "Failed to cancel service" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling service: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to cancel service" });
            }
        }

        [HttpPost("dispute-service")]
        public async Task<IActionResult> DisputeService([FromBody] DisputeServiceDto request)
        {
            _logger.LogInformation("DisputeService endpoint invoked for searchHireId={SearchHireId}", request.SearchHireId);

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                if (string.IsNullOrWhiteSpace(request.Reason))
                {
                    _logger.LogError("Dispute reason is required for searchHireId={SearchHireId}", request.SearchHireId);
                    return BadRequest(new { message = "Dispute reason is required" });
                }

                var searchHire = await _context.SearchHires
                    .FirstOrDefaultAsync(sh => sh.Id == request.SearchHireId && sh.ClientId == userId);

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found or user is not the client for searchHireId={SearchHireId}, userId={UserId}", request.SearchHireId, userId);
                    return NotFound(new { message = "Service not found or unauthorized" });
                }

                if (searchHire.Status != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                {
                    _logger.LogError("Service is not in awaiting_client_decision status: searchHireId={SearchHireId}, status={Status}", searchHire.Id, searchHire.Status);
                    return BadRequest(new { message = "Service is not awaiting client decision" });
                }

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    searchHire.Status = SearchHireStatus.Disputed.ToStringValue();
                    searchHire.ClientApproved = false;
                    searchHire.UpdatedAt = DateTime.UtcNow;

                    var dispute = new DataLayer.Models.PostGresModels.Dispute
                    {
                        SearchHireId = searchHire.Id,
                        ReporterId = userId,
                        Reason = request.Reason,
                        Status = "Pending",
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.Disputes.Add(dispute);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    _logger.LogInformation("Dispute opened for searchHireId={SearchHireId}, disputeId={DisputeId}", searchHire.Id, dispute.Id);
                    return Ok(new { message = "Dispute opened", disputeId = dispute.Id });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Database error disputing service for searchHireId={SearchHireId}", searchHire.Id);
                    return StatusCode(500, new { message = "Failed to open dispute" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disputing service: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to open dispute" });
            }
        }

        [HttpPost("force-finalize")]
        public async Task<IActionResult> ForceFinalize([FromBody] ForceFinalizeDto request)
        {
            var adminEmail = User.FindFirst(ClaimTypes.Email)?.Value;
            if (adminEmail != "dcastillaa@gmail.com")
            {
                _logger.LogError("Unauthorized access attempt to force-finalize endpoint by email={Email}", adminEmail);
                return Unauthorized(new { message = "Admin access required" });
            }

            _logger.LogInformation("ForceFinalize endpoint invoked for searchHireId={SearchHireId}, resolveInFavorOfClient={ResolveInFavorOfClient}",
                request.SearchHireId, request.ResolveInFavorOfClient);

            try
            {
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .FirstOrDefaultAsync(sh => sh.Id == request.SearchHireId);

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", request.SearchHireId);
                    return NotFound(new { message = "Service not found" });
                }

                _logger.LogInformation("Processing force-finalize for searchHireId={SearchHireId}, current status={Status}",
                    searchHire.Id, searchHire.Status);

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    if (request.ResolveInFavorOfClient)
                    {
                        searchHire.Client.Balance += searchHire.Amount;
                        var financialTransaction = new FinancialTransaction
                        {
                            UserId = searchHire.ClientId,
                            Amount = searchHire.Amount,
                            TransactionType = "Refund",
                            RelatedEntityType = "SearchHire",
                            RelatedEntityId = searchHire.Id,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.FinancialTransactions.Add(financialTransaction);
                        searchHire.Status = SearchHireStatus.Cancelled.ToStringValue();
                        searchHire.UpdatedAt = DateTime.UtcNow;
                        _logger.LogInformation("Force-finalized in favor of client for searchHireId={SearchHireId}, refunded amount={Amount}",
                            searchHire.Id, searchHire.Amount);
                    }
                    else
                    {
                        await ProcessTransferToExpert(searchHire.Id);
                        searchHire.Status = SearchHireStatus.Completed.ToStringValue();
                        searchHire.UpdatedAt = DateTime.UtcNow;
                        _logger.LogInformation("Force-finalized in favor of expert for searchHireId={SearchHireId}, transferred amount={Amount}",
                            searchHire.Id, searchHire.Amount * (1 - 0.1m));
                    }

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    return Ok(new { message = "Service finalized successfully" });
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finalizing service: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to finalize service" });
            }
        }

        [HttpPost("resolve-dispute")]
        public async Task<IActionResult> ResolveDispute([FromBody] ResolveDisputeDto request)
        {
            var adminEmail = User.FindFirst(ClaimTypes.Email)?.Value;
            if (adminEmail != "dcastillaa@gmail.com")
            {
                _logger.LogError("Unauthorized access attempt to resolve-dispute endpoint by email={Email}", adminEmail);
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

                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .FirstOrDefaultAsync(sh => sh.Id == request.SearchHireId);

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", request.SearchHireId);
                    return NotFound(new { message = "Service not found" });
                }

                if (searchHire.Status != SearchHireStatus.Disputed.ToStringValue())
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
                        searchHire.Client.Balance += searchHire.Amount;
                        var financialTransaction = new FinancialTransaction
                        {
                            UserId = searchHire.ClientId,
                            Amount = searchHire.Amount,
                            TransactionType = "Refund",
                            RelatedEntityType = "SearchHire",
                            RelatedEntityId = searchHire.Id,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.FinancialTransactions.Add(financialTransaction);
                        _logger.LogInformation("Dispute resolved in favor of client for searchHireId={SearchHireId}", searchHire.Id);
                    }
                    else
                    {
                        await ProcessTransferToExpert(searchHire.Id);
                        _logger.LogInformation("Dispute resolved in favor of expert for searchHireId={SearchHireId}", searchHire.Id);
                    }

                    searchHire.Status = SearchHireStatus.DisputeResolved.ToStringValue();
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
            var adminEmail = User.FindFirst(ClaimTypes.Email)?.Value;
            if (adminEmail != "dcastillaa@gmail.com")
            {
                _logger.LogError("Unauthorized access attempt by email={Email}", adminEmail);
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

        public async Task ProcessTransferToExpert(int searchHireId)
        {
            var searchHire = await _context.SearchHires
                .Include(sh => sh.Expert)
                .ThenInclude(e => e.ExpertProfile)
                .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

            if (searchHire == null)
            {
                _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                throw new Exception("SearchHire not found");
            }

            var commissionRate = 0.1m;
            var amountToExpert = searchHire.Amount * (1 - commissionRate);
            var amountInCents = (long)(amountToExpert * 100);

            var expertStripeAccountId = searchHire.Expert.ExpertProfile?.StripeAccountId;
            if (string.IsNullOrEmpty(expertStripeAccountId))
            {
                _logger.LogError("Expert has no Stripe account for searchHireId={SearchHireId}, expertId={ExpertId}", searchHireId, searchHire.ExpertId);
                throw new Exception("Expert has no Stripe account configured");
            }

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
            _logger.LogInformation("Transfer created for searchHireId={SearchHireId}, transferId={TransferId}, amount={Amount}", searchHireId, transfer.Id, amountToExpert);

            //var financialTransaction = new FinancialTransaction
            //{
            //    UserId = searchHire.ExpertId,
            //    Amount = amountToExpert,
            //    TransactionType = "Payout",
            //    RelatedEntityType = "SearchHire",
            //    RelatedEntityId = searchHireId,
            //    CreatedAt = DateTime.UtcNow
            //};
            //_context.FinancialTransactions.Add(financialTransaction);

            await _context.SaveChangesAsync();
        }

        private async Task HandleLoadMoneyCompleted(int userId, decimal amount)
        {
            _logger.LogInformation("Handling load money completed for userId={UserId}, amount={Amount}", userId, amount);

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                _logger.LogError("User not found for userId={UserId}", userId);
                throw new Exception("User not found");
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                user.Balance += amount;
                var financialTransaction = new FinancialTransaction
                {
                    UserId = userId,
                    Amount = amount,
                    TransactionType = "Deposit",
                    CreatedAt = DateTime.UtcNow
                };
                _context.FinancialTransactions.Add(financialTransaction);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Balance updated successfully for userId={UserId}, new balance={Balance}", userId, user.Balance);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Database error updating balance for userId={UserId}", userId);
                throw;
            }
        }

        private async Task HandleCheckoutSessionCompleted(int userId, int planId, bool isYearly, string subscriptionId)
        {
            _logger.LogInformation("Handling checkout.session.completed for userId={UserId}, planId={PlanId}, isYearly={IsYearly}, subscriptionId={SubscriptionId}", userId, planId, isYearly, subscriptionId);

            var user = await _context.Users.FindAsync(userId);
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
        public class DisputeServiceDto
        {
            public int SearchHireId { get; set; }
            public string Reason { get; set; }
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
}