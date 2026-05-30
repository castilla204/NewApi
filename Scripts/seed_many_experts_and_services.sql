-- Seed: 10 expertos demo + servicios (solo Coches/Motos/Inmobiliaria)
-- Para datos globales usar: Scripts/seed_global_world_experts_trim_categories.sql
BEGIN;

-- ─── Usuarios experto (Role = 1) ───
INSERT INTO "Users" (
  "Name", "Email", "Password", "GoogleId", "PhoneNumber", "PhoneVerified",
  "CreatedAt", "IsBlocked", "Role", "IsDeleted", "Balance"
)
SELECT
  v.name, v.email, NULL, v.google_id, NULL, false, NOW(), false, 1, false, 0
FROM (VALUES
  ('Ana García',      'expert.seed.ana@inspecciono.dev',      'seed-expert-ana-001'),
  ('Carlos Ruiz',     'expert.seed.carlos@inspecciono.dev',   'seed-expert-carlos-002'),
  ('Laura Martín',    'expert.seed.laura@inspecciono.dev',    'seed-expert-laura-003'),
  ('Miguel Torres',   'expert.seed.miguel@inspecciono.dev',   'seed-expert-miguel-004'),
  ('Elena Soto',      'expert.seed.elena@inspecciono.dev',    'seed-expert-elena-005'),
  ('Pablo Navarro',   'expert.seed.pablo@inspecciono.dev',    'seed-expert-pablo-006'),
  ('Sofía Herrera',   'expert.seed.sofia@inspecciono.dev',    'seed-expert-sofia-007'),
  ('Javier Romero',   'expert.seed.javier@inspecciono.dev',   'seed-expert-javier-008'),
  ('Carmen López',    'expert.seed.carmen@inspecciono.dev',   'seed-expert-carmen-009'),
  ('David Gil',       'expert.seed.david@inspecciono.dev',    'seed-expert-david-010')
) AS v(name, email, google_id)
WHERE NOT EXISTS (
  SELECT 1 FROM "Users" u WHERE u."Email" = v.email
);

-- ─── Perfiles experto (Stripe aprobado, ES, coordenadas por ciudad) ───
INSERT INTO "ExpertProfiles" (
  "UserId", "ProfilePictureUrl", "ProfilePictureObjectName", "Description",
  "OnboardingCompleted", "StripeStatus", "IsOnVacation", "CreatedAt",
  "Latitude", "Longitude", "Timezone", "Country", "City"
)
SELECT
  u."Id",
  v.pic_url,
  '',
  v.description,
  true,
  2,
  false,
  NOW(),
  v.lat::text,
  v.lng::text,
  'Europe/Madrid',
  'ES',
  v.city
