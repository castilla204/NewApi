-- P3-1 (versión mínima): añade SearchHire.RefundFailedAt
-- Marca temporal del momento en que la Fase 3 de RefundService agotó todos los reintentos
-- Hangfire sin lograr distribuir el dinero (transfer/refund). El digest diario
-- refund-failed-digest lista los hires con esta columna != NULL en las últimas 24h.
-- El hire sigue en su estado canónico (Completed / DisputeResolvedClient); la reconciliación
-- la hace soporte manualmente y, al resolverla, pone RefundFailedAt a NULL.

ALTER TABLE "SearchHires"
    ADD COLUMN IF NOT EXISTS "RefundFailedAt" timestamp with time zone NULL;

-- Índice parcial para acelerar el digest (solo hires marcados, cardinalidad muy baja).
CREATE INDEX IF NOT EXISTS "IX_SearchHires_RefundFailedAt"
    ON "SearchHires" ("RefundFailedAt")
    WHERE "RefundFailedAt" IS NOT NULL;
