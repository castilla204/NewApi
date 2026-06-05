-- Expertos y servicios globales (Coches, Motos, Inmobiliaria)
-- Desactiva el resto de categorías. Idempotente (emails expert.global.*).
-- Ejecutar en PostgreSQL (Render): psql $DATABASE_URL -f Scripts/seed_global_world_experts_trim_categories.sql

BEGIN;

-- ─── 1. Solo Coches (5), Motos (6), Inmobiliaria (3) — sin padre Vehículos ───
UPDATE "Categories"
SET "Name" = 'Inmobiliaria', "IsActive" = true, "UpdatedAt" = NOW()
WHERE "Id" = 3;

UPDATE "Categories"
SET "ParentId" = NULL, "IsActive" = true, "UpdatedAt" = NOW()
WHERE "Id" IN (5, 6);

UPDATE "Categories"
SET "IsActive" = false, "UpdatedAt" = NOW()
WHERE "Id" IN (1, 2, 4, 7, 8, 9, 10, 11, 12);

-- Servicios fuera de las 3 categorías visibles → desactivar
UPDATE "SearchServices"
SET "IsActive" = false
WHERE "CategoryId" NOT IN (3, 5, 6);

-- Inmobiliaria: unificar subcategorías en la categoría padre (homepage usa categoryId=3)
UPDATE "SearchServices"
SET "CategoryId" = 3
WHERE "CategoryId" IN (8, 9, 10);

-- Servicios seed antiguos en categorías eliminadas
UPDATE "SearchServices" ss
SET "IsActive" = false
FROM "Users" u
JOIN "ExpertProfiles" ep ON ep."UserId" = u."Id"
WHERE ss."ExpertProfileId" = ep."Id"
  AND u."Email" LIKE 'expert.seed.%@inspecciono.dev'
  AND ss."CategoryId" NOT IN (3, 5, 6);