FROM "Users" u
JOIN (VALUES
  ('expert.seed.ana@inspecciono.dev',    'Madrid',     '40.4168',  '-3.7038',  'Experta en inspección de vehículos e inmuebles en Madrid.', 'https://images.unsplash.com/photo-1494790108377-be9c29b29330?w=400&h=400&fit=crop&crop=faces'),
  ('expert.seed.carlos@inspecciono.dev', 'Barcelona',  '41.3851',  '2.1734',   'Perito automotriz y revisión de pisos en Barcelona.', 'https://images.unsplash.com/photo-1507003211169-0a1dd7228f2d?w=400&h=400&fit=crop&crop=faces'),
  ('expert.seed.laura@inspecciono.dev',  'Valencia',   '39.4699',  '-0.3763',  'Inspecciones de coches y viviendas en Valencia y alrededores.', 'https://images.unsplash.com/photo-1438761681033-6461ffad8d80?w=400&h=400&fit=crop&crop=faces'),
  ('expert.seed.miguel@inspecciono.dev', 'Sevilla',    '37.3891',  '-5.9845',  'Revisor profesional de inmuebles y vehículos en Sevilla.', 'https://images.unsplash.com/photo-1472099645785-5658abf4ff4e?w=400&h=400&fit=crop&crop=faces'),
  ('expert.seed.elena@inspecciono.dev',  'Bilbao',     '43.2630',  '-2.9350',  'Especialista en compraventa segura de coches y casas en Bilbao.', 'https://images.unsplash.com/photo-1544005313-94ddf0286df2?w=400&h=400&fit=crop&crop=faces'),
  ('expert.seed.pablo@inspecciono.dev',  'Málaga',     '36.7213',  '-4.4214',  'Inspecciones presenciales y online en la Costa del Sol.', 'https://images.unsplash.com/photo-1500648767791-00dcc994a43e?w=400&h=400&fit=crop&crop=faces'),
  ('expert.seed.sofia@inspecciono.dev',  'Zaragoza',   '41.6488',  '-0.8891',  'Revisión técnica de vehículos y due diligence inmobiliaria.', 'https://images.unsplash.com/photo-1534528741775-53994a69daeb?w=400&h=400&fit=crop&crop=faces'),
  ('expert.seed.javier@inspecciono.dev', 'Alicante',   '38.3452',  '-0.4810',  'Experto en revisar anuncios de coches y pisos antes de comprar.', 'https://images.unsplash.com/photo-1519085360753-af0119f7cbe7?w=400&h=400&fit=crop&crop=faces'),
  ('expert.seed.carmen@inspecciono.dev', 'Murcia',     '37.9922',  '-1.1307',  'Inspecciones rápidas con informe claro en Murcia.', 'https://images.unsplash.com/photo-1487412720507-e7ab37603c6f?w=400&h=400&fit=crop&crop=faces'),
  ('expert.seed.david@inspecciono.dev',  'Valladolid', '41.6523',  '-4.7245',  'Perito independiente: coches, furgonetas e inmuebles.', 'https://images.unsplash.com/photo-1506794778202-cad84cf45f1d?w=400&h=400&fit=crop&crop=faces')
) AS v(email, city, lat, lng, description, pic_url) ON u."Email" = v.email
WHERE NOT EXISTS (
  SELECT 1 FROM "ExpertProfiles" ep WHERE ep."UserId" = u."Id"
);

-- ─── Disponibilidad (L-V 9-18) ───
INSERT INTO "ExpertAvailabilities" (
  "ExpertId", "DaysOfWeek", "StartTime", "EndTime",
  "EffectiveFrom", "IsActive", "CreatedAt", "UpdatedAt"
)
SELECT
  ep."Id",
  '["Monday","Tuesday","Wednesday","Thursday","Friday"]',
  '09:00:00'::interval,
  '18:00:00'::interval,
  NOW(),
  true,
  NOW(),
  NOW()
FROM "ExpertProfiles" ep
JOIN "Users" u ON u."Id" = ep."UserId"
WHERE u."Email" LIKE 'expert.seed.%@inspecciono.dev'
  AND NOT EXISTS (
    SELECT 1 FROM "ExpertAvailabilities" ea WHERE ea."ExpertId" = ep."Id"
  );

-- ─── Servicios: cada experto seed recibe 6 categorías distintas ───
INSERT INTO "SearchServices" (
  "ExpertProfileId", "AIId", "CategoryId", "ServiceTypeId", "Price",
  "Conditions", "DurationInHours", "CreatedAt", "IsActive"
)
SELECT
  ep."Id",
  NULL,
  m.cat_id,
  m.st_id,
  m.price,
  m.conditions,
  m.hours,
  NOW(),
  true
FROM "ExpertProfiles" ep
JOIN "Users" u ON u."Id" = ep."UserId"
JOIN LATERAL (VALUES
  (5,  2, 52.00, 'Inspección presencial de turismos de ocasión. Informe en 24h.', 2),
  (6,  2, 38.00, 'Revisión de motos: chasis, motor y documentación.', 2),
  (3,  2, 72.00, 'Due diligence inmobiliaria integral.', 3),
  (5,  3, 42.00, 'Pre-compra online: revisión de anuncio y checklist.', 1),
  (3,  3, 58.00, 'Análisis online de inmueble antes de visitar.', 2),
  (6,  3, 36.00, 'Asesoramiento online compra de moto.', 1)
) AS m(cat_id, st_id, price, conditions, hours) ON true
WHERE u."Email" LIKE 'expert.seed.%@inspecciono.dev'
  AND NOT EXISTS (
    SELECT 1 FROM "SearchServices" ss
    WHERE ss."ExpertProfileId" = ep."Id" AND ss."CategoryId" = m.cat_id AND ss."ServiceTypeId" = m.st_id
  );

