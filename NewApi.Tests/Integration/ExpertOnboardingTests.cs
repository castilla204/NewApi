using Microsoft.EntityFrameworkCore;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using NewApi.Tests.Simulators;

namespace NewApi.Tests.Integration;

/// <summary>
/// E2E del viaje completo de creación de un experto + onboarding Stripe Connect
/// (Agente 1). Faithful al código auditado:
///   - UserController.BecomeExpert (UserController.cs:557)
///   - SubscriptionController.expert-onboarding / create-account-link (768 / 1505)
///   - SubscriptionController.EvaluateStripeAccount (251) + ApplyStripeAccountState (590)
///   - SubscriptionController.account.application.deauthorized (2911)
///   - StripeValidationService.ValidateExpertCanCreateServicesAsync (99) /
///     ValidateExpertCanProposeAppointmentsAsync (134)
///
/// Cada test arranca de un User+ExpertProfile reales en el testcontainer Postgres y
/// recorre las transiciones de StripeStatus disparadas por los webhooks simulados.
/// </summary>
public class ExpertOnboardingTests : IntegrationTestBase
{
    public ExpertOnboardingTests(PostgresContainerFixture fixture) : base(fixture) { }

    // ─────────────────────────────────────────────────────────────────────────
    // EXP-01 · Journey feliz
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "EXP-01 · become-expert → onboarding → account.updated(charges+payouts) → Approved → crea servicio")]
    public async Task Happy_journey_user_to_approved_can_create_service()
    {
        await using var db = NewDbContext();

        var user = await new UserBuilder("exp01@test.dev").AsExpert().Verified().PersistAsync(db);

        // become-expert: ExpertProfile inicial NotRequested / OnboardingCompleted=false
        var profile = await MarketplaceFlowSimulator.BecomeExpertAsync(db, user.Id);
        profile.StripeStatus.Should().Be(StripeStatus.NotRequested);
        profile.OnboardingCompleted.Should().BeFalse();
        profile.StripeAccountId.Should().BeNull();
        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id))
            .Should().BeFalse("NotRequested no permite crear servicios");

        // start onboarding: Pending + PendingStripeAccountId
        var acct = await MarketplaceFlowSimulator.StartStripeOnboardingAsync(db, profile.Id);
        await using (var v1 = NewDbContext())
        {
            var p = await v1.ExpertProfiles.SingleAsync(ep => ep.Id == profile.Id);
            p.StripeStatus.Should().Be(StripeStatus.Pending);
            p.PendingStripeAccountId.Should().Be(acct);
            p.OnboardingCompleted.Should().BeFalse();
        }

        // account.updated con charges + payouts habilitados → Approved
        var approved = await MarketplaceFlowSimulator.SimulateAccountUpdatedAsync(
            db, acct, chargesEnabled: true, payoutsEnabled: true);

        approved.StripeStatus.Should().Be(StripeStatus.Approved);
        approved.OnboardingCompleted.Should().BeTrue();
        approved.StripeAccountId.Should().Be(acct, "ApplyStripeAccountState promueve Pending→StripeAccountId vía ??=");
        approved.PendingStripeAccountId.Should().BeNull();

        // gating: ahora SÍ puede crear servicios y proponer citas
        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id)).Should().BeTrue();
        (await MarketplaceFlowSimulator.CanExpertProposeAppointmentsAsync(db, profile.Id)).Should().BeTrue();

        // y el servicio se persiste de verdad
        var svc = await new SearchServiceBuilder(profile.Id).WithPrice(120m, "EUR").PersistAsync(db);
        svc.Id.Should().BeGreaterThan(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EXP-02 · Onboarding incompleto (requirements.errors → bloqueado)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "EXP-02 · account.updated con currently_due → ActionRequired → NO crea servicios")]
    public async Task Incomplete_onboarding_blocks_service_creation()
    {
        await using var db = NewDbContext();

        var user = await new UserBuilder("exp02@test.dev").AsExpert().Verified().PersistAsync(db);
        var profile = await MarketplaceFlowSimulator.BecomeExpertAsync(db, user.Id);
        var acct = await MarketplaceFlowSimulator.StartStripeOnboardingAsync(db, profile.Id);

        // Stripe aún pide datos: currently_due poblado, charges/payouts deshabilitados
        var updated = await MarketplaceFlowSimulator.SimulateAccountUpdatedAsync(
            db, acct,
            chargesEnabled: false, payoutsEnabled: false,
            currentlyDue: new[] { "individual.verification.document" },
            detailsSubmitted: true, tosAccepted: true);

        updated.StripeStatus.Should().Be(StripeStatus.ActionRequired);
        updated.OnboardingCompleted.Should().BeFalse();

        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id))
            .Should().BeFalse("ActionRequired no es Approved");
        (await MarketplaceFlowSimulator.CanExpertProposeAppointmentsAsync(db, profile.Id))
            .Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EXP-03 · future_requirements.errors[] (Round 29 FIX-DOC-FAIL)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "EXP-03 · charges_enabled=true PERO future_requirements.errors[] poblado → ActionRequired (no Approved ciego)")]
    public async Task FutureRequirementsErrors_degrades_from_blind_approved()
    {
        await using var db = NewDbContext();

        var user = await new UserBuilder("exp03@test.dev").AsExpert().Verified().PersistAsync(db);
        var profile = await MarketplaceFlowSimulator.BecomeExpertAsync(db, user.Id);
        var acct = await MarketplaceFlowSimulator.StartStripeOnboardingAsync(db, profile.Id);

        // Round 29: charges+payouts habilitados (cuenta operativa) pero Stripe puso un
        // error de documento en future_requirements.errors[] (bucket SEPARADO de
        // requirements.errors[]). Sin el fix la cuenta caía a Approved ciego.
        var updated = await MarketplaceFlowSimulator.SimulateAccountUpdatedAsync(
            db, acct,
            chargesEnabled: true, payoutsEnabled: true,
            requirementsErrors: Array.Empty<string>(),
            futureRequirementsErrors: new[] { "verification_document_failed" },
            detailsSubmitted: true, tosAccepted: true);

        // El status correcto es ActionRequired (gate MUD-CY/FIX-DOC-FAIL degrada Approved)
        updated.StripeStatus.Should().Be(StripeStatus.ActionRequired,
            "Round 29: future_requirements.errors[] debe degradar el Approved ciego a ActionRequired");
        updated.OnboardingCompleted.Should().BeFalse();

        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id))
            .Should().BeFalse("aunque charges_enabled=true, hay un error de documento pendiente");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EXP-04 · Rechazo permanente (disabled_reason rejected.*)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "EXP-04 · account.updated disabled_reason=rejected.fraud → Rejected → NO crea servicios")]
    public async Task Permanent_rejection_blocks_and_is_terminal()
    {
        await using var db = NewDbContext();

        var user = await new UserBuilder("exp04@test.dev").AsExpert().Verified().PersistAsync(db);
        var profile = await MarketplaceFlowSimulator.BecomeExpertAsync(db, user.Id);
        var acct = await MarketplaceFlowSimulator.StartStripeOnboardingAsync(db, profile.Id);

        var rejected = await MarketplaceFlowSimulator.SimulateAccountRejectedAsync(db, acct, "rejected.fraud");

        rejected.StripeStatus.Should().Be(StripeStatus.Rejected);
        rejected.OnboardingCompleted.Should().BeFalse();

        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id)).Should().BeFalse();
        (await MarketplaceFlowSimulator.CanExpertProposeAppointmentsAsync(db, profile.Id)).Should().BeFalse();

        // Un nuevo account.updated del mismo rejected.* sigue siendo Rejected (terminal).
        // (create-account-link además bloquea explícitamente Rejected en SubscriptionController.cs:1530.)
        var stillRejected = await MarketplaceFlowSimulator.SimulateAccountUpdatedAsync(
            db, acct,
            chargesEnabled: false, payoutsEnabled: false,
            transfersCapability: "inactive",
            disabledReason: "rejected.terms_of_service");
        stillRejected.StripeStatus.Should().Be(StripeStatus.Rejected);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EXP-05 · Deauthorization tras aprobación
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "EXP-05 · Approved → deauthorized → Deauthorized + StripeAccountId=null + bloqueado")]
    public async Task Deauthorization_after_approval_clears_account_and_blocks()
    {
        await using var db = NewDbContext();

        var user = await new UserBuilder("exp05@test.dev").AsExpert().Verified().PersistAsync(db);
        var profile = await MarketplaceFlowSimulator.BecomeExpertAsync(db, user.Id);
        var acct = await MarketplaceFlowSimulator.StartStripeOnboardingAsync(db, profile.Id);

        var approved = await MarketplaceFlowSimulator.SimulateAccountApprovedAsync(db, acct);
        approved.StripeStatus.Should().Be(StripeStatus.Approved);
        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id)).Should().BeTrue();

        // Stripe desconecta la cuenta
        var deauth = await MarketplaceFlowSimulator.SimulateDeauthorizationAsync(db, acct);

        deauth.StripeStatus.Should().Be(StripeStatus.Deauthorized);
        deauth.OnboardingCompleted.Should().BeFalse();
        deauth.StripeAccountId.Should().BeNull("el handler nulea StripeAccountId tras deauth");
        deauth.PendingStripeAccountId.Should().BeNull();

        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id))
            .Should().BeFalse("Deauthorized bloquea nuevos servicios");
        (await MarketplaceFlowSimulator.CanExpertProposeAppointmentsAsync(db, profile.Id))
            .Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EXP-06 · Gating: Pending bloqueado, Approved permitido
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "EXP-06 · Experto Pending NO crea servicio; Experto Approved SÍ")]
    public async Task Gating_pending_blocked_approved_allowed()
    {
        await using var db = NewDbContext();

        var pendingUser = await new UserBuilder("exp06-pending@test.dev").AsExpert().Verified().PersistAsync(db);
        var pendingProfile = await new ExpertProfileBuilder(pendingUser.Id).Pending().PersistAsync(db);

        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, pendingProfile.Id))
            .Should().BeFalse("Pending no permite crear servicios");

        var approvedUser = await new UserBuilder("exp06-approved@test.dev").AsExpert().Verified().PersistAsync(db);
        var approvedProfile = await new ExpertProfileBuilder(approvedUser.Id).Approved().PersistAsync(db);

        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, approvedProfile.Id))
            .Should().BeTrue("Approved + OnboardingCompleted permite crear servicios");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EXP-07 · PendingVerification es informativo (no bloquea)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "EXP-07 · PendingVerification es informativo → SÍ permite crear servicios (no bloquea)")]
    public async Task PendingVerification_is_informative_not_blocking()
    {
        await using var db = NewDbContext();

        var user = await new UserBuilder("exp07@test.dev").AsExpert().Verified().PersistAsync(db);
        var profile = await PersistWithStatus(db, user.Id, StripeStatus.PendingVerification);

        (await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id))
            .Should().BeTrue("StripeValidationService trata PendingVerification como no-bloqueante (cs:108)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EXP-08 · Theory: los 13 StripeStatus y si permiten crear servicios
    // ─────────────────────────────────────────────────────────────────────────
    [Theory(DisplayName = "EXP-08 · Catálogo de los 13 StripeStatus → puede crear servicios?")]
    [InlineData(StripeStatus.NotRequested, false, false)]
    [InlineData(StripeStatus.Pending, false, false)]
    [InlineData(StripeStatus.Approved, true, true)]   // único que permite con OnboardingCompleted=true
    [InlineData(StripeStatus.Rejected, false, false)]
    [InlineData(StripeStatus.Deauthorized, false, false)]
    [InlineData(StripeStatus.ActionRequired, false, false)]
    [InlineData(StripeStatus.PendingVerification, false, true)]  // informativo → permite aunque OnboardingCompleted=false
    [InlineData(StripeStatus.RequirementsDue, false, false)]
    [InlineData(StripeStatus.RequirementsPastDue, false, false)]
    [InlineData(StripeStatus.RestrictedSoon, false, false)]
    [InlineData(StripeStatus.Restricted, false, false)]
    [InlineData(StripeStatus.Disabled, false, false)]
    [InlineData(StripeStatus.UnderReview, false, false)]
    public async Task All_13_statuses_gating_matrix(StripeStatus status, bool onboardingCompleted, bool expectedCanCreate)
    {
        await using var db = NewDbContext();

        var user = await new UserBuilder($"exp08-{status}@test.dev").AsExpert().Verified().PersistAsync(db);
        var profile = await PersistWithStatus(db, user.Id, status, onboardingCompleted);

        var canCreate = await MarketplaceFlowSimulator.CanExpertCreateServicesAsync(db, profile.Id);
        var canPropose = await MarketplaceFlowSimulator.CanExpertProposeAppointmentsAsync(db, profile.Id);

        canCreate.Should().Be(expectedCanCreate, $"StripeStatus={status} (OnboardingCompleted={onboardingCompleted})");
        // crear servicios y proponer citas comparten lógica idéntica
        canPropose.Should().Be(expectedCanCreate);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helper local
    // ─────────────────────────────────────────────────────────────────────────
    private static async Task<ExpertProfile> PersistWithStatus(
        AppDbContext db, int userId, StripeStatus status, bool onboardingCompleted = false)
    {
        var profile = new ExpertProfile
        {
            UserId = userId,
            ProfilePictureUrl = "https://example.test/avatar.png",
            ProfilePictureObjectName = "avatar.png",
            Description = "Test expert",
            Latitude = "40.4168",
            Longitude = "-3.7038",
            StripeAccountId = status == StripeStatus.NotRequested ? null : $"acct_{status}_{Guid.NewGuid():N}".Substring(0, 22),
            StripeStatus = status,
            OnboardingCompleted = onboardingCompleted,
            CreatedAt = DateTime.UtcNow,
        };
        db.ExpertProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }
}
