using Microsoft.EntityFrameworkCore;

namespace NewApi.Tests.Fixtures;

/// <summary>
/// Base para tests de integración que comparten el <see cref="PostgresContainerFixture"/>.
///
/// Truncea automáticamente las tablas mutables antes de cada test (CASCADE)
/// preservando los catálogos del seed (SystemStatuses, StatusConfigurations,
/// StatusMappings). Esto deja un estado limpio determinista en cada test sin
/// pagar el coste de recrear el esquema.
/// </summary>
[Collection("Postgres")]
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected readonly PostgresContainerFixture Fixture;

    protected IntegrationTestBase(PostgresContainerFixture fixture)
    {
        Fixture = fixture;
    }

    /// <summary>Crea un AppDbContext nuevo para el test.</summary>
    protected newApi.DataLayer.Models.AppDbContext NewDbContext() => Fixture.CreateDbContext();

    public virtual async Task InitializeAsync()
    {
        await using var db = NewDbContext();

        // TRUNCATE en orden CASCADE. El listado es defensivo: si una tabla no
        // existe en el modelo actual, Postgres ignora con un error que silenciamos
        // por tabla. Mucho más rápido que EnsureDeleted/Created entre tests.
        var tables = new[]
        {
            "\"FinancialTransactions\"",
            "\"Disputes\"",
            "\"SearchHireStatusHistories\"",
            "\"Appointments\"",
            "\"SearchHires\"",
            "\"SearchServiceImages\"",
            "\"SearchServices\"",
            "\"ExpertAvailabilities\"",
            "\"ExpertProfiles\"",
            "\"RefreshTokens\"",
            "\"EmailVerificationCodes\"",
            "\"Notifications\"",
            "\"Reviews\"",
            "\"ReviewImages\"",
            "\"ProcessedWebhookEvents\"",
            "\"Users\""
        };

        foreach (var table in tables)
        {
            try
            {
                // EF1002 silenciado a propósito: `table` es siempre un literal del array
                // de arriba, no input de usuario. Sin riesgo de SQL injection.
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"TRUNCATE TABLE {table} RESTART IDENTITY CASCADE;");
#pragma warning restore EF1002
            }
            catch
            {
                // Tabla no existe en el modelo actual — saltamos.
            }
        }
    }

    public virtual Task DisposeAsync() => Task.CompletedTask;
}
