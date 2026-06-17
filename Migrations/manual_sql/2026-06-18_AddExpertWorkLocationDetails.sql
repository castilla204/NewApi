-- 🏠 Detalle del punto de trabajo fijo del experto (puerta/piso/observaciones).
-- Idempotente: ADD COLUMN IF NOT EXISTS. Aditivo y nullable → seguro, sin pérdida de datos.
-- Aplica a dev (inspecciono_dev:5434) y prod (Render inspecciono_9l1g).
ALTER TABLE "ExpertProfiles" ADD COLUMN IF NOT EXISTS "WorkLocationDoor" character varying(60) NULL;
ALTER TABLE "ExpertProfiles" ADD COLUMN IF NOT EXISTS "WorkLocationFloor" character varying(40) NULL;
ALTER TABLE "ExpertProfiles" ADD COLUMN IF NOT EXISTS "WorkLocationDetails" character varying(300) NULL;
ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "ExpertWorkLocationDoorSnapshot" text NULL;
ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "ExpertWorkLocationFloorSnapshot" text NULL;
ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "ExpertWorkLocationDetailsSnapshot" text NULL;
