-- Añadir columna RequiresManualReview y CaptureStatus a la tabla SearchHires
-- Aplicar este SQL manualmente en la BD (no usar dotnet ef migrations add por riesgo de drift).

ALTER TABLE "SearchHires"
ADD COLUMN IF NOT EXISTS "RequiresManualReview" boolean NOT NULL DEFAULT false;

ALTER TABLE "SearchHires"
ADD COLUMN IF NOT EXISTS "CaptureStatus" varchar(32) NULL;

-- Índice opcional para acelerar watchdog de capturas pendientes.
CREATE INDEX IF NOT EXISTS "IX_SearchHires_CaptureStatus"
ON "SearchHires" ("CaptureStatus")
WHERE "CaptureStatus" IS NOT NULL;
