using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceTypeCategoryTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Crear la tabla ServiceTypeCategories
            migrationBuilder.CreateTable(
                name: "ServiceTypeCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceTypeCategories", x => x.Id);
                });

            // 2. Insertar categorías por defecto
            migrationBuilder.InsertData(
                table: "ServiceTypeCategories",
                columns: new[] { "Id", "Name", "Description", "Position", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, "Búsqueda + Revisión", "Servicio completo de búsqueda y revisión", 1, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 2, "Revisión", "Solo revisión presencial", 2, true, DateTime.UtcNow, DateTime.UtcNow },
                    { 3, "Búsqueda", "Solo búsqueda web", 3, true, DateTime.UtcNow, DateTime.UtcNow }
                });

            // 3. Agregar la columna ServiceTypeCategoryId a ServiceTypes (nullable)
            migrationBuilder.AddColumn<int>(
                name: "ServiceTypeCategoryId",
                table: "ServiceTypes",
                type: "integer",
                nullable: true);

            // 4. Crear la foreign key constraint (nullable)
            migrationBuilder.AddForeignKey(
                name: "FK_ServiceTypes_ServiceTypeCategories_ServiceTypeCategoryId",
                table: "ServiceTypes",
                column: "ServiceTypeCategoryId",
                principalTable: "ServiceTypeCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 1. Eliminar la foreign key constraint
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceTypes_ServiceTypeCategories_ServiceTypeCategoryId",
                table: "ServiceTypes");

            // 2. Eliminar la columna ServiceTypeCategoryId
            migrationBuilder.DropColumn(
                name: "ServiceTypeCategoryId",
                table: "ServiceTypes");

            // 3. Eliminar la tabla ServiceTypeCategories
            migrationBuilder.DropTable(
                name: "ServiceTypeCategories");
        }
    }
}