-- ─── 2. Usuarios experto globales ───
INSERT INTO "Users" (
  "Name", "Email", "Password", "GoogleId", "PhoneNumber", "PhoneVerified",
  "CreatedAt", "IsBlocked", "Role", "IsDeleted", "Balance"
)
SELECT v.name, v.email, NULL, v.google_id, NULL, false, NOW(), false, 1, false, 0
FROM (VALUES
  ('Sofia Mendez',       'expert.global.madrid@inspecciono.dev',       'seed-global-madrid'),
  ('Lucas Bernard',      'expert.global.paris@inspecciono.dev',        'seed-global-paris'),
  ('Emma Thompson',      'expert.global.london@inspecciono.dev',       'seed-global-london'),
  ('Hans Weber',         'expert.global.berlin@inspecciono.dev',       'seed-global-berlin'),
  ('Giulia Rossi',       'expert.global.rome@inspecciono.dev',         'seed-global-rome'),
  ('João Silva',         'expert.global.lisbon@inspecciono.dev',       'seed-global-lisbon'),
  ('Anna de Vries',      'expert.global.amsterdam@inspecciono.dev',    'seed-global-amsterdam'),
  ('Erik Lindqvist',     'expert.global.stockholm@inspecciono.dev',    'seed-global-stockholm'),
  ('Marco Novak',        'expert.global.prague@inspecciono.dev',       'seed-global-prague'),
  ('Yuki Tanaka',        'expert.global.tokyo@inspecciono.dev',        'seed-global-tokyo'),
  ('Min-jun Park',       'expert.global.seoul@inspecciono.dev',        'seed-global-seoul'),
  ('Raj Patel',          'expert.global.mumbai@inspecciono.dev',       'seed-global-mumbai'),
  ('Omar Al-Farsi',      'expert.global.dubai@inspecciono.dev',        'seed-global-dubai'),
  ('James Mitchell',     'expert.global.newyork@inspecciono.dev',      'seed-global-newyork'),
  ('Carlos Rivera',      'expert.global.miami@inspecciono.dev',        'seed-global-miami'),
  ('Diego Morales',      'expert.global.mexicocity@inspecciono.dev',   'seed-global-mexicocity'),
  ('Fernanda Lima',      'expert.global.saopaulo@inspecciono.dev',     'seed-global-saopaulo'),
  ('Mateo Fernández',    'expert.global.buenosaires@inspecciono.dev',  'seed-global-buenosaires'),
  ('Santiago Vargas',    'expert.global.bogota@inspecciono.dev',       'seed-global-bogota'),
  ('Camille Dubois',     'expert.global.montreal@inspecciono.dev',     'seed-global-montreal'),
  ('Liam O''Connor',     'expert.global.dublin@inspecciono.dev',       'seed-global-dublin'),
  ('Nikos Papadopoulos', 'expert.global.athens@inspecciono.dev',       'seed-global-athens'),
  ('Ahmet Yilmaz',       'expert.global.istanbul@inspecciono.dev',     'seed-global-istanbul'),
  ('Olivia Chen',        'expert.global.singapore@inspecciono.dev',    'seed-global-singapore'),
  ('Jack Morrison',      'expert.global.sydney@inspecciono.dev',       'seed-global-sydney'),
  ('Thabo Mbeki',        'expert.global.capetown@inspecciono.dev',     'seed-global-capetown'),
  ('Amira Benali',       'expert.global.casablanca@inspecciono.dev',   'seed-global-casablanca'),
  ('Barcelona Expert',   'expert.global.barcelona@inspecciono.dev',    'seed-global-barcelona'),
  ('Valencia Expert',    'expert.global.valencia@inspecciono.dev',     'seed-global-valencia'),
  ('Toronto Expert',     'expert.global.toronto@inspecciono.dev',      'seed-global-toronto'),
  ('Los Angeles Expert', 'expert.global.losangeles@inspecciono.dev',   'seed-global-losangeles'),
  ('Chicago Expert',     'expert.global.chicago@inspecciono.dev',      'seed-global-chicago'),
  ('Milan Expert',       'expert.global.milan@inspecciono.dev',        'seed-global-milan'),
  ('Warsaw Expert',      'expert.global.warsaw@inspecciono.dev',       'seed-global-warsaw'),
  ('Vienna Expert',      'expert.global.vienna@inspecciono.dev',       'seed-global-vienna'),
  ('Copenhagen Expert',  'expert.global.copenhagen@inspecciono.dev',   'seed-global-copenhagen'),
  ('Bangkok Expert',     'expert.global.bangkok@inspecciono.dev',      'seed-global-bangkok'),
  ('Jakarta Expert',     'expert.global.jakarta@inspecciono.dev',      'seed-global-jakarta'),
  ('Johannesburg Expert','expert.global.johannesburg@inspecciono.dev', 'seed-global-johannesburg')
) AS v(name, email, google_id)
WHERE NOT EXISTS (SELECT 1 FROM "Users" u WHERE u."Email" = v.email);

-- ─── 3. Perfiles (Stripe aprobado, coordenadas reales) ───
INSERT INTO "ExpertProfiles" (
  "UserId", "ProfilePictureUrl", "ProfilePictureObjectName", "Description",
  "OnboardingCompleted", "StripeStatus", "IsOnVacation", "CreatedAt",
  "Latitude", "Longitude", "Timezone", "Country", "City"
)
SELECT
  u."Id", v.pic, '', v.profile_desc, true, 2, false, NOW(),
  v.lat::text, v.lng::text, v.tz, v.country, v.city
