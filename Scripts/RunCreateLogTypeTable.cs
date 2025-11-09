using Microsoft.Extensions.Logging;
using newApi.Scripts;

namespace newApi.Scripts
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Crear logger factory para el script
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
            var logger = loggerFactory.CreateLogger<CreateLogTypeTable>();
            
            try
            {
                await CreateLogTypeTable.CreateTableAndDataAsync(logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Error: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }
    }
}
