using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddExpertAvailabilitySystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExpertAvailabilityId",
                table: "SearchHires",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExpertAvailabilities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExpertId = table.Column<int>(type: "integer", nullable: false),
                    DaysOfWeek = table.Column<string>(type: "text", nullable: false),
                    StartTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    EndTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpertAvailabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpertAvailabilities_ExpertProfiles_ExpertId",
                        column: x => x.ExpertId,
                        principalTable: "ExpertProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchHires_ExpertAvailabilityId",
                table: "SearchHires",
                column: "ExpertAvailabilityId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpertAvailabilities_ExpertId_IsActive_EffectiveTo",
                table: "ExpertAvailabilities",
                columns: new[] { "ExpertId", "IsActive", "EffectiveTo" },
                filter: "\"EffectiveTo\" IS NULL AND \"IsActive\" = true");

            migrationBuilder.AddForeignKey(
                name: "FK_SearchHires_ExpertAvailabilities_ExpertAvailabilityId",
                table: "SearchHires",
                column: "ExpertAvailabilityId",
                principalTable: "ExpertAvailabilities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SearchHires_ExpertAvailabilities_ExpertAvailabilityId",
                table: "SearchHires");

            migrationBuilder.DropTable(
                name: "ExpertAvailabilities");

            migrationBuilder.DropIndex(
                name: "IX_SearchHires_ExpertAvailabilityId",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "ExpertAvailabilityId",
                table: "SearchHires");
        }
    }
}
