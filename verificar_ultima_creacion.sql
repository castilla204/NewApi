-- Verificar últimas contrataciones creadas
SELECT 
    sh."Id", 
    sh."ClientId", 
    sh."ExpertId", 
    sh."StatusId", 
    ss."StatusValue", 
    sh."CreatedAt", 
    u."Email" as "ClientEmail"
FROM "SearchHires" sh
INNER JOIN "SystemStatuses" ss ON sh."StatusId" = ss."Id"
INNER JOIN "Users" u ON sh."ClientId" = u."Id"
WHERE u."Email" = 'dcastillaa@gmail.com'
ORDER BY sh."CreatedAt" DESC
LIMIT 5;

-- Verificar últimas búsquedas creadas
SELECT 
    s."Id", 
    s."UserId", 
    s."Title", 
    s."CreatedAt", 
    u."Email" as "UserEmail"
FROM "Searches" s
INNER JOIN "Users" u ON s."UserId" = u."Id"
WHERE u."Email" = 'dcastillaa@gmail.com'
ORDER BY s."CreatedAt" DESC
LIMIT 5;

-- Verificar si hay appointments creados recientemente
SELECT 
    a."Id",
    a."SearchHireId",
    a."StatusId",
    ss."StatusValue",
    a."CreatedAt"
FROM "Appointments" a
INNER JOIN "SystemStatuses" ss ON a."StatusId" = ss."Id"
INNER JOIN "SearchHires" sh ON a."SearchHireId" = sh."Id"
INNER JOIN "Users" u ON sh."ClientId" = u."Id"
WHERE u."Email" = 'dcastillaa@gmail.com'
ORDER BY a."CreatedAt" DESC
LIMIT 5;
