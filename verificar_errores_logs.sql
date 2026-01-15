-- Verificar si hay errores o warnings en los logs relacionados con la creación
SELECT 
    l."Id",
    l."Message",
    lt."Name" as "LogType",
    l."Source",
    l."RelatedEntityType",
    l."RelatedEntityId",
    l."CreatedAt",
    SUBSTRING(l."Details", 1, 300) as "Details"
FROM "Logs" l
LEFT JOIN "LogTypes" lt ON l."LogTypeId" = lt."Id"
WHERE l."CreatedAt" >= '2026-01-15 11:18:00'
  AND (
    l."RelatedEntityId" IN (22, 50, 9, 7, 4)
    OR l."Source" LIKE '%SearchHire%'
    OR l."Source" LIKE '%Appointment%'
    OR l."Source" LIKE '%Subscription%'
    OR l."Source" LIKE '%SearchController%'
  )
ORDER BY l."CreatedAt" DESC;
