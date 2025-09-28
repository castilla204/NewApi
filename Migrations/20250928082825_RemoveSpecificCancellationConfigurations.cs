using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSpecificCancellationConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Eliminar configuraciones de cancelaciones específicas que no deberían estar en SearchHireStatus
            migrationBuilder.DeleteData(
                table: "AppointmentStatusConfigs",
                keyColumn: "Status",
                keyValue: "cancelled_by_client");

            migrationBuilder.DeleteData(
                table: "AppointmentStatusConfigs",
                keyColumn: "Status",
                keyValue: "cancelled_by_client_second");

            migrationBuilder.DeleteData(
                table: "AppointmentStatusConfigs",
                keyColumn: "Status",
                keyValue: "cancelled_by_expert");

            migrationBuilder.DeleteData(
                table: "AppointmentStatusConfigs",
                keyColumn: "Status",
                keyValue: "cancelled_by_no_response");

            migrationBuilder.DeleteData(
                table: "AppointmentStatusConfigs",
                keyColumn: "Status",
                keyValue: "cancelled_by_expert_rejection");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restaurar configuraciones de cancelaciones específicas (rollback)
            migrationBuilder.InsertData(
                table: "AppointmentStatusConfigs",
                columns: new[] { "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { "cancelled_by_client", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { "cancelled_by_client_second", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { "cancelled_by_expert", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { "cancelled_by_no_response", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { "cancelled_by_expert_rejection", 98m, 0m, 2m, true, DateTime.UtcNow, DateTime.UtcNow }
                });
        }
    }
}
