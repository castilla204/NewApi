using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryServiceTypeConfigTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CategoryServiceTypeConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CategoryId = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("PK_CategoryServiceTypeConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryServiceTypeConfigs_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CategoryServiceTypeConfigs_ServiceTypeCategories_ServiceTyp~",
                        column: x => x.ServiceTypeCategoryId,
                        principalTable: "ServiceTypeCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryServiceTypeConfigs_CategoryId",
                table: "CategoryServiceTypeConfigs",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryServiceTypeConfigs_ServiceTypeCategoryId",
                table: "CategoryServiceTypeConfigs",
                column: "ServiceTypeCategoryId");

            // Crear índice único para evitar duplicados
            migrationBuilder.CreateIndex(
                name: "IX_CategoryServiceTypeConfigs_CategoryId_ServiceTypeCategoryId_Status",
                table: "CategoryServiceTypeConfigs",
                columns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status" },
                unique: true);

            // Insertar configuraciones por defecto para combinaciones Category + ServiceTypeCategory
            // Ejemplo: Category 1 (Electrodomésticos) + ServiceTypeCategory 1 (Búsqueda + Revisión)
            migrationBuilder.InsertData(
                table: "CategoryServiceTypeConfigs",
                columns: new[] { "CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    // Category 1 + ServiceTypeCategory 1 (Búsqueda + Revisión)
                    { 1, 1, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 1, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 1, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 1, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 1, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    
                    // Category 1 + ServiceTypeCategory 2 (Solo Revisión)
                    { 1, 2, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 2, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 2, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 2, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 2, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    
                    // Category 1 + ServiceTypeCategory 3 (Solo Búsqueda)
                    { 1, 3, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 3, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 3, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 3, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 1, 3, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    
                    // Category 2 + ServiceTypeCategory 1 (Búsqueda + Revisión)
                    { 2, 1, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 1, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 1, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 1, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 1, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    
                    // Category 2 + ServiceTypeCategory 2 (Solo Revisión)
                    { 2, 2, "appointment_cancelled_by_client_second", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 2, "appointment_cancelled_by_expert", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 2, "appointment_cancelled_by_no_response", 90.00m, 8.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 2, "appointment_cancelled_by_expert_rejection", 98.00m, 0.00m, 2.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, 2, "appointment_completed", 0.00m, 95.00m, 5.00m, true, DateTime.UtcNow, DateTime.UtcNow },
                    
                    // Category 2 + ServiceTypeCategory 3 (Solo Búsqueda)
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
            migrationBuilder.DropTable(
                name: "CategoryServiceTypeConfigs");
        }
    }
}
