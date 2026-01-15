-- Revisar logs relacionados con la creación reciente
SELECT 
    l."Id",
    l."Message",
    l."Source",
    l."LogTypeId",
    lt."Name" as "LogTypeName",
    l."UserId",
    l."RelatedEntityType",
    l."RelatedEntityId",
    l."CreatedAt",
    SUBSTRING(l."Details", 1, 200) as "DetailsPreview"
FROM "Logs" l
LEFT JOIN "LogTypes" lt ON l."LogTypeId" = lt."Id"
WHERE l."CreatedAt" >= NOW() - INTERVAL '10 minutes'
   OR (l."RelatedEntityId" = 22 AND l."RelatedEntityType" = 'SearchHire')
   OR (l."RelatedEntityId" = 50 AND l."RelatedEntityType" = 'Search')
   OR (l."RelatedEntityId" = 9 AND l."RelatedEntityType" = 'Appointment')
ORDER BY l."CreatedAt" DESC
LIMIT 50;

-- Verificar si hay errores en los logs
SELECT 
    l."Id",
    l."Message",
    l."Source",
    lt."Name" as "LogTypeName",
    l."Details",
    l."CreatedAt"
FROM "Logs" l
LEFT JOIN "LogTypes" lt ON l."LogTypeId" = lt."Id"
WHERE l."CreatedAt" >= NOW() - INTERVAL '10 minutes'
  AND (lt."Name" IN ('Error', 'Critical', 'Warning') OR l."Message" LIKE '%error%' OR l."Message" LIKE '%Error%' OR l."Message" LIKE '%failed%' OR l."Message" LIKE '%Failed%')
ORDER BY l."CreatedAt" DESC
LIMIT 20;
