using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataLayer.Migrations
{
    /// <inheritdoc />
    public partial class relationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReviewImage_Reviews_ReviewId",
                table: "ReviewImage");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ReviewImage",
                table: "ReviewImage");

            migrationBuilder.RenameTable(
                name: "ReviewImage",
                newName: "ReviewImages");

            migrationBuilder.RenameIndex(
                name: "IX_ReviewImage_ReviewId",
                table: "ReviewImages",
                newName: "IX_ReviewImages_ReviewId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ReviewImages",
                table: "ReviewImages",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ReviewImages_Reviews_ReviewId",
                table: "ReviewImages",
                column: "ReviewId",
                principalTable: "Reviews",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReviewImages_Reviews_ReviewId",
                table: "ReviewImages");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ReviewImages",
                table: "ReviewImages");

            migrationBuilder.RenameTable(
                name: "ReviewImages",
                newName: "ReviewImage");

            migrationBuilder.RenameIndex(
                name: "IX_ReviewImages_ReviewId",
                table: "ReviewImage",
                newName: "IX_ReviewImage_ReviewId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ReviewImage",
                table: "ReviewImage",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ReviewImage_Reviews_ReviewId",
                table: "ReviewImage",
                column: "ReviewId",
                principalTable: "Reviews",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
