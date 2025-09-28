using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDisputeResolvedConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRefunded",
                table: "FinancialTransactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "StripePaymentIntentId",
                table: "FinancialTransactions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeRefundId",
                table: "FinancialTransactions",
                type: "text",
                nullable: true);

            // Agregar configuraciones para disputas resueltas
            migrationBuilder.InsertData(
                table: "AppointmentStatusConfigs",
                columns: new[] { "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { "dispute-resolved-client", 90m, 8m, 2m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { "dispute-resolved-expert", 0m, 95m, 5m, true, DateTime.UtcNow, DateTime.UtcNow }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Eliminar configuraciones de disputas resueltas
            migrationBuilder.DeleteData(
                table: "AppointmentStatusConfigs",
                keyColumn: "Status",
                keyValue: "dispute-resolved-client");

            migrationBuilder.DeleteData(
                table: "AppointmentStatusConfigs",
                keyColumn: "Status",
                keyValue: "dispute-resolved-expert");

            migrationBuilder.DropColumn(
                name: "IsRefunded",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "StripePaymentIntentId",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "StripeRefundId",
                table: "FinancialTransactions");
        }
    }
}
