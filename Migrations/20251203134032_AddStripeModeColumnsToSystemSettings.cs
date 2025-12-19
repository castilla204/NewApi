using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeModeColumnsToSystemSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StripeMode",
                table: "SystemSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "StripeModeChangedAt",
                table: "SystemSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StripeModeChangedByUserId",
                table: "SystemSettings",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StripeMode",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "StripeModeChangedAt",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "StripeModeChangedByUserId",
                table: "SystemSettings");
        }
    }
}
