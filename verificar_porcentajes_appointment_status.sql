-- ============================================================
-- SCRIPT DE VERIFICACIÓN: Porcentajes de Distribución de Dinero
-- para AppointmentStatus (Estados de Finalización)
-- ============================================================

-- 1. VERIFICAR ESTADOS DE FINALIZACIÓN
-- ============================================================
SELECT 
    '=== ESTADOS DE FINALIZACIÓN ===' as Seccion,
    ss."Id",
    ss."StatusValue",
    ss."StatusName",
    ss."DisplayName",
    ss."IsFinalizationStatus"
FROM "SystemStatuses" ss
WHERE ss."StatusType" = 'AppointmentStatus'
    AND ss."IsFinalizationStatus" = true
ORDER BY ss."StatusValue";

-- 2. VERIFICAR CONFIGURACIONES EXISTENTES
-- ============================================================
SELECT 
    '=== CONFIGURACIONES EXISTENTES ===' as Seccion,
    sc."Id" as ConfigId,
    ss."StatusValue",
    ss."StatusName",
    sc."ClientPercentage",
    sc."ExpertPercentage",
    sc."PlatformPercentage",
    (sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") as TotalPercentage,
    CASE 
        WHEN ABS((sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") - 100) <= 0.01 THEN '✅ CORRECTO'
        ELSE '❌ ERROR: No suma 100%'
    END as ValidacionSuma,
    sc."CategoryId",
    sc."ServiceTypeCategoryId",
    CASE 
        WHEN sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL THEN '🌐 GLOBAL'
        WHEN sc."CategoryId" IS NOT NULL AND sc."ServiceTypeCategoryId" IS NULL THEN '📁 CATEGORÍA'
        WHEN sc."CategoryId" IS NOT NULL AND sc."ServiceTypeCategoryId" IS NOT NULL THEN '🔧 CATEGORÍA + TIPO'
        ELSE '❓ DESCONOCIDO'
    END as TipoConfiguracion,
    sc."IsActive"
FROM "StatusConfigurations" sc
INNER JOIN "SystemStatuses" ss ON sc."StatusId" = ss."Id"
WHERE ss."StatusType" = 'AppointmentStatus'
    AND ss."IsFinalizationStatus" = true
ORDER BY ss."StatusValue", sc."CategoryId" NULLS FIRST, sc."ServiceTypeCategoryId" NULLS FIRST;

-- 3. VERIFICAR ESTADOS DE FINALIZACIÓN SIN CONFIGURACIÓN
-- ============================================================
SELECT 
    '=== ⚠️ ESTADOS SIN CONFIGURACIÓN ===' as Seccion,
    ss."Id",
    ss."StatusValue",
    ss."StatusName",
    ss."DisplayName"
FROM "SystemStatuses" ss
WHERE ss."StatusType" = 'AppointmentStatus'
    AND ss."IsFinalizationStatus" = true
    AND NOT EXISTS (
        SELECT 1 
        FROM "StatusConfigurations" sc 
        WHERE sc."StatusId" = ss."Id" 
            AND sc."IsActive" = true
    )
ORDER BY ss."StatusValue";

-- 4. VERIFICAR CONFIGURACIONES CON PORCENTAJES INCORRECTOS
-- ============================================================
SELECT 
    '=== ❌ CONFIGURACIONES CON ERRORES ===' as Seccion,
    sc."Id" as ConfigId,
    ss."StatusValue",
    ss."StatusName",
    sc."ClientPercentage",
    sc."ExpertPercentage",
    sc."PlatformPercentage",
    (sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") as TotalPercentage,
    ABS((sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") - 100) as Diferencia
FROM "StatusConfigurations" sc
INNER JOIN "SystemStatuses" ss ON sc."StatusId" = ss."Id"
WHERE ss."StatusType" = 'AppointmentStatus'
    AND ss."IsFinalizationStatus" = true
    AND sc."IsActive" = true
    AND ABS((sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") - 100) > 0.01
ORDER BY ss."StatusValue", ABS((sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") - 100) DESC;

-- 5. RESUMEN POR ESTADO
-- ============================================================
SELECT 
    '=== 📊 RESUMEN POR ESTADO ===' as Seccion,
    ss."StatusValue",
    ss."StatusName",
    COUNT(sc."Id") as NumConfiguraciones,
    COUNT(CASE WHEN sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL THEN 1 END) as ConfigGlobales,
    COUNT(CASE WHEN sc."CategoryId" IS NOT NULL THEN 1 END) as ConfigPorCategoria,
    COUNT(CASE WHEN sc."ServiceTypeCategoryId" IS NOT NULL THEN 1 END) as ConfigPorTipo,
    COUNT(CASE WHEN sc."IsActive" = true THEN 1 END) as ConfigActivas,
    COUNT(CASE WHEN ABS((sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") - 100) <= 0.01 THEN 1 END) as ConfigCorrectas,
    COUNT(CASE WHEN ABS((sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") - 100) > 0.01 THEN 1 END) as ConfigIncorrectas
FROM "SystemStatuses" ss
LEFT JOIN "StatusConfigurations" sc ON sc."StatusId" = ss."Id"
WHERE ss."StatusType" = 'AppointmentStatus'
    AND ss."IsFinalizationStatus" = true
GROUP BY ss."Id", ss."StatusValue", ss."StatusName"
ORDER BY ss."StatusValue";

-- 6. VALIDACIÓN DE PORCENTAJES ESPERADOS
-- ============================================================
-- Esta consulta muestra los porcentajes esperados según la lógica de negocio
SELECT 
    '=== ✅ PORCENTAJES ESPERADOS ===' as Seccion,
    ss."StatusValue",
    ss."StatusName",
    CASE ss."StatusValue"
        WHEN 'appointment_cancelled_by_client_no_proposal' THEN 'Cliente: 0%, Experto: 100%, Plataforma: 0%'
        WHEN 'appointment_cancelled_by_expert_no_response' THEN 'Cliente: 100%, Experto: 0%, Plataforma: 0%'
        WHEN 'appointment_cancelled_by_no_report' THEN 'Cliente: 95%, Experto: 0%, Plataforma: 5%'
        WHEN 'appointment_cancelled_by_expert_rejection' THEN 'Cliente: 100%, Experto: 0%, Plataforma: 0%'
        WHEN 'appointment_cancelled_by_client_second' THEN 'Cliente: 0%, Experto: 95%, Plataforma: 5%'
        WHEN 'appointment_cancelled_by_expert_second' THEN 'Cliente: 100%, Experto: 0%, Plataforma: 0%'
        WHEN 'appointment_completed_without_client_approval' THEN 'Cliente: 0%, Experto: 95%, Plataforma: 5%'
        ELSE '⚠️ VERIFICAR LÓGICA DE NEGOCIO'
    END as PorcentajesEsperados
FROM "SystemStatuses" ss
WHERE ss."StatusType" = 'AppointmentStatus'
    AND ss."IsFinalizationStatus" = true
ORDER BY ss."StatusValue";
