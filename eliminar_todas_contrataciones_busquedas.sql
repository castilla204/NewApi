-- ============================================
-- Script para eliminar todas las contrataciones (SearchHires) y búsquedas (Searches)
-- Fecha: 15 de enero de 2026
-- ============================================

BEGIN;

-- ============================================
-- PASO 1: Eliminar dependencias de SearchHires
-- ============================================

-- Eliminar Appointments (dependen de SearchHires)
DELETE FROM "Appointments";
RAISE NOTICE 'Appointments eliminados';

-- Eliminar Conversations (dependen de SearchHires con CASCADE)
DELETE FROM "Conversations";
RAISE NOTICE 'Conversations eliminados';

-- Eliminar Disputes (dependen de SearchHires con CASCADE)
DELETE FROM "Disputes";
RAISE NOTICE 'Disputes eliminados';

-- Eliminar SearchHireDeliverables (dependen de SearchHires)
DELETE FROM "SearchHireDeliverables";
RAISE NOTICE 'SearchHireDeliverables eliminados';

-- Actualizar Reviews para poner SearchHireId en NULL (SET NULL)
UPDATE "Reviews" SET "SearchHireId" = NULL WHERE "SearchHireId" IS NOT NULL;
RAISE NOTICE 'Reviews actualizados (SearchHireId = NULL)';

-- ============================================
-- PASO 2: Eliminar SearchHires
-- ============================================

DELETE FROM "SearchHires";
RAISE NOTICE 'SearchHires eliminados';

-- ============================================
-- PASO 3: Eliminar dependencias de Searches
-- ============================================

-- Eliminar SearchResultsFiltered (dependen de SearchResults)
DELETE FROM "SearchResultsFiltered";
RAISE NOTICE 'SearchResultsFiltered eliminados';

-- Eliminar SearchResults (dependen de Searches)
DELETE FROM "SearchResults";
RAISE NOTICE 'SearchResults eliminados';

-- Eliminar SearchParameterPlatforms (dependen de SearchParameters)
DELETE FROM "SearchParameterPlatforms";
RAISE NOTICE 'SearchParameterPlatforms eliminados';

-- Eliminar SearchParameters (dependen de Searches)
DELETE FROM "SearchParameters";
RAISE NOTICE 'SearchParameters eliminados';

-- ============================================
-- PASO 4: Eliminar Searches
-- ============================================

DELETE FROM "Searches";
RAISE NOTICE 'Searches eliminados';

-- ============================================
-- Verificación final
-- ============================================

DO $$
DECLARE
    search_count INTEGER;
    searchhire_count INTEGER;
BEGIN
    SELECT COUNT(*) INTO search_count FROM "Searches";
    SELECT COUNT(*) INTO searchhire_count FROM "SearchHires";
    
    IF search_count = 0 AND searchhire_count = 0 THEN
        RAISE NOTICE '✅ Éxito: Todas las búsquedas y contrataciones han sido eliminadas';
    ELSE
        RAISE WARNING '⚠️ Advertencia: Quedan % búsquedas y % contrataciones', search_count, searchhire_count;
    END IF;
END $$;

COMMIT;

-- ============================================
-- Resumen de lo eliminado
-- ============================================
-- ✅ Appointments
-- ✅ Conversations
-- ✅ Disputes
-- ✅ SearchHireDeliverables
-- ✅ SearchHires
-- ✅ SearchResultsFiltered
-- ✅ SearchResults
-- ✅ SearchParameters
-- ✅ Searches
-- 
-- ⚠️ Reviews: Se mantienen pero con SearchHireId = NULL
-- ⚠️ SearchServices: Se mantienen (no se eliminan)
-- ⚠️ Users: Se mantienen (no se eliminan)
