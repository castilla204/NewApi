-- P3-10 (versión mínima / DEFERRED) — Añadir columna AmountCents en FinancialTransactions.
-- Importes en céntimos (entero) para eliminar pérdidas por redondeo decimal a futuro.
-- Esta migración solo añade la columna y backfillea desde Amount. NO elimina Amount.
-- La actualización paralela de todos los escritores está DEFERRED (ver RESUMEN_P3_PENDIENTES.md).
--
-- Idempotente: usa IF NOT EXISTS.

ALTER TABLE "FinancialTransactions"
    ADD COLUMN IF NOT EXISTS "AmountCents" bigint NOT NULL DEFAULT 0;

-- Backfill de céntimos desde Amount (decimal EUR). Round half-away-from-zero.
UPDATE "FinancialTransactions"
SET "AmountCents" = (ROUND("Amount" * 100))::bigint
WHERE "AmountCents" = 0 AND "Amount" <> 0;
