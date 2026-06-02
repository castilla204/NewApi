-- Reseñas de demo: varias por cada experto con servicios activos.
-- Las reseñas pertenecen al experto (User) y se muestran en todos sus SearchServices.
-- Idempotente: revisores reviewer.seed.* y no duplica (ExpertId, ReviewerId).
--
-- Ejecutar (Render / Postgres de la API):
--   psql "$DATABASE_URL" -f Scripts/seed_reviews_for_experts.sql

BEGIN;

-- ─── 1. Usuarios cliente ficticios (revisores) ───
INSERT INTO "Users" (
  "Name", "Email", "Password", "GoogleId", "PhoneNumber", "PhoneVerified",
  "CreatedAt", "IsBlocked", "Role", "IsDeleted", "Balance"
)
SELECT
  v.name,
  v.email,
  NULL,
  v.google_id,
  NULL,
  false,
  v.created_at,
  false,
  0,
  false,
  0
FROM (VALUES
  ('María L.',     'reviewer.seed.01@inspecciono.dev', 'seed-reviewer-01', NOW() - INTERVAL '14 months'),
  ('Jorge P.',     'reviewer.seed.02@inspecciono.dev', 'seed-reviewer-02', NOW() - INTERVAL '11 months'),
  ('Lucía V.',     'reviewer.seed.03@inspecciono.dev', 'seed-reviewer-03', NOW() - INTERVAL '9 months'),
  ('Andrés M.',    'reviewer.seed.04@inspecciono.dev', 'seed-reviewer-04', NOW() - INTERVAL '8 months'),
  ('Patricia R.',  'reviewer.seed.05@inspecciono.dev', 'seed-reviewer-05', NOW() - INTERVAL '6 months'),
  ('Rubén S.',     'reviewer.seed.06@inspecciono.dev', 'seed-reviewer-06', NOW() - INTERVAL '5 months'),
  ('Nuria G.',     'reviewer.seed.07@inspecciono.dev', 'seed-reviewer-07', NOW() - INTERVAL '4 months'),
  ('Iván T.',      'reviewer.seed.08@inspecciono.dev', 'seed-reviewer-08', NOW() - INTERVAL '3 months'),
  ('Claudia F.',   'reviewer.seed.09@inspecciono.dev', 'seed-reviewer-09', NOW() - INTERVAL '2 months'),
  ('Héctor D.',    'reviewer.seed.10@inspecciono.dev', 'seed-reviewer-10', NOW() - INTERVAL '18 months'),
  ('Beatriz N.',   'reviewer.seed.11@inspecciono.dev', 'seed-reviewer-11', NOW() - INTERVAL '16 months'),
  ('Óscar W.',     'reviewer.seed.12@inspecciono.dev', 'seed-reviewer-12', NOW() - INTERVAL '13 months'),
  ('Marta C.',     'reviewer.seed.13@inspecciono.dev', 'seed-reviewer-13', NOW() - INTERVAL '10 months'),
  ('Raúl B.',      'reviewer.seed.14@inspecciono.dev', 'seed-reviewer-14', NOW() - INTERVAL '7 months'),
  ('Silvia H.',    'reviewer.seed.15@inspecciono.dev', 'seed-reviewer-15', NOW() - INTERVAL '1 month')
) AS v(name, email, google_id, created_at)
WHERE NOT EXISTS (
  SELECT 1 FROM "Users" u WHERE LOWER(u."Email") = LOWER(v.email)
);

