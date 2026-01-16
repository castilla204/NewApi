# 📊 Análisis de Porcentajes de Distribución de Dinero para AppointmentStatus

## 🎯 Estados de Finalización que DEBEN tener configuración

Según el código, los siguientes estados de `AppointmentStatus` son estados de finalización (`IsFinalizationStatus = true`) y **DEBEN** tener configuración de distribución de dinero:

### 1. **appointment_cancelled_by_client_no_proposal**
- **Cuándo se usa**: Timer "proposal" expira (cliente no propone cita en 24h)
- **Lógica esperada**: 100% refund al cliente, 0% experto, 0% plataforma
- **Comentario en código**: `// Usa los % del AppointmentStatus (0/100/0) porque tiene configuración` ❌ **ERROR EN COMENTARIO**
- **Porcentajes correctos**: `ClientPercentage: 100, ExpertPercentage: 0, PlatformPercentage: 0`

### 2. **appointment_cancelled_by_expert_no_response**
- **Cuándo se usa**: Timer "response" expira (experto no responde a propuesta en 24h)
- **Lógica esperada**: 100% refund al cliente, 0% experto, 0% plataforma
- **Comentario en código**: `// Usa los % del AppointmentStatus (100/0/0) porque tiene configuración` ✅ **CORRECTO**
- **Porcentajes correctos**: `ClientPercentage: 100, ExpertPercentage: 0, PlatformPercentage: 0`

### 3. **appointment_cancelled_by_no_report**
- **Cuándo se usa**: Timer "expert_report" expira (experto no envía reporte en 24h)
- **Lógica esperada**: 95% refund al cliente, 0% experto, 5% plataforma (penalización menor)
- **Comentario en código**: `// Usa los % del AppointmentStatus (95/0/5) porque tiene configuración` ✅ **CORRECTO**
- **Porcentajes correctos**: `ClientPercentage: 95, ExpertPercentage: 0, PlatformPercentage: 5`

### 4. **appointment_cancelled_by_expert_rejection**
- **Cuándo se usa**: Experto rechaza 2 veces (segundo rechazo)
- **Lógica esperada**: Penalización máxima al experto
- **Comentario en código**: `// Segundo rechazo del experto - penalización máxima`
- **Porcentajes correctos**: `ClientPercentage: 100, ExpertPercentage: 0, PlatformPercentage: 0` (o según política de penalización)

### 5. **appointment_cancelled_by_client_second**
- **Cuándo se usa**: Segunda cancelación del cliente
- **Lógica esperada**: Penalización al cliente (menor que primera cancelación)
- **Porcentajes correctos**: Depende de política, pero típicamente: `ClientPercentage: 50-80, ExpertPercentage: 0-20, PlatformPercentage: 20-50`

### 6. **appointment_cancelled_by_expert_second**
- **Cuándo se usa**: Segunda cancelación del experto
- **Lógica esperada**: Penalización máxima al experto
- **Porcentajes correctos**: `ClientPercentage: 100, ExpertPercentage: 0, PlatformPercentage: 0`

### 7. **appointment_completed_without_client_approval**
- **Cuándo se usa**: Timer "client_decision" expira (cliente no decide en 24h)
- **Lógica esperada**: 0% cliente, 100% experto, 0% plataforma (completado a favor del experto)
- **Comentario en código**: `// Usa los % del AppointmentStatus (0/100/0) porque tiene configuración` ✅ **CORRECTO**
- **Porcentajes correctos**: `ClientPercentage: 0, ExpertPercentage: 100, PlatformPercentage: 0`

## ⚠️ Estados que NO deben tener configuración (no son de finalización)

- `awaiting_appointment` - Estado intermedio
- `appointment_proposed` - Estado intermedio
- `appointment_confirmed` - Estado intermedio
- `appointment_rejected` - Estado intermedio (primer rechazo)
- `appointment_cancelled_by_client` - Primera cancelación (no es finalización)
- `appointment_cancelled_by_expert` - Primera cancelación (no es finalización)
- `appointment_awaiting_report` - Estado intermedio
- `appointment_report_sent` - Estado intermedio
- `appointment_cancelled_by_no_response` - [DEPRECATED] No debe usarse

## 📋 Checklist de Verificación

Para cada estado de finalización, verificar:

1. ✅ Existe en `SystemStatuses` con `IsFinalizationStatus = true`
2. ✅ Tiene al menos una configuración global en `StatusConfigurations` (CategoryId = NULL, ServiceTypeCategoryId = NULL)
3. ✅ Los porcentajes suman exactamente 100%
4. ✅ Los porcentajes son correctos según la lógica de negocio
5. ✅ La configuración está activa (`IsActive = true`)

## 🔍 Consultas SQL para Verificación

```sql
-- 1. Verificar estados de finalización
SELECT 
    ss."Id",
    ss."StatusValue",
    ss."StatusName",
    ss."DisplayName",
    ss."IsFinalizationStatus"
FROM "SystemStatuses" ss
WHERE ss."StatusType" = 'AppointmentStatus'
    AND ss."IsFinalizationStatus" = true
ORDER BY ss."StatusValue";

-- 2. Verificar configuraciones existentes
SELECT 
    sc."Id",
    ss."StatusValue",
    ss."StatusName",
    sc."ClientPercentage",
    sc."ExpertPercentage",
    sc."PlatformPercentage",
    sc."CategoryId",
    sc."ServiceTypeCategoryId",
    sc."IsActive",
    (sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") as TotalPercentage
FROM "StatusConfigurations" sc
INNER JOIN "SystemStatuses" ss ON sc."StatusId" = ss."Id"
WHERE ss."StatusType" = 'AppointmentStatus'
    AND ss."IsFinalizationStatus" = true
ORDER BY ss."StatusValue", sc."CategoryId" NULLS FIRST, sc."ServiceTypeCategoryId" NULLS FIRST;

-- 3. Verificar estados de finalización SIN configuración
SELECT 
    ss."Id",
    ss."StatusValue",
    ss."StatusName"
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

-- 4. Verificar configuraciones con porcentajes incorrectos (no suman 100%)
SELECT 
    sc."Id",
    ss."StatusValue",
    sc."ClientPercentage",
    sc."ExpertPercentage",
    sc."PlatformPercentage",
    (sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") as TotalPercentage
FROM "StatusConfigurations" sc
INNER JOIN "SystemStatuses" ss ON sc."StatusId" = ss."Id"
WHERE ss."StatusType" = 'AppointmentStatus'
    AND ss."IsFinalizationStatus" = true
    AND sc."IsActive" = true
    AND ABS((sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") - 100) > 0.01
ORDER BY ss."StatusValue";
```
