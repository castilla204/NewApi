-- ============================================
-- Script para crear índice compuesto en ExpertAvailabilities
-- Mejora el rendimiento de la query que filtra por ExpertId, IsActive y EffectiveTo
-- ============================================

-- Crear índice compuesto para optimizar la query de disponibilidades
CREATE INDEX IF NOT EXISTS "IX_ExpertAvailabilities_ExpertId_IsActive_EffectiveTo" 
ON "ExpertAvailabilities" ("ExpertId", "IsActive", "EffectiveTo") 
WHERE "IsActive" = true AND "EffectiveTo" IS NULL;

-- Verificar que se creó correctamente
SELECT 
    indexname, 
    indexdef 
FROM pg_indexes 
WHERE tablename = 'ExpertAvailabilities' 
  AND indexname = 'IX_ExpertAvailabilities_ExpertId_IsActive_EffectiveTo';

-- Ver estadísticas del índice
SELECT 
    schemaname,
    tablename,
    indexname,
    idx_scan as index_scans,
    idx_tup_read as tuples_read,
    idx_tup_fetch as tuples_fetched
FROM pg_stat_user_indexes
WHERE tablename = 'ExpertAvailabilities'
  AND indexname = 'IX_ExpertAvailabilities_ExpertId_IsActive_EffectiveTo';
