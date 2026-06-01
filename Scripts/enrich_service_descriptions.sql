-- Enriquece descripciones de tipos de servicio y condiciones de todos los SearchServices activos.
-- Ejecutar: psql ... -f enrich_service_descriptions.sql

BEGIN;

-- ─── 1. Descripciones largas de ServiceTypes (sección "Acerca del servicio") ───
UPDATE "ServiceTypes" SET "Description" = $desc$
Revisión presencial con experto verificado. Acude al punto acordado para inspeccionar el bien, contrastar el anuncio y detectar defectos o incoherencias. Incluye informe con fotos y conclusiones claras. El pago queda en custodia hasta que valides el trabajo.
$desc$, "UpdatedAt" = NOW()
WHERE "Id" = 2;

UPDATE "ServiceTypes" SET "Description" = $desc$
Revisión online sin desplazarte: videollamada y/o análisis del anuncio, fotos y documentación que envíes. El experto señala riesgos y qué comprobar antes de pagar. Ideal para filtrar opciones a distancia o preparar una visita con checklist profesional.
$desc$, "UpdatedAt" = NOW()
WHERE "Id" = 3;

UPDATE "ServiceTypes" SET "Description" = $desc$
Búsqueda activa en portales según tus criterios (precio, zona, modelo). El experto monitoriza publicaciones, descarta anuncios dudosos y te envía opciones comentadas. Ahorra tiempo y reduce el riesgo de fraudes o precios irreales.
$desc$, "UpdatedAt" = NOW()
WHERE "Id" = 4;

UPDATE "ServiceTypes" SET "Description" = $desc$
Búsqueda en portales más revisión experta de las mejores candidatas (online o presencial). Un solo interlocutor para prospección y validación antes de comprar. El servicio más completo de Inspecciono.
$desc$, "UpdatedAt" = NOW()
WHERE "Id" = 5;

UPDATE "ServiceTypes" SET "Description" = $desc$
Servicio estándar de la plataforma. Elige revisión presencial, online o búsqueda según tu compra.
$desc$, "UpdatedAt" = NOW()
WHERE "Id" = 1;

