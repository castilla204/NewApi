using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class InsertDeliverableTypesDataFinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchHireDeliverableTypes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SearchHireDeliverableTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeliverableTypeId = table.Column<int>(type: "integer", nullable: false),
                    SearchHireId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    IsSelected = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchHireDeliverableTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SearchHireDeliverableTypes_DeliverableTypes_DeliverableType~",
                        column: x => x.DeliverableTypeId,
                        principalTable: "DeliverableTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SearchHireDeliverableTypes_SearchHires_SearchHireId",
                        column: x => x.SearchHireId,
                        principalTable: "SearchHires",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchHireDeliverableTypes_DeliverableTypeId",
                table: "SearchHireDeliverableTypes",
                column: "DeliverableTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchHireDeliverableTypes_SearchHireId_DeliverableTypeId",
                table: "SearchHireDeliverableTypes",
                columns: new[] { "SearchHireId", "DeliverableTypeId" },
                unique: true);
        }
    }
}
