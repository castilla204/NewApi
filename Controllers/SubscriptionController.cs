using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;
using System.Text.Json;
using System.Security.Claims;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SubscriptionController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<SubscriptionController> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _webhookSecret;

        public SubscriptionController(AppDbContext context, ILogger<SubscriptionController> logger, IConfiguration configuration)
        {
            _logger = logger;
            _logger.LogInformation("Initializing SubscriptionController");
            _context = context;
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

                // Get active subscription
                var subscription = await _context.UserSubscriptions
                    .Include(us => us.User)
                    .FirstOrDefaultAsync(us => us.UserId == userId && us.Status == "active");

                if (subscription == null)
                {
                    _logger.LogInformation("No active subscription found for user {UserId}", userId);
                    return NotFound(new { message = "No active subscription found" });
                }

                // Check if already pending cancellation
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

                // Mark subscription to cancel at period end in Stripe
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

                        // Update subscription status in database
                        subscription.Status = "pending_cancellation";
                        subscription.UpdatedAt = DateTime.UtcNow;
                        // EndDate remains unchanged as it already reflects the end of the billing period
                    }
                    catch (StripeException ex)
                    {
                        _logger.LogError(ex, "Stripe error marking subscription {StripeSubscriptionId} for cancellation: {StripeError}", subscription.StripeSubscriptionId, ex.StripeError?.Message);
                        return BadRequest(new { message = "Failed to process cancellation in Stripe" });
                    }
                }

                // Use transaction for database updates
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
    

    [HttpPost("webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> HandleStripeWebhook()
        {
            _logger.LogInformation("Webhook endpoint invoked");

            var json = await new StreamReader(Request.Body).ReadToEndAsync();
            var signatureHeader = Request.Headers["Stripe-Signature"];
            _logger.LogInformation("Received webhook request with signature: {SignatureHeader}", signatureHeader);

            if (string.IsNullOrEmpty(_webhookSecret))
            {
                _logger.LogError("Webhook secret is not configured");
                return BadRequest(new { error = "Webhook secret is not configured" });
            }

            if (string.IsNullOrEmpty(signatureHeader))
            {
                _logger.LogError("No signature header found in webhook request");
                return BadRequest(new { error = "No signature header found" });
            }

            try
            {
                _logger.LogInformation("Attempting to construct Stripe event with signature: {SignatureHeader}", signatureHeader);

                var stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, _webhookSecret);

                _logger.LogInformation("Event constructed successfully. Type: {EventType}, ID: {EventId}", stripeEvent.Type, stripeEvent.Id);

                switch (stripeEvent.Type)
                {
                    case EventTypes.CheckoutSessionCompleted:
                        _logger.LogInformation("Processing event: checkout.session.completed");
                        var session = stripeEvent.Data.Object as Session;
                        if (session == null)
                        {
                            _logger.LogError("Failed to cast event data to Session object");
                            return BadRequest();
                        }

                        _logger.LogInformation("Session details: ID={SessionId}, CustomerEmail={CustomerEmail}", session.Id, session.CustomerEmail);

                        // Extract metadata
                        if (!int.TryParse(session.Metadata["userId"], out int userId) ||
                            !int.TryParse(session.Metadata["planId"], out int planId) ||
                            !bool.TryParse(session.Metadata["isYearly"], out bool isYearly))
                        {
                            _logger.LogError("Invalid metadata in Stripe session. Metadata: {Metadata}", JsonSerializer.Serialize(session.Metadata));
                            return BadRequest();
                        }

                        _logger.LogInformation("Extracted metadata: userId={UserId}, planId={PlanId}, isYearly={IsYearly}", userId, planId, isYearly);

                        await HandleCheckoutSessionCompleted(userId, planId, isYearly, session.SubscriptionId);
                        break;

                    case EventTypes.CustomerSubscriptionUpdated:
                        _logger.LogInformation("Processing event: customer.subscription.updated");
                        var subscription = stripeEvent.Data.Object as Subscription;
                        if (subscription == null)
                        {
                            _logger.LogError("Failed to cast event data to Subscription object");
                            return BadRequest();
                        }
                        await HandleSubscriptionUpdated(subscription);
                        break;

                    case EventTypes.CustomerSubscriptionDeleted:
                        _logger.LogInformation("Processing event: customer.subscription.deleted");
                        var deletedSubscription = stripeEvent.Data.Object as Subscription;
                        if (deletedSubscription == null)
                        {
                            _logger.LogError("Failed to cast event data to Subscription object");
                            return BadRequest();
                        }
                        await HandleSubscriptionCanceled(deletedSubscription);
                        break;

                    case EventTypes.InvoicePaymentSucceeded:
                        _logger.LogInformation("Processing event: invoice.payment_succeeded");
                        var invoice = stripeEvent.Data.Object as Invoice;
                        if (invoice == null)
                        {
                            _logger.LogError("Failed to cast event data to Invoice object");
                            return BadRequest();
                        }
                        await HandlePaymentSucceeded(invoice);
                        break;

                    case EventTypes.InvoicePaymentFailed:
                        _logger.LogInformation("Processing event: invoice.payment_failed");
                        var failedInvoice = stripeEvent.Data.Object as Invoice;
                        if (failedInvoice == null)
                        {
                            _logger.LogError("Failed to cast event data to Invoice object");
                            return BadRequest();
                        }
                        await HandlePaymentFailed(failedInvoice);
                        break;

                    default:
                        _logger.LogWarning("Unhandled event type: {EventType}", stripeEvent.Type);
                        break;
                }

                _logger.LogInformation("Webhook processed successfully for event type: {EventType}", stripeEvent.Type);
                return Ok();
            }
            catch (StripeException e)
            {
                _logger.LogError(e, "Stripe webhook error: {ErrorMessage}", e.Message);
                return BadRequest(new { error = e.Message });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "General webhook error: {ErrorMessage}", e.Message);
                return StatusCode(500, new { error = "Internal server error" });
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

                // Revert user to free plan
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
                catch (Exception e)
                {
                    _logger.LogError(e, "Error creating checkout session: {ErrorMessage}", e.Message);
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
    }
}