-- ─── 2. Condiciones enriquecidas por categoría + tipo (sección "Detalles del experto") ───
UPDATE "SearchServices" ss
SET "Conditions" = CASE
  -- Inmobiliaria + presencial
  WHEN ss."CategoryId" = 3 AND ss."ServiceTypeId" = 2 THEN
    'Inspección presencial de inmuebles (pisos, casas o locales) antes de firmar arras o compraventa. Reviso humedades, estructura visible, instalaciones, orientación, ruidos, comunidad y coherencia entre anuncio y realidad.' || E'\n\n' ||
    'Entrego informe con fotos, puntos críticos y recomendaciones para negociar o descartar. Duración habitual según metros cuadrados; coordinamos fecha en tu franja disponible.' ||
    COALESCE(E'\n\n' || NULLIF(TRIM(ep."City"), ''), '') ||
    COALESCE(CASE WHEN ep."Country" IS NOT NULL AND ep."Country" <> '' THEN ' · ' || ep."Country" ELSE '' END, '')

  -- Inmobiliaria + online
  WHEN ss."CategoryId" = 3 AND ss."ServiceTypeId" = 3 THEN
    'Revisión online de inmuebles: analizo el anuncio, planos, fotos y vídeos que envíes, y en videollamada repasamos dudas sobre estado, precio de mercado y documentación a exigir al vendedor.' || E'\n\n' ||
    'Te indico qué visitar in situ si decides ir y qué preguntas hacer a la agencia o propietario. Informe y checklist por escrito en 24–48 h laborables.' ||
    COALESCE(E'\n\nÁmbito: ' || NULLIF(TRIM(ep."City"), ''), '')

  -- Coches + presencial
  WHEN ss."CategoryId" = 5 AND ss."ServiceTypeId" = 2 THEN
    'Revisión presencial de turismos de ocasión: carrocería, pintura, neumáticos, interior, motor en ralentí, prueba dinámica si el vendedor lo permite y lectura de avisos OBD cuando procede.' || E'\n\n' ||
    'Contraste kilómetros, equipamiento y historial declarado con lo observado. Informe con fotos y veredicto claro (comprar, negociar o descartar) en un plazo máximo de 24 h desde la inspección.' ||
    COALESCE(E'\n\n' || 'Desplazamiento habitual desde ' || NULLIF(TRIM(ep."City"), ''), '')

  -- Motos + presencial
  WHEN ss."CategoryId" = 6 AND ss."ServiceTypeId" = 2 THEN
    'Inspección presencial de motos de segunda mano: chasis, suspensiones, transmisión, frenos, neumáticos, escapes, luces y estado general. Compruebo número de bastidor, ITV vigente y coherencia del anuncio.' || E'\n\n' ||
    'Informe con fotos y recomendaciones de mantenimiento o riesgos mecánicos. Ideal antes de entregar señal o transportar la moto a otra provincia.' ||
    COALESCE(E'\n\n' || 'Base de operaciones: ' || NULLIF(TRIM(ep."City"), ''), '')

  -- Motos + online
  WHEN ss."CategoryId" = 6 AND ss."ServiceTypeId" = 3 THEN
    'Asesoramiento online para compra de moto: revisión del anuncio, fotos detalladas, ruidos en vídeo si los hay y checklist para la visita que hagas tú o con otro profesional.' || E'\n\n' ||
    'Te ayudo a detectar anuncios trampa, precios fuera de mercado y documentación imprescindible (DGT, cargas, revisiones). Respuesta con conclusiones por escrito en 24 h.' ||
    COALESCE(E'\n\n' || 'Experiencia en el mercado de ' || NULLIF(TRIM(ep."City"), ''), '')

  -- Furgonetas + presencial (por si se activan)
  WHEN ss."CategoryId" = 7 AND ss."ServiceTypeId" = 2 THEN
    'Revisión presencial de furgonetas y vehículos comerciales ligeros: estructura, puertas, suelo de carga, mecánica, kilometraje y uso profesional previo.' || E'\n\n' ||
    'Informe orientado a autónomos y empresas con estimación de costes de puesta a punto.' ||
    COALESCE(E'\n\n' || NULLIF(TRIM(ep."City"), ''), '')

  -- Pisos / Casas / Locales (categorías 8–10) + presencial
  WHEN ss."CategoryId" IN (8, 9, 10) AND ss."ServiceTypeId" = 2 THEN
    'Inspección presencial del inmueble con foco en habitabilidad, humedades, instalaciones y cumplimiento básico normativo visible. Informe fotográfico y resumen para tu decisión de compra o alquiler.' || E'\n\n' ||
    'Coordinamos visita con vendedor o inquilino según disponibilidad.' ||
    COALESCE(E'\n\n' || NULLIF(TRIM(ep."City"), ''), '')

  -- Búsqueda web (cualquier categoría vehículos/inmueble)
  WHEN ss."ServiceTypeId" = 4 THEN
  'Búsqueda personalizada en portales según tus filtros. Te envío listados comentados, descarto fraudes evidentes y priorizo opciones con mejor relación precio/estado.' || E'\n\n' ||
  'Actualizaciones periódicas hasta cerrar compra o pausar la búsqueda.' ||
  COALESCE(E'\n\n' || NULLIF(TRIM(ep."City"), ''), '')

  WHEN ss."ServiceTypeId" = 5 THEN
  'Paquete completo: búsqueda en portales más revisión experta de las mejores candidatas (online o presencial según convenga). Un único interlocutor para todo el proceso precompra.' || E'\n\n' ||
  COALESCE(E'\n\n' || NULLIF(TRIM(ep."City"), ''), '')

  ELSE ss."Conditions"
END
FROM "ExpertProfiles" ep
WHERE ss."ExpertProfileId" = ep."Id"
  AND ss."IsActive" = true;

-- Segunda pasada: cualquier activo que siga corto recibe ampliación genérica según tipo
UPDATE "SearchServices" ss
SET "Conditions" =
  COALESCE(NULLIF(TRIM(ss."Conditions"), ''), '') ||
  CASE WHEN LENGTH(COALESCE(ss."Conditions", '')) > 0 THEN E'\n\n' ELSE '' END ||
  CASE ss."ServiceTypeId"
    WHEN 2 THEN 'Incluye informe documentado, fotografías y soporte por chat hasta resolver las dudas principales del informe. Pago en custodia: se libera cuando confirmes que el trabajo cumple lo acordado.'
    WHEN 3 THEN 'Incluye videollamada o análisis asíncrono, resumen por escrito y recomendaciones antes de comprometer dinero. Sin desplazamiento inicial.'
    WHEN 4 THEN 'Monitorización de portales y envío de candidatas comentadas según tus criterios de precio y zona.'
    WHEN 5 THEN 'Búsqueda activa más validación experta de finalistas; modalidad de revisión acordada caso a caso.'
    ELSE 'Servicio verificado por Inspecciono con pago seguro y soporte ante incidencias.'
  END
FROM "ExpertProfiles" ep
WHERE ss."ExpertProfileId" = ep."Id"
  AND ss."IsActive" = true
  AND LENGTH(COALESCE(ss."Conditions", '')) < 200;

COMMIT;

-- Verificación
SELECT 'ServiceTypes' AS tbl, "Id", LENGTH("Description") AS chars FROM "ServiceTypes" WHERE "Id" <= 5
UNION ALL
SELECT 'SearchServices avg', NULL, AVG(LENGTH("Conditions"))::int FROM "SearchServices" WHERE "IsActive";
