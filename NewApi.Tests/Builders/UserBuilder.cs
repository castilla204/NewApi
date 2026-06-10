using Bogus;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace NewApi.Tests.Builders;

/// <summary>
/// Fluent builder para crear usuarios de test. Persiste vía AppDbContext.
///
/// Uso típico:
///   var client = await new UserBuilder().AsClient().PersistAsync(db);
///   var expert = await new UserBuilder("expert@x.com").AsExpert().Verified().PersistAsync(db);
/// </summary>
public class UserBuilder
{
    private static readonly Faker _faker = new("es");

    private string? _email;
    private string? _name;
    private string? _passwordHash;
    private UserRole _role = UserRole.Client;
    private bool _emailVerified = false;
    private bool _isBlocked = false;
    private bool _isDeleted = false;

    public UserBuilder() { }
    public UserBuilder(string email) { _email = email; }

    public UserBuilder WithEmail(string email) { _email = email; return this; }
    public UserBuilder WithName(string name) { _name = name; return this; }
    public UserBuilder WithPasswordHash(string hash) { _passwordHash = hash; return this; }
    public UserBuilder AsClient() { _role = UserRole.Client; return this; }
    public UserBuilder AsExpert() { _role = UserRole.Expert; return this; }
    public UserBuilder AsAdmin() { _role = UserRole.Admin; return this; }
    public UserBuilder Verified() { _emailVerified = true; return this; }
    public UserBuilder Blocked() { _isBlocked = true; return this; }
    public UserBuilder SoftDeleted() { _isDeleted = true; return this; }

    public User Build()
    {
        return new User
        {
            Name = _name ?? _faker.Name.FullName(),
            Email = _email ?? _faker.Internet.Email(),
            // Hash dummy BCrypt-shaped. Si el test necesita login real, pasa hash auténtico con WithPasswordHash.
            Password = _passwordHash ?? "$2a$12$abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMN.",
            Role = _role,
            EmailVerified = _emailVerified,
            EmailVerifiedAt = _emailVerified ? DateTime.UtcNow : (DateTime?)null,
            IsBlocked = _isBlocked,
            IsDeleted = _isDeleted,
            DeletedAt = _isDeleted ? DateTime.UtcNow : (DateTime?)null,
            CreatedAt = DateTime.UtcNow,
            FailedLoginAttempts = 0,
        };
    }

    public async Task<User> PersistAsync(AppDbContext db)
    {
        var user = Build();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
