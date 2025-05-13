using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataLayer.Migrations
{
    /// <inheritdoc />
    public partial class actualizandomodelov2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Searches_Users_ExpertId",
                table: "Searches");

            migrationBuilder.DropIndex(
                name: "IX_Searches_ExpertId",
                table: "Searches");

            migrationBuilder.DropColumn(
                name: "ExpertId",
                table: "Searches");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExpertId",
                table: "Searches",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Searches_ExpertId",
                table: "Searches",
                column: "ExpertId");

            migrationBuilder.AddForeignKey(
                name: "FK_Searches_Users_ExpertId",
                table: "Searches",
                column: "ExpertId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
