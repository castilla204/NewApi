-- ============================================================================
-- C1: Índices únicos PARCIALES en FinancialTransactions (ledger) — anti doble-pago
-- ============================================================================
-- Última línea de defensa además de las claves de idempotencia de Stripe + guardas
-- en código. Idempotente (IF NOT EXISTS). Seguro: la BD viva tiene 0 filas
-- transaccionales (no hay datos que violen los únicos). Coincide con OnModelCreating
-- en AppDbContext.cs.
--   (1) Cada refund de Stripe se registra UNA sola vez.
--   (2) Por cada transfer: como máximo un Payout y una TransferReversal.
--   (3) Un ServicePayment por contratación (anti doble-cobro de un mismo hire).
-- ============================================================================

CREATE UNIQUE INDEX IF NOT EXISTS "IX_FT_StripeRefundId_uq"
  ON "FinancialTransactions" ("StripeRefundId")
  WHERE "StripeRefundId" IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_FT_StripeTransferId_Type_uq"
  ON "FinancialTransactions" ("StripeTransferId", "TransactionType")
  WHERE "StripeTransferId" IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_FT_ServicePayment_perHire_uq"
  ON "FinancialTransactions" ("RelatedEntityType", "RelatedEntityId")
  WHERE "TransactionType" = 'ServicePayment';
