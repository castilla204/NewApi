-- N16 FIX: garantizar que TODOS los SearchHireStatus terminales tienen una config global
-- (CategoryId IS NULL AND ServiceTypeCategoryId IS NULL) para que la cascada de búsqueda en
-- SystemStatusService NUNCA retorne NULL — lo que dejaría un hire atascado (estado terminal
-- cambiado, pero el dinero nunca se procesa porque ProcessMoneyDistribution retorna false).
--
-- El seeder principal (SEED_ESTADOS_COMPLETO.sql) cubre 7 de los 11+ estados terminales.
-- Faltan: 'disputed', 'transfer_failed', y cualquier nuevo estado que se añada después.
--
-- Este script inserta config global "neutra" (PlatformPercentage=100, todo a plataforma)
-- para los estados terminales sin cobertura. Es un FALLBACK SEGURO: el admin debe revisar
-- estos hires manualmente, no se distribuye dinero automáticamente al lado incorrecto.
--
-- IDEMPOTENTE: solo inserta si no existe la combinación.

DO $$
DECLARE
    v_status_id INT;
    v_status_value TEXT;
    v_status_values TEXT[] := ARRAY[
        'disputed',           -- en disputa, money distribution NO debería ejecutarse
        'transfer_failed',    -- transfer al experto falló, requires admin review
        'refunded',           -- (si existe en algún seeder futuro)
        'expired'             -- (idem)
    ];
BEGIN
    FOREACH v_status_value IN ARRAY v_status_values
    LOOP
        SELECT "Id" INTO v_status_id
        FROM "SystemStatuses"
        WHERE "StatusType" = 'SearchHireStatus'
          AND "StatusValue" = v_status_value
        LIMIT 1;

        IF v_status_id IS NOT NULL THEN
            IF NOT EXISTS (
                SELECT 1 FROM "StatusConfigurations"
                WHERE "StatusId" = v_status_id
                  AND "CategoryId" IS NULL
                  AND "ServiceTypeCategoryId" IS NULL
            ) THEN
                INSERT INTO "StatusConfigurations"
                    ("StatusId", "CategoryId", "ServiceTypeCategoryId",
                     "ClientPercentage", "ExpertPercentage", "PlatformPercentage",
                     "Description", "CreatedAt", "UpdatedAt")
                VALUES
                    (v_status_id, NULL, NULL,
                     0, 0, 100,
                     'N16: fallback global neutro — requiere revisión admin',
                     NOW(), NOW());
                RAISE NOTICE 'N16: inserted global config for SearchHireStatus.%', v_status_value;
            ELSE
                RAISE NOTICE 'N16: global config already exists for SearchHireStatus.%, skipping', v_status_value;
            END IF;
        ELSE
            RAISE NOTICE 'N16: SearchHireStatus.% not found in SystemStatuses, skipping', v_status_value;
        END IF;
    END LOOP;
END $$;

-- Verificación:
-- SELECT s."StatusValue", c."ClientPercentage", c."ExpertPercentage", c."PlatformPercentage"
-- FROM "SystemStatuses" s
-- LEFT JOIN "StatusConfigurations" c ON c."StatusId" = s."Id"
--   AND c."CategoryId" IS NULL AND c."ServiceTypeCategoryId" IS NULL
-- WHERE s."StatusType" = 'SearchHireStatus'
-- ORDER BY s."StatusValue";
