using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// 📜 Round 9 — A2 FIX: tabla append-only de auditoría para cambios de estado de SearchHire.
    /// GDPR Art 5(2) "accountability" + soporte de reconstrucción de ciclo de vida en disputas.
    /// </summary>
    public partial class AddSearchHireStatusHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SearchHireStatusHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SearchHireId = table.Column<int>(type: "integer", nullable: false),
                    OldStatusId = table.Column<int>(type: "integer", nullable: true),
                    NewStatusId = table.Column<int>(type: "integer", nullable: false),
                    ChangedByUserId = table.Column<int>(type: "integer", nullable: true),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AdditionalDataJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchHireStatusHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SearchHireStatusHistory_SearchHires_SearchHireId",
                        column: x => x.SearchHireId,
                        principalTable: "SearchHires",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchHireStatusHistory_SearchHireId",
                table: "SearchHireStatusHistory",
                column: "SearchHireId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchHireStatusHistory_CreatedAt",
                table: "SearchHireStatusHistory",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchHireStatusHistory");
        }
    }
}
