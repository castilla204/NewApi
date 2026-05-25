-- ============================================================================
-- P2-3: Índices únicos parciales de integridad
-- ----------------------------------------------------------------------------
-- Objetivo: defensa en BD frente a doble PaymentIntent / doble Chargeback /
-- reservas duplicadas del mismo slot por experto activo.
--
-- Aplicación: estos índices son idempotentes (IF NOT EXISTS). Si una
-- duplicación previa hace fallar la creación, hay que limpiar antes los
-- duplicados (no se crea automáticamente para no perder datos).
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1) Anti doble PaymentIntent por hire (TransactionType = 'ServicePayment')
-- ---------------------------------------------------------------------------
CREATE UNIQUE INDEX IF NOT EXISTS "IX_FT_StripePaymentIntent_ServicePayment_uq"
    ON "FinancialTransactions" ("StripePaymentIntentId")
    WHERE "StripePaymentIntentId" IS NOT NULL
      AND "TransactionType" = 'ServicePayment';

-- ---------------------------------------------------------------------------
-- 2) Anti doble Chargeback por PaymentIntent
-- ---------------------------------------------------------------------------
CREATE UNIQUE INDEX IF NOT EXISTS "IX_FT_StripePaymentIntent_Chargeback_uq"
    ON "FinancialTransactions" ("StripePaymentIntentId")
    WHERE "StripePaymentIntentId" IS NOT NULL
      AND "TransactionType" = 'Chargeback';

-- ---------------------------------------------------------------------------
-- 3) Anti reservas duplicadas del mismo slot por experto activo
-- ---------------------------------------------------------------------------
-- Postgres NO admite subconsultas en el WHERE de un índice parcial, por lo
-- que no se puede expresar “StatusId IN (SELECT … FROM SystemStatuses …)”
-- como filtro de un UNIQUE INDEX. Como los IDs de SystemStatuses varían por
-- entorno (sembrados dinámicamente), la opción portable es un trigger
-- BEFORE INSERT OR UPDATE que rechace la inserción/actualización si ya
-- existe otro Appointment activo con (ExpertProfileId, ProposedDate,
-- ProposedTime).
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION trg_appointments_unique_active_slot()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_active_status_ids INT[];
    v_conflict_id INT;
    v_expert_profile_id INT;
BEGIN
    -- Resolver el ExpertProfileId del experto del SearchHire asociado a la cita
    SELECT ep."Id"
      INTO v_expert_profile_id
      FROM "SearchHires" sh
      JOIN "ExpertProfiles" ep ON ep."UserId" = sh."ExpertId"
     WHERE sh."Id" = NEW."SearchHireId";

    -- Sin experto resuelto, no hay nada que validar
    IF v_expert_profile_id IS NULL THEN
        RETURN NEW;
    END IF;

    -- Sin slot propuesto no aplica (no se conoce fecha/hora)
    IF NEW."ProposedDate" IS NULL OR NEW."ProposedTime" IS NULL THEN
        RETURN NEW;
    END IF;

    -- Conjunto de estados que consideramos "activos" (cita viva)
    SELECT array_agg("Id")
      INTO v_active_status_ids
      FROM "SystemStatuses"
     WHERE "StatusType" = 'AppointmentStatus'
       AND "StatusValue" IN (
           'awaiting_appointment',
           'appointment_proposed',
           'appointment_confirmed',
           'appointment_awaiting_report',
           'appointment_report_sent'
       );

    -- Si el estado actual no es activo, no validamos colisión
    IF v_active_status_ids IS NULL OR NOT (NEW."StatusId" = ANY(v_active_status_ids)) THEN
        RETURN NEW;
    END IF;

    -- Buscar conflicto: otra cita activa del MISMO experto, mismo slot
    SELECT a."Id"
      INTO v_conflict_id
      FROM "Appointments" a
      JOIN "SearchHires" sh2 ON sh2."Id" = a."SearchHireId"
      JOIN "ExpertProfiles" ep2 ON ep2."UserId" = sh2."ExpertId"
     WHERE ep2."Id" = v_expert_profile_id
       AND a."ProposedDate" = NEW."ProposedDate"
       AND a."ProposedTime" = NEW."ProposedTime"
       AND a."StatusId" = ANY(v_active_status_ids)
       AND a."Id" <> COALESCE(NEW."Id", -1)
     LIMIT 1;

    IF v_conflict_id IS NOT NULL THEN
        RAISE EXCEPTION
            'Duplicate active appointment slot for expert profile % at % %. Conflict with Appointment %.',
            v_expert_profile_id, NEW."ProposedDate", NEW."ProposedTime", v_conflict_id
        USING ERRCODE = 'unique_violation';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_appointments_unique_active_slot ON "Appointments";

CREATE TRIGGER trg_appointments_unique_active_slot
    BEFORE INSERT OR UPDATE OF "ProposedDate", "ProposedTime", "StatusId", "SearchHireId"
    ON "Appointments"
    FOR EACH ROW
    EXECUTE FUNCTION trg_appointments_unique_active_slot();

-- ============================================================================
-- Notas:
-- - Los índices 1) y 2) también se declaran en AppDbContext.OnModelCreating
--   con HasIndex(...).IsUnique().HasFilter(...) para que EF Core los conozca
--   en migraciones futuras.
-- - El trigger 3) se mantiene SOLO en SQL (EF Core no modela triggers de
--   integridad como éste de forma portable).
-- ============================================================================
