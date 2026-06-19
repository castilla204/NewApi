using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddExpertConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpertConfirmationToken",
                table: "SearchHires",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpertConfirmationDeadline",
                table: "SearchHires",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpertConfirmationToken",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "ExpertConfirmationDeadline",
                table: "SearchHires");
        }
    }
}
