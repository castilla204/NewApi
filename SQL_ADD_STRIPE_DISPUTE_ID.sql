-- Añadir columna StripeDisputeId a la tabla Disputes para permitir reenvío de evidencia a Stripe
-- Aplicar este SQL manualmente en la BD (no usar dotnet ef migrations add por riesgo de drift).

ALTER TABLE "Disputes"
ADD COLUMN IF NOT EXISTS "StripeDisputeId" varchar(255) NULL;

-- Índice opcional para acelerar búsquedas por StripeDisputeId desde webhooks.
CREATE INDEX IF NOT EXISTS "IX_Disputes_StripeDisputeId"
ON "Disputes" ("StripeDisputeId")
WHERE "StripeDisputeId" IS NOT NULL;
