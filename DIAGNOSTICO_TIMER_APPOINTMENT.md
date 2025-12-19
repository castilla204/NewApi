# 🔍 Diagnóstico: Timer de Appointment No Funciona

## 🚨 Problema Reportado

El timer que debería cancelar automáticamente la cita si el experto no acepta/deniega en plazo no funciona, incluso cuando se ejecuta manualmente desde Hangfire.

---

## 🔍 Verificaciones Necesarias

### 1. Verificar Estado de la Cita

**El método `ProcessAppointmentTimerAsync` verifica que el estado sea `"appointment_proposed"` antes de procesar:**

```csharp
// Línea 3949 de AppointmentService.cs
if (timer.TimerType == "response" && appointmentStatus != "appointment_proposed")
{
    timer.IsExpired = true;
    timer.ExpiredAt = DateTime.UtcNow;
    await _context.SaveChangesAsync();
    return; // ❌ Retorna sin hacer nada si el estado no es correcto
}
```

**Si el estado NO es `"appointment_proposed"`, el timer se marca como expirado pero NO cancela la cita.**

---

## 📋 Checklist de Diagnóstico

### Paso 1: Verificar Estado de la Cita en Base de Datos

Ejecuta esta query en PostgreSQL:

```sql
-- Reemplaza APPOINTMENT_ID con el ID de la cita que tiene el problema
SELECT 
    a."Id" as AppointmentId,
    ast."StatusValue" as AppointmentStatus,
    sh."Id" as SearchHireId,
    shs."StatusValue" as SearchHireStatus,
    at."Id" as TimerId,
    at."TimerType",
    at."IsExpired",
    at."EndTime",
    at."HangfireJobId",
    CASE 
        WHEN at."EndTime" <= NOW() THEN 'EXPIRADO'
        ELSE 'ACTIVO'
    END as TimerStatus
FROM "Appointments" a
INNER JOIN "SystemStatuses" ast ON a."StatusId" = ast."Id"
INNER JOIN "SearchHires" sh ON a."SearchHireId" = sh."Id"
INNER JOIN "SystemStatuses" shs ON sh."StatusId" = shs."Id"
LEFT JOIN "AppointmentTimers" at ON at."AppointmentId" = a."Id" 
    AND at."TimerType" = 'response' 
    AND at."IsExpired" = false
WHERE a."Id" = APPOINTMENT_ID; -- ⚠️ REEMPLAZA CON EL ID REAL
```

**Resultados esperados:**
- ✅ `AppointmentStatus` debe ser `"appointment_proposed"` para que funcione
- ✅ `TimerStatus` debe ser `"EXPIRADO"` si el plazo ya pasó
- ✅ `IsExpired` debe ser `false` antes de ejecutar el job

---

### Paso 2: Verificar Logs de la Ejecución

Cuando ejecutas el job manualmente desde Hangfire, revisa los logs:

**Busca estos mensajes:**

1. **Si el estado NO es correcto:**
   ```
   Timer expirado pero estado de cita no válido para timer de response
   ```

2. **Si el SearchHire NO está en "pending":**
   ```
   SearchHire no está en pending, no procesar
   ```

3. **Si el timer ya estaba expirado:**
   ```
   Timer ya procesado o cancelado
   ```

4. **Si hay un error:**
   ```
   Exception during ProcessAppointmentTimerAsync
   ```

---

### Paso 3: Verificar Estado del SearchHire

El método también verifica que el `SearchHire` esté en estado `"pending"`:

```csharp
// Línea 3914-3923
if (timer.TimerType == "proposal" || timer.TimerType == "response")
{
    if (searchHireStatus != "pending")
    {
        timer.IsExpired = true;
        timer.ExpiredAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return; // ❌ Retorna sin hacer nada
    }
}
```

**Si el `SearchHire` NO está en `"pending"`, el timer no se procesará.**

---

## 🛠️ Soluciones Posibles

### Solución 1: Estado de Cita Incorrecto

**Problema:** La cita no está en estado `"appointment_proposed"`

**Solución:** Cambiar manualmente el estado:

```sql
-- Cambiar estado de la cita a "appointment_proposed"
UPDATE "Appointments" 
SET "StatusId" = (
    SELECT "Id" FROM "SystemStatuses" 
    WHERE "StatusType" = 'AppointmentStatus' 
    AND "StatusValue" = 'appointment_proposed'
)
WHERE "Id" = APPOINTMENT_ID; -- ⚠️ REEMPLAZA CON EL ID REAL
```

**Luego ejecutar el job manualmente desde Hangfire.**

---

### Solución 2: Estado de SearchHire Incorrecto

**Problema:** El `SearchHire` no está en estado `"pending"`

**Solución:** Cambiar manualmente el estado:

```sql
-- Cambiar estado del SearchHire a "pending"
UPDATE "SearchHires" 
SET "StatusId" = (
    SELECT "Id" FROM "SystemStatuses" 
    WHERE "StatusType" = 'SearchHireStatus' 
    AND "StatusValue" = 'pending'
)
WHERE "Id" = SEARCH_HIRE_ID; -- ⚠️ REEMPLAZA CON EL ID REAL
```

**Luego ejecutar el job manualmente desde Hangfire.**

---

### Solución 3: Timer Ya Marcado como Expirado

**Problema:** El timer ya está marcado como `IsExpired = true`

**Solución:** Resetear el timer:

