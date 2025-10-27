-- Script para limpiar jobs problemáticos de Hangfire
-- Ejecutar en pgAdmin conectado a tu base de datos

-- 1. Ver qué jobs problemáticos existen
SELECT 'Jobs problemáticos encontrados:' as info;
SELECT jobid, invocationdata::text as invocationdata_text
FROM hangfire.job 
WHERE invocationdata::text LIKE '%HangfireJobService%';

-- 2. Ver qué recurring jobs problemáticos existen
SELECT 'Recurring jobs problemáticos encontrados:' as info;
SELECT key, value
FROM hangfire.set 
WHERE value LIKE '%HangfireJobService%';

-- 3. Ver todos los jobs actuales
SELECT 'Todos los jobs actuales:' as info;
SELECT jobid, invocationdata::text as invocationdata_text
FROM hangfire.job 
ORDER BY jobid DESC
LIMIT 10;

-- 4. Ver todos los recurring jobs actuales
SELECT 'Todos los recurring jobs actuales:' as info;
SELECT key, value
FROM hangfire.set 
WHERE key LIKE '%recurring%'
ORDER BY key;

-- 5. LIMPIAR jobs problemáticos (descomenta para ejecutar)
-- DELETE FROM hangfire.job 
-- WHERE invocationdata::text LIKE '%HangfireJobService%';

-- 6. LIMPIAR recurring jobs problemáticos (descomenta para ejecutar)
-- DELETE FROM hangfire.set 
-- WHERE value LIKE '%HangfireJobService%';

-- 7. Verificar que se limpiaron (descomenta después de limpiar)
-- SELECT 'Verificación - Jobs restantes:' as info;
-- SELECT COUNT(*) as total_jobs FROM hangfire.job;
-- SELECT 'Verificación - Recurring jobs restantes:' as info;
-- SELECT COUNT(*) as total_recurring_jobs FROM hangfire.set WHERE key LIKE '%recurring%';
















