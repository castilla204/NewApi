using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class MakeExpertProfileIdNullableInSearchServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ Hacer ExpertProfileId nullable en SearchServices para permitir anonimización completa en eliminación de cuentas
            migrationBuilder.DropForeignKey(
                name: "FK_SearchServices_ExpertProfiles_ExpertProfileId",
                table: "SearchServices");

            migrationBuilder.AlterColumn<int>(
                name: "ExpertProfileId",
                table: "SearchServices",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddForeignKey(
                name: "FK_SearchServices_ExpertProfiles_ExpertProfileId",
                table: "SearchServices",
                column: "ExpertProfileId",
                principalTable: "ExpertProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SearchServices_ExpertProfiles_ExpertProfileId",
                table: "SearchServices");

            migrationBuilder.AlterColumn<int>(
                name: "ExpertProfileId",
                table: "SearchServices",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SearchServices_ExpertProfiles_ExpertProfileId",
                table: "SearchServices",
                column: "ExpertProfileId",
                principalTable: "ExpertProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
