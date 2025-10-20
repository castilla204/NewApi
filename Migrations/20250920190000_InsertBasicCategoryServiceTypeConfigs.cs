using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class InsertBasicCategoryServiceTypeConfigs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Limpiar configuraciones existentes para evitar duplicados
            migrationBuilder.Sql("DELETE FROM \"CategoryServiceTypeConfigs\" WHERE \"CategoryId\" IN (1, 2) AND \"ServiceTypeCategoryId\" IN (1, 2, 3)");
            
            // Insertar configuraciones básicas para Category 1 y 2 con ServiceTypeCategory 1, 2, 3
            // Basado en los porcentajes acordados anteriormente

            // CATEGORY 1 + ServiceTypeCategory 1 (Búsqueda + Revisión)
            migrationBuilder.InsertData(
                table: "CategoryServiceTypeConfigs",
                columns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, 1, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 1, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 1, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 1, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 1, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow }
                });

            // CATEGORY 1 + ServiceTypeCategory 2 (Solo Revisión)
            migrationBuilder.InsertData(
                table: "CategoryServiceTypeConfigs",
                columns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, 2, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 2, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 2, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 2, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 2, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow }
                });

            // CATEGORY 1 + ServiceTypeCategory 3 (Solo Búsqueda)
            migrationBuilder.InsertData(
                table: "CategoryServiceTypeConfigs",
                columns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, 3, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 3, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 3, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 3, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 3, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow }
                });

            // CATEGORY 2 + ServiceTypeCategory 1 (Búsqueda + Revisión)
            migrationBuilder.InsertData(
                table: "CategoryServiceTypeConfigs",
                columns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { 2, 1, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 1, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 1, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 1, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 1, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow }
                });

            // CATEGORY 2 + ServiceTypeCategory 2 (Solo Revisión)
            migrationBuilder.InsertData(
                table: "CategoryServiceTypeConfigs",
                columns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { 2, 2, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 2, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 2, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 2, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 2, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow }
                });

            // CATEGORY 2 + ServiceTypeCategory 3 (Solo Búsqueda)
            migrationBuilder.InsertData(
                table: "CategoryServiceTypeConfigs",
                columns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { 2, 3, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 3, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 3, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 3, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 3, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Eliminar todas las configuraciones insertadas
            migrationBuilder.Sql("DELETE FROM \"CategoryServiceTypeConfigs\" WHERE \"CategoryId\" IN (1, 2) AND \"ServiceTypeCategoryId\" IN (1, 2, 3)");
        }
    }
}





















