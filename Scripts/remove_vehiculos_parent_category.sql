-- Elimina la categoría padre "Vehículos" (Id=2) y promueve Coches/Motos a raíz.
-- Idempotente. Ejecutar en PostgreSQL (Render):
--   psql "$DATABASE_URL" -f Scripts/remove_vehiculos_parent_category.sql

BEGIN;

-- 1. Servicios que apuntaran al padre → Coches (5)
UPDATE "SearchServices"
SET "CategoryId" = 5
WHERE "CategoryId" = 2;

-- 2. Anuncios vinculados al padre → Coches
UPDATE "Ads"
SET "CategoryId" = 5
WHERE "CategoryId" = 2;

-- 3. Configuraciones de estado por categoría
UPDATE "StatusConfigurations"
SET "CategoryId" = 5
WHERE "CategoryId" = 2;

-- 4. Promover subcategorías de vehículos a categorías raíz
UPDATE "Categories"
SET "ParentId" = NULL, "UpdatedAt" = NOW()
WHERE "ParentId" = 2;

-- 5. Mappings de plataforma (se recrean si hace falta)
DELETE FROM "PlatformCategoryMappings"
WHERE "CategoryId" = 2;

-- 6. Eliminar la categoría padre obsoleta
DELETE FROM "Categories"
WHERE "Id" = 2;

COMMIT;

SELECT "Id", "Name", "ParentId", "IsActive"
FROM "Categories"
WHERE "Id" IN (2, 3, 5, 6) OR "ParentId" IS NULL
ORDER BY "Id";
