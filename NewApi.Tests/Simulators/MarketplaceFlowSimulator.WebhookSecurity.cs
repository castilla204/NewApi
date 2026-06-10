using newApi.DataLayer.Models.PostGresModels;

namespace NewApi.Tests.Simulators;

/// <summary>
/// Réplica FIEL del árbol de decisión de
/// <c>SubscriptionController.EvaluateStripeAccount</c> (SubscriptionController.cs:251-533)
/// para poder testear la clasificación de cuentas Stripe Connect SIN arrancar el host
/// ni acoplar a los tipos pesados del SDK (<c>Stripe.Account</c>).
///
/// El simulador NO valida firmas ni idempotencia — eso se cubre a nivel de modelo en
/// <c>WebhookSecurityTests</c> contra la BD real (índice único en EventId). Aquí solo se
/// replica la lógica PURA de mapeo cuenta → estado.
///
/// IMPORTANTE: si <c>EvaluateStripeAccount</c> real cambia, este helper debe seguirlo.
/// Verificado contra Round 29 FIX-DOC-FAIL (future_requirements.errors[]) y MUD-CY
/// (requirements.errors[] degradan Approved → ActionRequired).
/// </summary>
public static partial class MarketplaceFlowSimulator
{
    /// <summary>
    /// Entrada mínima que necesita la clasificación (espejo de los campos que lee
    /// EvaluateStripeAccount sobre el objeto Stripe.Account).
    /// </summary>
    public sealed class StripeAccountSnapshot
    {
        public bool ChargesEnabled { get; init; }
        public bool PayoutsEnabled { get; init; }
        public bool DetailsSubmitted { get; init; }
        public bool TosAccepted { get; init; }
        public bool TransfersActive { get; init; } = true;

        public string? DisabledReason { get; init; }

        public List<string> CurrentlyDue { get; init; } = new();
        public List<string> PastDue { get; init; } = new();
        public List<string> PendingVerification { get; init; } = new();
        /// <summary>requirements.errors[] (códigos de error tal cual los emite Stripe).</summary>
        public List<string> RequirementsErrors { get; init; } = new();
        public DateTime? CurrentDeadline { get; init; }

        public List<string> FutureCurrentlyDue { get; init; } = new();
        public List<string> FutureEventuallyDue { get; init; } = new();
        public List<string> FuturePastDue { get; init; } = new();
        /// <summary>future_requirements.errors[] — bucket SEPARADO (Round 29 FIX-DOC-FAIL).</summary>
        public List<string> FutureRequirementsErrors { get; init; } = new();
    }

