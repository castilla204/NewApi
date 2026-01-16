# 📊 Resumen: Análisis de Porcentajes de Distribución de Dinero

## ⚠️ PROBLEMA DETECTADO

**Las tablas `SystemStatuses` y `StatusConfigurations` NO EXISTEN en la base de datos actual.**

Solo existen estas 6 tablas:
- `Conversations`
- `MessageAttachments`
- `Messages`
- `Notifications`
- `Users`
- `__EFMigrationsHistory`

**Esto significa que las migraciones del sistema de estados centralizado NO se han ejecutado.**

---

## 📋 Análisis de las Migraciones (Código)

He analizado las migraciones y encontré lo siguiente:

### ✅ Estados que SÍ tienen configuración en las migraciones:

1. **`appointment_completed`** → Cliente: 0%, Experto: 95%, Plataforma: 5% ✅
2. **`appointment_cancelled`** → Cliente: 100%, Experto: 0%, Plataforma: 0% ✅
3. **`appointment_rejected`** → Cliente: 100%, Experto: 0%, Plataforma: 0% ✅
4. **`appointment_cancelled_by_client`** → Cliente: 100%, Experto: 0%, Plataforma: 0% ✅
5. **`appointment_cancelled_by_client_second`** → Cliente: 100%, Experto: 0%, Plataforma: 0% ✅
6. **`appointment_cancelled_by_expert`** → Cliente: 100%, Experto: 0%, Plataforma: 0% ✅
7. **`appointment_cancelled_by_no_response`** → Cliente: 100%, Experto: 0%, Plataforma: 0% ✅ (DEPRECATED)
8. **`appointment_cancelled_by_expert_rejection`** → Cliente: 100%, Experto: 0%, Plataforma: 0% ✅

### ❌ Estados de Finalización que NO tienen configuración en las migraciones:

1. **`appointment_cancelled_by_client_no_proposal`** ❌ **FALTA**
   - **Necesario**: Cliente: 100%, Experto: 0%, Plataforma: 0%
   - **Usado en**: Timer "proposal" (línea 3975 de AppointmentService.cs)

2. **`appointment_cancelled_by_expert_no_response`** ❌ **FALTA**
   - **Necesario**: Cliente: 100%, Experto: 0%, Plataforma: 0%
   - **Usado en**: Timer "response" (línea 4006 de AppointmentService.cs)

3. **`appointment_cancelled_by_no_report`** ❌ **FALTA**
   - **Necesario**: Cliente: 95%, Experto: 0%, Plataforma: 5%
   - **Usado en**: Timer "expert_report" (línea 4037 de AppointmentService.cs)
   - **Nota**: El estado existe (migración 20251001112052), pero NO tiene configuración

4. **`appointment_completed_without_client_approval`** ❌ **FALTA**
   - **Necesario**: Cliente: 0%, Experto: 100%, Plataforma: 0%
   - **Usado en**: Timer "client_decision" (línea 4098 de AppointmentService.cs)

5. **`appointment_cancelled_by_expert_second`** ❌ **FALTA**
   - **Necesario**: Cliente: 100%, Experto: 0%, Plataforma: 0%
   - **Usado en**: CancelAppointmentAsync cuando es segunda cancelación del experto

---

## 🔧 Solución Requerida

### Paso 1: Ejecutar Migraciones Pendientes
```bash
dotnet ef database update
```

### Paso 2: Crear Migración para Agregar Configuraciones Faltantes

Necesitas crear una nueva migración que agregue las configuraciones para los 5 estados faltantes:

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
```

---

## 📊 Resumen de Porcentajes por Estado

| Estado | Cliente | Experto | Plataforma | Estado |
|--------|---------|---------|------------|--------|
| `appointment_cancelled_by_client_no_proposal` | 100% | 0% | 0% | ❌ **FALTA** |
| `appointment_cancelled_by_expert_no_response` | 100% | 0% | 0% | ❌ **FALTA** |
| `appointment_cancelled_by_no_report` | 95% | 0% | 5% | ❌ **FALTA** |
| `appointment_completed_without_client_approval` | 0% | 100% | 0% | ❌ **FALTA** |
| `appointment_cancelled_by_expert_second` | 100% | 0% | 0% | ❌ **FALTA** |
| `appointment_cancelled_by_expert_rejection` | 100% | 0% | 0% | ✅ Existe |
| `appointment_cancelled_by_client_second` | 100% | 0% | 0% | ✅ Existe |

---

## ⚠️ ACCIÓN REQUERIDA

1. **Ejecutar migraciones pendientes** para crear las tablas
2. **Crear nueva migración** para agregar las 5 configuraciones faltantes
3. **Verificar** que todos los estados de finalización tengan configuración
