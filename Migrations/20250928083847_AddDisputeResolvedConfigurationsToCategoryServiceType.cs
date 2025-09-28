using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDisputeResolvedConfigurationsToCategoryServiceType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Agregar configuraciones para disputas resueltas en CategoryServiceTypeConfigs
            // Estas configuraciones son necesarias porque se crean transacciones financieras
            // y se distribuye dinero entre cliente, experto y plataforma
            
            migrationBuilder.InsertData(
                table: "CategoryServiceTypeConfigs",
                columns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    // Configuración para dispute-resolved-client (refund automático a favor del cliente)
                    { 1, 1, "dispute-resolved-client", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 2, "dispute-resolved-client", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 3, "dispute-resolved-client", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 1, "dispute-resolved-client", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 2, "dispute-resolved-client", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 3, "dispute-resolved-client", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, 1, "dispute-resolved-client", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, 2, "dispute-resolved-client", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, 3, "dispute-resolved-client", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    
                    // Configuración para dispute-resolved-expert (pago automático a favor del experto)
                    { 1, 1, "dispute-resolved-expert", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 2, "dispute-resolved-expert", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 3, "dispute-resolved-expert", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 1, "dispute-resolved-expert", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 2, "dispute-resolved-expert", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 3, "dispute-resolved-expert", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, 1, "dispute-resolved-expert", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, 2, "dispute-resolved-expert", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, 3, "dispute-resolved-expert", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Eliminar configuraciones de disputas resueltas
            migrationBuilder.DeleteData(
                table: "CategoryServiceTypeConfigs",
                keyColumns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status" },
                keyValues: new object[,]
                {
                    // Eliminar dispute-resolved-client
                    { 1, 1, "dispute-resolved-client" },
                    { 1, 2, "dispute-resolved-client" },
                    { 1, 3, "dispute-resolved-client" },
                    { 2, 1, "dispute-resolved-client" },
                    { 2, 2, "dispute-resolved-client" },
                    { 2, 3, "dispute-resolved-client" },
                    { 3, 1, "dispute-resolved-client" },
                    { 3, 2, "dispute-resolved-client" },
                    { 3, 3, "dispute-resolved-client" },
                    
                    // Eliminar dispute-resolved-expert
                    { 1, 1, "dispute-resolved-expert" },
                    { 1, 2, "dispute-resolved-expert" },
                    { 1, 3, "dispute-resolved-expert" },
                    { 2, 1, "dispute-resolved-expert" },
                    { 2, 2, "dispute-resolved-expert" },
                    { 2, 3, "dispute-resolved-expert" },
                    { 3, 1, "dispute-resolved-expert" },
                    { 3, 2, "dispute-resolved-expert" },
                    { 3, 3, "dispute-resolved-expert" }
                });
        }
    }
}
