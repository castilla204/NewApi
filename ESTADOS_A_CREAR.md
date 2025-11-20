# Estados que DEBES AÑADIR en la Base de Datos

## 📋 Resumen

Según el análisis del código y la verificación en BD, necesitas crear **3 estados SearchHireStatus** que faltan:

---

## ✅ Estados a Crear

### 1. `cancelled_by_client_no_proposal`
**Cuándo se usa**: Cuando el cliente no propone una cita en 24h (timer "proposal")

**Porcentajes**:
- Cliente: **0%** (culpa del cliente)
- Experto: **100%** (recibe todo)
- Plataforma: **0%**

**Código que lo busca**: `AppointmentService.cs` línea 3657

**Por qué es necesario**: 
- Actualmente hace fallback a `cancelled_by_no_response` (genérico) con Cliente 100%
- Esto es **INCORRECTO** porque cuando el cliente no propone, el experto debería recibir el 100%

---

### 2. `cancelled_by_expert_no_response`
**Cuándo se usa**: Cuando el experto no responde a una propuesta en 24h (timer "response")

**Porcentajes**:
- Cliente: **100%** (recibe todo, culpa del experto)
- Experto: **0%** (culpa del experto)
- Plataforma: **0%**

**Código que lo busca**: `AppointmentService.cs` líneas 2748, 3712

**Por qué es necesario**:
- Actualmente hace fallback a `cancelled_by_no_response` (genérico) con Cliente 100%
- Este porcentaje es correcto, pero es mejor tener un estado específico para claridad y trazabilidad

---

### 3. `cancelled_by_expert_no_report`
**Cuándo se usa**: Cuando el experto no envía reporte en 24h (timer "expert_report")

**Porcentajes**:
- Cliente: **95%** (similar a `appointment_cancelled_by_no_report`)
- Experto: **0%**
- Plataforma: **5%**

**Código que lo busca**: `AppointmentService.cs` línea 3767

**Por qué es necesario**:
- Actualmente usa el estado genérico `cancelled` con Cliente 95%, Experto 0%, Plataforma 5%
- Aunque los porcentajes son similares, es mejor tener un estado específico para diferenciar el motivo de cancelación

---

## 📊 Tabla Comparativa

| Estado | Existe en BD | Código lo busca | Porcentajes Actuales (Fallback) | Porcentajes Correctos |
|--------|--------------|-----------------|--------------------------------|----------------------|
| `cancelled_by_client_no_proposal` | ❌ NO | ✅ SÍ | Cliente 100% (INCORRECTO) | Cliente 0%, Experto 100% |
| `cancelled_by_expert_no_response` | ❌ NO | ✅ SÍ | Cliente 100% (correcto pero genérico) | Cliente 100%, Experto 0% |
| `cancelled_by_expert_no_report` | ❌ NO | ✅ SÍ | Cliente 95% (genérico `cancelled`) | Cliente 95%, Experto 0%, Plataforma 5% |

---

## 🚨 Impacto si NO se crean

### Estado 1: `cancelled_by_client_no_proposal`
- **Problema crítico**: Cuando el cliente no propone, el sistema devuelve 100% al cliente (incorrecto)
- **Debería**: Devolver 100% al experto (culpa del cliente)

### Estado 2: `cancelled_by_expert_no_response`
- **Problema menor**: Funciona correctamente con el fallback, pero falta claridad
- **Debería**: Tener estado específico para mejor trazabilidad

### Estado 3: `cancelled_by_expert_no_report`
- **Problema menor**: Funciona con porcentajes similares, pero falta especificidad
- **Debería**: Tener estado específico para diferenciar del genérico `cancelled`

---

## ✅ Script SQL Listo

El archivo `EJECUTAR_ESTADOS_FALTANTES.sql` ya está preparado con:
- ✅ Creación de los 3 estados
- ✅ Configuraciones de porcentajes correctas
- ✅ Mapeo opcional para `appointment_cancelled_by_no_report`

**Solo necesitas ejecutarlo** y los estados se crearán automáticamente.

---

## 📝 Verificación Post-Creación

Después de ejecutar el script, verifica que se crearon:

```sql
SELECT "Id", "StatusValue", "DisplayName", "IsFinalizationStatus"
FROM "SystemStatuses" 
WHERE "StatusType" = 'SearchHireStatus' 
AND "StatusValue" IN (
    'cancelled_by_client_no_proposal', 
    'cancelled_by_expert_no_response', 
    'cancelled_by_expert_no_report'
)
ORDER BY "StatusValue";
```

Y verifica los porcentajes:

```sql
SELECT 
    ss."StatusValue",
    ss."DisplayName",
    sc."ClientPercentage",
    sc."ExpertPercentage",
    sc."PlatformPercentage"
FROM "StatusConfigurations" sc
INNER JOIN "SystemStatuses" ss ON sc."StatusId" = ss."Id"
WHERE ss."StatusType" = 'SearchHireStatus'
AND ss."StatusValue" IN (
    'cancelled_by_client_no_proposal', 
    'cancelled_by_expert_no_response', 
    'cancelled_by_expert_no_report'
)
AND sc."CategoryId" IS NULL
AND sc."ServiceTypeCategoryId" IS NULL
ORDER BY ss."StatusValue";
```