```sql
-- Resetear el timer para poder procesarlo
UPDATE "AppointmentTimers" 
SET "IsExpired" = false,
    "ExpiredAt" = NULL
WHERE "Id" = TIMER_ID; -- ⚠️ REEMPLAZA CON EL ID REAL
```

**Luego ejecutar el job manualmente desde Hangfire.**

---

### Solución 4: Ejecutar Cancelación Manualmente

Si necesitas cancelar la cita inmediatamente sin esperar al timer:

```sql
-- 1. Cambiar estado de la cita a "appointment_cancelled_by_expert_no_response"
UPDATE "Appointments" 
SET "StatusId" = (
    SELECT "Id" FROM "SystemStatuses" 
    WHERE "StatusType" = 'AppointmentStatus' 
    AND "StatusValue" = 'appointment_cancelled_by_expert_no_response'
),
"UpdatedAt" = NOW()
WHERE "Id" = APPOINTMENT_ID; -- ⚠️ REEMPLAZA CON EL ID REAL

-- 2. Marcar timer como expirado
UPDATE "AppointmentTimers" 
SET "IsExpired" = true,
    "ExpiredAt" = NOW()
WHERE "AppointmentId" = APPOINTMENT_ID 
AND "TimerType" = 'response'
AND "IsExpired" = false;

-- 3. Luego ejecutar ProcessMoneyDistributionAsync desde el código o API
```

---

## 🔧 Script SQL Completo de Diagnóstico

```sql
-- ============================================
-- DIAGNÓSTICO COMPLETO DE TIMER DE APPOINTMENT
-- ============================================
-- Reemplaza APPOINTMENT_ID con el ID real de la cita

WITH appointment_info AS (
    SELECT 
        a."Id" as appointment_id,
        ast."StatusValue" as appointment_status,
        sh."Id" as search_hire_id,
        shs."StatusValue" as search_hire_status,
        shs."IsFinalizationStatus" as search_hire_is_finalized,
        at."Id" as timer_id,
        at."TimerType",
        at."IsExpired",
        at."EndTime",
        at."HangfireJobId",
        CASE 
            WHEN at."EndTime" <= NOW() THEN 'EXPIRADO'
            ELSE 'ACTIVO'
        END as timer_status,
        NOW() as current_time
    FROM "Appointments" a
    INNER JOIN "SystemStatuses" ast ON a."StatusId" = ast."Id"
    INNER JOIN "SearchHires" sh ON a."SearchHireId" = sh."Id"
    INNER JOIN "SystemStatuses" shs ON sh."StatusId" = shs."Id"
    LEFT JOIN "AppointmentTimers" at ON at."AppointmentId" = a."Id" 
        AND at."TimerType" = 'response' 
        AND at."IsExpired" = false
    WHERE a."Id" = APPOINTMENT_ID -- ⚠️ REEMPLAZA CON EL ID REAL
)
SELECT 
    appointment_id,
    appointment_status,
    search_hire_id,
    search_hire_status,
    search_hire_is_finalized,
    timer_id,
    "TimerType",
    "IsExpired",
    "EndTime",
    timer_status,
    "HangfireJobId",
    current_time,
    -- Diagnóstico
    CASE 
        WHEN appointment_status != 'appointment_proposed' THEN 
            '❌ PROBLEMA: AppointmentStatus debe ser "appointment_proposed"'
        WHEN search_hire_status != 'pending' THEN 
            '❌ PROBLEMA: SearchHireStatus debe ser "pending"'
        WHEN search_hire_is_finalized = true THEN 
            '❌ PROBLEMA: SearchHire está finalizado'
        WHEN timer_id IS NULL THEN 
            '❌ PROBLEMA: No hay timer activo de tipo "response"'
        WHEN "IsExpired" = true THEN 
            '❌ PROBLEMA: Timer ya está marcado como expirado'
        WHEN timer_status = 'ACTIVO' THEN 
            '⚠️ ADVERTENCIA: Timer aún no ha expirado'
        ELSE 
            '✅ OK: Timer debería procesarse correctamente'
    END as diagnostico
FROM appointment_info;
```

---

## 📝 Pasos para Resolver

1. **Ejecuta el script SQL de diagnóstico** para identificar el problema
2. **Revisa los logs** cuando ejecutas el job manualmente
3. **Aplica la solución correspondiente** según el diagnóstico
4. **Vuelve a ejecutar el job** desde Hangfire
5. **Verifica que la cita se canceló** correctamente

---

## 🚨 Si Nada Funciona

Si después de aplicar todas las soluciones el timer aún no funciona:

1. **Verifica que el método `ProcessMoneyDistributionAsync` funcione correctamente**
2. **Revisa los logs de errores** en `Logs` table
3. **Verifica que el estado `appointment_cancelled_by_expert_no_response` exista** en `SystemStatuses`

```sql
-- Verificar que el estado existe
SELECT * FROM "SystemStatuses" 
WHERE "StatusType" = 'AppointmentStatus' 
AND "StatusValue" = 'appointment_cancelled_by_expert_no_response';
```

---

## 📚 Referencias

- `Services/AppointmentService.cs` línea 3843-4052: Método `ProcessAppointmentTimerAsync`
- `Services/AppointmentService.cs` línea 3949: Validación de estado para timer "response"
- `Services/AppointmentService.cs` línea 4023-4052: Lógica de cancelación por falta de respuesta


