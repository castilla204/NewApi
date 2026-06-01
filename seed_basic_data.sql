-- Datos básicos iniciales para Inspecciono (BD vacía)
BEGIN;

-- ========== Categorías principales ==========
INSERT INTO "Categories" ("Id", "Name", "ParentId", "IsActive", "CreatedAt", "UpdatedAt")
VALUES
  (1, E'Electrodom\u00e9sticos', NULL, TRUE, NOW(), NOW()),
  (2, E'Veh\u00edculos', NULL, TRUE, NOW(), NOW()),
  (3, 'Inmuebles', NULL, TRUE, NOW(), NOW()),
  (4, 'Servicios', NULL, TRUE, NOW(), NOW())
ON CONFLICT ("Id") DO NOTHING;

-- ========== Subcategorías ==========
INSERT INTO "Categories" ("Id", "Name", "ParentId", "IsActive", "CreatedAt", "UpdatedAt")
VALUES
  (5, 'Coches', 2, TRUE, NOW(), NOW()),
  (6, 'Motos', 2, TRUE, NOW(), NOW()),
  (7, 'Furgonetas', 2, TRUE, NOW(), NOW()),
  (8, 'Pisos', 3, TRUE, NOW(), NOW()),
  (9, 'Casas', 3, TRUE, NOW(), NOW()),
  (10, 'Locales', 3, TRUE, NOW(), NOW()),
  (11, E'Electrodom\u00e9sticos grandes', 1, TRUE, NOW(), NOW()),
  (12, E'Electrodom\u00e9sticos peque\u00f1os', 1, TRUE, NOW(), NOW())
ON CONFLICT ("Id") DO NOTHING;

SELECT setval(pg_get_serial_sequence('"Categories"', 'Id'), GREATEST((SELECT MAX("Id") FROM "Categories"), 12));

