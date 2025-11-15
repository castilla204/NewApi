-- ============================================================================
-- SCRIPT PARA CREAR ESTADOS FALTANTES Y CONFIGURACIONES DE PORCENTAJES
-- ============================================================================
-- Este script crea los estados SearchHireStatus que faltan en la BD
-- y sus respectivas configuraciones de porcentajes
-- ============================================================================

-- 1. CREAR ESTADO: cancelled_by_client_no_proposal
-- Cuando el cliente no propone una cita en 24h
INSERT INTO "SystemStatuses" 
    ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsFinalizationStatus", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
VALUES 
    ('SearchHireStatus', 'CancelledByClientNoProposal', 'cancelled_by_client_no_proposal', 
     'Cancelado por Cliente No Propone', 'Cliente no propuso cita en 24h', true, true, 9, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT DO NOTHING;

-- Configuración de porcentajes: Cliente 0%, Experto 100%, Plataforma 0%
INSERT INTO "StatusConfigurations" 
    ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT 
    s."Id",
    NULL,
    NULL,
    0,   -- Cliente: 0% (culpa del cliente)
    100, -- Experto: 100% (recibe todo)
    0,   -- Plataforma: 0%
    true,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'SearchHireStatus' 
AND s."StatusValue" = 'cancelled_by_client_no_proposal'
AND NOT EXISTS (
    SELECT 1 FROM "StatusConfigurations" sc 
    WHERE sc."StatusId" = s."Id" 
    AND sc."CategoryId" IS NULL 
    AND sc."ServiceTypeCategoryId" IS NULL
);

-- 2. CREAR ESTADO: cancelled_by_expert_no_response
-- Cuando el experto no responde a una propuesta en 24h
INSERT INTO "SystemStatuses" 
    ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsFinalizationStatus", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
VALUES 
    ('SearchHireStatus', 'CancelledByExpertNoResponse', 'cancelled_by_expert_no_response', 
     'Cancelado por Experto No Responde', 'Experto no respondió a propuesta en 24h', true, true, 10, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT DO NOTHING;

-- Configuración de porcentajes: Cliente 100%, Experto 0%, Plataforma 0%
INSERT INTO "StatusConfigurations" 
    ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT 
    s."Id",
    NULL,
    NULL,
    100, -- Cliente: 100% (recibe todo, culpa del experto)
    0,   -- Experto: 0% (culpa del experto)
    0,   -- Plataforma: 0%
    true,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'SearchHireStatus' 
AND s."StatusValue" = 'cancelled_by_expert_no_response'
AND NOT EXISTS (
    SELECT 1 FROM "StatusConfigurations" sc 
    WHERE sc."StatusId" = s."Id" 
    AND sc."CategoryId" IS NULL 
    AND sc."ServiceTypeCategoryId" IS NULL
);

-- 3. CREAR ESTADO: cancelled_by_expert_no_report
-- Cuando el experto no envía reporte en 24h
INSERT INTO "SystemStatuses" 
    ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsFinalizationStatus", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
VALUES 
    ('SearchHireStatus', 'CancelledByExpertNoReport', 'cancelled_by_expert_no_report', 
     'Cancelado por Experto No Envía Reporte', 'Experto no envió reporte en 24h', true, true, 11, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT DO NOTHING;

-- Configuración de porcentajes: Cliente 95%, Experto 0%, Plataforma 5%
-- (Mismos porcentajes que appointment_cancelled_by_no_report)
INSERT INTO "StatusConfigurations" 
    ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT 
    s."Id",
    NULL,
    NULL,
    95,  -- Cliente: 95% (similar a appointment_cancelled_by_no_report)
    0,   -- Experto: 0%
    5,   -- Plataforma: 5%
    true,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'SearchHireStatus' 
AND s."StatusValue" = 'cancelled_by_expert_no_report'
AND NOT EXISTS (
    SELECT 1 FROM "StatusConfigurations" sc 
    WHERE sc."StatusId" = s."Id" 
    AND sc."CategoryId" IS NULL 
    AND sc."ServiceTypeCategoryId" IS NULL
);

-- 4. CREAR MAPEO: appointment_cancelled_by_no_report → cancelled_by_expert_no_report
-- (Opcional: El código maneja esto dinámicamente, pero es bueno tenerlo en BD)
INSERT INTO "StatusMappings" ("SourceStatusId", "TargetStatusId", "IsActive", "CreatedAt")
SELECT 
    ss_source."Id",
    ss_target."Id",
    true,
    CURRENT_TIMESTAMP
FROM "SystemStatuses" ss_source
CROSS JOIN "SystemStatuses" ss_target
WHERE ss_source."StatusValue" = 'appointment_cancelled_by_no_report'
AND ss_source."StatusType" = 'AppointmentStatus'
AND ss_target."StatusValue" = 'cancelled_by_expert_no_report'
AND ss_target."StatusType" = 'SearchHireStatus'
AND NOT EXISTS (
    SELECT 1 FROM "StatusMappings" sm
    WHERE sm."SourceStatusId" = ss_source."Id"
    AND sm."TargetStatusId" = ss_target."Id"
);

-- ============================================================================
-- VERIFICACIÓN: Consultar estados creados
-- ============================================================================
-- SELECT "Id", "StatusType", "StatusValue", "DisplayName", "IsFinalizationStatus"
-- FROM "SystemStatuses" 
-- WHERE "StatusType" = 'SearchHireStatus' 
-- AND "StatusValue" IN ('cancelled_by_client_no_proposal', 'cancelled_by_expert_no_response', 'cancelled_by_expert_no_report')
-- ORDER BY "StatusValue";

-- ============================================================================
-- VERIFICACIÓN: Consultar configuraciones de porcentajes
-- ============================================================================
-- SELECT 
--     ss."StatusValue",
--     ss."DisplayName",
--     sc."ClientPercentage",
--     sc."ExpertPercentage",
--     sc."PlatformPercentage"
-- FROM "StatusConfigurations" sc
-- INNER JOIN "SystemStatuses" ss ON sc."StatusId" = ss."Id"
-- WHERE ss."StatusType" = 'SearchHireStatus'
-- AND ss."StatusValue" IN ('cancelled_by_client_no_proposal', 'cancelled_by_expert_no_response', 'cancelled_by_expert_no_report')
-- AND sc."CategoryId" IS NULL
-- AND sc."ServiceTypeCategoryId" IS NULL
-- ORDER BY ss."StatusValue";

