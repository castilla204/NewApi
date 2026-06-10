namespace NewApi.Tests.Unit;

/// <summary>
/// Tests triviales de Fase 1 para verificar que el harness xUnit arranca y
/// el proyecto NewApi.Tests puede ejecutar `dotnet test` sin errores.
/// Cuando lleguen los tests reales (Fase 3 del plan), borrar esta clase.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void Test_harness_works()
    {
        var two = 1 + 1;
        two.Should().Be(2);
    }

    [Fact]
    public void FluentAssertions_works()
    {
        var people = new[] { "Alice", "Bob" };
        people.Should().HaveCount(2).And.Contain("Alice");
    }

    [Theory]
    [InlineData("client", true)]
    [InlineData("expert", true)]
    [InlineData("admin",  true)]
    [InlineData("guest",  false)]
    public void Role_recognition(string role, bool isKnown)
    {
        var known = new[] { "client", "expert", "admin" };
        known.Contains(role).Should().Be(isKnown);
    }
}
