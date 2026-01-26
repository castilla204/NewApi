using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchServiceIdToConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "SearchHireId",
                table: "Conversations",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "SearchServiceId",
                table: "Conversations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_SearchServiceId",
                table: "Conversations",
                column: "SearchServiceId");

            // ✅ NOTA: El índice IX_Conversations_SearchHireId ya existe en la BD,
            // por lo que no lo creamos aquí para evitar errores

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_SearchServices_SearchServiceId",
                table: "Conversations",
                column: "SearchServiceId",
                principalTable: "SearchServices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_SearchServices_SearchServiceId",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_SearchServiceId",
                table: "Conversations");

            // ✅ NOTA: No eliminamos IX_Conversations_SearchHireId porque ya existía antes

            migrationBuilder.DropColumn(
                name: "SearchServiceId",
                table: "Conversations");

            migrationBuilder.AlterColumn<int>(
                name: "SearchHireId",
                table: "Conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
