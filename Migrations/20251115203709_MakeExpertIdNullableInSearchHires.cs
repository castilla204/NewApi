using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class MakeExpertIdNullableInSearchHires : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ Hacer ExpertId nullable en SearchHires para permitir anonimización completa en eliminación de cuentas
            migrationBuilder.DropForeignKey(
                name: "FK_SearchHires_Users_ExpertId",
                table: "SearchHires");

            migrationBuilder.AlterColumn<int>(
                name: "ExpertId",
                table: "SearchHires",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddForeignKey(
                name: "FK_SearchHires_Users_ExpertId",
                table: "SearchHires",
                column: "ExpertId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SearchHires_Users_ExpertId",
                table: "SearchHires");

            migrationBuilder.AlterColumn<int>(
                name: "ExpertId",
                table: "SearchHires",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SearchHires_Users_ExpertId",
                table: "SearchHires",
                column: "ExpertId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
