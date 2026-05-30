BEGIN;

WITH moto_services AS (
  SELECT ss."Id" AS service_id,
         ROW_NUMBER() OVER (ORDER BY ss."Id") AS rn
  FROM "SearchServices" ss
  WHERE ss."IsActive" = true
    AND ss."CategoryId" IN (6,7)
),
image_pool AS (
  SELECT * FROM (VALUES
    (1, 'https://images.unsplash.com/photo-1558981806-ec527fa84c39?w=1200&q=80&fit=crop'),
    (2, 'https://images.unsplash.com/photo-1568772585407-9361f9bf3a87?w=1200&q=80&fit=crop'),
    (3, 'https://images.unsplash.com/photo-1517841905240-472988babdf9?w=1200&q=80&fit=crop'),
    (4, 'https://images.unsplash.com/photo-1574015974293-817f0ebebb74?w=1200&q=80&fit=crop'),
    (5, 'https://images.unsplash.com/photo-1449426468159-d96dbf08f19f?w=1200&q=80&fit=crop'),
    (6, 'https://images.unsplash.com/photo-1527786355594-086adb9608e4?w=1200&q=80&fit=crop'),
    (7, 'https://images.unsplash.com/photo-1523966211575-eb4a01e7dd51?w=1200&q=80&fit=crop'),
    (8, 'https://images.unsplash.com/photo-1609630875171-b1321377ee65?w=1200&q=80&fit=crop'),
    (9, 'https://images.unsplash.com/photo-1492144534655-ae79c964c9d7?w=1200&q=80&fit=crop'),
    (10,'https://images.unsplash.com/photo-1503376780353-7e6692767b70?w=1200&q=80&fit=crop'),
    (11,'https://images.unsplash.com/photo-1533473359331-0135ef1b58bf?w=1200&q=80&fit=crop'),
    (12,'https://images.unsplash.com/photo-1486006920555-c77dcf18193c?w=1200&q=80&fit=crop'),
    (13,'https://images.unsplash.com/photo-1580273916550-e323be2ae537?w=1200&q=80&fit=crop'),
    (14,'https://images.unsplash.com/photo-1551836022-d5d88e9218df?w=1200&q=80&fit=crop'),
    (15,'https://images.unsplash.com/photo-1525609004556-c46c7d6cf023?w=1200&q=80&fit=crop'),
    (16,'https://images.unsplash.com/photo-1571607388263-1044f9ea01dd?w=1200&q=80&fit=crop'),
    (17,'https://images.unsplash.com/photo-1494976388531-d1058494cdd8?w=1200&q=80&fit=crop'),
    (18,'https://images.unsplash.com/photo-1552519507-da3b142c6e3d?w=1200&q=80&fit=crop'),
    (19,'https://images.unsplash.com/photo-1525609004556-c46c7d6cf023?w=1200&q=80&fit=crop&sat=-30'),
    (20,'https://images.unsplash.com/photo-1533473359331-0135ef1b58bf?w=1200&q=80&fit=crop&sat=20'),
    (21,'https://images.unsplash.com/photo-1517841905240-472988babdf9?w=1200&q=80&fit=crop&sat=-10'),
    (22,'https://images.unsplash.com/photo-1449426468159-d96dbf08f19f?w=1200&q=80&fit=crop&sat=10'),
    (23,'https://images.unsplash.com/photo-1571607388263-1044f9ea01dd?w=1200&q=80&fit=crop&sat=15'),
    (24,'https://images.unsplash.com/photo-1492144534655-ae79c964c9d7?w=1200&q=80&fit=crop&sat=-15'),
    (25,'https://images.unsplash.com/photo-1558981806-ec527fa84c39?w=1200&q=80&fit=crop&hue=30'),
    (26,'https://images.unsplash.com/photo-1568772585407-9361f9bf3a87?w=1200&q=80&fit=crop&hue=20'),
    (27,'https://images.unsplash.com/photo-1523966211575-eb4a01e7dd51?w=1200&q=80&fit=crop&hue=-20'),
    (28,'https://images.unsplash.com/photo-1609630875171-b1321377ee65?w=1200&q=80&fit=crop&hue=-10'),
    (29,'https://images.unsplash.com/photo-1503376780353-7e6692767b70?w=1200&q=80&fit=crop&hue=10'),
    (30,'https://images.unsplash.com/photo-1486006920555-c77dcf18193c?w=1200&q=80&fit=crop&hue=15'),
    (31,'https://images.unsplash.com/photo-1580273916550-e323be2ae537?w=1200&q=80&fit=crop&hue=-15'),
    (32,'https://images.unsplash.com/photo-1551836022-d5d88e9218df?w=1200&q=80&fit=crop&hue=25'),
    (33,'https://images.unsplash.com/photo-1494976388531-d1058494cdd8?w=1200&q=80&fit=crop&hue=-25'),
    (34,'https://images.unsplash.com/photo-1552519507-da3b142c6e3d?w=1200&q=80&fit=crop&hue=35'),
    (35,'https://images.unsplash.com/photo-1527786355594-086adb9608e4?w=1200&q=80&fit=crop&hue=-35'),
    (36,'https://images.unsplash.com/photo-1533473359331-0135ef1b58bf?w=1200&q=80&fit=crop&hue=5')
  ) AS t(idx, url)
)
DELETE FROM "SearchServiceImages"
WHERE "SearchServiceId" IN (SELECT service_id FROM moto_services);