    /// <summary>
    /// Réplica del árbol de decisión de EvaluateStripeAccount. Devuelve solo el
    /// <see cref="StripeStatus"/> y si el onboarding queda completo — suficiente para
    /// validar que el degradado de Approved funciona.
    /// </summary>
    public static (StripeStatus Status, bool OnboardingCompleted) EvaluateStripeAccountReplica(
        StripeAccountSnapshot account)
    {
        // requirementErrors combina current + future (Round 29 FIX-DOC-FAIL).
        var requirementErrors = new List<string>();
        requirementErrors.AddRange(account.RequirementsErrors);
        requirementErrors.AddRange(account.FutureRequirementsErrors);

        var disabledReason = account.DisabledReason ?? string.Empty;
        bool transfersActive = account.TransfersActive;
        bool chargesEnabled = account.ChargesEnabled;
        bool payoutsEnabled = account.PayoutsEnabled && transfersActive;
        bool detailsSubmitted = account.DetailsSubmitted;
        bool tosAccepted = account.TosAccepted;

        var status = StripeStatus.NotRequested;
        bool onboardingCompleted = false;

        // --- Rama disabledReason (early returns en el original) ---
        if (!string.IsNullOrEmpty(disabledReason) && IsPermanentRejectionReplica(disabledReason))
        {
            return (StripeStatus.Rejected, false);
        }

        if (!string.IsNullOrEmpty(disabledReason))
        {
            if ((disabledReason == "requirements.pending_verification" || disabledReason == "requirements.pending_review")
                && chargesEnabled && payoutsEnabled)
            {
                if (detailsSubmitted && tosAccepted)
                {
                    status = StripeStatus.Approved;
                    onboardingCompleted = true;
                    // MUD-DC gate: errors[] degrada en este early return también.
                    if (requirementErrors.Any())
                    {
                        status = StripeStatus.ActionRequired;
                        onboardingCompleted = false;
                    }
                    return (status, onboardingCompleted);
                }
            }

            status = disabledReason switch
            {
                "action_required.requested_capabilities" => StripeStatus.ActionRequired,
                "requirements.past_due" => StripeStatus.RequirementsPastDue,
                "requirements.pending_verification" => StripeStatus.PendingVerification,
                "under_review" => StripeStatus.UnderReview,
                "other" => StripeStatus.ActionRequired,
                "platform_paused" => StripeStatus.Restricted,
                _ when disabledReason.StartsWith("rejected.") => StripeStatus.Rejected,
                "listed" => StripeStatus.Rejected,
                "requirements.missing" or "requirements.currently_due" or "requirements.pending_review"
                    => StripeStatus.ActionRequired,
                "platform_disabled" or "platform_suspended" => StripeStatus.Restricted,
                _ when disabledReason.StartsWith("requirements.") => StripeStatus.Restricted,
                _ => StripeStatus.Disabled, // LogAndFallbackDisabled
            };
            return (status, false);
        }

        // --- Cascada principal ---
        if (account.PastDue.Any() || account.FuturePastDue.Any())
        {
            status = StripeStatus.RequirementsPastDue;
        }
        else if (account.CurrentlyDue.Any())
        {
            status = StripeStatus.ActionRequired;
        }
        else if (account.PendingVerification.Any() && (!chargesEnabled || !payoutsEnabled))
        {
            status = StripeStatus.PendingVerification;
        }
        else if (account.FutureCurrentlyDue.Any())
        {
            status = StripeStatus.RestrictedSoon;
        }
        else if (account.FutureEventuallyDue.Any())
        {
            status = StripeStatus.RequirementsDue;
        }
        else if (!chargesEnabled || !payoutsEnabled || !transfersActive)
        {
            status = StripeStatus.Disabled;
        }
        else if (detailsSubmitted && tosAccepted && chargesEnabled && payoutsEnabled)
        {
            status = StripeStatus.Approved;
            onboardingCompleted = true;
        }
        else if (!detailsSubmitted || !tosAccepted)
        {
            status = StripeStatus.Pending;
        }

        // Round 29 FIX-DOC-FAIL: deadline inminente degrada Approved → RestrictedSoon.
        if (status == StripeStatus.Approved && account.CurrentDeadline.HasValue)
        {
            status = StripeStatus.RestrictedSoon;
            onboardingCompleted = false;
        }

        // MUD-CY / FIX-DOC-FAIL: errors[] (current + future) degradan el estado final.
        if (status == StripeStatus.Approved && requirementErrors.Any())
        {
            status = StripeStatus.ActionRequired;
            onboardingCompleted = false;
        }
        else if (status == StripeStatus.RequirementsDue && requirementErrors.Any())
        {
            status = StripeStatus.RestrictedSoon;
            onboardingCompleted = false;
        }

        return (status, onboardingCompleted);
    }

    /// <summary>
    /// Réplica de IsPermanentRejection (SubscriptionController.cs:~7596): solo los
    /// reasons rejected.* / listed son permanentes; "other" NO lo es.
    /// </summary>
    private static bool IsPermanentRejectionReplica(string disabledReason)
        => disabledReason.StartsWith("rejected.") || disabledReason == "listed";

    /// <summary>
    /// Réplica de la limpieza que hace el handler account.application.deauthorized
    /// (SubscriptionController.cs:2972-2976): aplica estado Deauthorized + desvincula
    /// la cuenta Stripe del perfil. Mutación pura sobre la entidad (sin BD).
    /// </summary>
    public static void ApplyDeauthorizationReplica(ExpertProfile profile)
    {
        profile.StripeStatus = StripeStatus.Deauthorized;
        profile.OnboardingCompleted = false;
        profile.StripeAccountId = null;
        profile.PendingStripeAccountId = null;
    }
}
