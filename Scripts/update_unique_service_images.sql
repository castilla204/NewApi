BEGIN;

DELETE FROM "SearchServiceImages"
WHERE "SearchServiceId" IN (
  SELECT "Id" FROM "SearchServices" WHERE "IsActive" = true
);

INSERT INTO "SearchServiceImages" ("SearchServiceId", "ImageUrl", "ImageObjectName", "CreatedAt")
SELECT
  ss."Id",
  CASE
    WHEN ss."CategoryId" IN (5, 2) THEN 'https://source.unsplash.com/800x600/?car&sig=' || ss."Id"
    WHEN ss."CategoryId" IN (6, 7) THEN 'https://source.unsplash.com/800x600/?motorbike&sig=' || ss."Id"
    WHEN ss."CategoryId" IN (8, 9, 10, 3) THEN 'https://source.unsplash.com/800x600/?house,real-estate&sig=' || ss."Id"
    WHEN ss."CategoryId" IN (11, 12, 1) THEN 'https://source.unsplash.com/800x600/?appliance,kitchen&sig=' || ss."Id"
    ELSE 'https://source.unsplash.com/800x600/?inspection,service&sig=' || ss."Id"
  END,
  '',
  NOW()
FROM "SearchServices" ss
WHERE ss."IsActive" = true;

INSERT INTO "SearchServiceImages" ("SearchServiceId", "ImageUrl", "ImageObjectName", "CreatedAt")
SELECT
  ss."Id",
  CASE
    WHEN ss."CategoryId" IN (5, 2) THEN 'https://source.unsplash.com/800x600/?car,dealership&sig=' || (ss."Id" + 10000)
    WHEN ss."CategoryId" IN (6, 7) THEN 'https://source.unsplash.com/800x600/?motorcycle,road&sig=' || (ss."Id" + 10000)
    WHEN ss."CategoryId" IN (8, 9, 10, 3) THEN 'https://source.unsplash.com/800x600/?modern-house,interior&sig=' || (ss."Id" + 10000)
    WHEN ss."CategoryId" IN (11, 12, 1) THEN 'https://source.unsplash.com/800x600/?home-appliance,kitchen&sig=' || (ss."Id" + 10000)
    ELSE 'https://source.unsplash.com/800x600/?consulting,service&sig=' || (ss."Id" + 10000)
  END,
  '',
  NOW()
FROM "SearchServices" ss
WHERE ss."IsActive" = true;

COMMIT;

SELECT ss."Id" AS service_id, ss."CategoryId", COUNT(si."Id") AS total_images,
       MIN(si."ImageUrl") AS sample_image_1, MAX(si."ImageUrl") AS sample_image_2
FROM "SearchServices" ss
LEFT JOIN "SearchServiceImages" si ON si."SearchServiceId" = ss."Id"
WHERE ss."IsActive" = true
GROUP BY ss."Id", ss."CategoryId"
ORDER BY ss."Id"
LIMIT 12;
