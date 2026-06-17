using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// 🗓️ Fase D: knobs de cancelación escalonada (SystemSettings) + contador de strikes del experto.
    /// SQL crudo idempotente con los DEFAULTS reales (la fila singleton de SystemSettings ya existe en
    /// prod → debe heredar 24/6/0/30, no 0). EnsureCreated en tests ya crea las columnas desde el modelo.
    /// </summary>
    public partial class AddCancellationSettingsAndStrikes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""SystemSettings"" ADD COLUMN IF NOT EXISTS ""CancellationTierHighHours"" integer NOT NULL DEFAULT 24;");
            migrationBuilder.Sql(@"ALTER TABLE ""SystemSettings"" ADD COLUMN IF NOT EXISTS ""CancellationTierLowHours"" integer NOT NULL DEFAULT 6;");
            migrationBuilder.Sql(@"ALTER TABLE ""SystemSettings"" ADD COLUMN IF NOT EXISTS ""FreeCancellationsPerParty"" integer NOT NULL DEFAULT 0;");
            migrationBuilder.Sql(@"ALTER TABLE ""SystemSettings"" ADD COLUMN IF NOT EXISTS ""PenaltyFreeWindowDays"" integer NOT NULL DEFAULT 30;");
            migrationBuilder.Sql(@"ALTER TABLE ""ExpertProfiles"" ADD COLUMN IF NOT EXISTS ""CancellationStrikes"" integer NOT NULL DEFAULT 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""ExpertProfiles"" DROP COLUMN IF EXISTS ""CancellationStrikes"";");
            migrationBuilder.Sql(@"ALTER TABLE ""SystemSettings"" DROP COLUMN IF EXISTS ""PenaltyFreeWindowDays"";");
            migrationBuilder.Sql(@"ALTER TABLE ""SystemSettings"" DROP COLUMN IF EXISTS ""FreeCancellationsPerParty"";");
            migrationBuilder.Sql(@"ALTER TABLE ""SystemSettings"" DROP COLUMN IF EXISTS ""CancellationTierLowHours"";");
            migrationBuilder.Sql(@"ALTER TABLE ""SystemSettings"" DROP COLUMN IF EXISTS ""CancellationTierHighHours"";");
        }
    }
}
