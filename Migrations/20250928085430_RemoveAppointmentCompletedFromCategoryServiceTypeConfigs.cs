using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAppointmentCompletedFromCategoryServiceTypeConfigs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Eliminar configuraciones de appointment_completed porque no produce transferencias reales
            // appointment_completed es solo un estado intermedio de la cita, no un estado final de transferencia
            // La transferencia real se produce cuando el cliente aprueba (SearchHireStatus.Completed)
            
            migrationBuilder.DeleteData(
                table: "CategoryServiceTypeConfigs",
                keyColumns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status" },
                keyValues: new object[,]
                {
                    // Eliminar appointment_completed para todas las combinaciones
                    { 1, 1, "appointment_completed" },
                    { 1, 2, "appointment_completed" },
                    { 1, 3, "appointment_completed" },
                    { 2, 1, "appointment_completed" },
                    { 2, 2, "appointment_completed" },
                    { 2, 3, "appointment_completed" },
                    { 3, 1, "appointment_completed" },
                    { 3, 2, "appointment_completed" },
                    { 3, 3, "appointment_completed" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restaurar configuraciones de appointment_completed (rollback)
            migrationBuilder.InsertData(
                table: "CategoryServiceTypeConfigs",
                columns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    // Restaurar appointment_completed para todas las combinaciones
                    { 1, 1, "appointment_completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 2, "appointment_completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 3, "appointment_completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 1, "appointment_completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 2, "appointment_completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 3, "appointment_completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, 1, "appointment_completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, 2, "appointment_completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, 3, "appointment_completed", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow }
                });
        }
    }
}
