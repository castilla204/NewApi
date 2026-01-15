-- Resumen completo de la creación
SELECT 
    'SearchHire' as "Tipo",
    sh."Id" as "Id",
    ss."StatusValue" as "Status",
    sh."CreatedAt"
FROM "SearchHires" sh
INNER JOIN "SystemStatuses" ss ON sh."StatusId" = ss."Id"
WHERE sh."Id" = 22

UNION ALL

SELECT 
    'Search' as "Tipo",
    s."Id" as "Id",
    'N/A' as "Status",
    s."CreatedAt"
FROM "Searches" s
WHERE s."Id" = 50

UNION ALL

SELECT 
    'Appointment' as "Tipo",
    a."Id" as "Id",
    ass."StatusValue" as "Status",
    a."CreatedAt"
FROM "Appointments" a
INNER JOIN "SystemStatuses" ass ON a."StatusId" = ass."Id"
WHERE a."Id" = 9

UNION ALL

SELECT 
    'AppointmentTimer' as "Tipo",
    at."Id" as "Id",
    CASE WHEN at."HangfireJobId" IS NOT NULL THEN 'JobId: ' || at."HangfireJobId"::text ELSE 'Sin JobId' END as "Status",
    at."CreatedAt"
FROM "AppointmentTimers" at
WHERE at."Id" = 7

UNION ALL

SELECT 
    'Conversation' as "Tipo",
    c."Id" as "Id",
    CASE WHEN c."IsActive" THEN 'Active' ELSE 'Inactive' END as "Status",
    c."CreatedAt"
FROM "Conversations" c
WHERE c."SearchHireId" = 22;