-- ========== Tipos de entregables ==========
INSERT INTO "DeliverableTypes" ("Id", "Name", "DisplayName", "Description", "IsRequired", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
VALUES
  (1, 'PDF', 'Informe PDF', 'Informe detallado en formato PDF', TRUE, TRUE, 1, NOW(), NOW()),
  (2, 'Video', 'Vídeo de inspección', 'Vídeo grabado durante la revisión presencial.', FALSE, TRUE, 2, NOW(), NOW())
ON CONFLICT ("Id") DO NOTHING;

SELECT setval(pg_get_serial_sequence('"DeliverableTypes"', 'Id'), GREATEST((SELECT MAX("Id") FROM "DeliverableTypes"), 2));

-- ========== Plan de suscripción gratuito ==========
INSERT INTO "SubscriptionPlans" (
  "Id", "Name", "Description", "imageUrl", "PriceMonthly", "PriceYearly",
  "HumanFilter", "AdvanceIAModels", "AdvancedAdImagesAnalysis",
  "MaxSearches", "MinSearchInterval", "IsActive", "CreatedAt", "UpdatedAt"
)
VALUES (
  1, 'Gratis', 'Plan gratuito con búsquedas limitadas', '',
  0, 0, FALSE, FALSE, FALSE,
  5, 24, TRUE, NOW(), NOW()
)
ON CONFLICT ("Id") DO NOTHING;

SELECT setval(pg_get_serial_sequence('"SubscriptionPlans"', 'Id'), GREATEST((SELECT MAX("Id") FROM "SubscriptionPlans"), 1));

-- ========== Configuración del sistema ==========
INSERT INTO "SystemSettings" (
  "Id", "IsWhatsAppNotificationEnabled", "IsEmailNotificationEnabled", "Theme",
  "StripeMode", "CreatedAt", "UpdatedAt"
)
SELECT 1, TRUE, TRUE, 'light', 'production', NOW(), NOW()
WHERE NOT EXISTS (SELECT 1 FROM "SystemSettings" WHERE "Id" = 1);

SELECT setval(pg_get_serial_sequence('"SystemSettings"', 'Id'), GREATEST((SELECT MAX("Id") FROM "SystemSettings"), 1));

-- ========== Plataformas de anuncios ==========
INSERT INTO "Platforms" ("Id", "Name", "Description", "ImageUrl", "PlatformWebsiteUrl", "IsActive")
VALUES
  (1, 'Wallapop', 'Marketplace de segunda mano', '', 'https://es.wallapop.com', TRUE),
  (2, 'Coches.net', 'Portal de vehículos', '', 'https://www.coches.net', TRUE),
  (3, 'Milanuncios', 'Anuncios clasificados', '', 'https://www.milanuncios.com', TRUE),
  (4, 'Idealista', 'Portal inmobiliario', '', 'https://www.idealista.com', TRUE)
ON CONFLICT ("Id") DO NOTHING;

SELECT setval(pg_get_serial_sequence('"Platforms"', 'Id'), GREATEST((SELECT MAX("Id") FROM "Platforms"), 4));

-- ========== Tipos de servicio ==========
UPDATE "ServiceTypes"
SET
  "Name" = 'Default Service',
  "Description" = 'Tipo de servicio por defecto para registros existentes',
  "ServiceTypeCategoryId" = 2,
  "Position" = 0,
  "RequiresAppointment" = FALSE,
  "UpdatedAt" = NOW()
WHERE "Id" = 1;

INSERT INTO "ServiceTypes" (
  "Id", "Name", "Description", "ServiceTypeCategoryId", "Position",
  "IsActive", "RequiresAppointment", "CreatedAt", "UpdatedAt"
)
VALUES
  (2, E'Revisi\u00f3n presencial', 'Revisi\u00f3n presencial de un anuncio espec\u00edfico', 2, 1, TRUE, TRUE, NOW(), NOW()),
  (3, E'Revisi\u00f3n online', 'Revisi\u00f3n remota mediante videollamada o material enviado', 2, 2, TRUE, FALSE, NOW(), NOW()),
  (4, E'B\u00fasqueda web', 'B\u00fasqueda automatizada en portales de anuncios', 3, 1, TRUE, FALSE, NOW(), NOW()),
  (5, E'B\u00fasqueda web + revisi\u00f3n', 'B\u00fasqueda automatizada m\u00e1s revisi\u00f3n manual experta', 1, 1, TRUE, TRUE, NOW(), NOW())
ON CONFLICT ("Id") DO NOTHING;

SELECT setval(pg_get_serial_sequence('"ServiceTypes"', 'Id'), GREATEST((SELECT MAX("Id") FROM "ServiceTypes"), 5));

-- ========== Usuario administrador ==========
-- Role: 0=Client, 1=Expert, 2=Admin
INSERT INTO "Users" (
  "Id", "Name", "Email", "Password", "GoogleId", "PhoneNumber", "PhoneVerified",
  "CreatedAt", "IsBlocked", "Role", "IsDeleted", "SubscriptionPlanId", "Balance"
)
SELECT
  1,
  'Diego Castilla',
  'dcastillaa@gmail.com',
  NULL,
  'admin-pending-google-login',
  NULL,
  FALSE,
  NOW(),
  FALSE,
  2,
  FALSE,
  1,
  0
WHERE NOT EXISTS (
  SELECT 1 FROM "Users" WHERE LOWER("Email") = 'dcastillaa@gmail.com'
);

INSERT INTO "UserSettings" ("UserId", "IsWhatsAppEnabled", "IsEmailEnabled", "Theme", "CreatedAt", "UpdatedAt")
SELECT u."Id", TRUE, TRUE, 'light', NOW(), NOW()
FROM "Users" u
WHERE LOWER(u."Email") = 'dcastillaa@gmail.com'
  AND NOT EXISTS (SELECT 1 FROM "UserSettings" us WHERE us."UserId" = u."Id");

-- Asegurar rol admin si el usuario ya existía
UPDATE "Users"
SET "Role" = 2, "IsBlocked" = FALSE, "IsDeleted" = FALSE
WHERE LOWER("Email") = 'dcastillaa@gmail.com';

SELECT setval(pg_get_serial_sequence('"Users"', 'Id'), GREATEST((SELECT MAX("Id") FROM "Users"), 1));

COMMIT;
