using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// 🗓️ Reserva atómica de citas (Fase A): tabla normalizada ExpertAvailabilityRule
    /// (horas por día + turnos partidos) y columnas UTC en Appointments
    /// (ExpertId denormalizado, StartsAtUtc/EndsAtUtc, BlocksCalendar).
    ///
    /// SQL crudo idempotente (IF NOT EXISTS) para tolerar el drift de migraciones de prod
    /// (__EFMigrationsHistory llega solo a 20260530; esquema aplicado a mano). Si no
    /// auto-aplica en prod, ejecutar el mismo SQL a mano. EnsureCreated en tests ya crea
    /// estas estructuras desde el modelo, así que esta migración es no-op ahí.
    ///
    /// NO incluye las columnas Users.ProfilePictureUrl/ObjectName que el scaffolder añadió
    /// por drift de snapshot: pertenecen a otro feature y ya existen en la BD.
    /// </summary>
    public partial class AddExpertAvailabilityRuleAndAppointmentSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""ExpertAvailabilityRules"" (
                    ""Id"" serial PRIMARY KEY,
                    ""ExpertId"" integer NOT NULL,
                    ""DayOfWeek"" integer NOT NULL,
                    ""StartLocal"" interval NOT NULL,
                    ""EndLocal"" interval NOT NULL,
                    ""Timezone"" varchar(64) NULL,
                    ""EffectiveFrom"" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    ""EffectiveTo"" timestamptz NULL,
                    ""IsActive"" boolean NOT NULL DEFAULT true,
                    ""CreatedAt"" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    ""UpdatedAt"" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    CONSTRAINT ""FK_ExpertAvailabilityRules_ExpertProfiles_ExpertId""
                        FOREIGN KEY (""ExpertId"") REFERENCES ""ExpertProfiles"" (""Id"") ON DELETE CASCADE
                );
            ");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ExpertAvailabilityRules_ExpertId_DayOfWeek"" ON ""ExpertAvailabilityRules"" (""ExpertId"", ""DayOfWeek"");");

            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" ADD COLUMN IF NOT EXISTS ""ExpertId"" integer NULL;");
            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" ADD COLUMN IF NOT EXISTS ""StartsAtUtc"" timestamptz NULL;");
            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" ADD COLUMN IF NOT EXISTS ""EndsAtUtc"" timestamptz NULL;");
            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" ADD COLUMN IF NOT EXISTS ""BlocksCalendar"" boolean NOT NULL DEFAULT false;");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Appointments_ExpertId_StartsAtUtc"" ON ""Appointments"" (""ExpertId"", ""StartsAtUtc"") WHERE ""BlocksCalendar"" = true;");

            // Exclusion constraint GiST: impide físicamente dos citas vivas solapadas del mismo experto.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" DROP CONSTRAINT IF EXISTS ux_expert_no_overlap;");
            migrationBuilder.Sql(@"
                ALTER TABLE ""Appointments""
                ADD CONSTRAINT ux_expert_no_overlap
                EXCLUDE USING gist (
                    ""ExpertId"" WITH =,
                    tstzrange(""StartsAtUtc"", ""EndsAtUtc"", '[)') WITH &&
                )
                WHERE (""BlocksCalendar"" = true AND ""ExpertId"" IS NOT NULL
                       AND ""StartsAtUtc"" IS NOT NULL AND ""EndsAtUtc"" IS NOT NULL);
            ");

            // Integridad del intervalo: fin > inicio (permite NULLs de filas legacy).
            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" DROP CONSTRAINT IF EXISTS ck_appointment_interval_order;");
            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" ADD CONSTRAINT ck_appointment_interval_order CHECK (""StartsAtUtc"" IS NULL OR ""EndsAtUtc"" IS NULL OR ""EndsAtUtc"" > ""StartsAtUtc"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" DROP CONSTRAINT IF EXISTS ck_appointment_interval_order;");
            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" DROP CONSTRAINT IF EXISTS ux_expert_no_overlap;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Appointments_ExpertId_StartsAtUtc"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" DROP COLUMN IF EXISTS ""BlocksCalendar"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" DROP COLUMN IF EXISTS ""EndsAtUtc"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" DROP COLUMN IF EXISTS ""StartsAtUtc"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Appointments"" DROP COLUMN IF EXISTS ""ExpertId"";");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""ExpertAvailabilityRules"";");
        }
    }
}
