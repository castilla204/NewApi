using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSeparateCancellationCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClientCancellationCount",
                table: "Appointments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ExpertCancellationCount",
                table: "Appointments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastClientCancellationAt",
                table: "Appointments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastExpertCancellationAt",
                table: "Appointments",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientCancellationCount",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "ExpertCancellationCount",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "LastClientCancellationAt",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "LastExpertCancellationAt",
                table: "Appointments");
        }
    }
}
