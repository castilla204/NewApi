-- Seed: servicios del experto 3 en categorías que faltaban + país ES para homepage-wall
BEGIN;

UPDATE "ExpertProfiles"
SET "Country" = 'ES',
    "City" = 'Madrid'
WHERE "Id" = 3
  AND ("Country" IS NULL OR "Country" = '');

-- Imágenes reutilizables (Unsplash)
-- Coches/inmuebles/electrodomésticos según categoría

INSERT INTO "SearchServices" ("ExpertProfileId", "AIId", "CategoryId", "ServiceTypeId", "Price", "Conditions", "DurationInHours", "CreatedAt", "IsActive")
VALUES
  (3, NULL, 1,  2, 45.00, 'Revisión presencial de electrodomésticos. Informe con fotos y recomendaciones.', 2, NOW(), true),
  (3, NULL, 2,  3, 35.00, 'Asesoramiento online para compra de vehículos. Revisión de documentación y checklist.', 1, NOW(), true),
  (3, NULL, 3,  2, 69.00, 'Inspección de inmuebles antes de comprar. Informe detallado de estado y riesgos.', 3, NOW(), true),
  (3, NULL, 4,  2, 40.00, 'Revisión presencial de servicios y contratos antes de contratar.', 2, NOW(), true),
  (3, NULL, 10, 3, 55.00, 'Revisión online de locales comerciales. Análisis de ubicación, licencias y estado.', 2, NOW(), true),
  (3, NULL, 12, 4, 29.00, 'Búsqueda y revisión de electrodomésticos pequeños en segunda mano.', 1, NOW(), true)
RETURNING "Id", "CategoryId", "ServiceTypeId", "Price";

-- Imágenes para los nuevos servicios (IDs asignados en orden de inserción)
-- Usamos subconsultas por CategoryId para no depender de IDs fijos

INSERT INTO "SearchServiceImages" ("SearchServiceId", "ImageUrl", "ImageObjectName", "CreatedAt")
SELECT ss."Id", v.img, '', NOW()
FROM "SearchServices" ss
JOIN (VALUES
  (1,  'https://images.unsplash.com/photo-1556911220-bff31c812dba?w=800&q=80&fit=crop'),
  (1,  'https://images.unsplash.com/photo-1585659722983-3c075712f88e?w=800&q=80&fit=crop'),
  (2,  'https://images.unsplash.com/photo-1492144534655-ae79c964c9d7?w=800&q=80&fit=crop'),
  (2,  'https://images.unsplash.com/photo-1503376780353-7e6692767b70?w=800&q=80&fit=crop'),
  (3,  'https://images.unsplash.com/photo-1600596542815-ffad4c1539a9?w=800&q=80&fit=crop'),
  (3,  'https://images.unsplash.com/photo-1600585154340-be6161a56a0c?w=800&q=80&fit=crop'),
  (4,  'https://images.unsplash.com/photo-1454165804606-c3d57bc86b40?w=800&q=80&fit=crop'),
  (4,  'https://images.unsplash.com/photo-1521791136064-7986c2920216?w=800&q=80&fit=crop'),
  (10, 'https://images.unsplash.com/photo-1497366216548-37526070297c?w=800&q=80&fit=crop'),
  (10, 'https://images.unsplash.com/photo-1497366754035-f200968a6e72?w=800&q=80&fit=crop'),
  (12, 'https://images.unsplash.com/photo-1585659722983-3c075712f88e?w=800&q=80&fit=crop'),
  (12, 'https://images.unsplash.com/photo-1556911220-bff31c812dba?w=800&q=80&fit=crop')
) AS v(cat_id, img) ON v.cat_id = ss."CategoryId"
WHERE ss."ExpertProfileId" = 3
  AND ss."CategoryId" IN (1, 2, 3, 4, 10, 12)
  AND NOT EXISTS (
    SELECT 1 FROM "SearchServiceImages" si WHERE si."SearchServiceId" = ss."Id"
  );

COMMIT;

-- Verificación
SELECT c."Id", c."Name", COUNT(ss."Id") AS service_count
FROM "Categories" c
LEFT JOIN "SearchServices" ss ON ss."CategoryId" = c."Id" AND ss."ExpertProfileId" = 3 AND ss."IsActive" = true
WHERE c."IsActive" = true
GROUP BY c."Id", c."Name"
ORDER BY c."Id";

SELECT ss."Id", ss."CategoryId", c."Name", ss."ServiceTypeId", ss."Price"
FROM "SearchServices" ss
JOIN "Categories" c ON c."Id" = ss."CategoryId"
WHERE ss."ExpertProfileId" = 3 AND ss."IsActive" = true
ORDER BY ss."CategoryId", ss."Id";