FROM "Users" u
JOIN (VALUES
  ('expert.global.madrid@inspecciono.dev',       'Madrid',        'ES', '40.4168',  '-3.7038',  'Europe/Madrid',     'Revisión de coches e inmuebles en Madrid.', 'https://images.unsplash.com/photo-1494790108377-be9c29b29330?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.barcelona@inspecciono.dev',    'Barcelona',     'ES', '41.3851',  '2.1734',   'Europe/Madrid',     'Perito en Barcelona: vehículos e inmobiliaria.', 'https://images.unsplash.com/photo-1507003211169-0a1dd7228f2d?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.valencia@inspecciono.dev',     'Valencia',      'ES', '39.4699',  '-0.3763',  'Europe/Madrid',     'Inspecciones en Valencia y Comunidad Valenciana.', 'https://images.unsplash.com/photo-1438761681033-6461ffad8d80?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.paris@inspecciono.dev',        'Paris',         'FR', '48.8566',  '2.3522',   'Europe/Paris',      'Expert véhicules et immobilier à Paris.', 'https://images.unsplash.com/photo-1544005313-94ddf0286df2?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.london@inspecciono.dev',       'London',        'GB', '51.5074',  '-0.1278',  'Europe/London',     'UK vehicle and property inspections.', 'https://images.unsplash.com/photo-1472099645785-5658abf4ff4e?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.berlin@inspecciono.dev',       'Berlin',        'DE', '52.5200',  '13.4050',  'Europe/Berlin',     'Fahrzeug- und Immobilienchecks in Berlin.', 'https://images.unsplash.com/photo-1500648767791-00dcc994a43e?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.rome@inspecciono.dev',         'Rome',          'IT', '41.9028',  '12.4964',  'Europe/Rome',       'Revisioni auto e immobili a Roma.', 'https://images.unsplash.com/photo-1506794778202-cad84cf45f1d?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.milan@inspecciono.dev',        'Milan',         'IT', '45.4642',  '9.1900',   'Europe/Rome',       'Perizie veicoli e case a Milano.', 'https://images.unsplash.com/photo-1519085360753-af0119f7cbe7?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.lisbon@inspecciono.dev',       'Lisbon',        'PT', '38.7223',  '-9.1393',  'Europe/Lisbon',     'Inspeções em Lisboa e arredores.', 'https://images.unsplash.com/photo-1534528741775-53994a69daeb?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.amsterdam@inspecciono.dev',    'Amsterdam',     'NL', '52.3676',  '4.9041',   'Europe/Amsterdam',  'Auto- en vastgoedinspecties in Amsterdam.', 'https://images.unsplash.com/photo-1487412720507-e7ab37603c6f?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.dublin@inspecciono.dev',       'Dublin',        'IE', '53.3498',  '-6.2603',  'Europe/Dublin',     'Car and property checks in Dublin.', 'https://images.unsplash.com/photo-1507003211169-0a1dd7228f2d?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.stockholm@inspecciono.dev',    'Stockholm',     'SE', '59.3293',  '18.0686',  'Europe/Stockholm',  'Fordons- och bostadsinspektioner i Stockholm.', 'https://images.unsplash.com/photo-1494790108377-be9c29b29330?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.copenhagen@inspecciono.dev',   'Copenhagen',    'DK', '55.6761',  '12.5683',  'Europe/Copenhagen', 'Bil- og boliginspektion i København.', 'https://images.unsplash.com/photo-1438761681033-6461ffad8d80?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.vienna@inspecciono.dev',       'Vienna',        'AT', '48.2082',  '16.3738',  'Europe/Vienna',     'Fahrzeug- und Immobilienprüfung Wien.', 'https://images.unsplash.com/photo-1544005313-94ddf0286df2?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.prague@inspecciono.dev',       'Prague',        'CZ', '50.0755',  '14.4378',  'Europe/Prague',     'Kontrola vozidel a nemovitostí v Praze.', 'https://images.unsplash.com/photo-1472099645785-5658abf4ff4e?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.warsaw@inspecciono.dev',       'Warsaw',        'PL', '52.2297',  '21.0122',  'Europe/Warsaw',     'Inspekcje aut i nieruchomości w Warszawie.', 'https://images.unsplash.com/photo-1500648767791-00dcc994a43e?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.athens@inspecciono.dev',       'Athens',        'GR', '37.9838',  '23.7275',  'Europe/Athens',     'Έλεγχος αυτοκινήτων και ακινήτων.', 'https://images.unsplash.com/photo-1506794778202-cad84cf45f1d?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.istanbul@inspecciono.dev',    'Istanbul',      'TR', '41.0082',  '28.9784',  'Europe/Istanbul',   'Araç ve emlak denetimi İstanbul.', 'https://images.unsplash.com/photo-1519085360753-af0119f7cbe7?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.newyork@inspecciono.dev',      'New York',      'US', '40.7128',  '-74.0060', 'America/New_York',  'NYC car and real estate inspections.', 'https://images.unsplash.com/photo-1534528741775-53994a69daeb?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.miami@inspecciono.dev',        'Miami',         'US', '25.7617',  '-80.1918', 'America/New_York',  'Vehicle and property expert in Miami.', 'https://images.unsplash.com/photo-1487412720507-e7ab37603c6f?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.losangeles@inspecciono.dev',   'Los Angeles',   'US', '34.0522',  '-118.2437','America/Los_Angeles','LA automotive and housing inspections.', 'https://images.unsplash.com/photo-1494790108377-be9c29b29330?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.chicago@inspecciono.dev',      'Chicago',       'US', '41.8781',  '-87.6298', 'America/Chicago',   'Chicago pre-purchase inspections.', 'https://images.unsplash.com/photo-1507003211169-0a1dd7228f2d?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.toronto@inspecciono.dev',      'Toronto',       'CA', '43.6532',  '-79.3832', 'America/Toronto',   'Car and home inspections in Toronto.', 'https://images.unsplash.com/photo-1438761681033-6461ffad8d80?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.montreal@inspecciono.dev',     'Montreal',      'CA', '45.5017',  '-73.5673', 'America/Toronto',   'Inspections véhicules et maisons à Montréal.', 'https://images.unsplash.com/photo-1544005313-94ddf0286df2?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.mexicocity@inspecciono.dev',   'Mexico City',   'MX', '19.4326',  '-99.1332', 'America/Mexico_City','Revisiones en CDMX.', 'https://images.unsplash.com/photo-1472099645785-5658abf4ff4e?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.saopaulo@inspecciono.dev',      'São Paulo',     'BR', '-23.5505', '-46.6333', 'America/Sao_Paulo', 'Vistorias em São Paulo.', 'https://images.unsplash.com/photo-1500648767791-00dcc994a43e?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.buenosaires@inspecciono.dev',  'Buenos Aires',  'AR', '-34.6037', '-58.3816', 'America/Argentina/Buenos_Aires', 'Peritajes en Buenos Aires.', 'https://images.unsplash.com/photo-1506794778202-cad84cf45f1d?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.bogota@inspecciono.dev',       'Bogotá',        'CO', '4.7110',   '-74.0721', 'America/Bogota',    'Inspecciones en Bogotá.', 'https://images.unsplash.com/photo-1519085360753-af0119f7cbe7?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.tokyo@inspecciono.dev',        'Tokyo',         'JP', '35.6762',  '139.6503', 'Asia/Tokyo',        '東京の車両・不動産検査。', 'https://images.unsplash.com/photo-1534528741775-53994a69daeb?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.seoul@inspecciono.dev',        'Seoul',         'KR', '37.5665',  '126.9780', 'Asia/Seoul',        '서울 자동차·부동산 검수.', 'https://images.unsplash.com/photo-1487412720507-e7ab37603c6f?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.singapore@inspecciono.dev',    'Singapore',     'SG', '1.3521',   '103.8198', 'Asia/Singapore',    'Vehicle and property checks in Singapore.', 'https://images.unsplash.com/photo-1494790108377-be9c29b29330?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.mumbai@inspecciono.dev',       'Mumbai',        'IN', '19.0760',  '72.8777',  'Asia/Kolkata',      'Car and property inspections in Mumbai.', 'https://images.unsplash.com/photo-1507003211169-0a1dd7228f2d?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.dubai@inspecciono.dev',        'Dubai',         'AE', '25.2048',  '55.2708',  'Asia/Dubai',        'Vehicle and real estate inspections in Dubai.', 'https://images.unsplash.com/photo-1438761681033-6461ffad8d80?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.bangkok@inspecciono.dev',      'Bangkok',       'TH', '13.7563',  '100.5018', 'Asia/Bangkok',      'Inspections in Bangkok.', 'https://images.unsplash.com/photo-1544005313-94ddf0286df2?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.jakarta@inspecciono.dev',     'Jakarta',       'ID', '-6.2088',  '106.8456', 'Asia/Jakarta',      'Inspeksi kendaraan dan properti Jakarta.', 'https://images.unsplash.com/photo-1472099645785-5658abf4ff4e?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.sydney@inspecciono.dev',       'Sydney',        'AU', '-33.8688', '151.2093', 'Australia/Sydney',  'Pre-purchase inspections in Sydney.', 'https://images.unsplash.com/photo-1500648767791-00dcc994a43e?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.capetown@inspecciono.dev',     'Cape Town',     'ZA', '-33.9249', '18.4241',  'Africa/Johannesburg','Vehicle and property checks Cape Town.', 'https://images.unsplash.com/photo-1506794778202-cad84cf45f1d?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.johannesburg@inspecciono.dev','Johannesburg',  'ZA', '-26.2041', '28.0473',  'Africa/Johannesburg','Inspections in Johannesburg.', 'https://images.unsplash.com/photo-1519085360753-af0119f7cbe7?w=400&h=400&fit=crop&crop=faces'),
  ('expert.global.casablanca@inspecciono.dev',   'Casablanca',    'MA', '33.5731',  '-7.5898',  'Africa/Casablanca', 'Contrôles véhicules et biens à Casablanca.', 'https://images.unsplash.com/photo-1534528741775-53994a69daeb?w=400&h=400&fit=crop&crop=faces')
) AS v(email, city, country, lat, lng, tz, profile_desc, pic) ON u."Email" = v.email
WHERE u."Email" LIKE 'expert.global.%@inspecciono.dev'
  AND NOT EXISTS (SELECT 1 FROM "ExpertProfiles" ep WHERE ep."UserId" = u."Id");

