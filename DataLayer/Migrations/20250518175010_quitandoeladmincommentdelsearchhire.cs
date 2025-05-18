using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataLayer.Migrations
{
    /// <inheritdoc />
    public partial class quitandoeladmincommentdelsearchhire : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminComments",
                table: "Disputes");

            migrationBuilder.DropColumn(
                name: "Resolution",
                table: "Disputes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdminComments",
                table: "Disputes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Resolution",
                table: "Disputes",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
