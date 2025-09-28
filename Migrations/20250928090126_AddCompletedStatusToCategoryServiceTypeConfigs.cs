using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCompletedStatusToCategoryServiceTypeConfigs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Agregar configuración para "completed" - cuando el cliente aprueba desde AwaitingClientDecision
            // Este estado produce transferencia real al experto cuando el cliente aprueba el servicio
            // Distribución: Cliente: 0%, Experto: 95%, Plataforma: 5%
            
            migrationBuilder.InsertData(
                table: "CategoryServiceTypeConfigs",
                columns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    // Configuración para completed (cliente aprueba servicio)
                    { 1, 1, "completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 2, "completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 3, "completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 1, "completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 2, "completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 3, "completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, 1, "completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, 2, "completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, 3, "completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Eliminar configuraciones de completed
            migrationBuilder.DeleteData(
                table: "CategoryServiceTypeConfigs",
                keyColumns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status" },
                keyValues: new object[,]
                {
                    // Eliminar completed para todas las combinaciones
                    { 1, 1, "completed" },
                    { 1, 2, "completed" },
                    { 1, 3, "completed" },
                    { 2, 1, "completed" },
                    { 2, 2, "completed" },
                    { 2, 3, "completed" },
                    { 3, 1, "completed" },
                    { 3, 2, "completed" },
                    { 3, 3, "completed" }
                });
        }
    }
}
