-- Script para corregir la secuencia de IDs en la tabla Categories
-- Ejecutar este script en PostgreSQL para sincronizar la secuencia

-- Paso 1: Verificar el estado actual
SELECT 
    'Current max ID in table' as check_type,
    MAX("Id") as value
FROM "Categories"
UNION ALL
SELECT 
    'Current sequence value' as check_type,
    last_value::text as value
FROM "Categories_Id_seq";

-- Paso 2: Corregir la secuencia
-- Esto establece la secuencia al máximo ID existente + 1
SELECT setval('"Categories_Id_seq"', 
    COALESCE((SELECT MAX("Id") FROM "Categories"), 0) + 1, 
    false);

-- Paso 3: Verificar que se corrigió
SELECT 
    'New sequence value' as check_type,
    last_value::text as value
FROM "Categories_Id_seq";


