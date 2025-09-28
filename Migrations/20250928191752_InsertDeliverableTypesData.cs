using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class InsertDeliverableTypesData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "DeliverableTypes",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.CreateTable(
                name: "SearchServiceDeliverableTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SearchServiceId = table.Column<int>(type: "integer", nullable: false),
                    DeliverableTypeId = table.Column<int>(type: "integer", nullable: false),
                    IsSelected = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchServiceDeliverableTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SearchServiceDeliverableTypes_DeliverableTypes_DeliverableT~",
                        column: x => x.DeliverableTypeId,
                        principalTable: "DeliverableTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SearchServiceDeliverableTypes_SearchServices_SearchServiceId",
                        column: x => x.SearchServiceId,
                        principalTable: "SearchServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchServiceDeliverableTypes_DeliverableTypeId",
                table: "SearchServiceDeliverableTypes",
                column: "DeliverableTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchServiceDeliverableTypes_SearchServiceId_DeliverableTy~",
                table: "SearchServiceDeliverableTypes",
                columns: new[] { "SearchServiceId", "DeliverableTypeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchServiceDeliverableTypes");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "DeliverableTypes",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }
    }
}
