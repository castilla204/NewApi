# 📊 Resultado del Análisis: Porcentajes de Distribución de Dinero

## ✅ ESTADOS DE FINALIZACIÓN ENCONTRADOS (8 estados)

1. ✅ `appointment_cancelled_by_client_no_proposal` (ID: 24)
2. ✅ `appointment_cancelled_by_client_second` (ID: 15)
3. ✅ `appointment_cancelled_by_expert_no_response` (ID: 26)
4. ✅ `appointment_cancelled_by_expert_rejection` (ID: 18)
5. ✅ `appointment_cancelled_by_expert_second` (ID: 25)
6. ✅ `appointment_cancelled_by_no_report` (ID: 21)
7. ✅ `appointment_cancelled_by_no_response` (ID: 17) - [DEPRECATED]
8. ✅ `appointment_completed_without_client_approval` (ID: 23)

---

## ❌ PROBLEMA CRÍTICO DETECTADO

**TODOS los 8 estados de finalización NO tienen configuraciones activas.**

- **Configuraciones existentes**: 0
- **Configuraciones activas**: 0
- **Estados sin configuración**: 8 (TODOS)

---

## 📋 CONFIGURACIONES NECESARIAS

### Estados que DEBEN tener configuración con estos porcentajes:

| Estado | Cliente | Experto | Plataforma | Estado Actual |
|--------|---------|---------|------------|---------------|
| `appointment_cancelled_by_client_no_proposal` | 100% | 0% | 0% | ❌ **FALTA** |
| `appointment_cancelled_by_expert_no_response` | 100% | 0% | 0% | ❌ **FALTA** |
| `appointment_cancelled_by_no_report` | 95% | 0% | 5% | ❌ **FALTA** |
| `appointment_completed_without_client_approval` | 0% | 100% | 0% | ❌ **FALTA** |
| `appointment_cancelled_by_expert_second` | 100% | 0% | 0% | ❌ **FALTA** |
| `appointment_cancelled_by_client_second` | 100% | 0% | 0% | ❌ **FALTA** |
| `appointment_cancelled_by_expert_rejection` | 100% | 0% | 0% | ❌ **FALTA** |
| `appointment_cancelled_by_no_response` | 100% | 0% | 0% | ❌ **FALTA** (DEPRECATED) |

---

## 🔧 SOLUCIÓN: Crear Migración para Agregar Configuraciones

Necesitas crear una migración que inserte las configuraciones para los 8 estados. Aquí está el SQL necesario:

```sql
-- 1. appointment_cancelled_by_client_no_proposal
INSERT INTO "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT s."Id", NULL, NULL, 100, 0, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'AppointmentStatus' 
AND s."StatusValue" = 'appointment_cancelled_by_client_no_proposal'
AND NOT EXISTS (SELECT 1 FROM "StatusConfigurations" sc WHERE sc."StatusId" = s."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL);

-- 2. appointment_cancelled_by_expert_no_response
INSERT INTO "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT s."Id", NULL, NULL, 100, 0, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'AppointmentStatus' 
AND s."StatusValue" = 'appointment_cancelled_by_expert_no_response'
AND NOT EXISTS (SELECT 1 FROM "StatusConfigurations" sc WHERE sc."StatusId" = s."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL);

-- 3. appointment_cancelled_by_no_report
INSERT INTO "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT s."Id", NULL, NULL, 95, 0, 5, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'AppointmentStatus' 
AND s."StatusValue" = 'appointment_cancelled_by_no_report'
AND NOT EXISTS (SELECT 1 FROM "StatusConfigurations" sc WHERE sc."StatusId" = s."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL);

-- 4. appointment_completed_without_client_approval
INSERT INTO "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT s."Id", NULL, NULL, 0, 100, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'AppointmentStatus' 
AND s."StatusValue" = 'appointment_completed_without_client_approval'
AND NOT EXISTS (SELECT 1 FROM "StatusConfigurations" sc WHERE sc."StatusId" = s."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL);

-- 5. appointment_cancelled_by_expert_second
INSERT INTO "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT s."Id", NULL, NULL, 100, 0, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'AppointmentStatus' 
AND s."StatusValue" = 'appointment_cancelled_by_expert_second'
AND NOT EXISTS (SELECT 1 FROM "StatusConfigurations" sc WHERE sc."StatusId" = s."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL);

-- 6. appointment_cancelled_by_client_second
INSERT INTO "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT s."Id", NULL, NULL, 100, 0, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'AppointmentStatus' 
AND s."StatusValue" = 'appointment_cancelled_by_client_second'
AND NOT EXISTS (SELECT 1 FROM "StatusConfigurations" sc WHERE sc."StatusId" = s."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL);

-- 7. appointment_cancelled_by_expert_rejection
INSERT INTO "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT s."Id", NULL, NULL, 100, 0, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'AppointmentStatus' 
AND s."StatusValue" = 'appointment_cancelled_by_expert_rejection'
AND NOT EXISTS (SELECT 1 FROM "StatusConfigurations" sc WHERE sc."StatusId" = s."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL);

-- 8. appointment_cancelled_by_no_response (DEPRECATED - pero mantener por compatibilidad)
INSERT INTO "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
SELECT s."Id", NULL, NULL, 100, 0, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
FROM "SystemStatuses" s
WHERE s."StatusType" = 'AppointmentStatus' 
AND s."StatusValue" = 'appointment_cancelled_by_no_response'
AND NOT EXISTS (SELECT 1 FROM "StatusConfigurations" sc WHERE sc."StatusId" = s."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL);
```

---

## ⚠️ ACCIÓN URGENTE REQUERIDA

**Todos los estados de finalización necesitan configuraciones activas para que el sistema funcione correctamente.**

Sin estas configuraciones, `ProcessMoneyDistributionAsync` fallará cuando se ejecuten los timers o se cancelen citas.
