-- ============================================================================
-- StatusConfigurations FALTANTES para los 5 AppointmentStatus nuevos
-- ============================================================================
-- Estos repartos describen cómo se distribuye el dinero retenido en escrow
-- cuando la cita finaliza por uno de estos motivos. Sin estas filas, los
-- timers Hangfire caen al fallback genérico (cancelled_by_no_response, 100/0/0)
-- lo cual es INCORRECTO en algunos casos.
--
-- Reparto (Client / Expert / Platform, suma = 100):
--   appointment_cancelled_by_client_no_proposal       ->  0 / 100 / 0
--   appointment_cancelled_by_expert_no_response       -> 100 /   0 / 0
--   appointment_cancelled_by_expert_second            -> 100 /   0 / 0
--   appointment_cancelled_by_no_report                ->  95 /   0 / 5
--   appointment_completed_without_client_approval     ->   0 /  95 / 5
--
-- Tabla "StatusConfigurations" (ver DataLayer/Models/PostGresModels/StatusConfiguration.cs):
--   Id (identity), StatusId (FK SystemStatuses.Id), CategoryId NULL, ServiceTypeCategoryId NULL,
--   ClientPercentage, ExpertPercentage, PlatformPercentage, IsActive, CreatedAt, UpdatedAt
--
-- Idempotente: usa NOT EXISTS para no duplicar.
-- Requiere que SQL_CREAR_APPOINTMENT_STATUS_FALTANTES.sql se haya ejecutado antes.
-- ============================================================================

-- 1) appointment_cancelled_by_client_no_proposal -> 0 / 100 / 0
INSERT INTO "StatusConfigurations"
    ("StatusId","CategoryId","ServiceTypeCategoryId","ClientPercentage","ExpertPercentage","PlatformPercentage","IsActive","CreatedAt","UpdatedAt")
SELECT ss."Id", NULL, NULL, 0, 100, 0, true, now(), now()
FROM "SystemStatuses" ss
WHERE ss."StatusValue" = 'appointment_cancelled_by_client_no_proposal'
  AND ss."StatusType"  = 'AppointmentStatus'
  AND NOT EXISTS (
        SELECT 1 FROM "StatusConfigurations" sc
        WHERE sc."StatusId" = ss."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL
  );

-- 2) appointment_cancelled_by_expert_no_response -> 100 / 0 / 0
INSERT INTO "StatusConfigurations"
    ("StatusId","CategoryId","ServiceTypeCategoryId","ClientPercentage","ExpertPercentage","PlatformPercentage","IsActive","CreatedAt","UpdatedAt")
SELECT ss."Id", NULL, NULL, 100, 0, 0, true, now(), now()
FROM "SystemStatuses" ss
WHERE ss."StatusValue" = 'appointment_cancelled_by_expert_no_response'
  AND ss."StatusType"  = 'AppointmentStatus'
  AND NOT EXISTS (
        SELECT 1 FROM "StatusConfigurations" sc
        WHERE sc."StatusId" = ss."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL
  );

-- 3) appointment_cancelled_by_expert_second -> 100 / 0 / 0
INSERT INTO "StatusConfigurations"
    ("StatusId","CategoryId","ServiceTypeCategoryId","ClientPercentage","ExpertPercentage","PlatformPercentage","IsActive","CreatedAt","UpdatedAt")
SELECT ss."Id", NULL, NULL, 100, 0, 0, true, now(), now()
FROM "SystemStatuses" ss
WHERE ss."StatusValue" = 'appointment_cancelled_by_expert_second'
  AND ss."StatusType"  = 'AppointmentStatus'
  AND NOT EXISTS (
        SELECT 1 FROM "StatusConfigurations" sc
        WHERE sc."StatusId" = ss."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL
  );

-- 4) appointment_cancelled_by_no_report -> 95 / 0 / 5
INSERT INTO "StatusConfigurations"
    ("StatusId","CategoryId","ServiceTypeCategoryId","ClientPercentage","ExpertPercentage","PlatformPercentage","IsActive","CreatedAt","UpdatedAt")
SELECT ss."Id", NULL, NULL, 95, 0, 5, true, now(), now()
FROM "SystemStatuses" ss
WHERE ss."StatusValue" = 'appointment_cancelled_by_no_report'
  AND ss."StatusType"  = 'AppointmentStatus'
  AND NOT EXISTS (
        SELECT 1 FROM "StatusConfigurations" sc
        WHERE sc."StatusId" = ss."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL
  );

-- 5) appointment_completed_without_client_approval -> 0 / 95 / 5
INSERT INTO "StatusConfigurations"
    ("StatusId","CategoryId","ServiceTypeCategoryId","ClientPercentage","ExpertPercentage","PlatformPercentage","IsActive","CreatedAt","UpdatedAt")
SELECT ss."Id", NULL, NULL, 0, 95, 5, true, now(), now()
FROM "SystemStatuses" ss
WHERE ss."StatusValue" = 'appointment_completed_without_client_approval'
  AND ss."StatusType"  = 'AppointmentStatus'
  AND NOT EXISTS (
        SELECT 1 FROM "StatusConfigurations" sc
        WHERE sc."StatusId" = ss."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL
  );

-- ============================================================================
-- VERIFICACIÓN: listar las 5 configs recién creadas (suma debe ser 100)
-- ============================================================================
SELECT  ss."StatusValue",
        sc."ClientPercentage"   AS "Client",
        sc."ExpertPercentage"   AS "Expert",
        sc."PlatformPercentage" AS "Platform",
        (sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") AS "Total",
        sc."IsActive",
        sc."CreatedAt"
FROM "StatusConfigurations" sc
JOIN "SystemStatuses" ss ON ss."Id" = sc."StatusId"
WHERE ss."StatusType" = 'AppointmentStatus'
  AND ss."StatusValue" IN (
        'appointment_cancelled_by_client_no_proposal',
        'appointment_cancelled_by_expert_no_response',
        'appointment_cancelled_by_expert_second',
        'appointment_cancelled_by_no_report',
        'appointment_completed_without_client_approval'
  )
ORDER BY ss."StatusValue";
