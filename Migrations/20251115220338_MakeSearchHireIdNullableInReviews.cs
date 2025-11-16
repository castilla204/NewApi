using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class MakeSearchHireIdNullableInReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Reviews_SearchHires_SearchHireId",
                table: "Reviews");

            migrationBuilder.AlterColumn<int>(
                name: "SearchHireId",
                table: "Reviews",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddForeignKey(
                name: "FK_Reviews_SearchHires_SearchHireId",
                table: "Reviews",
                column: "SearchHireId",
                principalTable: "SearchHires",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Reviews_SearchHires_SearchHireId",
                table: "Reviews");

            migrationBuilder.AlterColumn<int>(
                name: "SearchHireId",
                table: "Reviews",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Reviews_SearchHires_SearchHireId",
                table: "Reviews",
                column: "SearchHireId",
                principalTable: "SearchHires",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
