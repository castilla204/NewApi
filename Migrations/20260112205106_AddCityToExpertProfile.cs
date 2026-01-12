using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCityToExpertProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "ExpertProfiles",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SearchServiceFavorites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    SearchServiceId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchServiceFavorites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SearchServiceFavorites_SearchServices_SearchServiceId",
                        column: x => x.SearchServiceId,
                        principalTable: "SearchServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SearchServiceFavorites_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchServiceFavorites_CreatedAt",
                table: "SearchServiceFavorites",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SearchServiceFavorites_SearchServiceId",
                table: "SearchServiceFavorites",
                column: "SearchServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchServiceFavorites_UserId",
                table: "SearchServiceFavorites",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchServiceFavorites_UserId_SearchServiceId",
                table: "SearchServiceFavorites",
                columns: new[] { "UserId", "SearchServiceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchServiceFavorites");

            migrationBuilder.DropColumn(
                name: "City",
                table: "ExpertProfiles");
        }
    }
}
