# Estados Faltantes y Problemas de Mapeo en Base de Datos

## 🔍 Análisis de la Base de Datos

### Estados SearchHireStatus que EXISTEN en BD:
✅ `pending`
✅ `awaiting_client_decision`
✅ `disputed`
✅ `completed`
✅ `cancelled` (genérico)
✅ `transfer_failed`
✅ `dispute_resolved_client`
✅ `dispute_resolved_expert`
✅ `cancelled_by_no_response` (genérico - Cliente 100%, Experto 0%, Plataforma 0%)
✅ `completed_without_client_approval` (Cliente 0%, Experto 100%, Plataforma 0%)
✅ `cancelled_by_client_account_delete`
✅ `cancelled_by_expert_account_delete`

### Estados SearchHireStatus que FALTAN en BD:
❌ `cancelled_by_client_no_proposal` - **FALTA** (el código lo busca en línea 3632)
❌ `cancelled_by_expert_no_response` - **FALTA** (el código lo busca en línea 3687 y 2746)
❌ `cancelled_by_expert_no_report` - **FALTA** (debería existir para el evento expert_report)

---

## 🚨 PROBLEMAS CRÍTICOS ENCONTRADOS

### Problema 1: Estados específicos no existen

**Código busca pero no encuentra:**
1. `cancelled_by_client_no_proposal` (línea 3632) - Cuando cliente no propone
2. `cancelled_by_expert_no_response` (línea 3687, 2746) - Cuando experto no responde

**Impacto:**
- El código hace fallback a `cancelled_by_no_response` (genérico)
- `cancelled_by_no_response` tiene porcentajes: Cliente 100%, Experto 0%, Plataforma 0%
- Esto es **INCORRECTO** para cuando el cliente no propone (debería ser Cliente 0%, Experto 100%)

### Problema 2: Mapeos incorrectos o faltantes

**Mapeos actuales en BD:**
- `appointment_cancelled_by_no_response` → `cancelled` (genérico)
- `appointment_cancelled_by_no_report` → **NO HAY MAPEO**

**Problema:**
- `appointment_cancelled_by_no_response` se usa para DOS casos diferentes:
  1. Cliente no propone (debería mapear a `cancelled_by_client_no_proposal`)
  2. Experto no responde (debería mapear a `cancelled_by_expert_no_response`)
- `appointment_cancelled_by_no_report` no tiene mapeo, debería mapear a `cancelled_by_expert_no_report`

### Problema 3: Configuraciones de porcentajes

**Estado `cancelled_by_no_response` (genérico):**
- Cliente: 100%
- Experto: 0%
- Plataforma: 0%
- **Problema**: Este porcentaje es correcto solo para cuando el experto no responde, pero se usa como fallback para cuando el cliente no propone (donde debería ser Cliente 0%, Experto 100%)

---

## ✅ SOLUCIONES REQUERIDAS

### 1. Crear estados faltantes en SearchHireStatus

```sql
-- Estado para cuando cliente no propone
INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsFinalizationStatus", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
VALUES ('SearchHireStatus', 'CancelledByClientNoProposal', 'cancelled_by_client_no_proposal', 'Cancelado por Cliente No Propone', 'Cliente no propuso cita en 24h', true, true, 9, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT DO NOTHING;

-- Estado para cuando experto no responde
INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsFinalizationStatus", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
VALUES ('SearchHireStatus', 'CancelledByExpertNoResponse', 'cancelled_by_expert_no_response', 'Cancelado por Experto No Responde', 'Experto no respondió a propuesta en 24h', true, true, 10, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT DO NOTHING;

-- Estado para cuando experto no envía reporte
INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsFinalizationStatus", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
VALUES ('SearchHireStatus', 'CancelledByExpertNoReport', 'cancelled_by_expert_no_report', 'Cancelado por Experto No Envía Reporte', 'Experto no envió reporte en 24h', true, true, 11, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT DO NOTHING;
```

### 2. Crear configuraciones de porcentajes

