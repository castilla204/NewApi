using newApi.Services.StripeBranding;
using FluentAssertions;

namespace NewApi.Tests.Unit;

/// <summary>Builders de branding de Stripe: colores/nombre fijos, file_id inyectados.</summary>
public class InspeccionoBrandingTests
{
    [Fact]
    public void BuildAccountBranding_UsesBrandColorsAndFileIds()
    {
        var branding = InspeccionoBranding.BuildAccountBranding("file_icon", "file_logo");

        branding.PrimaryColor.Should().Be("#0066CC");
        branding.SecondaryColor.Should().Be("#F59E0B");
        branding.Icon.Should().Be("file_icon");
        branding.Logo.Should().Be("file_logo");
    }

    [Fact]
    public void BuildAccountBranding_OmitsFileIdsWhenBlank()
    {
        var branding = InspeccionoBranding.BuildAccountBranding(null, "");

        branding.Icon.Should().BeNull();
        branding.Logo.Should().BeNull();
        branding.PrimaryColor.Should().Be("#0066CC");
    }

    [Fact]
    public void Constants_AreTheBrandTokens()
    {
        InspeccionoBranding.PrimaryColor.Should().Be("#0066CC");
        InspeccionoBranding.SecondaryColor.Should().Be("#F59E0B");
        InspeccionoBranding.DisplayName.Should().Be("Inspecciono");
    }
}
