-- 🗓️ Aplica a mano el esquema del rediseño de citas (Fases A + D) cuando MigrateAsync
-- no lo auto-aplica por el drift de __EFMigrationsHistory (aborta el lote en el primer 42P07).
-- Todo idempotente: seguro de re-ejecutar.
--   docker exec -i inspecciono-dev-db psql -U dev -d inspecciono_dev < apply_dev_schema.sql

-- ── Fase A: tabla de reglas + columnas UTC en Appointments + constraints ──
CREATE TABLE IF NOT EXISTS "ExpertAvailabilityRules" (
    "Id" serial PRIMARY KEY,
    "ExpertId" integer NOT NULL,
    "DayOfWeek" integer NOT NULL,
    "StartLocal" interval NOT NULL,
    "EndLocal" interval NOT NULL,
    "Timezone" varchar(64) NULL,
    "EffectiveFrom" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "EffectiveTo" timestamptz NULL,
    "IsActive" boolean NOT NULL DEFAULT true,
    "CreatedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT "FK_ExpertAvailabilityRules_ExpertProfiles_ExpertId"
        FOREIGN KEY ("ExpertId") REFERENCES "ExpertProfiles" ("Id") ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS "IX_ExpertAvailabilityRules_ExpertId_DayOfWeek"
    ON "ExpertAvailabilityRules" ("ExpertId", "DayOfWeek");

ALTER TABLE "Appointments" ADD COLUMN IF NOT EXISTS "ExpertId" integer NULL;
ALTER TABLE "Appointments" ADD COLUMN IF NOT EXISTS "StartsAtUtc" timestamptz NULL;
ALTER TABLE "Appointments" ADD COLUMN IF NOT EXISTS "EndsAtUtc" timestamptz NULL;
ALTER TABLE "Appointments" ADD COLUMN IF NOT EXISTS "BlocksCalendar" boolean NOT NULL DEFAULT false;
CREATE INDEX IF NOT EXISTS "IX_Appointments_ExpertId_StartsAtUtc"
    ON "Appointments" ("ExpertId", "StartsAtUtc") WHERE "BlocksCalendar" = true;

CREATE EXTENSION IF NOT EXISTS btree_gist;
ALTER TABLE "Appointments" DROP CONSTRAINT IF EXISTS ux_expert_no_overlap;
ALTER TABLE "Appointments" ADD CONSTRAINT ux_expert_no_overlap
    EXCLUDE USING gist (
        "ExpertId" WITH =,
        tstzrange("StartsAtUtc", "EndsAtUtc", '[)') WITH &&
    )
    WHERE ("BlocksCalendar" = true AND "ExpertId" IS NOT NULL
           AND "StartsAtUtc" IS NOT NULL AND "EndsAtUtc" IS NOT NULL);
ALTER TABLE "Appointments" DROP CONSTRAINT IF EXISTS ck_appointment_interval_order;
ALTER TABLE "Appointments" ADD CONSTRAINT ck_appointment_interval_order
    CHECK ("StartsAtUtc" IS NULL OR "EndsAtUtc" IS NULL OR "EndsAtUtc" > "StartsAtUtc");

-- ── Fase D: knobs de cancelación + strikes ──
ALTER TABLE "SystemSettings" ADD COLUMN IF NOT EXISTS "CancellationTierHighHours" integer NOT NULL DEFAULT 24;
ALTER TABLE "SystemSettings" ADD COLUMN IF NOT EXISTS "CancellationTierLowHours" integer NOT NULL DEFAULT 6;
ALTER TABLE "SystemSettings" ADD COLUMN IF NOT EXISTS "FreeCancellationsPerParty" integer NOT NULL DEFAULT 0;
ALTER TABLE "SystemSettings" ADD COLUMN IF NOT EXISTS "PenaltyFreeWindowDays" integer NOT NULL DEFAULT 30;
ALTER TABLE "ExpertProfiles" ADD COLUMN IF NOT EXISTS "CancellationStrikes" integer NOT NULL DEFAULT 0;