-- ─── 4. Disponibilidad L-V ───
INSERT INTO "ExpertAvailabilities" (
  "ExpertId", "DaysOfWeek", "StartTime", "EndTime",
  "EffectiveFrom", "IsActive", "CreatedAt", "UpdatedAt"
)
SELECT ep."Id", '["Monday","Tuesday","Wednesday","Thursday","Friday"]',
  '09:00:00'::interval, '18:00:00'::interval, NOW(), true, NOW(), NOW()
FROM "ExpertProfiles" ep
JOIN "Users" u ON u."Id" = ep."UserId"
WHERE u."Email" LIKE 'expert.global.%@inspecciono.dev'
  AND NOT EXISTS (SELECT 1 FROM "ExpertAvailabilities" ea WHERE ea."ExpertId" = ep."Id");

-- ─── 5. Servicios: vehículo (5 o 6) + inmobiliaria (3) ───
INSERT INTO "SearchServices" (
  "ExpertProfileId", "AIId", "CategoryId", "ServiceTypeId", "Price",
  "Conditions", "DurationInHours", "CreatedAt", "IsActive"
)
SELECT ep."Id", NULL, m.cat_id, m.st_id, m.price, m.conditions, m.hours, NOW(), true
FROM "ExpertProfiles" ep
JOIN "Users" u ON u."Id" = ep."UserId"
JOIN LATERAL (VALUES
  (CASE WHEN (ep."Id" % 2) = 0 THEN 5 ELSE 6 END, 2, 49.00, 'Revisión presencial antes de comprar. Informe con fotos.', 2),
  (3, 2, 69.00, 'Inspección inmobiliaria presencial con informe detallado.', 3)
) AS m(cat_id, st_id, price, conditions, hours) ON true
WHERE u."Email" LIKE 'expert.global.%@inspecciono.dev'
  AND NOT EXISTS (
    SELECT 1 FROM "SearchServices" ss
    WHERE ss."ExpertProfileId" = ep."Id"
      AND ss."CategoryId" = m.cat_id
      AND ss."ServiceTypeId" = m.st_id
  );

