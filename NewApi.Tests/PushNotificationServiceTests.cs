using System.Threading.Tasks;
using FirebaseAdmin.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using newApi.Services;
using Xunit;

namespace NewApi.Tests
{
    public class PushNotificationServiceTests
    {
        [Theory]
        [InlineData(MessagingErrorCode.Unregistered, true)]
        [InlineData(MessagingErrorCode.InvalidArgument, true)]
        [InlineData(MessagingErrorCode.Unavailable, false)]
        [InlineData(MessagingErrorCode.Internal, false)]
        [InlineData(MessagingErrorCode.QuotaExceeded, false)]
        public void ShouldDelete_borra_solo_tokens_definitivamente_invalidos(MessagingErrorCode code, bool expected)
        {
            DeadTokenClassifier.ShouldDelete(code).Should().Be(expected);
        }

        [Fact]
        public void ShouldDelete_null_no_borra()
        {
            DeadTokenClassifier.ShouldDelete(null).Should().BeFalse();
        }

        [Fact]
        public async Task SendToUserAsync_es_noop_si_no_hay_credencial_firebase()
        {
            var config = new ConfigurationBuilder().Build(); // sin Firebase:ServiceAccountJson
            var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict); // no debe usarse
            var svc = new PushNotificationService(scopeFactory.Object, config, NullLogger<PushNotificationService>.Instance);

            svc.IsEnabled.Should().BeFalse();
            await svc.Invoking(s => s.SendToUserAsync(1, "titulo", "cuerpo", "/x", "test"))
                     .Should().NotThrowAsync();
            scopeFactory.VerifyNoOtherCalls(); // no tocó la BD
        }
    }
}
