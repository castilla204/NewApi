-- Script para probar el timer de citas
-- Ejecutar en pgAdmin conectado a tu base de datos

-- 1. Ver citas confirmadas actuales
SELECT 'Citas confirmadas actuales:' as info;
SELECT 
    a."Id" as appointment_id,
    a."ProposedDate",
    a."ProposedTime",
    s."StatusValue",
    a."ProposedDate" + a."ProposedTime" as appointment_datetime,
    NOW() as current_time,
    (a."ProposedDate" + a."ProposedTime" + INTERVAL '3 hours') as should_change_at,
    CASE 
        WHEN (a."ProposedDate" + a."ProposedTime" + INTERVAL '3 hours') <= NOW() 
        THEN 'DEBERÍA CAMBIAR A awaiting_report' 
        ELSE 'AÚN NO DEBE CAMBIAR' 
    END as status_check
FROM "Appointments" a
JOIN "SystemStatuses" s ON a."StatusId" = s."Id"
WHERE s."StatusValue" = 'appointment_confirmed'
ORDER BY a."Id" DESC
LIMIT 5;

-- 2. Adelantar una cita para testing (cambia el ID por uno real)
-- UPDATE "Appointments" 
-- SET "ProposedDate" = NOW() - INTERVAL '4 hours',
--     "UpdatedAt" = NOW()
-- WHERE "Id" = 1; -- CAMBIAR POR UN ID REAL

-- 3. Verificar el cambio
-- SELECT 'Cita modificada:' as info;
-- SELECT 
--     a."Id" as appointment_id,
--     a."ProposedDate",
--     a."ProposedTime",
--     s."StatusValue",
--     a."ProposedDate" + a."ProposedTime" as appointment_datetime,
--     NOW() as current_time,
--     (a."ProposedDate" + a."ProposedTime" + INTERVAL '3 hours') as should_change_at,
--     CASE 
--         WHEN (a."ProposedDate" + a."ProposedTime" + INTERVAL '3 hours') <= NOW() 
--         THEN 'DEBERÍA CAMBIAR A awaiting_report' 
--         ELSE 'AÚN NO DEBE CAMBIAR' 
--     END as status_check
-- FROM "Appointments" a
-- JOIN "SystemStatuses" s ON a."StatusId" = s."Id"
-- WHERE a."Id" = 1; -- CAMBIAR POR EL MISMO ID DE ARRIBA

-- 4. Verificar estados disponibles
SELECT 'Estados de citas disponibles:' as info;
SELECT "Id", "StatusValue", "StatusName"
FROM "SystemStatuses"
WHERE "StatusType" = 'AppointmentStatus'
ORDER BY "Id";








