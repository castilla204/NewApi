using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLogTypeTableOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Crear tabla LogTypes
            migrationBuilder.CreateTable(
                name: "LogTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequiresAdminNotification = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresEmailAlert = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresSmsAlert = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogTypes", x => x.Id);
                    table.UniqueConstraint("AK_LogTypes_Name", x => x.Name);
                });

            // Agregar columnas a la tabla Logs
            migrationBuilder.AddColumn<string>(
                name: "AdditionalData",
                table: "Logs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LogTypeId",
                table: "Logs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RelatedEntityId",
                table: "Logs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RelatedEntityType",
                table: "Logs",
                type: "text",
                nullable: true);

            // Crear índice
            migrationBuilder.CreateIndex(
                name: "IX_Logs_LogTypeId",
                table: "Logs",
                column: "LogTypeId");

            // Agregar foreign key
            migrationBuilder.AddForeignKey(
                name: "FK_Logs_LogTypes_LogTypeId",
                table: "Logs",
                column: "LogTypeId",
                principalTable: "LogTypes",
                principalColumn: "Id");

            // Insertar tipos de logs por defecto
            migrationBuilder.Sql(@"
                INSERT INTO ""LogTypes"" (""Name"", ""Description"", ""Category"", ""Severity"", ""RequiresAdminNotification"", ""RequiresEmailAlert"", ""RequiresSmsAlert"", ""IsActive"", ""CreatedAt"")
                VALUES 
                -- Critical Log Types
                ('TRANSFER_FAILED', 'Transfer to expert failed but service completed', 'Critical', 'Critical', true, true, false, true, NOW()),
                ('REFUND_FAILED', 'Automatic refund failed after payment', 'Critical', 'Critical', true, true, false, true, NOW()),
                ('PAYMENT_PROCESSING_ERROR', 'Error processing payment in Stripe', 'Critical', 'Critical', true, true, false, true, NOW()),
                ('STRIPE_WEBHOOK_ERROR', 'Error processing Stripe webhook', 'Critical', 'Critical', true, false, false, true, NOW()),

                -- Error Log Types
                ('SEARCH_CREATION_ERROR', 'Error creating search after payment', 'Error', 'High', true, false, false, true, NOW()),
                ('EXPERT_ACCOUNT_VERIFICATION_FAILED', 'Expert account verification failed', 'Error', 'High', false, false, false, true, NOW()),
                ('DATABASE_CONNECTION_ERROR', 'Database connection error', 'Error', 'High', true, false, false, true, NOW()),
                ('EXTERNAL_API_ERROR', 'Error calling external API', 'Error', 'Medium', false, false, false, true, NOW()),

                -- Warning Log Types
                ('EXPERT_ACCOUNT_PENDING', 'Expert account pending verification', 'Warning', 'Medium', false, false, false, true, NOW()),
                ('PAYMENT_RETRY_ATTEMPT', 'Payment retry attempt', 'Warning', 'Medium', false, false, false, true, NOW()),
                ('USER_ACTION_LIMIT_EXCEEDED', 'User exceeded action limits', 'Warning', 'Low', false, false, false, true, NOW()),

                -- Info Log Types
                ('SERVICE_COMPLETED', 'Service completed successfully', 'Info', 'Low', false, false, false, true, NOW()),
                ('REFUND_PROCESSED', 'Refund processed successfully', 'Info', 'Low', false, false, false, true, NOW()),
                ('PAYMENT_SUCCESSFUL', 'Payment processed successfully', 'Info', 'Low', false, false, false, true, NOW()),
                ('USER_LOGIN', 'User logged in', 'Info', 'Low', false, false, false, true, NOW()),
                ('SEARCH_CREATED', 'Search created successfully', 'Info', 'Low', false, false, false, true, NOW()),
                ('EXPERT_ACCOUNT_VERIFIED', 'Expert account verified', 'Info', 'Low', false, false, false, true, NOW())
                ON CONFLICT (""Name"") DO NOTHING;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Logs_LogTypes_LogTypeId",
                table: "Logs");

            migrationBuilder.DropIndex(
                name: "IX_Logs_LogTypeId",
                table: "Logs");

            migrationBuilder.DropColumn(
                name: "AdditionalData",
                table: "Logs");

            migrationBuilder.DropColumn(
                name: "LogTypeId",
                table: "Logs");

            migrationBuilder.DropColumn(
                name: "RelatedEntityId",
                table: "Logs");

            migrationBuilder.DropColumn(
                name: "RelatedEntityType",
                table: "Logs");

            migrationBuilder.DropTable(
                name: "LogTypes");
        }
    }
}