-- Revisión online inmobiliaria (mitad de expertos)
INSERT INTO "SearchServices" (
  "ExpertProfileId", "AIId", "CategoryId", "ServiceTypeId", "Price",
  "Conditions", "DurationInHours", "CreatedAt", "IsActive"
)
SELECT ep."Id", NULL, 3, 3, 39.00 + (ep."Id" % 5) * 4,
  'Revisión online de anuncio inmobiliario antes de visitar.', 1, NOW(), true
FROM "ExpertProfiles" ep
JOIN "Users" u ON u."Id" = ep."UserId"
WHERE u."Email" LIKE 'expert.global.%@inspecciono.dev'
  AND (ep."Id" % 2) = 0
  AND NOT EXISTS (
    SELECT 1 FROM "SearchServices" ss
    WHERE ss."ExpertProfileId" = ep."Id" AND ss."CategoryId" = 3 AND ss."ServiceTypeId" = 3
  );

-- ─── 6. Imágenes por categoría ───
INSERT INTO "SearchServiceImages" ("SearchServiceId", "ImageUrl", "ImageObjectName", "CreatedAt")
SELECT ss."Id", img.url, '', NOW()
FROM "SearchServices" ss
JOIN "ExpertProfiles" ep ON ep."Id" = ss."ExpertProfileId"
JOIN "Users" u ON u."Id" = ep."UserId"
JOIN (VALUES
  (5, 'https://images.unsplash.com/photo-1492144534655-ae79c964c9d7?w=800&q=80'),
  (5, 'https://images.unsplash.com/photo-1503376780353-7e6692767b70?w=800&q=80'),
  (6, 'https://images.unsplash.com/photo-1558981806-ec527fa84c39?w=800&q=80'),
  (6, 'https://images.unsplash.com/photo-1568772585407-9361f9bf3a87?w=800&q=80'),
  (3, 'https://images.unsplash.com/photo-1600596542815-ffad4c1539a9?w=800&q=80'),
  (3, 'https://images.unsplash.com/photo-1502672260266-1c1ef2d93688?w=800&q=80')
) AS img(cat_id, url) ON img.cat_id = ss."CategoryId"
WHERE u."Email" LIKE 'expert.global.%@inspecciono.dev'
  AND ss."IsActive" = true
  AND NOT EXISTS (SELECT 1 FROM "SearchServiceImages" si WHERE si."SearchServiceId" = ss."Id");

COMMIT;

-- Resumen
SELECT c."Id", c."Name", c."IsActive", c."ParentId"
FROM "Categories" c
ORDER BY c."Id";

SELECT COUNT(*) AS expertos_globales
FROM "ExpertProfiles" ep
JOIN "Users" u ON u."Id" = ep."UserId"
WHERE u."Email" LIKE 'expert.global.%@inspecciono.dev';

SELECT ss."CategoryId", c."Name", COUNT(*) AS servicios
FROM "SearchServices" ss
JOIN "Categories" c ON c."Id" = ss."CategoryId"
WHERE ss."IsActive" = true
GROUP BY ss."CategoryId", c."Name"
ORDER BY ss."CategoryId";

SELECT ep."Country", COUNT(DISTINCT ep."Id") AS expertos
FROM "ExpertProfiles" ep
JOIN "Users" u ON u."Id" = ep."UserId"
WHERE u."Email" LIKE 'expert.global.%@inspecciono.dev'
GROUP BY ep."Country"
ORDER BY expertos DESC;
