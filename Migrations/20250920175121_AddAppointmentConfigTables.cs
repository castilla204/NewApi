using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentConfigTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppointmentStatusConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ClientPercentage = table.Column<decimal>(type: "numeric", nullable: false),
                    ExpertPercentage = table.Column<decimal>(type: "numeric", nullable: false),
                    PlatformPercentage = table.Column<decimal>(type: "numeric", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentStatusConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceTypeCategoryConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServiceTypeCategoryId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ClientPercentage = table.Column<decimal>(type: "numeric", nullable: false),
                    ExpertPercentage = table.Column<decimal>(type: "numeric", nullable: false),
                    PlatformPercentage = table.Column<decimal>(type: "numeric", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceTypeCategoryConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceTypeCategoryConfigs_ServiceTypeCategories_ServiceTyp~",
                        column: x => x.ServiceTypeCategoryId,
                        principalTable: "ServiceTypeCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceTypeCategoryConfigs_ServiceTypeCategoryId",
                table: "ServiceTypeCategoryConfigs",
                column: "ServiceTypeCategoryId");

            // Insertar configuraciones por defecto para estados de citas
            migrationBuilder.InsertData(
                table: "AppointmentStatusConfigs",
                columns: new[] { "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow }
                });

            // Insertar configuraciones por defecto para categorías de servicios
            // Categoría 1: Servicios básicos (por defecto)
            migrationBuilder.InsertData(
                table: "ServiceTypeCategoryConfigs",
                columns: new[] { "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow }
                });

            // Categoría 2: Servicios premium (por defecto)
            migrationBuilder.InsertData(
                table: "ServiceTypeCategoryConfigs",
                columns: new[] { "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { 2, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppointmentStatusConfigs");

            migrationBuilder.DropTable(
                name: "ServiceTypeCategoryConfigs");
        }
    }
}
