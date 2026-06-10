using Microsoft.EntityFrameworkCore;
using newApi.Common;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace NewApi.Tests.Simulators;

/// <summary>
/// Extensión del simulador para el viaje de creación de un experto + onboarding
/// Stripe Connect (Agente 1).
///
/// FIEL al código real auditado:
///   - UserController.BecomeExpert (UserController.cs:557): crea ExpertProfile vía
///     UserService.BecomeExpert. El profile arranca StripeStatus=NotRequested,
///     OnboardingCompleted=false, StripeAccountId=null (no se toca Stripe aún).
///   - SubscriptionController.expert-onboarding (SubscriptionController.cs:768) +
///     create-account-link (1505): crean la cuenta Express y setean
///     PendingStripeAccountId=account.Id, OnboardingCompleted=false,
///     StripeStatus=Pending (SubscriptionController.cs:1372-1374).
///   - SubscriptionController.account.application.authorized (2889): mueve
///     PendingStripeAccountId → StripeAccountId SIN cambiar StripeStatus (sigue Pending).
///   - SubscriptionController.account.updated (3003) → EvaluateStripeAccount (251) →
///     ApplyStripeAccountState (590): mapea el snapshot de la cuenta Stripe al
///     StripeStatus correcto + OnboardingCompleted. SimulateAccountUpdatedAsync
///     replica el ÁRBOL DE DECISIÓN COMPLETO de EvaluateStripeAccount.
///   - SubscriptionController.account.application.deauthorized (2911): pone
///     StripeStatus=Deauthorized, OnboardingCompleted=false y NULLEA StripeAccountId
///     + PendingStripeAccountId (SubscriptionController.cs:2954-2976).
///   - StripeValidationService.ValidateExpertCanCreateServicesAsync (StripeValidationService.cs:99)
///     y ValidateExpertCanProposeAppointmentsAsync (134): sólo Approved+OnboardingCompleted
///     (o PendingVerification) permiten crear servicios / proponer citas.
///
/// Lo que NO hace (deliberado): no llama a la Stripe API; los snapshots de cuenta
/// se pasan como parámetros explícitos que imitan los campos de Stripe.Account
/// (ChargesEnabled, PayoutsEnabled, Capabilities.Transfers, Requirements.Errors[],
/// FutureRequirements.Errors[], DisabledReason, DetailsSubmitted, TosAcceptance.Date,
/// Requirements.CurrentDeadline).
/// </summary>
public static partial class MarketplaceFlowSimulator
{
    // ─────────────────────────────────────────────────────────────────────────
    // STEP 0 · become-expert → ExpertProfile(NotRequested, OnboardingCompleted=false)
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Excepción que el simulador lanza cuando el país auto-detectado del experto NO
    /// está en la whitelist <see cref="SupportedConnectCountries"/>. Replica el efecto
    /// observable del gate de UserService.BecomeExpert (UserService.cs:853): el
    /// ExpertProfile NUNCA se crea (se evita el "zombie expert") y el caller recibe el
    /// errorCode <c>CountryNotSupported</c>.
    /// </summary>
    public sealed class CountryNotSupportedException : InvalidOperationException
    {
        public string ErrorCode => "CountryNotSupported";
        public string? CountryCode { get; }
        public CountryNotSupportedException(string? countryCode)
            : base($"CountryNotSupported: país '{countryCode ?? "<null>"}' no soportado por Stripe Connect") =>
            CountryCode = countryCode;
    }

