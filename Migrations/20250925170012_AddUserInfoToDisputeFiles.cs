using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddUserInfoToDisputeFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Agregar columnas como nullable primero
            migrationBuilder.AddColumn<string>(
                name: "FileCategory",
                table: "DisputeFiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UploadedByUserId",
                table: "DisputeFiles",
                type: "integer",
                nullable: true);

            // Actualizar archivos existentes - asumir que son del cliente (reportero de la disputa)
            migrationBuilder.Sql(@"
                UPDATE ""DisputeFiles"" 
                SET ""UploadedByUserId"" = d.""ReporterId"", 
                    ""FileCategory"" = 'client'
                FROM ""Disputes"" d 
                WHERE ""DisputeFiles"".""DisputeId"" = d.""Id""
                AND ""DisputeFiles"".""UploadedByUserId"" IS NULL;
            ");

            // Hacer las columnas NOT NULL después de actualizar
            migrationBuilder.AlterColumn<string>(
                name: "FileCategory",
                table: "DisputeFiles",
                type: "text",
                nullable: false);

            migrationBuilder.AlterColumn<int>(
                name: "UploadedByUserId",
                table: "DisputeFiles",
                type: "integer",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "IX_DisputeFiles_UploadedByUserId",
                table: "DisputeFiles",
                column: "UploadedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_DisputeFiles_Users_UploadedByUserId",
                table: "DisputeFiles",
                column: "UploadedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DisputeFiles_Users_UploadedByUserId",
                table: "DisputeFiles");

            migrationBuilder.DropIndex(
                name: "IX_DisputeFiles_UploadedByUserId",
                table: "DisputeFiles");

            migrationBuilder.DropColumn(
                name: "FileCategory",
                table: "DisputeFiles");

            migrationBuilder.DropColumn(
                name: "UploadedByUserId",
                table: "DisputeFiles");
        }
    }
}