```sql
-- Porcentajes para cliente no propone (Cliente 0%, Experto 100%, Plataforma 0%)
INSERT INTO "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT 
    s."Id",
    NULL,
    NULL,
    0,   -- Cliente: 0% (culpa del cliente)
    100, -- Experto: 100% (recibe todo)
    0,   -- Plataforma: 0%
    true,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'SearchHireStatus' 
AND s."StatusValue" = 'cancelled_by_client_no_proposal'
AND NOT EXISTS (
    SELECT 1 FROM "StatusConfigurations" sc 
    WHERE sc."StatusId" = s."Id" 
    AND sc."CategoryId" IS NULL 
    AND sc."ServiceTypeCategoryId" IS NULL
);

-- Porcentajes para experto no responde (Cliente 100%, Experto 0%, Plataforma 0%)
INSERT INTO "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT 
    s."Id",
    NULL,
    NULL,
    100, -- Cliente: 100% (recibe todo, culpa del experto)
    0,   -- Experto: 0% (culpa del experto)
    0,   -- Plataforma: 0%
    true,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'SearchHireStatus' 
AND s."StatusValue" = 'cancelled_by_expert_no_response'
AND NOT EXISTS (
    SELECT 1 FROM "StatusConfigurations" sc 
    WHERE sc."StatusId" = s."Id" 
    AND sc."CategoryId" IS NULL 
    AND sc."ServiceTypeCategoryId" IS NULL
);

-- Porcentajes para experto no envía reporte (Cliente 95%, Experto 0%, Plataforma 5%)
-- Nota: Usar los mismos porcentajes que appointment_cancelled_by_no_report
INSERT INTO "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT 
    s."Id",
    NULL,
    NULL,
    95,  -- Cliente: 95% (similar a appointment_cancelled_by_no_report)
    0,   -- Experto: 0%
    5,   -- Plataforma: 5%
    true,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'SearchHireStatus' 
AND s."StatusValue" = 'cancelled_by_expert_no_report'
AND NOT EXISTS (
    SELECT 1 FROM "StatusConfigurations" sc 
    WHERE sc."StatusId" = s."Id" 
    AND sc."CategoryId" IS NULL 
    AND sc."ServiceTypeCategoryId" IS NULL
);
```

### 3. Crear/Actualizar mapeos

```sql
-- Eliminar mapeo genérico incorrecto
DELETE FROM "StatusMappings" 
WHERE "SourceStatusId" = (SELECT "Id" FROM "SystemStatuses" WHERE "StatusValue" = 'appointment_cancelled_by_no_response' AND "StatusType" = 'AppointmentStatus')
AND "TargetStatusId" = (SELECT "Id" FROM "SystemStatuses" WHERE "StatusValue" = 'cancelled' AND "StatusType" = 'SearchHireStatus');

-- Nota: Los mapeos específicos se manejan en el código, no en BD, porque el mismo AppointmentStatus puede mapear a diferentes SearchHireStatus según el contexto
-- El código ya maneja esto correctamente buscando primero el estado específico y luego el genérico

-- Crear mapeo para appointment_cancelled_by_no_report → cancelled_by_expert_no_report
INSERT INTO "StatusMappings" ("SourceStatusId", "TargetStatusId", "IsActive", "CreatedAt", "UpdatedAt")
SELECT 
    ss_source."Id",
    ss_target."Id",
    true,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
FROM "SystemStatuses" ss_source
CROSS JOIN "SystemStatuses" ss_target
WHERE ss_source."StatusValue" = 'appointment_cancelled_by_no_report'
AND ss_source."StatusType" = 'AppointmentStatus'
AND ss_target."StatusValue" = 'cancelled_by_expert_no_report'
AND ss_target."StatusType" = 'SearchHireStatus'
AND NOT EXISTS (
    SELECT 1 FROM "StatusMappings" sm
    WHERE sm."SourceStatusId" = ss_source."Id"
    AND sm."TargetStatusId" = ss_target."Id"
);
```

---

## 📊 Resumen de Estados y Porcentajes Esperados

| Evento | AppointmentStatus | SearchHireStatus (Específico) | Cliente % | Experto % | Plataforma % | Estado en BD |
|--------|------------------|-------------------------------|-----------|-----------|--------------|--------------|
| Cliente no propone | `appointment_cancelled_by_no_response` | `cancelled_by_client_no_proposal` | 0% | 100% | 0% | ❌ FALTA |
| Experto no responde | `appointment_cancelled_by_no_response` | `cancelled_by_expert_no_response` | 100% | 0% | 0% | ❌ FALTA |
| Experto no envía reporte | `appointment_cancelled_by_no_report` | `cancelled_by_expert_no_report` | 95% | 0% | 5% | ❌ FALTA |
| Cliente no decide | `appointment_report_sent` | `completed_without_client_approval` | 0% | 100% | 0% | ✅ EXISTE |

---

## ⚠️ NOTA IMPORTANTE

El código actualmente maneja los mapeos dinámicamente según el contexto (timer "proposal" vs "response"), lo cual es correcto. Sin embargo, necesita que los estados específicos existan en la BD para funcionar correctamente.

El estado genérico `cancelled_by_no_response` con Cliente 100% es incorrecto como fallback para cuando el cliente no propone, por lo que es crítico crear los estados específicos.

## ✅ CÓDIGO ACTUALIZADO

El código en `AppointmentService.cs` ha sido actualizado para:
1. ✅ Buscar estado específico `cancelled_by_expert_no_report` para el evento "expert_report"
2. ✅ Hacer fallback al estado genérico `cancelled` si no existe el específico
3. ✅ Usar el estado correcto para procesar dinero (SearchHireStatus específico o AppointmentStatus como fallback)

## 📝 PRÓXIMOS PASOS

1. **Ejecutar el script SQL** `SQL_CREAR_ESTADOS_FALTANTES.sql` para crear los estados faltantes
2. **Verificar** que los estados se crearon correctamente
3. **Verificar** que las configuraciones de porcentajes se crearon correctamente
4. **Probar** los eventos para asegurar que usan los estados correctos