-- Servicios extra variados (furgonetas, electrodomésticos, padres) por experto
INSERT INTO "SearchServices" (
  "ExpertProfileId", "AIId", "CategoryId", "ServiceTypeId", "Price",
  "Conditions", "DurationInHours", "CreatedAt", "IsActive"
)
SELECT
  ep."Id",
  NULL,
  m.cat_id,
  m.st_id,
  m.price + (ep."Id" % 7) * 3,
  m.conditions,
  m.hours,
  NOW(),
  true
FROM "ExpertProfiles" ep
JOIN "Users" u ON u."Id" = ep."UserId"
JOIN LATERAL (VALUES
  (5,  4, 48.00, 'Búsqueda de coches en portales de anuncios.', 2),
  (6,  4, 44.00, 'Búsqueda de motos en portales.', 2),
  (3,  4, 56.00, 'Búsqueda inmobiliaria automatizada.', 2)
) AS m(cat_id, st_id, price, conditions, hours) ON true
WHERE u."Email" LIKE 'expert.seed.%@inspecciono.dev'
  AND (ep."Id" % 3) = (m.cat_id % 3)
  AND NOT EXISTS (
    SELECT 1 FROM "SearchServices" ss
    WHERE ss."ExpertProfileId" = ep."Id" AND ss."CategoryId" = m.cat_id
  );

-- Más servicios para el experto original (id 3) en categorías con poca oferta
INSERT INTO "SearchServices" (
  "ExpertProfileId", "AIId", "CategoryId", "ServiceTypeId", "Price",
  "Conditions", "DurationInHours", "CreatedAt", "IsActive"
)
SELECT 3, NULL, m.cat_id, m.st_id, m.price, m.conditions, m.hours, NOW(), true
FROM (VALUES
  (3,  2, 62.00, 'Inspección presencial inmobiliaria.', 3),
  (6,  2, 54.00, 'Revisión de moto de segunda mano.', 2),
  (5,  2, 58.00, 'Revisión presencial de turismo.', 2),
  (6,  3, 36.00, 'Asesoramiento online compra de moto.', 1)
) AS m(cat_id, st_id, price, conditions, hours)
WHERE NOT EXISTS (
  SELECT 1 FROM "SearchServices" ss
  WHERE ss."ExpertProfileId" = 3 AND ss."CategoryId" = m.cat_id AND ss."ServiceTypeId" = m.st_id
);

-- ─── Imágenes (2 por categoría para servicios sin imagen) ───
INSERT INTO "SearchServiceImages" ("SearchServiceId", "ImageUrl", "ImageObjectName", "CreatedAt")
SELECT ss."Id", img.url, '', NOW()
FROM "SearchServices" ss
JOIN (VALUES
  (5,  'https://images.unsplash.com/photo-1492144534655-ae79c964c9d7?w=800&q=80'),
  (5,  'https://images.unsplash.com/photo-1503376780353-7e6692767b70?w=800&q=80'),
  (6,  'https://images.unsplash.com/photo-1558981806-ec527fa84c39?w=800&q=80'),
  (6,  'https://images.unsplash.com/photo-1568772585407-9361f9bf3a87?w=800&q=80'),
  (3,  'https://images.unsplash.com/photo-1600596542815-ffad4c1539a9?w=800&q=80'),
  (3,  'https://images.unsplash.com/photo-1502672260266-1c1ef2d93688?w=800&q=80')
) AS img(cat_id, url) ON img.cat_id = ss."CategoryId"
WHERE ss."IsActive" = true
  AND NOT EXISTS (SELECT 1 FROM "SearchServiceImages" si WHERE si."SearchServiceId" = ss."Id");

COMMIT;

-- Resumen
SELECT COUNT(*) AS expertos FROM "ExpertProfiles";
SELECT COUNT(*) AS servicios_activos FROM "SearchServices" WHERE "IsActive" = true;
SELECT COUNT(*) AS imagenes FROM "SearchServiceImages";
SELECT ep."Id", u."Name", ep."City", COUNT(ss."Id") AS servicios
FROM "ExpertProfiles" ep
JOIN "Users" u ON u."Id" = ep."UserId"
LEFT JOIN "SearchServices" ss ON ss."ExpertProfileId" = ep."Id" AND ss."IsActive" = true
GROUP BY ep."Id", u."Name", ep."City"
ORDER BY ep."Id";
ORDER BY ep."Id";
ORDER BY ep."Id";
