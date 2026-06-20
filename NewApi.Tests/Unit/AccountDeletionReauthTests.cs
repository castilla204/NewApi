using System.Threading;
using FluentAssertions;
using Moq;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using Xunit;

namespace NewApi.Tests.Unit
{
    /// <summary>
    /// 🛡️ SEC-1 — Reautenticación obligatoria antes del borrado de cuenta.
    /// Cubre cuentas con contraseña (verificación BCrypt) y cuentas OAuth (OTP step-up por email).
    /// </summary>
    public class AccountDeletionReauthTests
    {
        private const string Hash = "$2a$12$abcdefghijklmnopqrstuv"; // hash BCrypt simulado
        private static User PasswordUser() => new User { Id = 1, Email = "a@b.com", Password = Hash };
        private static User OAuthUser() => new User { Id = 137, Email = "p@gmail.com", Password = null, GoogleId = "g" };

        private static AccountDeletionReauthService Sut(
            Mock<IPasswordHashingService>? hasher = null,
            Mock<IEmailVerificationService>? email = null)
            => new AccountDeletionReauthService(
                (hasher ?? new Mock<IPasswordHashingService>()).Object,
                (email ?? new Mock<IEmailVerificationService>()).Object);

        // ── Cuentas con contraseña ────────────────────────────────────────────────

        [Fact]
        public async Task PasswordAccount_CorrectPassword_Succeeds()
        {
            var hasher = new Mock<IPasswordHashingService>();
            hasher.Setup(h => h.Verify("secret", Hash)).Returns(true);

            var r = await Sut(hasher).VerifyAsync(PasswordUser(),
                new AccountDeletionRequestDto { Password = "secret" }, null);

            r.Success.Should().BeTrue();
        }

        [Fact]
        public async Task PasswordAccount_WrongPassword_Fails()
        {
            var hasher = new Mock<IPasswordHashingService>();
            hasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

            var r = await Sut(hasher).VerifyAsync(PasswordUser(),
                new AccountDeletionRequestDto { Password = "wrong" }, null);

            r.Success.Should().BeFalse();
        }

        [Fact]
        public async Task PasswordAccount_MissingPassword_Fails()
        {
            var r = await Sut().VerifyAsync(PasswordUser(),
                new AccountDeletionRequestDto { }, null);

            r.Success.Should().BeFalse();
        }

        // ── Cuentas OAuth (OTP step-up) ───────────────────────────────────────────

        [Fact]
        public async Task OAuthAccount_ValidStepUpOtp_Succeeds()
        {
            var email = new Mock<IEmailVerificationService>();
            email.Setup(e => e.VerifyAsync("tok", "123456", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new EmailVerificationVerifyResult(true, "p@gmail.com", EmailVerificationPurpose.StepUp, 137));

            var r = await Sut(email: email).VerifyAsync(OAuthUser(),
                new AccountDeletionRequestDto { VerificationToken = "tok", Code = "123456" }, null);

            r.Success.Should().BeTrue();
        }

        [Fact]
        public async Task OAuthAccount_MissingCode_Fails()
        {
            var r = await Sut().VerifyAsync(OAuthUser(),
                new AccountDeletionRequestDto { VerificationToken = "tok" }, null);

            r.Success.Should().BeFalse();
        }

        [Fact]
        public async Task OAuthAccount_OtpVerificationFails_Fails()
        {
            var email = new Mock<IEmailVerificationService>();
            email.Setup(e => e.VerifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new EmailVerificationVerifyResult(false, ErrorMessage: "expirado"));

            var r = await Sut(email: email).VerifyAsync(OAuthUser(),
                new AccountDeletionRequestDto { VerificationToken = "tok", Code = "000000" }, null);

            r.Success.Should().BeFalse();
        }

        [Fact]
        public async Task OAuthAccount_WrongPurposeOtp_Fails()
        {
            var email = new Mock<IEmailVerificationService>();
            email.Setup(e => e.VerifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new EmailVerificationVerifyResult(true, "p@gmail.com", EmailVerificationPurpose.PasswordReset, 137));

            var r = await Sut(email: email).VerifyAsync(OAuthUser(),
                new AccountDeletionRequestDto { VerificationToken = "tok", Code = "123456" }, null);

            r.Success.Should().BeFalse();
        }

        [Fact]
        public async Task OAuthAccount_OtpForDifferentUser_Fails()
        {
            var email = new Mock<IEmailVerificationService>();
            email.Setup(e => e.VerifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new EmailVerificationVerifyResult(true, "x@gmail.com", EmailVerificationPurpose.StepUp, 999));

            var r = await Sut(email: email).VerifyAsync(OAuthUser(),
                new AccountDeletionRequestDto { VerificationToken = "tok", Code = "123456" }, null);

            r.Success.Should().BeFalse();
        }

        [Fact]
        public async Task PasswordAccount_DoesNotConsultEmailOtp()
        {
            var hasher = new Mock<IPasswordHashingService>();
            hasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            var email = new Mock<IEmailVerificationService>(MockBehavior.Strict); // falla si se llama

            var r = await Sut(hasher, email).VerifyAsync(PasswordUser(),
                new AccountDeletionRequestDto { Password = "secret" }, null);

            r.Success.Should().BeTrue();
            email.VerifyNoOtherCalls();
        }
    }
}
