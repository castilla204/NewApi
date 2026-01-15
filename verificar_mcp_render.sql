-- Verificar logs recientes relacionados con la creación
SELECT 
    l."Id",
    l."Message",
    lt."Name" as "LogType",
    l."Source",
    l."RelatedEntityType",
    l."RelatedEntityId",
    l."CreatedAt",
    SUBSTRING(l."Details", 1, 200) as "Details"
FROM "Logs" l
LEFT JOIN "LogTypes" lt ON l."LogTypeId" = lt."Id"
WHERE l."CreatedAt" >= NOW() - INTERVAL '10 minutes'
  AND (
    l."RelatedEntityId" IN (22, 50, 9, 7, 4)
    OR l."Source" LIKE '%SearchHire%'
    OR l."Source" LIKE '%Appointment%'
    OR l."Source" LIKE '%Subscription%'
    OR l."Source" LIKE '%SearchController%'
  )
ORDER BY l."CreatedAt" DESC
LIMIT 30;

-- Verificar estado completo de la contratación 22
SELECT 
    sh."Id" as "SearchHireId", 
    sh."StatusId" as "SearchHireStatusId", 
    ss."StatusValue" as "SearchHireStatus",
    a."Id" as "AppointmentId", 
    a."StatusId" as "AppointmentStatusId", 
    ass."StatusValue" as "AppointmentStatus",
    at."Id" as "TimerId", 
    at."HangfireJobId",
    at."IsExpired",
    at."EndTime"
FROM "SearchHires" sh
INNER JOIN "SystemStatuses" ss ON sh."StatusId" = ss."Id"
LEFT JOIN "Appointments" a ON a."SearchHireId" = sh."Id"
LEFT JOIN "SystemStatuses" ass ON a."StatusId" = ass."Id"
LEFT JOIN "AppointmentTimers" at ON at."AppointmentId" = a."Id"
WHERE sh."Id" = 22;

-- Verificar si el Appointment tiene los campos requeridos
SELECT 
    a."Id",
    a."SearchHireId",
    a."ProposedDate",
    a."ProposedTime",
    a."Location",
    a."StatusId",
    ss."StatusValue" as "Status",
    a."CreatedAt"
FROM "Appointments" a
INNER JOIN "SystemStatuses" ss ON a."StatusId" = ss."Id"
WHERE a."Id" = 9;

-- Verificar si el Hangfire job existe
SELECT 
    j.id as "JobId",
    j.statename as "StateName",
    j.createdat as "CreatedAt"
FROM hangfire.job j
WHERE j.id = 9;

-- Verificar si hay errores o warnings
SELECT 
    l."Id",
    l."Message",
    lt."Name" as "LogType",
    l."Source",
    SUBSTRING(l."Details", 1, 300) as "Details",
    l."CreatedAt"
FROM "Logs" l
LEFT JOIN "LogTypes" lt ON l."LogTypeId" = lt."Id"
WHERE l."CreatedAt" >= NOW() - INTERVAL '10 minutes'
  AND (lt."Name" IN ('Error', 'Critical', 'Warning') 
       OR l."Message" ILIKE '%error%'
       OR l."Message" ILIKE '%failed%')
ORDER BY l."CreatedAt" DESC
LIMIT 20;
