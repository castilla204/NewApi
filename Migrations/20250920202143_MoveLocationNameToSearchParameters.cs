using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class MoveLocationNameToSearchParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Añadir LocationName a SearchParameters
            migrationBuilder.AddColumn<string>(
                name: "LocationName",
                table: "SearchParameters",
                type: "text",
                nullable: true);

            // Copiar datos de Searches.LocationName a SearchParameters.LocationName
            migrationBuilder.Sql(@"
                UPDATE ""SearchParameters"" 
                SET ""LocationName"" = s.""LocationName""
                FROM ""Searches"" s 
                WHERE ""SearchParameters"".""SearchId"" = s.""Id"" 
                AND s.""LocationName"" IS NOT NULL
            ");

            // Eliminar LocationName de Searches
            migrationBuilder.DropColumn(
                name: "LocationName",
                table: "Searches");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Añadir LocationName de vuelta a Searches
            migrationBuilder.AddColumn<string>(
                name: "LocationName",
                table: "Searches",
                type: "text",
                nullable: true);

            // Copiar datos de vuelta de SearchParameters.LocationName a Searches.LocationName
            migrationBuilder.Sql(@"
                UPDATE ""Searches"" 
                SET ""LocationName"" = sp.""LocationName""
                FROM ""SearchParameters"" sp 
                WHERE ""Searches"".""Id"" = sp.""SearchId"" 
                AND sp.""LocationName"" IS NOT NULL
            ");

            // Eliminar LocationName de SearchParameters
            migrationBuilder.DropColumn(
                name: "LocationName",
                table: "SearchParameters");
        }
    }
}
