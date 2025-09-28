using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRedundantConfigTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===== ELIMINAR TABLAS REDUNDANTES =====
            
            // 1. Eliminar AppointmentStatusConfigs (reemplazada por StatusConfigurations)
            migrationBuilder.DropTable(
                name: "AppointmentStatusConfigs");

            // 2. Eliminar ServiceTypeCategoryConfigs (reemplazada por StatusConfigurations)
            migrationBuilder.DropTable(
                name: "ServiceTypeCategoryConfigs");

            // 3. Eliminar CategoryServiceTypeConfigs (reemplazada por StatusConfigurations)
            migrationBuilder.DropTable(
                name: "CategoryServiceTypeConfigs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ===== RECREAR TABLAS ELIMINADAS =====
            
            // 1. Recrear CategoryServiceTypeConfigs
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
                        name: "FK_CategoryServiceTypeConfigs_ServiceTypeCategories_ServiceTypeCateg~",
                        column: x => x.ServiceTypeCategoryId,
                        principalTable: "ServiceTypeCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // 2. Recrear ServiceTypeCategoryConfigs
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
                        name: "FK_ServiceTypeCategoryConfigs_ServiceTypeCategories_ServiceTypeCateg~",
                        column: x => x.ServiceTypeCategoryId,
                        principalTable: "ServiceTypeCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // 3. Recrear AppointmentStatusConfigs
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

            // Recrear índices
            migrationBuilder.CreateIndex(
                name: "IX_CategoryServiceTypeConfigs_CategoryId",
                table: "CategoryServiceTypeConfigs",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryServiceTypeConfigs_ServiceTypeCategoryId",
                table: "CategoryServiceTypeConfigs",
                column: "ServiceTypeCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceTypeCategoryConfigs_ServiceTypeCategoryId",
                table: "ServiceTypeCategoryConfigs",
                column: "ServiceTypeCategoryId");
        }
    }
}
