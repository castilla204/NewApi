using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;

// Script para aplicar la migración de StripeMode
// Ejecutar con: dotnet run --project . -- ApplyStripeModeMigration.cs
// O compilar y ejecutar directamente

class ApplyStripeModeMigration
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("🔧 Aplicando migración: AddStripeModeToSystemSettings");
        Console.WriteLine();

        // Obtener la cadena de conexión desde la configuración
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables();

        var configuration = builder.Build();
        var connectionString = configuration.GetConnectionString("PostgresConnection");

        if (string.IsNullOrEmpty(connectionString))
        {
            Console.WriteLine("❌ No se encontró la cadena de conexión.");
            Console.WriteLine("   Asegúrate de tener configurada la variable de entorno o el Secret Manager.");
            return;
        }

        // Crear DbContext
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(connectionString);
        
        using var context = new AppDbContext(optionsBuilder.Options);

        try
        {
            Console.WriteLine("📊 Conectando a la base de datos...");
            
            // Verificar conexión
            await context.Database.CanConnectAsync();
            Console.WriteLine("✅ Conexión exitosa");
            Console.WriteLine();

            // SQL para agregar las columnas
            var sql = @"
DO $$ 
BEGIN
    -- Add StripeMode column
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                  WHERE table_name = 'SystemSettings' AND column_name = 'StripeMode') THEN
        ALTER TABLE ""SystemSettings"" 
        ADD COLUMN ""StripeMode"" character varying(20) NOT NULL DEFAULT 'production';
        RAISE NOTICE 'Columna StripeMode agregada';
    ELSE
        RAISE NOTICE 'Columna StripeMode ya existe';
    END IF;

    -- Add StripeModeChangedAt column
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                  WHERE table_name = 'SystemSettings' AND column_name = 'StripeModeChangedAt') THEN
        ALTER TABLE ""SystemSettings"" 
        ADD COLUMN ""StripeModeChangedAt"" timestamp with time zone NULL;
        RAISE NOTICE 'Columna StripeModeChangedAt agregada';
    ELSE
        RAISE NOTICE 'Columna StripeModeChangedAt ya existe';
    END IF;

    -- Add StripeModeChangedByUserId column
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                  WHERE table_name = 'SystemSettings' AND column_name = 'StripeModeChangedByUserId') THEN
        ALTER TABLE ""SystemSettings"" 
        ADD COLUMN ""StripeModeChangedByUserId"" integer NULL;
        RAISE NOTICE 'Columna StripeModeChangedByUserId agregada';
    ELSE
        RAISE NOTICE 'Columna StripeModeChangedByUserId ya existe';
    END IF;
END $$;
";

            Console.WriteLine("🚀 Ejecutando migración...");
            await context.Database.ExecuteSqlRawAsync(sql);
            
            Console.WriteLine("✅ Migración aplicada exitosamente!");
            Console.WriteLine();

            // Verificar que las columnas se agregaron
            Console.WriteLine("🔍 Verificando columnas...");
            var columns = await context.Database.SqlQueryRaw<string>(
                @"SELECT column_name 
                  FROM information_schema.columns 
                  WHERE table_name = 'SystemSettings' 
                  AND column_name LIKE 'Stripe%'
                  ORDER BY column_name"
            ).ToListAsync();

            if (columns.Any())
            {
                Console.WriteLine("✅ Columnas encontradas:");
                foreach (var column in columns)
                {
                    Console.WriteLine($"   - {column}");
                }
            }
            else
            {
                Console.WriteLine("⚠️  No se encontraron columnas Stripe* (puede que ya existan o haya un error)");
            }

            Console.WriteLine();
            Console.WriteLine("✨ ¡Migración completada!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("Stack trace:");
            Console.WriteLine(ex.StackTrace);
        }
    }
}



