using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCountryToExpertProfileAndSearchHire : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ Agregar campo Country a ExpertProfiles
            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "ExpertProfiles",
                type: "text",
                nullable: true);

            // ✅ Agregar campo ExpertCountry a SearchHires
            migrationBuilder.AddColumn<string>(
                name: "ExpertCountry",
                table: "SearchHires",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revertir: Eliminar campo ExpertCountry de SearchHires
            migrationBuilder.DropColumn(
                name: "ExpertCountry",
                table: "SearchHires");

            // Revertir: Eliminar campo Country de ExpertProfiles
            migrationBuilder.DropColumn(
                name: "Country",
                table: "ExpertProfiles");
        }
    }
}







