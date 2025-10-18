-- 🧪 SCRIPT DE PRUEBA: Timer de Reporte del Experto
-- Este script simula el flujo completo del timer de reporte del experto

-- 1. 📋 Verificar que el nuevo estado existe
SELECT 
    "StatusType",
    "StatusValue", 
    "DisplayName",
    "Description"
FROM "SystemStatuses" 
WHERE "StatusValue" = 'appointment_cancelled_by_no_report';

-- 2. 🔍 Buscar una cita en estado awaiting_report para probar
SELECT 
    a."Id" as appointment_id,
    a."ProposedDate",
    a."ProposedTime",
    s."StatusValue" as current_status,
    sh."Id" as searchhire_id,
    sh."Status" as searchhire_status
FROM "Appointments" a
JOIN "SystemStatuses" s ON a."StatusId" = s."Id"
JOIN "SearchHires" sh ON a."SearchHireId" = sh."Id"
WHERE s."StatusValue" = 'appointment_awaiting_report'
LIMIT 1;

-- 3. ⏰ Verificar si hay timers de expert_report activos
SELECT 
    at."Id" as timer_id,
    at."AppointmentId",
    at."TimerType",
    at."StartTime",
    at."EndTime",
    at."IsExpired",
    at."ExpiredAt"
FROM "AppointmentTimers" at
WHERE at."TimerType" = 'expert_report'
AND at."IsExpired" = false
ORDER BY at."CreatedAt" DESC
LIMIT 5;

-- 4. 🎯 SIMULACIÓN: Crear una cita de prueba en awaiting_report
-- (Solo ejecutar si no hay citas reales para probar)

-- Paso 4a: Crear una cita de prueba
INSERT INTO "Appointments" (
    "SearchHireId", 
    "StatusId", 
    "ProposedDate", 
    "ProposedTime", 
    "Location", 
    "CreatedAt", 
    "UpdatedAt"
) VALUES (
    1, -- Cambiar por un SearchHireId real
    (SELECT "Id" FROM "SystemStatuses" WHERE "StatusValue" = 'appointment_awaiting_report'),
    CURRENT_DATE - INTERVAL '1 day', -- Ayer
    '10:00:00',
    'Ubicación de prueba',
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
) RETURNING "Id";

-- Paso 4b: Crear timer de expert_report que expire en 1 hora (para prueba rápida)
INSERT INTO "AppointmentTimers" (
    "AppointmentId",
    "TimerType", 
    "StartTime",
    "EndTime", 
    "IsExpired",
    "CreatedAt"
) VALUES (
    (SELECT "Id" FROM "Appointments" WHERE "Location" = 'Ubicación de prueba' ORDER BY "Id" DESC LIMIT 1),
    'expert_report',
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP + INTERVAL '1 hour', -- Expira en 1 hora para prueba rápida
    false,
    CURRENT_TIMESTAMP
);

-- 5. 📊 Verificar el estado actual
SELECT 
    'Citas en awaiting_report' as metric,
    COUNT(*) as count
FROM "Appointments" a
JOIN "SystemStatuses" s ON a."StatusId" = s."Id"
WHERE s."StatusValue" = 'appointment_awaiting_report'

UNION ALL

SELECT 
    'Timers expert_report activos' as metric,
    COUNT(*) as count
FROM "AppointmentTimers" 
WHERE "TimerType" = 'expert_report' 
AND "IsExpired" = false

UNION ALL

SELECT 
    'Timers expert_report expirados' as metric,
    COUNT(*) as count
FROM "AppointmentTimers" 
WHERE "TimerType" = 'expert_report' 
AND "IsExpired" = true;

-- 6. 🧹 LIMPIEZA: Eliminar datos de prueba (ejecutar al final)
-- DELETE FROM "AppointmentTimers" WHERE "AppointmentId" IN (
--     SELECT "Id" FROM "Appointments" WHERE "Location" = 'Ubicación de prueba'
-- );
-- DELETE FROM "Appointments" WHERE "Location" = 'Ubicación de prueba';









