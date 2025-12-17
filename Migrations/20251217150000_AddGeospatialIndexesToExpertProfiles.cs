using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddGeospatialIndexesToExpertProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ Crear índices en coordenadas para optimizar búsquedas geoespaciales
            // Aunque las coordenadas son strings, los índices ayudan con las búsquedas
            migrationBuilder.CreateIndex(
                name: "IX_ExpertProfiles_Latitude_Longitude",
                table: "ExpertProfiles",
                columns: new[] { "Latitude", "Longitude" },
                filter: "\"Latitude\" IS NOT NULL AND \"Latitude\" != '' AND \"Longitude\" IS NOT NULL AND \"Longitude\" != ''");

            // ✅ Índice adicional solo en Latitude para búsquedas por latitud
            migrationBuilder.CreateIndex(
                name: "IX_ExpertProfiles_Latitude",
                table: "ExpertProfiles",
                column: "Latitude",
                filter: "\"Latitude\" IS NOT NULL AND \"Latitude\" != ''");

            // ✅ Índice adicional solo en Longitude para búsquedas por longitud
            migrationBuilder.CreateIndex(
                name: "IX_ExpertProfiles_Longitude",
                table: "ExpertProfiles",
                column: "Longitude",
                filter: "\"Longitude\" IS NOT NULL AND \"Longitude\" != ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExpertProfiles_Latitude_Longitude",
                table: "ExpertProfiles");

            migrationBuilder.DropIndex(
                name: "IX_ExpertProfiles_Latitude",
                table: "ExpertProfiles");

            migrationBuilder.DropIndex(
                name: "IX_ExpertProfiles_Longitude",
                table: "ExpertProfiles");
        }
    }
}

