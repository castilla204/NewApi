using System.Text.Json;

namespace NewApi.Tests.StripeMocks;

/// <summary>
/// Construye payloads JSON de eventos Stripe para tests de webhooks.
/// Cubre la forma mínima que necesita <c>EventUtility.ConstructEvent</c> para deserializar
/// sin lanzar excepción. Si un test necesita un campo específico (p. ej.
/// <c>future_requirements.errors[]</c>), se añade aquí con un helper named-args.
/// </summary>
public static class StripeEventBuilder
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    /// <summary>checkout.session.completed con metadata mínima del hire.</summary>
    public static string CheckoutSessionCompleted(
        string eventId,
        string sessionId,
        string paymentIntentId,
        int clientUserId,
        int serviceId,
        decimal amount,
        string currency = "eur")
    {
        var payload = new
        {
            id = eventId,
            @object = "event",
            api_version = "2025-09-30.clover",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            type = "checkout.session.completed",
            livemode = false,
            // EventConverter (Stripe.net v50, línea 57) exige 'request' presente:
            // accede a jsonObject["request"].Type sin null-check → NRE si falta.
            request = new { id = (string?)null, idempotency_key = (string?)null },
            pending_webhooks = 1,
            data = new
            {
                @object = new
                {
                    id = sessionId,
                    @object = "checkout.session",
                    mode = "payment", // el handler exige session.Mode == "payment" (SubscriptionController.cs:4314)
                    payment_intent = paymentIntentId,
                    payment_status = "paid",
                    status = "complete",
                    amount_total = (long)(amount * 100),
                    currency,
                    metadata = new Dictionary<string, string>
                    {
                        ["userId"] = clientUserId.ToString(),
                        ["serviceId"] = serviceId.ToString(),
                        ["amount"] = amount.ToString("0.##"),
                        ["pendingHire"] = "true",
                    },
                },
            },
        };
        return JsonSerializer.Serialize(payload, _opts);
    }

    /// <summary>account.application.deauthorized — el experto desconecta su cuenta Connect.</summary>
    public static string AccountApplicationDeauthorized(string eventId, string accountId)
    {
        var payload = new
        {
            id = eventId,
            @object = "event",
            api_version = "2025-09-30.clover",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            type = "account.application.deauthorized",
            livemode = false,
            account = accountId,
            request = new { id = (string?)null, idempotency_key = (string?)null },
            pending_webhooks = 1,
            data = new
            {
                @object = new
                {
                    id = "ca_test_" + accountId,
                    @object = "application",
                    name = "Inspecciono",
                },
            },
        };
        return JsonSerializer.Serialize(payload, _opts);
    }

    /// <summary>account.updated con Approved (charges + payouts enabled).</summary>
    public static string AccountUpdatedApproved(string eventId, string accountId)
    {
        var payload = new
        {
            id = eventId,
            @object = "event",
            api_version = "2025-09-30.clover",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            type = "account.updated",
            livemode = false,
            account = accountId,
            request = new { id = (string?)null, idempotency_key = (string?)null },
            pending_webhooks = 1,
            data = new
            {
                @object = new
                {
                    id = accountId,
                    @object = "account",
                    type = "express",
                    charges_enabled = true,
                    payouts_enabled = true,
                    details_submitted = true,
                    capabilities = new { transfers = "active", card_payments = "active" },
                    requirements = new
                    {
                        currently_due = Array.Empty<string>(),
                        past_due = Array.Empty<string>(),
                        errors = Array.Empty<object>(),
                        disabled_reason = (string?)null,
                    },
                    future_requirements = new
                    {
                        currently_due = Array.Empty<string>(),
                        past_due = Array.Empty<string>(),
                        errors = Array.Empty<object>(),
                    },
                },
            },
        };
        return JsonSerializer.Serialize(payload, _opts);
    }

    /// <summary>
    /// 🔴 Round 29 FIX-DOC-FAIL: cuenta active pero con error en bucket
    /// <c>future_requirements.errors[]</c> (separado de <c>requirements.errors</c>).
    /// </summary>
    public static string AccountUpdatedFutureRequirementsDocFailed(string eventId, string accountId)
    {
        var payload = new
        {
            id = eventId,
            @object = "event",
            api_version = "2025-09-30.clover",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            type = "account.updated",
            livemode = false,
            account = accountId,
            request = new { id = (string?)null, idempotency_key = (string?)null },
            pending_webhooks = 1,
            data = new
            {
                @object = new
                {
                    id = accountId,
                    @object = "account",
                    type = "express",
                    charges_enabled = true,
                    payouts_enabled = true,
                    details_submitted = true,
                    capabilities = new { transfers = "active", card_payments = "active" },
                    requirements = new
                    {
                        currently_due = Array.Empty<string>(),
                        past_due = Array.Empty<string>(),
                        errors = Array.Empty<object>(),
                        disabled_reason = (string?)null,
                    },
                    future_requirements = new
                    {
                        currently_due = new[] { "individual.verification.document" },
                        past_due = Array.Empty<string>(),
                        errors = new[]
                        {
                            new
                            {
                                code = "verification_document_failed",
                                reason = "The uploaded file was not readable.",
                                requirement = "individual.verification.document",
                            },
                        },
                        current_deadline = DateTimeOffset.UtcNow.AddDays(14).ToUnixTimeSeconds(),
                    },
                },
            },
        };
        return JsonSerializer.Serialize(payload, _opts);
    }
}
