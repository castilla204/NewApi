using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddExpertResponseToDispute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpertResponse",
                table: "Disputes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpertResponseAt",
                table: "Disputes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpertResponseDeadline",
                table: "Disputes",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpertResponse",
                table: "Disputes");

            migrationBuilder.DropColumn(
                name: "ExpertResponseAt",
                table: "Disputes");

            migrationBuilder.DropColumn(
                name: "ExpertResponseDeadline",
                table: "Disputes");
        }
    }
}
