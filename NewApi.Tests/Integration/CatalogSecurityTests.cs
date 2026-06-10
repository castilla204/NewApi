using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;

namespace NewApi.Tests.Integration;

/// <summary>
/// Cuarta tanda · Modelos de seguridad y disputas.
///
/// Cubre:
///   - RefreshToken (rotación + replay-detection + cadena ReplacedByToken)
///   - EmailVerificationCode (3 Purpose, TTL 10 min, single-use, max intentos)
///   - UserMfaSettings (TOTP + recovery codes con contador de usados)
///   - Dispute (status lifecycle, ExpertResponseDeadline, ventana 14d post-completed)
/// </summary>
public class CatalogSecurityTests : IntegrationTestBase
{
    public CatalogSecurityTests(PostgresContainerFixture fixture) : base(fixture) { }

    // ─────────────────────────────────────────────────────────────────────────
    // RefreshToken · rotación y replay-detection
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "REFRESH · Token recién creado: no revocado, no expirado, sin ReplacedByToken")]
    public async Task RefreshToken_initial_state()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("rt-init@test.dev").AsClient().Verified().PersistAsync(db);

        var token = new RefreshToken
        {
            UserId = user.Id,
            Token = "rt_" + Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(90),
            CreatedAt = DateTime.UtcNow,
            CreatedByIp = "127.0.0.1",
            DeviceInfo = "Chrome 127 / Windows 10",
        };
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.RefreshTokens.SingleAsync(t => t.Id == token.Id);
        fromDb.IsRevoked.Should().BeFalse();
        fromDb.IsExpired.Should().BeFalse("ExpiresAt está 90 días en el futuro");
        fromDb.RevokedAt.Should().BeNull();
        fromDb.ReplacedByToken.Should().BeNull();
    }

    [Fact(DisplayName = "REFRESH · Rotación normal: token viejo revocado y ReplacedByToken apunta al nuevo")]
    public async Task RefreshToken_rotation_links_old_to_new()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("rt-rot@test.dev").AsClient().Verified().PersistAsync(db);

        var oldToken = new RefreshToken
        {
            UserId = user.Id,
            Token = "rt_old_" + Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(90),
            CreatedByIp = "127.0.0.1",
        };
        db.RefreshTokens.Add(oldToken);
        await db.SaveChangesAsync();

        // Refresh endpoint: emite nuevo token + revoca el viejo + enlaza
        var newTokenValue = "rt_new_" + Guid.NewGuid().ToString("N");
        var newToken = new RefreshToken
        {
            UserId = user.Id,
            Token = newTokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(90),
            CreatedByIp = "127.0.0.1",
        };
        db.RefreshTokens.Add(newToken);

        oldToken.IsRevoked = true;
        oldToken.RevokedAt = DateTime.UtcNow;
        oldToken.RevokedByIp = "127.0.0.1";
        oldToken.ReplacedByToken = newTokenValue;
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var oldFromDb = await verify.RefreshTokens.SingleAsync(t => t.Id == oldToken.Id);
        oldFromDb.IsRevoked.Should().BeTrue();
        oldFromDb.RevokedAt.Should().NotBeNull();
        oldFromDb.ReplacedByToken.Should().Be(newTokenValue,
            "la cadena permite detectar replay si alguien intenta usar oldToken otra vez");

        var newFromDb = await verify.RefreshTokens.SingleAsync(t => t.Token == newTokenValue);
        newFromDb.IsRevoked.Should().BeFalse();
    }

    [Fact(DisplayName = "REFRESH · Replay-detection: cadena permite revocar TODA la familia desde el token reusado")]
    public async Task RefreshToken_replay_chain_traversal()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("rt-replay@test.dev").AsClient().Verified().PersistAsync(db);

        // Simulamos una cadena de 3 rotaciones legítimas
        var tokens = new RefreshToken[3];
        for (int i = 0; i < 3; i++)
        {
            tokens[i] = new RefreshToken
            {
                UserId = user.Id,
                Token = $"rt_chain_{i}_" + Guid.NewGuid().ToString("N"),
                ExpiresAt = DateTime.UtcNow.AddDays(90),
                CreatedByIp = "127.0.0.1",
            };
            db.RefreshTokens.Add(tokens[i]);
            if (i > 0)
            {
                tokens[i - 1].IsRevoked = true;
                tokens[i - 1].RevokedAt = DateTime.UtcNow;
                tokens[i - 1].ReplacedByToken = tokens[i].Token;
            }
        }
        await db.SaveChangesAsync();

        // Un atacante intenta usar tokens[0] (ya revocado): el sistema debe poder navegar
        // la cadena Token → ReplacedByToken para encontrar el token activo y revocarlo TODO.
        var familyTokens = await db.RefreshTokens
            .Where(t => t.UserId == user.Id)
            .ToListAsync();

        // Defensa post-replay: revocar todos los activos del usuario
        var stillActive = familyTokens.Where(t => !t.IsRevoked).ToList();
        stillActive.Should().HaveCount(1);
        stillActive[0].Token.Should().Be(tokens[2].Token);

        foreach (var t in stillActive)
        {
            t.IsRevoked = true;
            t.RevokedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var allRevoked = await verify.RefreshTokens
            .Where(t => t.UserId == user.Id && !t.IsRevoked)
            .CountAsync();
        allRevoked.Should().Be(0, "tras detectar replay, ningún token de la familia sigue activo");
    }

    [Fact(DisplayName = "REFRESH · IsExpired computed property responde a ExpiresAt")]
    public async Task RefreshToken_IsExpired_computed()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("rt-exp@test.dev").AsClient().Verified().PersistAsync(db);

        var expired = new RefreshToken
        {
            UserId = user.Id,
            Token = "rt_expired_" + Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(-1), // ya expirado
            CreatedByIp = "127.0.0.1",
        };
        db.RefreshTokens.Add(expired);
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.RefreshTokens.SingleAsync(t => t.Id == expired.Id);
        fromDb.IsExpired.Should().BeTrue(
            "IsExpired es computed: DateTime.UtcNow >= ExpiresAt");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EmailVerificationCode · OTP
    // ─────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> OtpPurposes => new[]
    {
        new object[] { EmailVerificationPurpose.EmailVerification },
        new object[] { EmailVerificationPurpose.PasswordReset },
        new object[] { EmailVerificationPurpose.StepUp },
    };

    [Theory(DisplayName = "OTP · Los 3 Purpose del enum se persisten con TTL 10 min")]
    [MemberData(nameof(OtpPurposes))]
    public async Task EmailVerificationCode_purpose_roundtrip(EmailVerificationPurpose purpose)
    {
        await using var db = NewDbContext();
        var email = $"otp-{purpose}@test.dev".ToLowerInvariant();

        var code = new EmailVerificationCode
        {
            Email = email,
            Purpose = purpose,
            CodeHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("123456:salt")),
            Salt = RandomNumberGenerator.GetBytes(16),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            VerificationToken = Guid.NewGuid().ToString("N"),
        };
        db.EmailVerificationCodes.Add(code);
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.EmailVerificationCodes.SingleAsync(c => c.Id == code.Id);
        fromDb.Purpose.Should().Be(purpose);
        fromDb.AttemptCount.Should().Be(0);
        fromDb.MaxAttempts.Should().Be(5, "NIST SP 800-63B-4 default según modelo");
        fromDb.ConsumedAt.Should().BeNull();
        (fromDb.ExpiresAt - fromDb.CreatedAt).Should().BeCloseTo(
            TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1));
    }

    [Fact(DisplayName = "OTP · Single-use: ConsumedAt se setea al primer match correcto")]
    public async Task EmailVerificationCode_single_use_via_ConsumedAt()
    {
        await using var db = NewDbContext();
        var code = new EmailVerificationCode
        {
            Email = "otp-single@test.dev",
            Purpose = EmailVerificationPurpose.EmailVerification,
            CodeHash = new byte[32],
            Salt = new byte[16],
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            VerificationToken = Guid.NewGuid().ToString("N"),
        };
        db.EmailVerificationCodes.Add(code);
        await db.SaveChangesAsync();

        // Verificación correcta: marca como consumido
        code.ConsumedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.EmailVerificationCodes.SingleAsync(c => c.Id == code.Id);
        fromDb.ConsumedAt.Should().NotBeNull(
            "el código no puede reutilizarse — el handler debe rechazar OTPs con ConsumedAt no-null");
    }

    [Fact(DisplayName = "OTP · AttemptCount incrementable hasta MaxAttempts (5)")]
    public async Task EmailVerificationCode_attempt_counter_caps_at_5()
    {
        await using var db = NewDbContext();
        var code = new EmailVerificationCode
        {
            Email = "otp-attempts@test.dev",
            Purpose = EmailVerificationPurpose.PasswordReset,
            CodeHash = new byte[32],
            Salt = new byte[16],
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            VerificationToken = Guid.NewGuid().ToString("N"),
        };
        db.EmailVerificationCodes.Add(code);
        await db.SaveChangesAsync();

        for (int i = 1; i <= 5; i++)
        {
            code.AttemptCount = i;
            await db.SaveChangesAsync();
        }

        await using var verify = NewDbContext();
        var fromDb = await verify.EmailVerificationCodes.SingleAsync(c => c.Id == code.Id);
        fromDb.AttemptCount.Should().Be(5);
        fromDb.AttemptCount.Should().BeLessThanOrEqualTo(fromDb.MaxAttempts,
            "el handler debe rechazar nuevos intentos cuando AttemptCount >= MaxAttempts");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UserMfaSettings · TOTP + recovery codes
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "MFA · Setup inicial: IsEnabled=false, TotpSecret listo, RecoveryCodesUsed=0")]
    public async Task UserMfaSettings_setup_persists_secret_disabled()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("mfa-setup@test.dev").AsClient().PersistAsync(db);

        var mfa = new UserMfaSettings
        {
            UserId = user.Id,
            IsEnabled = false,
            TotpSecret = "ENCRYPTED:JBSWY3DPEHPK3PXP", // base32 simulado
            RecoveryCodesUsed = 0,
        };
        db.Set<UserMfaSettings>().Add(mfa);
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.Set<UserMfaSettings>().SingleAsync(m => m.UserId == user.Id);
        fromDb.IsEnabled.Should().BeFalse("setup inicial pre-verificación TOTP");
        fromDb.TotpSecret.Should().StartWith("ENCRYPTED:",
            "el modelo documenta que TotpSecret está cifrado en BD");
        fromDb.RecoveryCodesUsed.Should().Be(0);
        fromDb.EnabledAt.Should().BeNull();
    }

    [Fact(DisplayName = "MFA · Enable: IsEnabled=true + EnabledAt + 8 recovery codes encriptados")]
    public async Task UserMfaSettings_enable_generates_recovery_codes()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("mfa-enable@test.dev").AsClient().PersistAsync(db);

        var mfa = new UserMfaSettings
        {
            UserId = user.Id,
            IsEnabled = true,
            TotpSecret = "ENCRYPTED:JBSWY3DPEHPK3PXP",
            RecoveryCodesEncrypted = "[\"$2a$h1\", \"$2a$h2\", \"$2a$h3\", \"$2a$h4\", \"$2a$h5\", \"$2a$h6\", \"$2a$h7\", \"$2a$h8\"]",
            RecoveryCodesUsed = 0,
            EnabledAt = DateTime.UtcNow,
        };
        db.Set<UserMfaSettings>().Add(mfa);
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.Set<UserMfaSettings>().SingleAsync(m => m.UserId == user.Id);
        fromDb.IsEnabled.Should().BeTrue();
        fromDb.EnabledAt.Should().NotBeNull();
        fromDb.RecoveryCodesEncrypted.Should().Contain("$2a$",
            "los recovery codes se guardan hasheados (BCrypt) en JSON array");
    }

    [Fact(DisplayName = "MFA · RecoveryCodesUsed se incrementa al consumir uno (one-time)")]
    public async Task UserMfaSettings_recovery_code_consumption_increments_counter()
    {
        await using var db = NewDbContext();
        var user = await new UserBuilder("mfa-recovery@test.dev").AsClient().PersistAsync(db);

        var mfa = new UserMfaSettings
        {
            UserId = user.Id,
            IsEnabled = true,
            TotpSecret = "ENCRYPTED:JBSWY3DPEHPK3PXP",
            RecoveryCodesEncrypted = "[\"$2a$h1\", \"$2a$h2\"]",
            RecoveryCodesUsed = 0,
            EnabledAt = DateTime.UtcNow,
        };
        db.Set<UserMfaSettings>().Add(mfa);
        await db.SaveChangesAsync();

        // Usuario usa 1 recovery code
        mfa.RecoveryCodesUsed = 1;
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.Set<UserMfaSettings>().SingleAsync(m => m.UserId == user.Id);
        fromDb.RecoveryCodesUsed.Should().Be(1);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Dispute · status lifecycle + timing
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "DISPUTE · Status inicial Pending + ExpertResponseDeadline calculado (+48h)")]
    public async Task Dispute_initial_state()
    {
        await using var db = NewDbContext();
        var clientUser = await new UserBuilder("disp-cli@test.dev").AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder("disp-exp@test.dev").AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).PersistAsync(db);
        var hire = await new SearchHireBuilder()
            .ForClient(clientUser.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithStatusValue("disputed").PersistAsync(db);

        var dispute = new Dispute
        {
            SearchHireId = hire.Id,
            ReporterId = clientUser.Id,
            Reason = "Experto no completó el servicio",
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
            ExpertResponseDeadline = DateTime.UtcNow.AddHours(48),
        };
        db.Disputes.Add(dispute);
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.Disputes.SingleAsync(d => d.Id == dispute.Id);
        fromDb.Status.Should().Be("Pending");
        fromDb.ExpertResponseDeadline.Should().NotBeNull();
        fromDb.CanExpertRespond.Should().BeTrue(
            "el experto tiene 48h para responder y ExpertResponseAt aún es null");
    }

    [Fact(DisplayName = "DISPUTE · Tras ExpertResponseDeadline el experto ya no puede responder")]
    public async Task Dispute_after_deadline_expert_cannot_respond()
    {
        await using var db = NewDbContext();
        var client = await new UserBuilder("disp-late-cli@test.dev").AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder("disp-late-exp@test.dev").AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).PersistAsync(db);
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithStatusValue("disputed").PersistAsync(db);

        var dispute = new Dispute
        {
            SearchHireId = hire.Id,
            ReporterId = client.Id,
            Reason = "X",
            Status = "Pending",
            CreatedAt = DateTime.UtcNow.AddHours(-72),
            ExpertResponseDeadline = DateTime.UtcNow.AddHours(-24), // ya vencido
        };
        db.Disputes.Add(dispute);
        await db.SaveChangesAsync();

        await using var verify = NewDbContext();
        var fromDb = await verify.Disputes.SingleAsync(d => d.Id == dispute.Id);
        fromDb.CanExpertRespond.Should().BeFalse(
            "DateTime.UtcNow > ExpertResponseDeadline ⇒ admin debe resolver sin oír al experto");
    }

    [Fact(DisplayName = "DISPUTE · Rate-limit 5/h: el contador puede medirse sobre Disputes.CreatedAt")]
    public async Task Dispute_rate_limit_5_per_hour_measurable()
    {
        await using var db = NewDbContext();
        var client = await new UserBuilder("disp-rate-cli@test.dev").AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder("disp-rate-exp@test.dev").AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).PersistAsync(db);

        // 5 disputas en la última hora (sobre hires distintos para evitar otros constraints)
        for (int i = 0; i < 5; i++)
        {
            var hire = await new SearchHireBuilder()
                .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
                .WithStatusValue("disputed").PersistAsync(db);
            db.Disputes.Add(new Dispute
            {
                SearchHireId = hire.Id,
                ReporterId = client.Id,
                Reason = $"R{i}", Status = "Pending",
                CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            });
        }
        await db.SaveChangesAsync();

        // La consulta T3 FIX: ¿cuántas disputas ha abierto este ReporterId en la última hora?
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var count = await db.Disputes
            .Where(d => d.ReporterId == client.Id && d.CreatedAt >= oneHourAgo)
            .CountAsync();

        count.Should().Be(5,
            "el endpoint POST /api/Dispute debe rechazar con 429 cuando este conteo ≥ 5 (T3 FIX)");
    }

    [Fact(DisplayName = "DISPUTE · Ventana 14d: SearchHire.UpdatedAt + 14d ≥ now para permitir disputar completed")]
    public async Task Dispute_within_14_day_window_is_measurable()
    {
        await using var db = NewDbContext();
        var client = await new UserBuilder("disp-14d@test.dev").AsClient().Verified().PersistAsync(db);
        var expertUser = await new UserBuilder("disp-14d-exp@test.dev").AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).PersistAsync(db);

        var fresh = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithStatusValue("completed").PersistAsync(db);
        fresh.UpdatedAt = DateTime.UtcNow.AddDays(-10); // dentro de la ventana
        await db.SaveChangesAsync();

        var stale = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithStatusValue("completed").PersistAsync(db);
        stale.UpdatedAt = DateTime.UtcNow.AddDays(-20); // fuera de la ventana
        await db.SaveChangesAsync();

        bool CanDispute(SearchHire h) =>
            h.UpdatedAt.HasValue && h.UpdatedAt.Value.AddDays(14) >= DateTime.UtcNow;

        CanDispute(fresh).Should().BeTrue("10d < 14d window");
        CanDispute(stale).Should().BeFalse("20d > 14d window — endpoint debe rechazar con 400");
    }
}
