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
