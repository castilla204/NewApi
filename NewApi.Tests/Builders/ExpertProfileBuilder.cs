using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace NewApi.Tests.Builders;

/// <summary>
/// Crea un ExpertProfile asociado a un User existente.
///
/// Uso:
///   var user = await new UserBuilder().AsExpert().PersistAsync(db);
///   var expert = await new ExpertProfileBuilder(user.Id).Approved().PersistAsync(db);
/// </summary>
public class ExpertProfileBuilder
{
    private readonly int _userId;
    private string? _stripeAccountId;
    private StripeStatus _stripeStatus = StripeStatus.NotRequested;
    private bool _onboardingCompleted = false;
    private bool _isOnVacation = false;

    public ExpertProfileBuilder(int userId) { _userId = userId; }

    public ExpertProfileBuilder WithStripeAccountId(string id) { _stripeAccountId = id; return this; }

    public ExpertProfileBuilder Approved()
    {
        _stripeAccountId ??= $"acct_test_{Guid.NewGuid():N}".Substring(0, 24);
        _stripeStatus = StripeStatus.Approved;
        _onboardingCompleted = true;
        return this;
    }

    public ExpertProfileBuilder Pending() { _stripeStatus = StripeStatus.Pending; return this; }
    public ExpertProfileBuilder Rejected() { _stripeStatus = StripeStatus.Rejected; return this; }
    public ExpertProfileBuilder Deauthorized() { _stripeStatus = StripeStatus.Deauthorized; return this; }
    public ExpertProfileBuilder OnVacation() { _isOnVacation = true; return this; }

    public ExpertProfile Build() => new ExpertProfile
    {
        UserId = _userId,
        ProfilePictureUrl = "https://example.test/avatar.png",
        ProfilePictureObjectName = "avatar.png",
        Description = "Test expert profile",
        Latitude = "40.4168",
        Longitude = "-3.7038",
        StripeAccountId = _stripeAccountId,
        StripeStatus = _stripeStatus,
        OnboardingCompleted = _onboardingCompleted,
        IsOnVacation = _isOnVacation,
        CreatedAt = DateTime.UtcNow,
    };

    public async Task<ExpertProfile> PersistAsync(AppDbContext db)
    {
        var profile = Build();
        db.ExpertProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }
}
