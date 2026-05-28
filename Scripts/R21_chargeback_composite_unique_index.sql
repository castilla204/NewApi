-- R21 FIX: índice único parcial COMPUESTO sobre FinancialTransactions para Chargeback.
--
-- Bug original: HandleChargeDisputeCreated (SubscriptionController:6313-6337) hace
-- check-then-act (AnyAsync + Add + SaveChanges) sin atomicidad. Si dos webhooks distintos
-- llegan en paralelo para el MISMO dispute (eventIds distintos → TryBeginProcessingEventAsync
-- no protege), ambos ven alreadyMarked=false y crean DOS filas Chargeback + DOS clawbacks
-- encolados → doble reversión del transfer al experto.
--
-- El índice único parcial existente (IX_FT_StripePaymentIntent_Chargeback_uq, en
-- AppDbContext línea ~384-388) solo cubre StripePaymentIntentId — insuficiente si dos
-- SearchHires distintos comparten PI (raro pero teórico). Este script crea un índice
-- COMPUESTO que garantiza unicidad sobre (RelatedEntityType, RelatedEntityId, StripePaymentIntentId)
-- filtrado a TransactionType='Chargeback', que es el escenario real del bug.
--
-- Resultado: el segundo INSERT concurrente falla con violación de unique key (sqlstate 23505),
-- la transacción rolea y solo 1 fila Chargeback queda → solo 1 clawback encolado.
--
-- IDEMPOTENTE: usa DO block para chequear existencia antes de crear.

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE indexname = 'IX_FT_Chargeback_HireAndPI_uq'
    ) THEN
        CREATE UNIQUE INDEX "IX_FT_Chargeback_HireAndPI_uq"
            ON "FinancialTransactions" ("RelatedEntityType", "RelatedEntityId", "StripePaymentIntentId")
            WHERE "TransactionType" = 'Chargeback' AND "StripePaymentIntentId" IS NOT NULL;
        RAISE NOTICE 'R21: Composite unique index IX_FT_Chargeback_HireAndPI_uq created.';
    ELSE
        RAISE NOTICE 'R21: Index already exists, skipping.';
    END IF;
END $$;

-- Verificación:
-- SELECT indexname, indexdef FROM pg_indexes WHERE indexname = 'IX_FT_Chargeback_HireAndPI_uq';
