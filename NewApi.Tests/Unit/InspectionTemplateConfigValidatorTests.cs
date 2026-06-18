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
    public void AcceptsDisablingPointsAndSections()
    {
        // El servidor solo valida la estructura; qué puntos son obligatorios por
        // categoría lo aplica el cliente con su catálogo (coche/moto/inmueble…).
        Assert.True(InspectionTemplateConfigValidator.IsValid("{\"disabledPoints\":[2]}", out _));
        Assert.True(InspectionTemplateConfigValidator.IsValid("{\"disabledSections\":[\"A\"]}", out _));
    }

    [Fact]
    public void RejectsCustomPointWithoutSection()
    {
        var json = "{\"customPoints\":[{\"label\":\"X\"}]}";
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

    [Fact]
    public void RejectsTooManyCustomPoints()
    {
        var points = string.Join(",", Enumerable.Range(1, 51).Select(i => $"{{\"section\":\"B\",\"label\":\"Q{i}\"}}"));
        var json = $"{{\"customPoints\":[{points}]}}";
        Assert.False(InspectionTemplateConfigValidator.IsValid(json, out var error));
        Assert.Contains("50", error);
    }

    [Fact]
    public void RejectsCustomPointLabelTooLong()
    {
        var longLabel = new string('X', 201);
        var json = $"{{\"customPoints\":[{{\"section\":\"B\",\"label\":\"{longLabel}\"}}]}}";
        Assert.False(InspectionTemplateConfigValidator.IsValid(json, out var error));
        Assert.Contains("200", error);
    }
}
