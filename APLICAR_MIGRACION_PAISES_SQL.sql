-- ✅ MIGRACIÓN: Agregar campos Country y ExpertCountry
-- Ejecutar este script directamente en PostgreSQL si dotnet ef no funciona

-- 1. Agregar campo Country a ExpertProfiles
ALTER TABLE "ExpertProfiles"
ADD COLUMN IF NOT EXISTS "Country" text NULL;

-- 2. Agregar campo ExpertCountry a SearchHires
ALTER TABLE "SearchHires"
ADD COLUMN IF NOT EXISTS "ExpertCountry" text NULL;

-- 3. Verificar que las columnas se crearon correctamente
SELECT 
    table_name,
    column_name,
    data_type,
    is_nullable
FROM information_schema.columns
WHERE (table_name = 'ExpertProfiles' AND column_name = 'Country')
   OR (table_name = 'SearchHires' AND column_name = 'ExpertCountry')
ORDER BY table_name, column_name;

-- ✅ Si ves las dos columnas en el resultado, la migración fue exitosa








