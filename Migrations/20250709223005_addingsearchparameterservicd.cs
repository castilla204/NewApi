using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class addingsearchparameterservicd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ServiceTypeId",
                table: "SearchParameters",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchParameters_ServiceTypeId",
                table: "SearchParameters",
                column: "ServiceTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_SearchParameters_ServiceTypes_ServiceTypeId",
                table: "SearchParameters",
                column: "ServiceTypeId",
                principalTable: "ServiceTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SearchParameters_ServiceTypes_ServiceTypeId",
                table: "SearchParameters");

            migrationBuilder.DropIndex(
                name: "IX_SearchParameters_ServiceTypeId",
                table: "SearchParameters");

            migrationBuilder.DropColumn(
                name: "ServiceTypeId",
                table: "SearchParameters");
        }
    }
}