WITH moto_services AS (
  SELECT ss."Id" AS service_id,
         ROW_NUMBER() OVER (ORDER BY ss."Id") AS rn
  FROM "SearchServices" ss
  WHERE ss."IsActive" = true
    AND ss."CategoryId" IN (6,7)
),
image_pool AS (
  SELECT * FROM (VALUES
    (1, 'https://images.unsplash.com/photo-1558981806-ec527fa84c39?w=1200&q=80&fit=crop'),
    (2, 'https://images.unsplash.com/photo-1568772585407-9361f9bf3a87?w=1200&q=80&fit=crop'),
    (3, 'https://images.unsplash.com/photo-1517841905240-472988babdf9?w=1200&q=80&fit=crop'),
    (4, 'https://images.unsplash.com/photo-1574015974293-817f0ebebb74?w=1200&q=80&fit=crop'),
    (5, 'https://images.unsplash.com/photo-1449426468159-d96dbf08f19f?w=1200&q=80&fit=crop'),
    (6, 'https://images.unsplash.com/photo-1527786355594-086adb9608e4?w=1200&q=80&fit=crop'),
    (7, 'https://images.unsplash.com/photo-1523966211575-eb4a01e7dd51?w=1200&q=80&fit=crop'),
    (8, 'https://images.unsplash.com/photo-1609630875171-b1321377ee65?w=1200&q=80&fit=crop'),
    (9, 'https://images.unsplash.com/photo-1492144534655-ae79c964c9d7?w=1200&q=80&fit=crop'),
    (10,'https://images.unsplash.com/photo-1503376780353-7e6692767b70?w=1200&q=80&fit=crop'),
    (11,'https://images.unsplash.com/photo-1533473359331-0135ef1b58bf?w=1200&q=80&fit=crop'),
    (12,'https://images.unsplash.com/photo-1486006920555-c77dcf18193c?w=1200&q=80&fit=crop'),
    (13,'https://images.unsplash.com/photo-1580273916550-e323be2ae537?w=1200&q=80&fit=crop'),
    (14,'https://images.unsplash.com/photo-1551836022-d5d88e9218df?w=1200&q=80&fit=crop'),
    (15,'https://images.unsplash.com/photo-1525609004556-c46c7d6cf023?w=1200&q=80&fit=crop'),
    (16,'https://images.unsplash.com/photo-1571607388263-1044f9ea01dd?w=1200&q=80&fit=crop'),
    (17,'https://images.unsplash.com/photo-1494976388531-d1058494cdd8?w=1200&q=80&fit=crop'),
    (18,'https://images.unsplash.com/photo-1552519507-da3b142c6e3d?w=1200&q=80&fit=crop'),
    (19,'https://images.unsplash.com/photo-1525609004556-c46c7d6cf023?w=1200&q=80&fit=crop&sat=-30'),
    (20,'https://images.unsplash.com/photo-1533473359331-0135ef1b58bf?w=1200&q=80&fit=crop&sat=20'),
    (21,'https://images.unsplash.com/photo-1517841905240-472988babdf9?w=1200&q=80&fit=crop&sat=-10'),
    (22,'https://images.unsplash.com/photo-1449426468159-d96dbf08f19f?w=1200&q=80&fit=crop&sat=10'),
    (23,'https://images.unsplash.com/photo-1571607388263-1044f9ea01dd?w=1200&q=80&fit=crop&sat=15'),
    (24,'https://images.unsplash.com/photo-1492144534655-ae79c964c9d7?w=1200&q=80&fit=crop&sat=-15'),
    (25,'https://images.unsplash.com/photo-1558981806-ec527fa84c39?w=1200&q=80&fit=crop&hue=30'),
    (26,'https://images.unsplash.com/photo-1568772585407-9361f9bf3a87?w=1200&q=80&fit=crop&hue=20'),
    (27,'https://images.unsplash.com/photo-1523966211575-eb4a01e7dd51?w=1200&q=80&fit=crop&hue=-20'),
    (28,'https://images.unsplash.com/photo-1609630875171-b1321377ee65?w=1200&q=80&fit=crop&hue=-10'),
    (29,'https://images.unsplash.com/photo-1503376780353-7e6692767b70?w=1200&q=80&fit=crop&hue=10'),
    (30,'https://images.unsplash.com/photo-1486006920555-c77dcf18193c?w=1200&q=80&fit=crop&hue=15'),
    (31,'https://images.unsplash.com/photo-1580273916550-e323be2ae537?w=1200&q=80&fit=crop&hue=-15'),
    (32,'https://images.unsplash.com/photo-1551836022-d5d88e9218df?w=1200&q=80&fit=crop&hue=25'),
    (33,'https://images.unsplash.com/photo-1494976388531-d1058494cdd8?w=1200&q=80&fit=crop&hue=-25'),
    (34,'https://images.unsplash.com/photo-1552519507-da3b142c6e3d?w=1200&q=80&fit=crop&hue=35'),
    (35,'https://images.unsplash.com/photo-1527786355594-086adb9608e4?w=1200&q=80&fit=crop&hue=-35'),
    (36,'https://images.unsplash.com/photo-1533473359331-0135ef1b58bf?w=1200&q=80&fit=crop&hue=5')
  ) AS t(idx, url)
)
INSERT INTO "SearchServiceImages" ("SearchServiceId", "ImageUrl", "ImageObjectName", "CreatedAt")
SELECT ms.service_id, ip.url, '', NOW()
FROM moto_services ms
JOIN image_pool ip ON ip.idx IN ((ms.rn * 2) - 1, (ms.rn * 2));

COMMIT;

SELECT ss."Id" AS service_id, ss."CategoryId", c."Name", COUNT(si."Id") AS imgs,
       MIN(si."ImageUrl") AS img1, MAX(si."ImageUrl") AS img2
FROM "SearchServices" ss
JOIN "Categories" c ON c."Id" = ss."CategoryId"
LEFT JOIN "SearchServiceImages" si ON si."SearchServiceId" = ss."Id"
WHERE ss."IsActive" = true AND ss."CategoryId" IN (6,7)
GROUP BY ss."Id", ss."CategoryId", c."Name"
ORDER BY ss."Id";
