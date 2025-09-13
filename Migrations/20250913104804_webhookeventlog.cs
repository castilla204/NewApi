using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class webhookeventlog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProcessedWebhookEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StripeAccountId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "Success"),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    EventData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedWebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedWebhookEvents_EventId",
                table: "ProcessedWebhookEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedWebhookEvents_StripeAccountId",
                table: "ProcessedWebhookEvents",
                column: "StripeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedWebhookEvents_UserId",
                table: "ProcessedWebhookEvents",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedWebhookEvents");
        }
    }
}
