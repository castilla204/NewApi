using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using Testcontainers.PostgreSql;

namespace NewApi.Tests.Fixtures;

/// <summary>
/// Fixture compartido por toda la colección de tests de integración.
///
/// Arranca UN solo contenedor Postgres por suite (cuesta ~6-10s) y:
///   1. Crea el esquema con <c>EnsureCreatedAsync</c> (NO usa migraciones EF
///      porque están desincronizadas con prod — ver memoria del proyecto).
///   2. Ejecuta el seed <c>SEED_ESTADOS_COMPLETO.sql</c> para poblar
///      SystemStatuses / StatusConfigurations / StatusMappings.
///
/// Cada test individual debe TRUNCAR las tablas mutables al inicio
/// (ver <see cref="IntegrationTestBase"/>). Los catálogos del seed sobreviven.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("inspecciono_test")
        .WithUsername("test")
        .WithPassword("test")
        .WithCleanUp(true)
        .Build();

    /// <summary>Cadena de conexión Npgsql al contenedor levantado.</summary>
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // 1) Crear esquema con EnsureCreated sobre TestAppDbContext (que parchea el
        //    modelo para suprimir el CHECK constraint huérfano "Balance"). Más rápido
        //    que MigrateAsync y sin replay de migraciones con drift conocido.
        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        // 2) Aplicar seed de estados/mappings/configuraciones
        var seedPath = Path.Combine(AppContext.BaseDirectory, "Resources", "SEED_ESTADOS_COMPLETO.sql");
        if (File.Exists(seedPath))
        {
            var sql = await File.ReadAllTextAsync(seedPath);
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Crea un <see cref="AppDbContext"/> nuevo apuntando al testcontainer.
    /// El llamante es responsable de hacer <c>using</c>/disponer.
    /// </summary>
    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString, n => n.EnableRetryOnFailure(0))
            .EnableSensitiveDataLogging()
            .Options;
        return new AppDbContext(options);
    }
}

/// <summary>
/// Definición de colección xUnit. Todos los tests con
/// <c>[Collection("Postgres")]</c> comparten el mismo contenedor.
/// </summary>
[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresContainerFixture> { }
