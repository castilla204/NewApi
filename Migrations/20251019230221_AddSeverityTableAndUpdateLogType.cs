using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSeverityTableAndUpdateLogType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Logs_LogTypes_LogTypeId",
                table: "Logs");

            // Crear tabla Severities primero
            migrationBuilder.CreateTable(
                name: "Severities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Severities", x => x.Id);
                });

            // Crear índice único en Name para Severities
            migrationBuilder.CreateIndex(
                name: "IX_Severities_Name",
                table: "Severities",
                column: "Name",
                unique: true);

            // Insertar datos de severidad
            migrationBuilder.Sql(@"
                INSERT INTO ""Severities"" (""Name"", ""Description"", ""SortOrder"", ""IsActive"", ""CreatedAt"")
                VALUES
                ('Critical', 'Critical severity level', 1, true, NOW()),
                ('High', 'High severity level', 2, true, NOW()),
                ('Medium', 'Medium severity level', 3, true, NOW()),
                ('Low', 'Low severity level', 4, true, NOW())
                ON CONFLICT (""Name"") DO NOTHING;
            ");

            // Agregar columna SeverityId como nullable
            migrationBuilder.AddColumn<int>(
                name: "SeverityId",
                table: "LogTypes",
                type: "integer",
                nullable: true);

            // Actualizar registros existentes basándose en la columna Severity anterior
            migrationBuilder.Sql(@"
                UPDATE ""LogTypes"" 
                SET ""SeverityId"" = (
                    CASE 
                        WHEN ""Severity"" = 'Critical' THEN (SELECT ""Id"" FROM ""Severities"" WHERE ""Name"" = 'Critical')
                        WHEN ""Severity"" = 'High' THEN (SELECT ""Id"" FROM ""Severities"" WHERE ""Name"" = 'High')
                        WHEN ""Severity"" = 'Medium' THEN (SELECT ""Id"" FROM ""Severities"" WHERE ""Name"" = 'Medium')
                        WHEN ""Severity"" = 'Low' THEN (SELECT ""Id"" FROM ""Severities"" WHERE ""Name"" = 'Low')
                        ELSE (SELECT ""Id"" FROM ""Severities"" WHERE ""Name"" = 'Low')
                    END
                )
                WHERE ""SeverityId"" IS NULL;
            ");

            // Hacer la columna NOT NULL
            migrationBuilder.AlterColumn<int>(
                name: "SeverityId",
                table: "LogTypes",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            // Eliminar la columna Severity anterior
            migrationBuilder.DropColumn(
                name: "Severity",
                table: "LogTypes");

            migrationBuilder.CreateIndex(
                name: "IX_LogTypes_SeverityId",
                table: "LogTypes",
                column: "SeverityId");

            migrationBuilder.AddForeignKey(
                name: "FK_Logs_LogTypes_LogTypeId",
                table: "Logs",
                column: "LogTypeId",
                principalTable: "LogTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_LogTypes_Severities_SeverityId",
                table: "LogTypes",
                column: "SeverityId",
                principalTable: "Severities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Logs_LogTypes_LogTypeId",
                table: "Logs");

            migrationBuilder.DropForeignKey(
                name: "FK_LogTypes_Severities_SeverityId",
                table: "LogTypes");

            migrationBuilder.DropIndex(
                name: "IX_LogTypes_SeverityId",
                table: "LogTypes");

            // Agregar columna Severity como nullable primero
            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "LogTypes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // Actualizar registros existentes basándose en SeverityId
            migrationBuilder.Sql(@"
                UPDATE ""LogTypes"" 
                SET ""Severity"" = (
                    CASE 
                        WHEN ""SeverityId"" = (SELECT ""Id"" FROM ""Severities"" WHERE ""Name"" = 'Critical') THEN 'Critical'
                        WHEN ""SeverityId"" = (SELECT ""Id"" FROM ""Severities"" WHERE ""Name"" = 'High') THEN 'High'
                        WHEN ""SeverityId"" = (SELECT ""Id"" FROM ""Severities"" WHERE ""Name"" = 'Medium') THEN 'Medium'
                        WHEN ""SeverityId"" = (SELECT ""Id"" FROM ""Severities"" WHERE ""Name"" = 'Low') THEN 'Low'
                        ELSE 'Low'
                    END
                )
                WHERE ""Severity"" IS NULL;
            ");

            // Hacer la columna NOT NULL
            migrationBuilder.AlterColumn<string>(
                name: "Severity",
                table: "LogTypes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            // Eliminar la columna SeverityId
            migrationBuilder.DropColumn(
                name: "SeverityId",
                table: "LogTypes");

            // Eliminar tabla Severities
            migrationBuilder.DropTable(
                name: "Severities");

            migrationBuilder.AddForeignKey(
                name: "FK_Logs_LogTypes_LogTypeId",
                table: "Logs",
                column: "LogTypeId",
                principalTable: "LogTypes",
                principalColumn: "Id");
        }
    }
}