-- ─── 2. Expertos con al menos un servicio activo ───
WITH active_experts AS (
  SELECT DISTINCT ep."UserId" AS expert_user_id, ep."Id" AS expert_profile_id, ep."City"
  FROM "ExpertProfiles" ep
  INNER JOIN "SearchServices" ss ON ss."ExpertProfileId" = ep."Id" AND ss."IsActive" = true
),
reviewers AS (
  SELECT u."Id" AS reviewer_id, u."Email", ROW_NUMBER() OVER (ORDER BY u."Email") AS slot
  FROM "Users" u
  WHERE u."Email" LIKE 'reviewer.seed.%@inspecciono.dev'
),
review_templates AS (
  SELECT *
  FROM (VALUES
    (1, 5, 'Excelente experiencia. El informe fue claro, con fotos útiles y llegó el mismo día. Repetiría sin dudarlo.'),
    (2, 5, 'Muy profesional en la visita. Detectó detalles del anuncio que yo no había visto. Me ayudó a negociar el precio.'),
    (3, 4, 'Buen servicio en general. La revisión fue exhaustiva; solo tardó un poco más de lo esperado en enviar el PDF.'),
    (4, 5, 'Comunicación fluida por chat y videollamada. Me explicó cada punto del checklist antes de decidir la compra.'),
    (5, 5, 'Puntuales, educados y con criterio técnico. La inspección presencial valió cada euro.'),
    (6, 4, 'Informe completo y bien estructurado. Hubiera preferido alguna foto más del interior, pero el resultado fue útil.'),
    (7, 5, 'Gran tranquilidad antes de firmar. Señaló humedades y ruidos que no aparecían en el anuncio.'),
    (8, 3, 'Correcto, aunque tuve que insistir para concretar la hora. El contenido del informe sí cumplió lo prometido.'),
    (9, 5, 'Revisión online muy práctica: en una hora descarté un anuncio sospechoso y ahorré un desplazamiento.'),
    (10, 4, 'Detalle fino en motor y documentación. Recomendable para comprar coche de particulares.'),
    (11, 5, 'Trato cercano y explicaciones sin tecnicismos innecesarios. Ideal si es tu primera compra.'),
    (12, 5, 'Comparó el precio de mercado y me indicó qué era negociable. Muy útil para no pagar de más.'),
    (13, 4, 'Buena revisión del piso; faltó profundizar en la comunidad de vecinos, pero el resto impecable.'),
    (14, 5, 'Rápido, serio y con informe accionable. Contraté la búsqueda web y acertó con tres opciones.'),
    (15, 5, 'Experiencia impecable de principio a fin. El pago en custodia da mucha confianza.')
  ) AS t(slot, score, description)
)
INSERT INTO "Reviews" (
  "ReviewerId", "ExpertId", "SearchHireId", "Score", "Description", "Images", "CreatedAt"
)
SELECT
  rv.reviewer_id,
  ae.expert_user_id,
  NULL,
  rt.score,
  rt.description || CASE
    WHEN ae."City" IS NOT NULL AND TRIM(ae."City") <> '' THEN ' · ' || TRIM(ae."City")
    ELSE ''
  END,
  ARRAY[]::text[],
  NOW() - ((ae.expert_profile_id % 5) + rt.slot) * INTERVAL '12 days'
     - (rt.slot * INTERVAL '3 days')
FROM active_experts ae
CROSS JOIN review_templates rt
INNER JOIN reviewers rv ON rv.slot = rt.slot
WHERE NOT EXISTS (
  SELECT 1
  FROM "Reviews" r
  WHERE r."ExpertId" = ae.expert_user_id
    AND r."ReviewerId" = rv.reviewer_id
);

-- ─── 3. Fotos en ~25 % de reseñas seed (ReviewImages) ───
INSERT INTO "ReviewImages" ("ReviewId", "ImageUrl", "ImageObjectName", "CreatedAt")
SELECT
  r."Id",
  CASE (r."Id" % 3)
    WHEN 0 THEN 'https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=600&q=80'
    WHEN 1 THEN 'https://images.unsplash.com/photo-1600585154340-be6161a56a0c?w=600&q=80'
    ELSE 'https://images.unsplash.com/photo-1492144534655-ae79c964c9d7?w=600&q=80'
  END,
  '',
  r."CreatedAt"
FROM "Reviews" r
INNER JOIN "Users" reviewer ON reviewer."Id" = r."ReviewerId"
WHERE reviewer."Email" LIKE 'reviewer.seed.%@inspecciono.dev'
  AND (r."Id" % 4) = 0
  AND NOT EXISTS (
    SELECT 1 FROM "ReviewImages" ri WHERE ri."ReviewId" = r."Id"
  );

-- Segunda foto en reseñas con id múltiplo de 8
INSERT INTO "ReviewImages" ("ReviewId", "ImageUrl", "ImageObjectName", "CreatedAt")
SELECT
  r."Id",
  'https://images.unsplash.com/photo-1568605117036-5fe5e7bab0b8?w=600&q=80',
  '',
  r."CreatedAt"
FROM "Reviews" r
INNER JOIN "Users" reviewer ON reviewer."Id" = r."ReviewerId"
WHERE reviewer."Email" LIKE 'reviewer.seed.%@inspecciono.dev'
  AND (r."Id" % 8) = 0
  AND (SELECT COUNT(*) FROM "ReviewImages" ri WHERE ri."ReviewId" = r."Id") = 1;

COMMIT;

-- Resumen (sin duplicar filas por JOIN servicios × reseñas)
SELECT
  u_expert."Name" AS experto,
  ep."City",
  svc.cnt AS servicios_activos,
  rev.cnt AS reseñas,
  rev.media
FROM "ExpertProfiles" ep
JOIN "Users" u_expert ON u_expert."Id" = ep."UserId"
JOIN LATERAL (
  SELECT COUNT(*)::int AS cnt
  FROM "SearchServices" ss
  WHERE ss."ExpertProfileId" = ep."Id" AND ss."IsActive" = true
) svc ON true
JOIN LATERAL (
  SELECT COUNT(*)::int AS cnt, ROUND(AVG(r."Score")::numeric, 2) AS media
  FROM "Reviews" r
  WHERE r."ExpertId" = ep."UserId"
) rev ON true
WHERE svc.cnt > 0
ORDER BY rev.cnt DESC, experto
LIMIT 30;
