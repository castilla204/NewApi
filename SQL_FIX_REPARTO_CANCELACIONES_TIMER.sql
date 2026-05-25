-- ============================================================================
-- FIX: reparto de dinero en cancelaciones por TIMER (config ligada a AppointmentStatus)
-- ============================================================================
-- BUG (confirmado 2026-05-25): los timers de cita llaman a
-- ProcessMoneyDistributionAsync con el valor *AppointmentStatus*
-- (p.ej. 'appointment_cancelled_by_client_no_proposal'). Las StatusConfigurations
-- solo existían ligadas a filas SearchHireStatus, así que la 1ª búsqueda devolvía
-- null y caía al fallback GetTargetSearchHireStatusAsync → GetDefaultMapping.
-- Como el enum C# SearchHireStatus NO tiene los estados granulares
-- (cancelled_by_client_no_proposal, cancelled_by_expert_no_report, ...), el fallback
-- resolvía a 'cancelled' (100/0/0) o 'completed' (0/95/5). Resultado real:
--   * cancelled_by_client_no_proposal: aplicaba 100/0/0 en vez de 0/100/0
--       → el EXPERTO cobraba 0 y el cliente (que no propuso) recibía el 100%.
--   * cancelled_by_expert_no_report:   aplicaba 100/0/0 en vez de 95/0/5
--       → la PLATAFORMA perdía su 5% y el cliente recibía un 5% de más.
--   * cancelled_by_expert_no_response (100/0/0) y completed_without_approval (0/95/5)
--       eran correctos por coincidencia del fallback; aquí se hacen EXPLÍCITOS.
--
-- FIX: crear StatusConfigurations ligadas DIRECTAMENTE a las filas AppointmentStatus
-- con el split correcto, para que la 1ª búsqueda (GetMoneyDistributionAsync por
-- StatusValue) acierte y nunca dependa del fallback. NO cambia el estado final del
-- hire (sigue siendo 'cancelled'/'completed'): solo corrige el reparto de dinero.
-- Política de % confirmada por el usuario (misma que SEED_ESTADOS_COMPLETO.sql).
-- IDEMPOTENTE: NOT EXISTS.
-- ============================================================================

INSERT INTO "StatusConfigurations" ("StatusId","CategoryId","ServiceTypeCategoryId","ClientPercentage","ExpertPercentage","PlatformPercentage","IsActive","CreatedAt","UpdatedAt")
SELECT s."Id", NULL, NULL, v."cp", v."ep", v."pp", true, now(), now()
FROM (VALUES
  ('appointment_cancelled_by_client_no_proposal',   0, 100, 0),  -- culpa del cliente -> experto 100%
  ('appointment_cancelled_by_expert_no_response', 100,   0, 0),  -- culpa del experto -> cliente 100%
  ('appointment_cancelled_by_no_report',           95,   0, 5),  -- culpa del experto -> cliente 95% / plataforma 5%
  ('appointment_completed_without_client_approval', 0,  95, 5)   -- auto-aprobado a favor del experto -> experto 95% / plataforma 5%
) AS v("sv","cp","ep","pp")
JOIN "SystemStatuses" s ON s."StatusValue"=v."sv" AND s."StatusType"='AppointmentStatus'
WHERE NOT EXISTS (
  SELECT 1 FROM "StatusConfigurations" sc
  WHERE sc."StatusId"=s."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL
);

-- Verificación
SELECT ss."StatusValue", sc."ClientPercentage", sc."ExpertPercentage", sc."PlatformPercentage"
FROM "StatusConfigurations" sc
JOIN "SystemStatuses" ss ON sc."StatusId"=ss."Id"
WHERE ss."StatusType"='AppointmentStatus'
  AND ss."StatusValue" IN (
    'appointment_cancelled_by_client_no_proposal',
    'appointment_cancelled_by_expert_no_response',
    'appointment_cancelled_by_no_report',
    'appointment_completed_without_client_approval')
  AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL
ORDER BY ss."StatusValue";
