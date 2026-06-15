using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class SearchHireInvoiceNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvoiceNumber",
                table: "SearchHires",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AdminAlertedAt",
                table: "Disputes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailureCount",
                table: "AppointmentTimers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastFailedAt",
                table: "AppointmentTimers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvoiceNumber",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "AdminAlertedAt",
                table: "Disputes");

            migrationBuilder.DropColumn(
                name: "FailureCount",
                table: "AppointmentTimers");

            migrationBuilder.DropColumn(
                name: "LastFailedAt",
                table: "AppointmentTimers");
        }
    }
}
