-- Añade la columna opcional "Formacion" a ExpertProfiles.
-- Guarda un JSON con la formación del experto (lista de items { titulo, centro, anio }).
-- Es OPCIONAL y no requiere verificación de ninguna entidad: se muestra al cliente
-- como señal positiva de confianza.
--
-- Ejecutar una sola vez contra la base de datos (Render/Postgres).
-- Es seguro: columna nullable, sin tocar datos existentes.

ALTER TABLE "ExpertProfiles"
    ADD COLUMN IF NOT EXISTS "Formacion" text NULL;
