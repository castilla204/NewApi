-- Script para eliminar todas las contrataciones activas de dcastillaa@gmail.com
-- Ejecutar directamente en Render PostgreSQL usando psql o cualquier cliente PostgreSQL

-- Primero, eliminar AppointmentTimers relacionados
DELETE FROM "AppointmentTimers" 
WHERE "AppointmentId" IN (
    SELECT "Id" FROM "Appointments" 
    WHERE "SearchHireId" IN (
        SELECT "Id" FROM "SearchHires" 
        WHERE "ClientId" = 1 
        AND "StatusId" IN (
            SELECT "Id" FROM "SystemStatuses" 
            WHERE "StatusType" = 'SearchHireStatus' 
            AND "IsFinalizationStatus" = false
        )
    )
);

-- Eliminar Appointments relacionados
DELETE FROM "Appointments" 
WHERE "SearchHireId" IN (
    SELECT "Id" FROM "SearchHires" 
    WHERE "ClientId" = 1 
    AND "StatusId" IN (
        SELECT "Id" FROM "SystemStatuses" 
        WHERE "StatusType" = 'SearchHireStatus' 
        AND "IsFinalizationStatus" = false
    )
);

-- Eliminar Messages de las conversaciones relacionadas
DELETE FROM "Messages" 
WHERE "ConversationId" IN (
    SELECT "Id" FROM "Conversations" 
    WHERE "SearchHireId" IN (
        SELECT "Id" FROM "SearchHires" 
        WHERE "ClientId" = 1 
        AND "StatusId" IN (
            SELECT "Id" FROM "SystemStatuses" 
            WHERE "StatusType" = 'SearchHireStatus' 
            AND "IsFinalizationStatus" = false
        )
    )
);

-- Eliminar Conversations relacionadas
DELETE FROM "Conversations" 
WHERE "SearchHireId" IN (
    SELECT "Id" FROM "SearchHires" 
    WHERE "ClientId" = 1 
    AND "StatusId" IN (
        SELECT "Id" FROM "SystemStatuses" 
        WHERE "StatusType" = 'SearchHireStatus' 
        AND "IsFinalizationStatus" = false
    )
);

-- Eliminar Deliverables relacionados
DELETE FROM "SearchHireDeliverables" 
WHERE "SearchHireId" IN (
    SELECT "Id" FROM "SearchHires" 
    WHERE "ClientId" = 1 
    AND "StatusId" IN (
        SELECT "Id" FROM "SystemStatuses" 
        WHERE "StatusType" = 'SearchHireStatus' 
        AND "IsFinalizationStatus" = false
    )
);

-- Eliminar Disputes relacionados
DELETE FROM "Disputes" 
WHERE "SearchHireId" IN (
    SELECT "Id" FROM "SearchHires" 
    WHERE "ClientId" = 1 
    AND "StatusId" IN (
        SELECT "Id" FROM "SystemStatuses" 
        WHERE "StatusType" = 'SearchHireStatus' 
        AND "IsFinalizationStatus" = false
    )
);

-- Eliminar SearchHires activas
DELETE FROM "SearchHires" 
WHERE "ClientId" = 1 
AND "StatusId" IN (
    SELECT "Id" FROM "SystemStatuses" 
    WHERE "StatusType" = 'SearchHireStatus' 
    AND "IsFinalizationStatus" = false
);

-- Eliminar SearchParameters relacionados
DELETE FROM "SearchParameters" 
WHERE "SearchId" IN (
    SELECT "Id" FROM "Searches" 
    WHERE "UserId" = 1
);

-- Eliminar Searches
DELETE FROM "Searches" 
WHERE "UserId" = 1;

-- Verificar que se eliminaron
SELECT 
    (SELECT COUNT(*) FROM "SearchHires" WHERE "ClientId" = 1) as "SearchHiresRestantes",
    (SELECT COUNT(*) FROM "Searches" WHERE "UserId" = 1) as "SearchesRestantes";
