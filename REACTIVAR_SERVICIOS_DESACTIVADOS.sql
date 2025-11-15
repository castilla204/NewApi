-- Script para reactivar servicios que fueron desactivados incorrectamente
-- cuando un CLIENTE eliminó su cuenta (no deberían haberse desactivado)

-- ✅ REACTIVAR servicios que tienen ExpertProfileId válido (no NULL)
-- pero están desactivados (IsActive = false)
-- Esto reactiva servicios que fueron desactivados por error

UPDATE "SearchServices"
SET "IsActive" = true,
    "UpdatedAt" = CURRENT_TIMESTAMP
WHERE "IsActive" = false
  AND "ExpertProfileId" IS NOT NULL
  AND EXISTS (
      SELECT 1 
      FROM "ExpertProfiles" 
      WHERE "ExpertProfiles"."Id" = "SearchServices"."ExpertProfileId"
        AND "ExpertProfiles"."UserId" IS NOT NULL
        AND EXISTS (
            SELECT 1 
            FROM "Users" 
            WHERE "Users"."Id" = "ExpertProfiles"."UserId"
              AND "Users"."IsDeleted" = false
        )
  );

-- ✅ Verificar cuántos servicios se reactivaron
SELECT COUNT(*) as "ServiciosReactivados"
FROM "SearchServices"
WHERE "IsActive" = true
  AND "ExpertProfileId" IS NOT NULL
  AND "UpdatedAt" >= CURRENT_TIMESTAMP - INTERVAL '1 minute';

