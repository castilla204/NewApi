using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "ServiceTypeCategoryId",
                table: "ServiceTypes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.CreateTable(
                name: "Appointments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SearchHireId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "awaiting_appointment"),
                    ProposedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProposedTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    Location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Latitude = table.Column<decimal>(type: "numeric", nullable: true),
                    Longitude = table.Column<decimal>(type: "numeric", nullable: true),
                    DisputeReason = table.Column<string>(type: "text", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedBy = table.Column<int>(type: "integer", nullable: true),
                    RejectionCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CancellationCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastRejectionAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastProposalAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastResponseAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appointments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Appointments_SearchHires_SearchHireId",
                        column: x => x.SearchHireId,
                        principalTable: "SearchHires",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppointmentTimers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppointmentId = table.Column<int>(type: "integer", nullable: false),
                    TimerType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsExpired = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ExpiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentTimers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppointmentTimers_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_ProposedDate",
                table: "Appointments",
                column: "ProposedDate");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_SearchHireId",
                table: "Appointments",
                column: "SearchHireId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_Status",
                table: "Appointments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentTimers_AppointmentId",
                table: "AppointmentTimers",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentTimers_EndTime",
                table: "AppointmentTimers",
                column: "EndTime");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentTimers_IsExpired",
                table: "AppointmentTimers",
                column: "IsExpired");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentTimers_TimerType",
                table: "AppointmentTimers",
                column: "TimerType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppointmentTimers");

            migrationBuilder.DropTable(
                name: "Appointments");

            migrationBuilder.AlterColumn<int>(
                name: "ServiceTypeCategoryId",
                table: "ServiceTypes",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
