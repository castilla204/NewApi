using newApi.Services;
using FluentAssertions;

namespace NewApi.Tests.Unit;

public class EmailTemplateRendererTests
{
    [Fact]
    public void GenerateEmailTemplate_ReplacesTitleAndContent()
    {
        var html = EmailTemplateRenderer.GenerateEmailTemplate("Mi Título", "<p>Hola</p>");

        html.Should().Contain("Mi Título");
        html.Should().Contain("<p>Hola</p>");
        html.Should().NotContain("{{TITLE}}");
        html.Should().NotContain("{{CONTENT}}");
        html.Should().NotContain("{{ACTION_BUTTON}}");
        html.Should().NotContain("{{YEAR}}");
    }

    [Fact]
    public void GenerateEmailTemplate_WithAction_RendersBulletproofButton()
    {
        var html = EmailTemplateRenderer.GenerateEmailTemplate(
            "T", "<p>c</p>", "Ver detalles", "https://inspecciono.com/x");

        html.Should().Contain("Ver detalles");
        html.Should().Contain("https://inspecciono.com/x");
        html.Should().Contain("bgcolor='#2563EB'"); // botón canónico (NotificationService)
    }

    [Fact]
    public void GenerateEmailTemplate_WithoutAction_OmitsButton()
    {
        var html = EmailTemplateRenderer.GenerateEmailTemplate("T", "<p>c</p>");

        html.Should().NotContain("v:roundrect");
    }
}
