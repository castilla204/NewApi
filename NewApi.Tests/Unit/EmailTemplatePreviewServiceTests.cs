using newApi.Services;
using FluentAssertions;

namespace NewApi.Tests.Unit;

public class EmailTemplatePreviewServiceTests
{
    [Fact]
    public void GetAll_ReturnsAllExpectedTemplateKeys()
    {
        var previews = EmailTemplatePreviewService.GetAll();

        previews.Select(p => p.Key).Should().BeEquivalentTo(new[]
        {
            "welcome", "appointment-client", "appointment-expert", "service-completion",
            "general-notification", "user-notification", "otp-email-verification",
            "otp-password-reset", "otp-stepup", "invoice", "admin-digest", "refund-failed-digest"
        });
    }

    [Fact]
    public void GetAll_EverySubjectAndHtmlNonEmpty()
    {
        foreach (var p in EmailTemplatePreviewService.GetAll())
        {
            p.Subject.Should().NotBeNullOrWhiteSpace($"subject de {p.Key}");
            p.Html.Should().NotBeNullOrWhiteSpace($"html de {p.Key}");
            p.Label.Should().NotBeNullOrWhiteSpace();
            p.Group.Should().NotBeNullOrWhiteSpace();
        }
    }
}
