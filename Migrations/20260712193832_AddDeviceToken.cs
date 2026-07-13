using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 🛡️ IDEMPOTENTE (drift PROD): CREATE TABLE / INDEX IF NOT EXISTS para que un re-run
            // o un apply parcial no rompa MigrateAsync. Esquema idéntico al que generaría EF.
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""DeviceTokens"" (
                    ""Id"" uuid NOT NULL,
                    ""UserId"" integer NOT NULL,
                    ""Token"" text NOT NULL,
                    ""Platform"" text NOT NULL,
                    ""CreatedAt"" timestamp with time zone NOT NULL,
                    ""LastSeenAt"" timestamp with time zone NOT NULL,
                    CONSTRAINT ""PK_DeviceTokens"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_DeviceTokens_Users_UserId"" FOREIGN KEY (""UserId"")
                        REFERENCES ""Users"" (""Id"") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_DeviceTokens_Token"" ON ""DeviceTokens"" (""Token"");
                CREATE INDEX IF NOT EXISTS ""IX_DeviceTokens_UserId"" ON ""DeviceTokens"" (""UserId"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceTokens");
        }
    }
}
