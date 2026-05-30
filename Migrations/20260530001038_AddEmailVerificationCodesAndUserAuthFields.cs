using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// 🛡️ Round 16: Soporte completo de autenticación por email/password + OTP + Sign in with Apple.
    ///
    /// Cambios:
    ///  - Users: nuevos campos (AppleId, EmailVerified, EmailVerifiedAt, PasswordChangedAt,
    ///    FailedLoginAttempts, LockedUntil) + índices (Email, GoogleId, AppleId).
    ///  - EmailVerificationCodes: tabla nueva para códigos OTP single-use (registro/reset/step-up).
    ///
    /// El diseño cumple con NIST SP 800-63B-4 (2024) y OWASP 2025:
    ///  - Códigos hasheados (SHA-256 + salt por fila), nunca en plano.
    ///  - TTL 10 minutos máximo (NIST SHALL).
    ///  - VerificationToken opaco para encadenar pasos sin exponer UserId.
    ///  - Índices parciales para lookup O(log n) en verify y para job de cleanup.
    /// </summary>
    public partial class AddEmailVerificationCodesAndUserAuthFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ─────────────────────────────────────────────────────────────────────────
            // 1) Users: nuevos campos de auth password/OAuth Apple + bloqueo brute-force.
            // ─────────────────────────────────────────────────────────────────────────
            migrationBuilder.AddColumn<string>(
                name: "AppleId",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailVerified",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerifiedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PasswordChangedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailedLoginAttempts",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedUntil",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            // ─────────────────────────────────────────────────────────────────────────
            // 2) Users: índices para lookup rápido en login/oauth.
            //    Email NO es unique (OAuth-only + password coexisten + soft delete).
            //    Uniqueness lógico se valida en service layer.
            // ─────────────────────────────────────────────────────────────────────────
            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Users_GoogleId",
                table: "Users",
                column: "GoogleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_AppleId",
                table: "Users",
                column: "AppleId");

            // ─────────────────────────────────────────────────────────────────────────
            // 3) EmailVerificationCodes: tabla de OTPs.
            // ─────────────────────────────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "EmailVerificationCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Salt = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RequestIp = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    VerifyIp = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    VerificationToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailVerificationCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailVerificationCodes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            // 🔎 Lookup principal en verify (último OTP activo por email+purpose).
            migrationBuilder.CreateIndex(
                name: "IX_EmailVerificationCodes_Email_Purpose",
                table: "EmailVerificationCodes",
                columns: new[] { "Email", "Purpose" });

            // 🔑 Token opaco devuelto al cliente. UNIQUE + O(log n) lookup en verify-email.
            migrationBuilder.CreateIndex(
                name: "IX_EmailVerificationCodes_VerificationToken",
                table: "EmailVerificationCodes",
                column: "VerificationToken",
                unique: true);

            // 🧹 Cleanup job: filtra códigos expirados / no consumidos.
            migrationBuilder.CreateIndex(
                name: "IX_EmailVerificationCodes_ExpiresAt",
                table: "EmailVerificationCodes",
                column: "ExpiresAt");

            // 🛡️ Rate-limit por (email, purpose) en ventana corta.
            migrationBuilder.CreateIndex(
                name: "IX_EmailVerificationCodes_Email_Purpose_CreatedAt",
                table: "EmailVerificationCodes",
                columns: new[] { "Email", "Purpose", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerificationCodes_UserId",
                table: "EmailVerificationCodes",
                column: "UserId");

            // ─────────────────────────────────────────────────────────────────────────
            // 4) Backfill: usuarios OAuth existentes ya tienen email "verificado" por Google.
            //    Sin esto, después del deploy nadie podría loguearse (todos quedarían pendientes
            //    de verificación). Solo usuarios con GoogleId que aún no estén verificados.
            // ─────────────────────────────────────────────────────────────────────────
            migrationBuilder.Sql(@"
                UPDATE ""Users""
                SET ""EmailVerified"" = TRUE,
                    ""EmailVerifiedAt"" = COALESCE(""CreatedAt"", CURRENT_TIMESTAMP)
                WHERE ""GoogleId"" IS NOT NULL
                  AND ""EmailVerified"" = FALSE
                  AND ""IsDeleted"" = FALSE;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailVerificationCodes");

            migrationBuilder.DropIndex(
                name: "IX_Users_AppleId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_GoogleId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FailedLoginAttempts",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PasswordChangedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "EmailVerifiedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "EmailVerified",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AppleId",
                table: "Users");
        }
    }
}
