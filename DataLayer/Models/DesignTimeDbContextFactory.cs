using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Npgsql;

namespace newApi.DataLayer.Models
{
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            var basePath = Directory.GetCurrentDirectory();
            Console.WriteLine($"🔍 Directorio actual: {basePath}");
            
            var devSettingsPath = Path.Combine(basePath, "appsettings.Development.json");
            var settingsPath = Path.Combine(basePath, "appsettings.json");
            
            Console.WriteLine($"🔍 Buscando appsettings.Development.json en: {devSettingsPath}");
            Console.WriteLine($"🔍 Existe: {File.Exists(devSettingsPath)}");
            
            var configuration = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();

            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
            
            // Intentar obtener la cadena de conexión desde configuración
            var connectionString = configuration.GetConnectionString("PostgresConnection");
            
            // Debug: mostrar qué se está leyendo
            if (string.IsNullOrEmpty(connectionString))
            {
                Console.WriteLine($"🔍 Connection string leído: (vacío o null)");
                // Intentar leer directamente desde la sección ConnectionStrings
                var connectionStrings = configuration.GetSection("ConnectionStrings");
                var postgresConnection = connectionStrings["PostgresConnection"];
                Console.WriteLine($"🔍 Connection string desde GetSection: {(string.IsNullOrEmpty(postgresConnection) ? "(vacío o null)" : postgresConnection.Substring(0, Math.Min(50, postgresConnection.Length)) + "...")}");
                if (!string.IsNullOrEmpty(postgresConnection))
                {
                    connectionString = postgresConnection;
                }
                
                // Si aún está vacío, leer el archivo directamente
                if (string.IsNullOrEmpty(connectionString) && File.Exists(devSettingsPath))
                {
                    try
                    {
                        var jsonContent = File.ReadAllText(devSettingsPath);
                        Console.WriteLine($"🔍 Contenido del archivo (primeros 200 chars): {jsonContent.Substring(0, Math.Min(200, jsonContent.Length))}...");
                        // Parsear manualmente el JSON
                        var jsonDoc = JsonDocument.Parse(jsonContent);
                        if (jsonDoc.RootElement.TryGetProperty("ConnectionStrings", out var connStrings))
                        {
                            if (connStrings.TryGetProperty("PostgresConnection", out var postgresConn))
                            {
                                connectionString = postgresConn.GetString();
                                if (!string.IsNullOrEmpty(connectionString))
                                {
                                    Console.WriteLine($"🔍 Connection string desde JSON directo: {connectionString.Substring(0, Math.Min(50, connectionString.Length))}...");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Error al leer JSON: {ex.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine($"🔍 Connection string leído: {connectionString.Substring(0, Math.Min(50, connectionString.Length))}...");
            }
            
            // Si no está en configuración, construir desde variables de entorno
            if (string.IsNullOrEmpty(connectionString))
            {
                var dbHost = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
                
                // ✅ PRIORIDAD: Puerto 5433 para desarrollo
                var dbPort = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5433";
                
                var dbUsername = Environment.GetEnvironmentVariable("POSTGRES_USERNAME") ?? "admin";
                var dbPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "";
                var dbName = Environment.GetEnvironmentVariable("POSTGRES_DATABASE") ?? "atrapo";
                
                connectionString = $"Host={dbHost};Port={dbPort};Username={dbUsername};Password={dbPassword};Database={dbName};Timeout=30;CommandTimeout=30;";
                
                Console.WriteLine($"🔧 Design-time: Usando conexión construida - Host: {dbHost}, Port: {dbPort}, Database: {dbName}, User: {dbUsername}");
            }
            else
            {
                Console.WriteLine($"🔧 Design-time: Usando cadena de conexión desde configuración");
            }

            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException(
                    "No se pudo construir la cadena de conexión. " +
                    "Configura 'PostgresConnection' en appsettings.json o " +
                    "las variables de entorno: POSTGRES_HOST, POSTGRES_PORT, POSTGRES_USERNAME, POSTGRES_PASSWORD, POSTGRES_DATABASE");
            }

            // Configurar para Session Pooler de Supabase
            // Usar un DataSource con configuración específica para evitar problemas con el pooler
            var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
            // Deshabilitar prepared statements completamente para Session Pooler
            dataSourceBuilder.EnableParameterLogging();
            var dataSource = dataSourceBuilder.Build();
            optionsBuilder.UseNpgsql(dataSource, npgsqlOptions =>
            {
                npgsqlOptions.CommandTimeout(60);
            });

            return new AppDbContext(optionsBuilder.Options);
        }
    }
}