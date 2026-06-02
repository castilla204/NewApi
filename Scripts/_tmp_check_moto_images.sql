SELECT ss."Id" AS service_id, ss."CategoryId", c."Name" AS category_name,
       MIN(si."ImageUrl") AS img1, MAX(si."ImageUrl") AS img2,
       COUNT(DISTINCT si."ImageUrl") AS distinct_imgs
FROM "SearchServices" ss
JOIN "Categories" c ON c."Id" = ss."CategoryId"
LEFT JOIN "SearchServiceImages" si ON si."SearchServiceId" = ss."Id"
WHERE ss."IsActive" = true
  AND ss."CategoryId" IN (6,7)
GROUP BY ss."Id", ss."CategoryId", c."Name"
ORDER BY ss."Id";

SELECT si."ImageUrl", COUNT(*) AS used_by_services
FROM "SearchServices" ss
JOIN "SearchServiceImages" si ON si."SearchServiceId" = ss."Id"
WHERE ss."IsActive" = true
  AND ss."CategoryId" IN (6,7)
GROUP BY si."ImageUrl"
HAVING COUNT(*) > 1
ORDER BY used_by_services DESC, si."ImageUrl";