    /// <summary>
    /// Replica el efecto de UserController.BecomeExpert → UserService.BecomeExpert:
    /// crea el ExpertProfile inicial sin tocar Stripe. StripeStatus=NotRequested,
    /// OnboardingCompleted=false, StripeAccountId=null.
    /// El User debe existir; el caller decide si además lo promociona a UserRole.Expert.
    ///
    /// <paramref name="countryCode"/> imita el país que Mapbox auto-detecta a partir de las
    /// coordenadas (UserService.cs:748-852). Se valida contra
    /// <see cref="SupportedConnectCountries.IsSupported"/> (gate de UserService.cs:853):
    /// si no está soportado se lanza <see cref="CountryNotSupportedException"/> y el profile
    /// NO se crea, igual que el backend real. El país soportado queda persistido en
    /// <see cref="ExpertProfile.Country"/> (lo que luego determina las capabilities Stripe).
    /// </summary>
    public static async Task<ExpertProfile> BecomeExpertAsync(
        AppDbContext db, int userId, string countryCode = "ES")
    {
        // Gate de país (UserService.cs:853) — antes de crear nada, como el backend real.
        if (!SupportedConnectCountries.IsSupported(countryCode))
            throw new CountryNotSupportedException(countryCode);

        var existing = await db.ExpertProfiles.FirstOrDefaultAsync(ep => ep.UserId == userId);
        if (existing != null)
            throw new InvalidOperationException(
                $"User {userId} ya tiene ExpertProfile (BecomeExpert rebota con 'You already have an expert profile')");

        var profile = new ExpertProfile
        {
            UserId = userId,
            ProfilePictureUrl = "https://example.test/avatar.png",
            ProfilePictureObjectName = "avatar.png",
            Description = "Test expert profile (become-expert)",
            Latitude = "40.4168",
            Longitude = "-3.7038",
            Country = countryCode.Trim(),
            StripeAccountId = null,
            PendingStripeAccountId = null,
            StripeStatus = StripeStatus.NotRequested,
            OnboardingCompleted = false,
            CreatedAt = DateTime.UtcNow,
        };
        db.ExpertProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP 1 · expert-onboarding / create-account-link → Pending + PendingStripeAccountId
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Replica SubscriptionController.expert-onboarding: tras crear la cuenta Express
    /// en Stripe, persiste PendingStripeAccountId=account.Id, OnboardingCompleted=false,
    /// StripeStatus=Pending (SubscriptionController.cs:1372-1374).
    /// Devuelve el stripeAccountId sintético generado.
    /// </summary>
    public static async Task<string> StartStripeOnboardingAsync(AppDbContext db, int expertProfileId)
    {
        var profile = await db.ExpertProfiles.SingleAsync(ep => ep.Id == expertProfileId);
        var accountId = "acct_sim_" + Guid.NewGuid().ToString("N").Substring(0, 16);

        profile.PendingStripeAccountId = accountId;
        profile.OnboardingCompleted = false;
        profile.StripeStatus = StripeStatus.Pending;
        await db.SaveChangesAsync();
        return accountId;
    }

    /// <summary>
    /// Replica el webhook account.application.authorized (SubscriptionController.cs:2889):
    /// promueve PendingStripeAccountId → StripeAccountId SIN cambiar StripeStatus
    /// (sigue en Pending hasta que llegue account.updated).
    /// Opcional en los tests: account.updated también sabe localizar el profile por
    /// PendingStripeAccountId (FindExpertProfileForAccountAsync).
    /// </summary>
    public static async Task ConfirmStripeAuthorizedAsync(AppDbContext db, string stripeAccountId)
    {
        var profile = await db.ExpertProfiles
            .SingleAsync(ep => ep.PendingStripeAccountId == stripeAccountId);
        profile.StripeAccountId = stripeAccountId;
        profile.PendingStripeAccountId = null;
        await db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP 2 · account.updated → EvaluateStripeAccount → ApplyStripeAccountState
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Replica FIELMENTE el árbol de decisión de
    /// SubscriptionController.EvaluateStripeAccount (SubscriptionController.cs:251)
    /// y lo aplica vía la misma semántica de ApplyStripeAccountState (590):
    ///   - StripeAccountId se setea con ??= (no pisa uno existente),
    ///   - PendingStripeAccountId = null,
    ///   - StripeStatus / OnboardingCompleted / StripeFutureRequirements / StripeFutureDueAt.
    ///
    /// Los parámetros imitan el snapshot de Stripe.Account.
    /// </summary>
    public static async Task<ExpertProfile> SimulateAccountUpdatedAsync(
        AppDbContext db,
        string stripeAccountId,
        bool chargesEnabled,
        bool payoutsEnabled,
        string[]? requirementsErrors = null,
        string[]? futureRequirementsErrors = null,
        string[]? currentlyDue = null,
        string[]? pastDue = null,
        string[]? pendingVerification = null,
        string[]? futureCurrentlyDue = null,
        string[]? futureEventuallyDue = null,
        string[]? futurePastDue = null,
        string transfersCapability = "active",
        bool detailsSubmitted = true,
        bool tosAccepted = true,
        string? disabledReason = null,
        DateTime? requirementsCurrentDeadline = null,
        DateTime? futureRequirementsCurrentDeadline = null)
    {
        // El webhook busca por StripeAccountId O PendingStripeAccountId
        // (FindExpertProfileForAccountAsync). Si el profile aún tenía Pending, el
        // ApplyStripeAccountState lo limpia. Aquí promovemos PendingStripeAccountId
        // a StripeAccountId para imitar el ??= cuando StripeAccountId era null.
        var profile = await db.ExpertProfiles.SingleAsync(ep =>
            ep.StripeAccountId == stripeAccountId || ep.PendingStripeAccountId == stripeAccountId);

        var state = EvaluateStripeAccountFaithful(
            chargesEnabled,
            payoutsEnabled,
            transfersCapability,
            detailsSubmitted,
            tosAccepted,
            disabledReason,
            requirementsErrors ?? Array.Empty<string>(),
            futureRequirementsErrors ?? Array.Empty<string>(),
            currentlyDue ?? Array.Empty<string>(),
            pastDue ?? Array.Empty<string>(),
            pendingVerification ?? Array.Empty<string>(),
            futureCurrentlyDue ?? Array.Empty<string>(),
            futureEventuallyDue ?? Array.Empty<string>(),
            futurePastDue ?? Array.Empty<string>(),
            requirementsCurrentDeadline,
            futureRequirementsCurrentDeadline);

        // ApplyStripeAccountState semantics (SubscriptionController.cs:590)
        profile.StripeAccountId ??= stripeAccountId;
        profile.PendingStripeAccountId = null;
        profile.StripeStatus = state.Status;
        profile.OnboardingCompleted = state.OnboardingCompleted;
        profile.StripeStatusDetails = state.StatusDetails;
        profile.StripeFutureRequirements = state.FutureRequirements.Length > 0
            ? string.Join(", ", state.FutureRequirements)
            : null;
        profile.StripeFutureDueAt = state.FutureRequirementsDueAt;

        await db.SaveChangesAsync();
        return profile;
    }

    /// <summary>
    /// Helper "feliz": account.updated con charges+payouts habilitados, sin requirements,
    /// transfers activo, details+tos OK → Approved + OnboardingCompleted.
    /// </summary>
    public static Task<ExpertProfile> SimulateAccountApprovedAsync(AppDbContext db, string stripeAccountId)
        => SimulateAccountUpdatedAsync(db, stripeAccountId, chargesEnabled: true, payoutsEnabled: true);

    /// <summary>
    /// Helper de rechazo permanente: disabled_reason rejected.* → Rejected.
    /// </summary>
    public static Task<ExpertProfile> SimulateAccountRejectedAsync(
        AppDbContext db, string stripeAccountId, string disabledReason = "rejected.fraud")
        => SimulateAccountUpdatedAsync(
            db, stripeAccountId,
            chargesEnabled: false, payoutsEnabled: false,
            transfersCapability: "inactive",
            detailsSubmitted: true, tosAccepted: true,
            disabledReason: disabledReason);

    /// <summary>
    /// Replica el webhook account.application.deauthorized (SubscriptionController.cs:2911):
    /// StripeStatus=Deauthorized, OnboardingCompleted=false, StripeAccountId=null,
    /// PendingStripeAccountId=null.
    /// </summary>
    public static async Task<ExpertProfile> SimulateDeauthorizationAsync(AppDbContext db, string stripeAccountId)
    {
        var profile = await db.ExpertProfiles.SingleAsync(ep =>
            ep.StripeAccountId == stripeAccountId || ep.PendingStripeAccountId == stripeAccountId);

        profile.StripeStatus = StripeStatus.Deauthorized;
        profile.OnboardingCompleted = false;
        profile.StripeStatusDetails = "Tu cuenta se desconectó. Vuelve a conectarla para seguir cobrando.";
        // El handler real nulea ambos ids tras ApplyStripeAccountState
        profile.StripeAccountId = null;
        profile.PendingStripeAccountId = null;
        profile.StripeFutureRequirements = null;
        profile.StripeFutureDueAt = null;
        await db.SaveChangesAsync();
        return profile;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GATING · ValidateExpertCanCreateServicesAsync / ProposeAppointmentsAsync
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Replica StripeValidationService.ValidateExpertCanCreateServicesAsync
    /// (StripeValidationService.cs:99): PendingVerification pasa (informativo);
    /// el resto exige StripeStatus==Approved &amp;&amp; OnboardingCompleted.
    /// </summary>
    public static async Task<bool> CanExpertCreateServicesAsync(AppDbContext db, int expertProfileId)
    {
        var profile = await db.ExpertProfiles.SingleAsync(ep => ep.Id == expertProfileId);
        return CanCreateOrPropose(profile);
    }

    /// <summary>
    /// Replica StripeValidationService.ValidateExpertCanProposeAppointmentsAsync
    /// (StripeValidationService.cs:134): misma lógica que crear servicios.
    /// </summary>
    public static async Task<bool> CanExpertProposeAppointmentsAsync(AppDbContext db, int expertProfileId)
    {
        var profile = await db.ExpertProfiles.SingleAsync(ep => ep.Id == expertProfileId);
        return CanCreateOrPropose(profile);
    }

    private static bool CanCreateOrPropose(ExpertProfile profile)
    {
        // PendingVerification es informativo, no bloqueante (StripeValidationService.cs:108/143)
        if (profile.StripeStatus == StripeStatus.PendingVerification)
            return true;

        return profile.StripeStatus == StripeStatus.Approved && profile.OnboardingCompleted;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CORE · réplica fiel de EvaluateStripeAccount (SubscriptionController.cs:251)
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class FaithfulStripeState
    {
        public StripeStatus Status { get; set; } = StripeStatus.Pending;
        public bool OnboardingCompleted { get; set; }
        public string? StatusDetails { get; set; }
        public string[] FutureRequirements { get; set; } = Array.Empty<string>();
        public bool FutureRequirementsPastDue { get; set; }
        public bool FutureRequirementsCurrentlyDue { get; set; }
        public DateTime? FutureRequirementsDueAt { get; set; }
        public string[] RequirementErrors { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Réplica del árbol de decisión completo. Cada bloque corresponde 1:1 a las
    /// líneas citadas de SubscriptionController.EvaluateStripeAccount.
    /// </summary>
    private static FaithfulStripeState EvaluateStripeAccountFaithful(
        bool chargesEnabledRaw,
        bool payoutsEnabledRaw,
        string transfersCapability,
        bool detailsSubmitted,
        bool tosAccepted,
        string? disabledReasonRaw,
        string[] requirementsErrors,
        string[] futureRequirementsErrors,
        string[] currentlyDue,
        string[] pastDue,
        string[] pendingVerification,
        string[] futureCurrentlyDue,
        string[] futureEventuallyDue,
        string[] futurePastDue,
        DateTime? requirementsCurrentDeadline,
        DateTime? futureRequirementsCurrentDeadline)
    {
        var disabledReason = disabledReasonRaw ?? string.Empty;

        // requirementErrors = requirements.errors[] + future_requirements.errors[] (Round 29 FIX-DOC-FAIL),
        // Distinct (SubscriptionController.cs:259-325)
        var requirementErrors = requirementsErrors
            .Concat(futureRequirementsErrors)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // SubscriptionController.cs:308-312
        bool transfersActive = transfersCapability == "active";
        bool chargesEnabled = chargesEnabledRaw;
        bool payoutsEnabled = payoutsEnabledRaw && transfersActive;

        var state = new FaithfulStripeState
        {
            FutureRequirements = futureCurrentlyDue.Concat(futureEventuallyDue).Concat(futurePastDue).Distinct().ToArray(),
            FutureRequirementsCurrentlyDue = futureCurrentlyDue.Any(),
            FutureRequirementsPastDue = futurePastDue.Any(),
            RequirementErrors = requirementErrors,
        };

        // SubscriptionController.cs:334-340 — deadline real o heurístico
        state.FutureRequirementsDueAt =
            futureRequirementsCurrentDeadline
            ?? requirementsCurrentDeadline
            ?? (state.FutureRequirementsPastDue ? DateTime.UtcNow.AddDays(-1)
              : state.FutureRequirementsCurrentlyDue ? DateTime.UtcNow.AddDays(7)
              : futureEventuallyDue.Any() ? DateTime.UtcNow.AddDays(30)
              : (DateTime?)null);

        // SubscriptionController.cs:342-348 — rechazo permanente
        if (!string.IsNullOrEmpty(disabledReason) && IsPermanentRejectionFaithful(disabledReason))
        {
            state.Status = StripeStatus.Rejected;
            state.OnboardingCompleted = false;
            state.StatusDetails = "Stripe rechazó tu cuenta. Revisa el motivo más abajo.";
            return state;
        }

        // SubscriptionController.cs:350-423 — disabledReason no permanente
        if (!string.IsNullOrEmpty(disabledReason))
        {
            // 352-376 — pending_verification/pending_review con pagos habilitados
            if ((disabledReason == "requirements.pending_verification" || disabledReason == "requirements.pending_review")
                && chargesEnabled && payoutsEnabled)
            {
                if (detailsSubmitted && tosAccepted)
                {
                    state.Status = StripeStatus.Approved;
                    state.OnboardingCompleted = true;
                    // MUD-DC gate (368-372)
                    if (requirementErrors.Any())
                    {
                        state.Status = StripeStatus.ActionRequired;
                        state.OnboardingCompleted = false;
                    }
                    state.StatusDetails = $"status={state.Status}";
                    return state;
                }
            }

            // 390-419 — switch de disabledReason
            state.Status = disabledReason switch
            {
                "action_required.requested_capabilities" => StripeStatus.ActionRequired,
                "requirements.past_due" => StripeStatus.RequirementsPastDue,
                "requirements.pending_verification" => StripeStatus.PendingVerification,
                "under_review" => StripeStatus.UnderReview,
                "other" => StripeStatus.ActionRequired,
                "platform_paused" => StripeStatus.Restricted,
                _ when disabledReason.StartsWith("rejected.") => StripeStatus.Rejected,
                "listed" => StripeStatus.Rejected,
                "requirements.missing" or "requirements.currently_due" or "requirements.pending_review" => StripeStatus.ActionRequired,
                "platform_disabled" or "platform_suspended" => StripeStatus.Restricted,
                _ when disabledReason.StartsWith("requirements.") => StripeStatus.Restricted,
                _ => StripeStatus.Disabled, // LogAndFallbackDisabled
            };
            state.OnboardingCompleted = false;
            state.StatusDetails = $"status={state.Status}";
            return state;
        }

        // SubscriptionController.cs:425-462 — cascada sin disabledReason
        if (pastDue.Any() || futurePastDue.Any())
        {
            state.Status = StripeStatus.RequirementsPastDue;
        }
        else if (currentlyDue.Any())
        {
            state.Status = StripeStatus.ActionRequired;
        }
        else if (pendingVerification.Any() && (!chargesEnabled || !payoutsEnabled))
        {
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
            state.Status = StripeStatus.Approved;
            state.OnboardingCompleted = true;
        }
        else if (!detailsSubmitted || !tosAccepted)
        {
            state.Status = StripeStatus.Pending;
        }

        // SubscriptionController.cs:491-495 — deadline inminente degrada Approved
        if (state.Status == StripeStatus.Approved && requirementsCurrentDeadline.HasValue)
        {
            state.Status = StripeStatus.RestrictedSoon;
            state.OnboardingCompleted = false;
        }

        // SubscriptionController.cs:520-529 — errors[] degrada Approved/RequirementsDue
        if (state.Status == StripeStatus.Approved && requirementErrors.Any())
        {
            state.Status = StripeStatus.ActionRequired;
            state.OnboardingCompleted = false;
        }
        else if (state.Status == StripeStatus.RequirementsDue && requirementErrors.Any())
        {
            state.Status = StripeStatus.RestrictedSoon;
            state.OnboardingCompleted = false;
        }

        state.StatusDetails = $"status={state.Status}";
        return state;
    }

    /// <summary>
    /// Réplica EXACTA de IsPermanentRejection (SubscriptionController.cs:7683-7736),
    /// respetando el ORDEN de las reglas (importa: la primera que matchea gana).
    ///
    /// ⚠️ DIVERGENCIA REAL DOCUMENTADA: como este método se invoca en el GATE de
    /// EvaluateStripeAccount (SubscriptionController.cs:342) ANTES del switch de
    /// disabledReason (390), todos los reasons que aquí devuelven `true` se mapean a
    /// Rejected y NUNCA alcanzan su case del switch. En consecuencia varios cases del
    /// switch son CÓDIGO MUERTO en la práctica:
    ///   - "under_review"        → gate=true  ⇒ Rejected   (su case `=> UnderReview` nunca corre)
    ///   - "platform_paused"     → gate=true  ⇒ Rejected   (su case `=> Restricted` nunca corre)
    ///   - "requirements.missing"/"requirements.currently_due"/"requirements.pending_review"
    ///                            → gate=true  ⇒ Rejected   (su case `=> ActionRequired/...` nunca corre)
    ///   - "platform_disabled"/"platform_suspended" → gate=true ⇒ Rejected
    /// Reasons que SÍ llegan al switch (gate=false): requirements.past_due,
    /// requirements.pending_verification, action_required.requested_capabilities,
    /// other, fields_needed.
    /// </summary>
    private static bool IsPermanentRejectionFaithful(string? disabledReason)
    {
        // 7685-7686
        if (string.IsNullOrEmpty(disabledReason))
            return true;

        // 7693-7699 — temporales que permiten reintentar
        if (disabledReason == "requirements.past_due" ||
            disabledReason == "requirements.pending_verification" ||
            disabledReason == "action_required.requested_capabilities" ||
            disabledReason == "fields_needed")
        {
            return false;
        }

        // 7706-7709 — rechazos permanentes
        if (disabledReason.StartsWith("rejected.") || disabledReason == "listed")
            return true;

        // 7716-7721 — temporales en revisión: bloquean crear OTRA cuenta ⇒ true
        if (disabledReason == "under_review" ||
            disabledReason == "requirements.past_due" ||
            disabledReason == "requirements.pending_verification")
        {
            return true;
        }

        // 7727-7732 — permiten reintentar con nueva cuenta
        if (disabledReason == "action_required.requested_capabilities" ||
            disabledReason == "other" ||
            disabledReason == "fields_needed")
        {
            return false;
        }

        // 7735 — por defecto: bloquear por seguridad
        return true;
    }
}
