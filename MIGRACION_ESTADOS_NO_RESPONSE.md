# Migración: Estados Separados para No Response

## 📋 **PROBLEMA**

Actualmente ambos casos (cliente no propone vs experto no responde) usan el mismo estado `cancelled_by_no_response` con los mismos porcentajes (Cliente 100%, Experto 0%, Plataforma 0%).

Esto es incorrecto porque:
- **Cliente no propone** → Es culpa del cliente → Debería ser Cliente 0%, Experto 100%, Plataforma 0%
- **Experto no responde** → Es culpa del experto → Debería ser Cliente 100%, Experto 0%, Plataforma 0%

## ✅ **SOLUCIÓN**

Crear dos estados separados en la BD con porcentajes distintos:

### **1. Estado: `cancelled_by_client_no_proposal`**
- **DisplayName**: "Cancelado por Cliente No Propone"
- **Porcentajes**: Cliente 0%, Experto 100%, Plataforma 0%
- **Uso**: Cuando el cliente no propone una cita en 24h

### **2. Estado: `cancelled_by_expert_no_response`**
- **DisplayName**: "Cancelado por Experto No Responde"
- **Porcentajes**: Cliente 100%, Experto 0%, Plataforma 0%
- **Uso**: Cuando el experto no responde a una propuesta en 24h

## 🔧 **SQL PARA CREAR LOS ESTADOS**

```sql
-- 1. Crear estado para cliente no propone
INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "IsFinalizationStatus", "CreatedAt", "UpdatedAt")
VALUES ('SearchHireStatus', 'CancelledByClientNoProposal', 'cancelled_by_client_no_proposal', 'Cancelado por Cliente No Propone', true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT DO NOTHING;

-- 2. Crear estado para experto no responde
INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "IsFinalizationStatus", "CreatedAt", "UpdatedAt")
VALUES ('SearchHireStatus', 'CancelledByExpertNoResponse', 'cancelled_by_expert_no_response', 'Cancelado por Experto No Responde', true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT DO NOTHING;

-- 3. Crear configuración de porcentajes para cliente no propone
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

-- 4. Crear configuración de porcentajes para experto no responde
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
```

## ✅ **CÓDIGO ACTUALIZADO**

El código en `AppointmentService.cs` ya está actualizado para:
1. Buscar primero el estado específico (`cancelled_by_client_no_proposal` o `cancelled_by_expert_no_response`)
2. Si no existe, usar el estado genérico (`cancelled_by_no_response`) como fallback
3. Usar el estado correcto para procesar el dinero

## 📝 **NOTAS**

- El código tiene fallback al estado genérico, así que funcionará incluso si los nuevos estados no existen todavía
- Una vez creados los estados en la BD, el código usará automáticamente los porcentajes correctos
- El estado genérico `cancelled_by_no_response` puede mantenerse para compatibilidad con datos antiguos

