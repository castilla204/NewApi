using newApi.Services;
using Xunit;

public class InspectionTemplateConfigValidatorTests
{
    [Fact]
    public void NullOrEmpty_IsValid()
    {
        Assert.True(InspectionTemplateConfigValidator.IsValid(null, out _));
        Assert.True(InspectionTemplateConfigValidator.IsValid("", out _));
    }

    [Fact]
    public void RejectsDisablingRequiredPoint()
    {
        var json = "{\"disabledPoints\":[2]}";
        Assert.False(InspectionTemplateConfigValidator.IsValid(json, out var error));
        Assert.Contains("obligatorio", error);
    }

    [Fact]
    public void RejectsDisablingSectionA()
    {
        var json = "{\"disabledSections\":[\"A\"]}";
        Assert.False(InspectionTemplateConfigValidator.IsValid(json, out _));
    }

    [Fact]
    public void AcceptsValidConfig()
    {
        var json = "{\"disabledSections\":[\"G\"],\"disabledPoints\":[6],\"customPoints\":[{\"section\":\"B\",\"label\":\"X\"}]}";
        Assert.True(InspectionTemplateConfigValidator.IsValid(json, out _));
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        Assert.False(InspectionTemplateConfigValidator.IsValid("{not json", out _));
    }
}
