using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class FixDisputeResolvedStatusConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ CORRECCIÓN: Crear configuraciones en StatusConfigurations para dispute_resolved_expert y dispute_resolved_client
            // La migración CreateCentralizedStatusSystem falló porque CategoryServiceTypeConfigs usa "dispute-resolved-expert" (guiones)
            // pero SystemStatuses tiene "dispute_resolved_expert" (guiones bajos), por lo que el JOIN no encontró coincidencias
            
            // Crear configuraciones globales y específicas por categoría/tipo para dispute_resolved_client
            // Basado en los valores que estaban en CategoryServiceTypeConfigs: 90% cliente, 8% experto, 2% plataforma
            migrationBuilder.Sql(@"
                -- Crear configuración global para dispute_resolved_client
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", 
                                                   ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", 
                                                   ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 
                    ss.""Id"",
                    NULL,
                    NULL,
                    90,
                    8,
                    2,
                    true,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" ss
                WHERE ss.""StatusValue"" = 'dispute_resolved_client'
                AND ss.""StatusType"" = 'SearchHireStatus'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = ss.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // Crear configuraciones específicas por categoría y tipo para dispute_resolved_client
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", 
                                                   ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", 
                                                   ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 
                    ss.""Id"",
                    c.""Id"",
                    stc.""Id"",
                    90,
                    8,
                    2,
                    true,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" ss
                CROSS JOIN ""Categories"" c
                CROSS JOIN ""ServiceTypeCategories"" stc
                WHERE ss.""StatusValue"" = 'dispute_resolved_client'
                AND ss.""StatusType"" = 'SearchHireStatus'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = ss.""Id"" 
                    AND sc.""CategoryId"" = c.""Id""
                    AND sc.""ServiceTypeCategoryId"" = stc.""Id""
                );
            ");

            // Crear configuraciones globales y específicas por categoría/tipo para dispute_resolved_expert
            // Basado en los valores que estaban en CategoryServiceTypeConfigs: 0% cliente, 95% experto, 5% plataforma
            migrationBuilder.Sql(@"
                -- Crear configuración global para dispute_resolved_expert
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", 
                                                   ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", 
                                                   ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 
                    ss.""Id"",
                    NULL,
                    NULL,
                    0,
                    95,
                    5,
                    true,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" ss
                WHERE ss.""StatusValue"" = 'dispute_resolved_expert'
                AND ss.""StatusType"" = 'SearchHireStatus'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = ss.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // Crear configuraciones específicas por categoría y tipo para dispute_resolved_expert
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", 
                                                   ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", 
                                                   ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 
                    ss.""Id"",
                    c.""Id"",
                    stc.""Id"",
                    0,
                    95,
                    5,
                    true,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" ss
                CROSS JOIN ""Categories"" c
                CROSS JOIN ""ServiceTypeCategories"" stc
                WHERE ss.""StatusValue"" = 'dispute_resolved_expert'
                AND ss.""StatusType"" = 'SearchHireStatus'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = ss.""Id"" 
                    AND sc.""CategoryId"" = c.""Id""
                    AND sc.""ServiceTypeCategoryId"" = stc.""Id""
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Eliminar configuraciones creadas
            migrationBuilder.Sql(@"
                DELETE FROM ""StatusConfigurations""
                WHERE ""StatusId"" IN (
                    SELECT ""Id"" FROM ""SystemStatuses""
                    WHERE ""StatusValue"" IN ('dispute_resolved_client', 'dispute_resolved_expert')
                    AND ""StatusType"" = 'SearchHireStatus'
                );
            ");
        }
    }
}

