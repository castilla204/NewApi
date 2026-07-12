using FirebaseAdmin.Messaging;
using FluentAssertions;
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
    }
}
