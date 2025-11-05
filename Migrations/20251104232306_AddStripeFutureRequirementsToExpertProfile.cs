using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeFutureRequirementsToExpertProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SearchHires_ExpertAvailabilities_ExpertAvailabilityId",
                table: "SearchHires");

            migrationBuilder.DropIndex(
                name: "IX_ExpertAvailabilities_ExpertId_IsActive_EffectiveTo",
                table: "ExpertAvailabilities");

            migrationBuilder.AddColumn<DateTime>(
                name: "StripeFutureDueAt",
                table: "ExpertProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeFutureRequirements",
                table: "ExpertProfiles",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExpertAvailabilities_ExpertId",
                table: "ExpertAvailabilities",
                column: "ExpertId");

            migrationBuilder.AddForeignKey(
                name: "FK_SearchHires_ExpertAvailabilities_ExpertAvailabilityId",
                table: "SearchHires",
                column: "ExpertAvailabilityId",
                principalTable: "ExpertAvailabilities",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SearchHires_ExpertAvailabilities_ExpertAvailabilityId",
                table: "SearchHires");

            migrationBuilder.DropIndex(
                name: "IX_ExpertAvailabilities_ExpertId",
                table: "ExpertAvailabilities");

            migrationBuilder.DropColumn(
                name: "StripeFutureDueAt",
                table: "ExpertProfiles");

            migrationBuilder.DropColumn(
                name: "StripeFutureRequirements",
                table: "ExpertProfiles");

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
    }
}
