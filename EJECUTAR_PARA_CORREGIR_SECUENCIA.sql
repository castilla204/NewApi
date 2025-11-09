-- ============================================
-- SCRIPT PARA CORREGIR LA SECUENCIA DE CATEGORIES
-- ============================================
-- Ejecuta este script en tu base de datos PostgreSQL
-- para corregir el error "duplicate key value violates unique constraint PK_Categories"

-- Opción 1: Corregir directamente (RECOMENDADO)
SELECT setval('"Categories_Id_seq"', 
    COALESCE((SELECT MAX("Id") FROM "Categories"), 0) + 1, 
    false);

-- Opción 2: Verificar antes y después
-- ANTES:
SELECT 
    'ANTES' as estado,
    (SELECT MAX("Id") FROM "Categories") as max_id_tabla,
    last_value as valor_secuencia
FROM "Categories_Id_seq";

-- CORREGIR:
SELECT setval('"Categories_Id_seq"', 
    COALESCE((SELECT MAX("Id") FROM "Categories"), 0) + 1, 
    false);

-- DESPUÉS:
SELECT 
    'DESPUÉS' as estado,
    (SELECT MAX("Id") FROM "Categories") as max_id_tabla,
    last_value as valor_secuencia
FROM "Categories_Id_seq";

-- El valor de la secuencia debería ser max_id_tabla + 1


