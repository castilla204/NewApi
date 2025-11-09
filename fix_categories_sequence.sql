-- Script para corregir la secuencia de IDs en la tabla Categories
-- Este error ocurre cuando la secuencia de PostgreSQL está desincronizada
-- con los valores reales en la tabla

-- Paso 1: Verificar el valor actual de la secuencia
SELECT setval(
    pg_get_serial_sequence('"Categories"', 'Id'),
    COALESCE((SELECT MAX("Id") FROM "Categories"), 1),
    true
);

-- Paso 2: Verificar que la secuencia está correcta
SELECT 
    pg_get_serial_sequence('"Categories"', 'Id') as sequence_name,
    last_value as current_sequence_value,
    (SELECT MAX("Id") FROM "Categories") as max_id_in_table
FROM pg_get_serial_sequence('"Categories"', 'Id')::regclass;

-- Si el max_id_in_table es mayor que current_sequence_value, 
-- la secuencia se actualizará automáticamente con el comando setval


