using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// 🚀 PERF: Índices para acelerar /api/SearchService/map-experts (página /crear-busqueda).
    ///
    /// Detectado por análisis multi-agente (2026-06-03):
    ///
    ///   1) `IX_SearchServices_Category_ServiceType_Active` (compuesto + parcial)
    ///      → El endpoint del mapa SIEMPRE filtra por
    ///        WHERE CategoryId=X AND ServiceTypeId=Y AND IsActive=true.
    ///        Hoy sólo hay índices single-column, así que el planner elige uno y
    ///        filtra el resto en memoria. A escala (>1k servicios por par cat/type)
    ///        eso son 5-20 ms perdidos por cada movimiento de mapa.
    ///        Coste mantenimiento: ínfimo, sólo entra al índice si IsActive=true.
    ///
    ///   2) `IX_ExpertProfiles_Operativos` (parcial)
    ///      → Cada query del mapa filtra por
    ///        NOT IsOnVacation AND ((StripeStatus=2 AND OnboardingCompleted) OR StripeStatus=6).
    ///        Hoy Seq Scan sobre ExpertProfiles + Filter en memoria.
    ///        El parcial actúa como un "set materializado de expertos operativos".
    ///
    ///   3) `IX_ExpertProfiles_LatLng_Numeric` (expresión)
    ///      → Las queries hacen CAST("Latitude" AS NUMERIC) sobre columna TEXT.
    ///        Un btree normal sobre el TEXT no podría servir para BETWEEN numérico.
    ///        Un índice de expresión SÍ — convierte la búsqueda por bounds en
    ///        Index Range Scan en lugar de Seq Scan + Filter.
    ///
    ///   4) `IX_ExpertAvailabilities_Active` (parcial)
    ///      → El endpoint hace SELECT ... WHERE IsActive=true AND EffectiveTo IS NULL
    ///        en cada llamada. El índice parcial reduce el set a "disponibilidades vigentes".
    ///
    /// Todos los CREATE INDEX usan IF NOT EXISTS para que sean idempotentes
    /// (por si se aplican manualmente desde el panel de Render Postgres antes).
    /// CONCURRENTLY no se usa aquí porque EF Core lo envuelve en transacción —
    /// si quieres aplicarlo en producción sin lock, aplícalo a mano fuera de la
    /// migration con `dotnet ef migrations script` + edición manual añadiendo
    /// CONCURRENTLY y quitando la transacción.
    /// </summary>
    public partial class AddMapPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) SearchServices: filtro principal de map-experts
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_SearchServices_Category_ServiceType_Active""
                ON ""SearchServices"" (""CategoryId"", ""ServiceTypeId"")
                WHERE ""IsActive"" = true;
            ");

            // 2) ExpertProfiles: 'operativos' = no vacaciones + Stripe activo
            //    StripeStatus = 2 (Active) o 6 (LegacyOk) según el enum del proyecto.
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_ExpertProfiles_Operativos""
                ON ""ExpertProfiles"" (""Id"")
                WHERE ""IsOnVacation"" = false
                  AND (
                    (""StripeStatus"" = 2 AND ""OnboardingCompleted"" = true)
                    OR ""StripeStatus"" = 6
                  );
            ");

            // 3) ExpertProfiles: índice de expresión para CAST(Latitude/Longitude AS NUMERIC).
            //    Sólo entra al índice si lat/lng no son NULL ni cadena vacía,
            //    así no nos rompe el CAST con valores sucios.
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_ExpertProfiles_LatLng_Numeric""
                ON ""ExpertProfiles""
                    ((CAST(""Latitude"" AS NUMERIC)), (CAST(""Longitude"" AS NUMERIC)))
                WHERE ""Latitude"" IS NOT NULL
                  AND ""Latitude"" <> ''
                  AND ""Longitude"" IS NOT NULL
                  AND ""Longitude"" <> '';
            ");

            // 4) ExpertAvailabilities: 'vigentes'
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_ExpertAvailabilities_Active""
                ON ""ExpertAvailabilities"" (""ExpertId"", ""EffectiveFrom"" DESC)
                WHERE ""IsActive"" = true AND ""EffectiveTo"" IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SearchServices_Category_ServiceType_Active"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_ExpertProfiles_Operativos"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_ExpertProfiles_LatLng_Numeric"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_ExpertAvailabilities_Active"";");
        }
    }
}
