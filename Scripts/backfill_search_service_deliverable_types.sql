-- Vincula tipos de entregable activos a servicios que no tienen ninguno (p. ej. seeds).
-- Ejecutar contra la misma BD que usa la API (Render / local).

BEGIN;

INSERT INTO "SearchServiceDeliverableTypes" (
  "SearchServiceId",
  "DeliverableTypeId",
  "IsSelected",
  "CreatedAt",
  "UpdatedAt"
)
SELECT
  ss."Id",
  dt."Id",
  true,
  NOW(),
  NOW()
FROM "SearchServices" ss
CROSS JOIN "DeliverableTypes" dt
WHERE ss."IsActive" = true
  AND dt."IsActive" = true
  AND NOT EXISTS (
    SELECT 1
    FROM "SearchServiceDeliverableTypes" existing
    WHERE existing."SearchServiceId" = ss."Id"
  );

COMMIT;

-- Comprobar servicio 75
SELECT ss."Id", dt."Name", dt."DisplayName", ssdt."IsSelected"
FROM "SearchServices" ss
LEFT JOIN "SearchServiceDeliverableTypes" ssdt ON ssdt."SearchServiceId" = ss."Id"
LEFT JOIN "DeliverableTypes" dt ON dt."Id" = ssdt."DeliverableTypeId"
WHERE ss."Id" = 75;
