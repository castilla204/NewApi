using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace newApi.DataLayer.Models
{
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.Development.json", optional: true)
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
            
            // Intentar obtener la cadena de conexión desde configuración
            var connectionString = configuration.GetConnectionString("PostgresConnection");
            
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

            optionsBuilder.UseNpgsql(connectionString);

            return new AppDbContext(optionsBuilder.Options);
        }
    }
}