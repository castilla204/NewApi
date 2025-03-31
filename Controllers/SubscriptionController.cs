using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DataLayer.Models;
using DataLayer.Models.DTOs;
using Stripe;
using Stripe.Checkout;
using System.Text.Json;
using System.Security.Claims;
using DataLayer.Models.PostGresModels;

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
            _context = context;
            _logger = logger;
            _configuration = configuration;
            _webhookSecret = _configuration["Stripe:WebhookSecret"];
            StripeConfiguration.ApiKey = _configuration["Stripe:SecretKey"];
        }

        [HttpPost("webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> HandleStripeWebhook()
        {
            _logger.LogInformation("Webhook received");

            var json = await new StreamReader(Request.Body).ReadToEndAsync();
            var signatureHeader = Request.Headers["Stripe-Signature"];

            if (string.IsNullOrEmpty(_webhookSecret))
            {
                _logger.LogError("Webhook secret is not configured");
                return BadRequest(new { error = "Webhook secret is not configured" });
            }

            if (string.IsNullOrEmpty(signatureHeader))
            {
                _logger.LogError("No signature header found");
                return BadRequest(new { error = "No signature header found" });
            }

            try
            {
                _logger.LogInformation($"Attempting to construct event with signature: {signatureHeader}");

                var stripeEvent = EventUtility.ConstructEvent(json,
                    signatureHeader, _webhookSecret);

                _logger.LogInformation($"Event constructed successfully. Type: {stripeEvent.Type}");

                switch (stripeEvent.Type)
                {
                    case EventTypes.CheckoutSessionCompleted:
                        var session = stripeEvent.Data.Object as Session;
                        if (session == null) return BadRequest();

                        // Extract metadata
                        if (!int.TryParse(session.Metadata["userId"], out int userId) ||
                            !int.TryParse(session.Metadata["planId"], out int planId) ||
                            !bool.TryParse(session.Metadata["isYearly"], out bool isYearly))
                        {
                            _logger.LogError("Invalid metadata in Stripe session");
                            return BadRequest();
                        }

                        await HandleCheckoutSessionCompleted(userId, planId, isYearly, session.SubscriptionId);
                        break;

                    case EventTypes.CustomerSubscriptionUpdated:
                        var subscription = stripeEvent.Data.Object as Subscription;
                        await HandleSubscriptionUpdated(subscription);
                        break;

                    case EventTypes.CustomerSubscriptionDeleted:
                        var deletedSubscription = stripeEvent.Data.Object as Subscription;
                        await HandleSubscriptionCanceled(deletedSubscription);
                        break;

                    case EventTypes.InvoicePaymentSucceeded:
                        var invoice = stripeEvent.Data.Object as Invoice;
                        await HandlePaymentSucceeded(invoice);
                        break;

                    case EventTypes.InvoicePaymentFailed:
                        var failedInvoice = stripeEvent.Data.Object as Invoice;
                        await HandlePaymentFailed(failedInvoice);
                        break;
                }

                return Ok();
            }
            catch (StripeException e)
            {
                _logger.LogError(e, "Stripe webhook error");
                return BadRequest(new { error = e.Message });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "General webhook error");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        private async Task HandleCheckoutSessionCompleted(int userId, int planId, bool isYearly, string subscriptionId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                _logger.LogError($"User {userId} not found");
                return;
            }

            user.SubscriptionPlanId = planId;

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

            _context.UserSubscriptions.Add(userSubscription);
            await _context.SaveChangesAsync();

            _logger.LogInformation($"Successfully processed subscription for user {userId}");
        }

        private async Task HandleSubscriptionUpdated(Subscription subscription)
        {
            var userSubscription = await _context.UserSubscriptions
                .FirstOrDefaultAsync(us => us.StripeSubscriptionId == subscription.Id);

            if (userSubscription != null)
            {
                userSubscription.Status = subscription.Status;
                userSubscription.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }
        }

        private async Task HandleSubscriptionCanceled(Subscription subscription)
        {
            var userSubscription = await _context.UserSubscriptions
                .Include(us => us.User)
                .FirstOrDefaultAsync(us => us.StripeSubscriptionId == subscription.Id);

            if (userSubscription != null)
            {
                userSubscription.Status = "cancelled";
                userSubscription.EndDate = DateTime.UtcNow;
                userSubscription.UpdatedAt = DateTime.UtcNow;

                // Revert user to free plan
                var freePlan = await _context.SubscriptionPlans
                    .FirstOrDefaultAsync(p => p.PriceMonthly == 0);
                userSubscription.User.SubscriptionPlanId = freePlan?.Id;

                await _context.SaveChangesAsync();
            }
        }

        private async Task HandlePaymentSucceeded(Invoice invoice)
        {
            if (string.IsNullOrEmpty(invoice.SubscriptionId)) return;

            var userSubscription = await _context.UserSubscriptions
                .FirstOrDefaultAsync(us => us.StripeSubscriptionId == invoice.SubscriptionId);

            if (userSubscription != null)
            {
                userSubscription.EndDate = userSubscription.IsYearly ?
                    userSubscription.EndDate.AddYears(1) :
                    userSubscription.EndDate.AddMonths(1);
                userSubscription.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }
        }

        private async Task HandlePaymentFailed(Invoice invoice)
        {
            if (string.IsNullOrEmpty(invoice.SubscriptionId)) return;

            var userSubscription = await _context.UserSubscriptions
                .Include(us => us.User)
                .FirstOrDefaultAsync(us => us.StripeSubscriptionId == invoice.SubscriptionId);

            if (userSubscription != null)
            {
                userSubscription.Status = "payment_failed";
                userSubscription.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }
        }

        [HttpPost("create-checkout-session")]
        public async Task<IActionResult> CreateCheckoutSession([FromBody] CreateSubscriptionDto request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var plan = await _context.SubscriptionPlans.FindAsync(request.PlanId);
                if (plan == null)
                {
                    return NotFound(new { message = "Subscription plan not found" });
                }

                var priceId = request.IsYearly ? plan.StripePriceIdYearly : plan.StripePriceIdMonthly;
                var domain = "https://900b-62-175-125-211.ngrok-free.app";

                if (string.IsNullOrEmpty(priceId))
                {
                    return BadRequest(new { message = "Stripe price ID not configured for this plan" });
                }

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

                var service = new SessionService();
                Session session;
                try
                {
                    session = await service.CreateAsync(options);
                }
                catch (StripeException e)
                {
                    _logger.LogError(e, "Stripe error creating checkout session");
                    return StatusCode(500, new { message = e.Message });
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error creating checkout session");
                    return StatusCode(500, new { message = "Failed to create checkout session" });
                }

                return Ok(new { url = session.Url });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating checkout session");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost("confirm-subscription")]
        public async Task<IActionResult> ConfirmSubscription([FromBody] ConfirmSubscriptionDto request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var user = await _context.Users.FindAsync(userId);
                if (user == null)
                {
                    return NotFound(new { message = "User not found" });
                }

                var plan = await _context.SubscriptionPlans.FindAsync(request.PlanId);
                if (plan == null)
                {
                    return NotFound(new { message = "Subscription plan not found" });
                }

                // Update user's subscription plan
                user.SubscriptionPlanId = request.PlanId;
                // Create user subscription record
                var userSubscription = new UserSubscription
                {
                    UserId = userId,
                    SubscriptionPlanId = request.PlanId,
                    IsYearly = request.IsYearly,
                    StartDate = DateTime.UtcNow,
                    EndDate = DateTime.UtcNow.AddMonths(request.IsYearly ? 12 : 1),
                    Status = "active",
                    StripeSubscriptionId = request.StripeSubscriptionId
                };

                _context.UserSubscriptions.Add(userSubscription);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Subscription confirmed successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error confirming subscription");
                return StatusCode(500, new { message = "Failed to confirm subscription" });
            }
        }

        [HttpGet("plans")]
        public async Task<IActionResult> GetSubscriptionPlans()
        {
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

                return Ok(plans);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving subscription plans");
                return StatusCode(500, new { message = "Failed to retrieve subscription plans" });
            }
        }

        [HttpGet("details")]
        public async Task<IActionResult> GetSubscriptionDetails()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var subscription = await _context.UserSubscriptions
                    .Include(us => us.SubscriptionPlan)
                    .Where(us => us.UserId == userId && us.Status == "active")
                    .OrderByDescending(us => us.CreatedAt)
                    .FirstOrDefaultAsync();

                if (subscription == null)
                {
                    return Ok(new SubscriptionDetailsDto
                    {
                        IsYearly = false,
                        Status = "none",
                        BillingPeriod = "none"
                    });
                }

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
                _logger.LogError(ex, "Error retrieving subscription details");
                return StatusCode(500, new { message = "Failed to retrieve subscription details" });
            }
        }

        [HttpGet("current")]
        public async Task<IActionResult> GetCurrentSubscription()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var user = await _context.Users
                    .Include(u => u.SubscriptionPlan)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user == null)
                {
                    return NotFound(new { message = "User not found" });
                }

                if (user.SubscriptionPlan == null)
                {
                    // Return free plan if no subscription is found
                    var freePlan = await _context.SubscriptionPlans
                        .FirstOrDefaultAsync(p => p.PriceMonthly == 0);

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
                _logger.LogError(ex, "Error retrieving current subscription");
                return StatusCode(500, new { message = "Failed to retrieve current subscription" });
            }
        }
    }
}