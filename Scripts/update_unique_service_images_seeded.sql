BEGIN;

DELETE FROM "SearchServiceImages"
WHERE "SearchServiceId" IN (
  SELECT "Id" FROM "SearchServices" WHERE "IsActive" = true
);

INSERT INTO "SearchServiceImages" ("SearchServiceId", "ImageUrl", "ImageObjectName", "CreatedAt")
SELECT
  ss."Id",
  CASE
    WHEN ss."CategoryId" IN (5, 2) THEN 'https://picsum.photos/seed/car-' || ss."Id" || '/800/600'
    WHEN ss."CategoryId" IN (6, 7) THEN 'https://picsum.photos/seed/moto-' || ss."Id" || '/800/600'
    WHEN ss."CategoryId" IN (8, 9, 10, 3) THEN 'https://picsum.photos/seed/home-' || ss."Id" || '/800/600'
    WHEN ss."CategoryId" IN (11, 12, 1) THEN 'https://picsum.photos/seed/appliance-' || ss."Id" || '/800/600'
    ELSE 'https://picsum.photos/seed/service-' || ss."Id" || '/800/600'
  END,
  '',
  NOW()
FROM "SearchServices" ss
WHERE ss."IsActive" = true;

INSERT INTO "SearchServiceImages" ("SearchServiceId", "ImageUrl", "ImageObjectName", "CreatedAt")
SELECT
  ss."Id",
  CASE
    WHEN ss."CategoryId" IN (5, 2) THEN 'https://picsum.photos/seed/car-alt-' || ss."Id" || '/800/600'
    WHEN ss."CategoryId" IN (6, 7) THEN 'https://picsum.photos/seed/moto-alt-' || ss."Id" || '/800/600'
    WHEN ss."CategoryId" IN (8, 9, 10, 3) THEN 'https://picsum.photos/seed/home-alt-' || ss."Id" || '/800/600'
    WHEN ss."CategoryId" IN (11, 12, 1) THEN 'https://picsum.photos/seed/appliance-alt-' || ss."Id" || '/800/600'
    ELSE 'https://picsum.photos/seed/service-alt-' || ss."Id" || '/800/600'
  END,
  '',
  NOW()
FROM "SearchServices" ss
WHERE ss."IsActive" = true;

COMMIT;

SELECT COUNT(*) AS servicios_activos FROM "SearchServices" WHERE "IsActive" = true;
SELECT COUNT(*) AS imagenes_total FROM "SearchServiceImages";
SELECT COUNT(DISTINCT "ImageUrl") AS urls_distintas FROM "SearchServiceImages";

SELECT ss."Id", ss."CategoryId", MIN(si."ImageUrl") AS img1, MAX(si."ImageUrl") AS img2
FROM "SearchServices" ss
JOIN "SearchServiceImages" si ON si."SearchServiceId" = ss."Id"
WHERE ss."IsActive" = true
GROUP BY ss."Id", ss."CategoryId"
ORDER BY ss."Id"
LIMIT 8;
