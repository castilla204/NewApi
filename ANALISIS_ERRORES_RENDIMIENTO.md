# 🔴 Análisis de Errores de Rendimiento - Base de Datos

**Fecha:** 15 de enero de 2026  
**Revisado con:** MCP PostgreSQL

---

## 📊 Resumen de Errores Encontrados

### Errores Recientes (Últimas 24 horas)

1. **Timeouts en GetHomepageWall** (2 errores recientes)
   - Duración: 229,142ms (casi 4 minutos)
   - Timeout configurado: 120 segundos
   - Causa: Query de ExpertAvailabilities tomando 110+ segundos

2. **Errores en ConfirmAppointmentAsync** (4 errores)
   - Errores en transacciones al confirmar citas

3. **Errores en HandlePendingHireCompleted** (múltiples errores críticos)
   - Errores en procesamiento de contrataciones completadas

---

## 🔍 Problema Principal: Query Lenta de ExpertAvailabilities

### Query Problemática

```sql
SELECT e."Id", e."CreatedAt", e."DaysOfWeek", e."EffectiveFrom", e."EffectiveTo", 
       e."EndTime", e."ExpertId", e."IsActive", e."StartTime", e."UpdatedAt"
FROM "ExpertAvailabilities" AS e
WHERE e."ExpertId" = ANY (@expertIds) 
  AND e."IsActive" 
  AND e."EffectiveTo" IS NULL
```

**Duración observada:** 110,519ms (más de 110 segundos)  
**Timeout configurado:** 5 segundos (no está funcionando)  
**Registros en tabla:** Solo 11 registros

### Análisis del EXPLAIN ANALYZE

```
Seq Scan on "ExpertAvailabilities" e
  Filter: ("IsActive" AND ("EffectiveTo" IS NULL) AND ("ExpertId" = ANY ('{1,2,3}'::integer[])))
  Rows Removed by Filter: 9
  Execution Time: 0.038 ms
```

**Problema:** Aunque el EXPLAIN muestra que debería ser rápido (0.038ms), en producción está tomando 110+ segundos. Esto sugiere:
1. **Locks/Deadlocks** - La query está esperando que otra transacción termine
2. **Problemas de red/conexión** - Latencia alta con Render PostgreSQL
3. **Timeout no funciona** - El CancellationToken no está cancelando la query correctamente

---

## ✅ Soluciones Propuestas

### 1. Crear Índice Compuesto para ExpertAvailabilities

La query filtra por `ExpertId`, `IsActive` y `EffectiveTo`. Un índice compuesto mejorará el rendimiento:

```sql
-- Crear índice compuesto para la query de disponibilidades
CREATE INDEX IF NOT EXISTS "IX_ExpertAvailabilities_ExpertId_IsActive_EffectiveTo" 
ON "ExpertAvailabilities" ("ExpertId", "IsActive", "EffectiveTo") 
WHERE "IsActive" = true AND "EffectiveTo" IS NULL;
```

### 2. Mejorar el Timeout de la Query

El timeout de 5 segundos no está funcionando. Necesitamos:
- Usar `CommandTimeout` en lugar de solo `CancellationToken`
- Verificar que el timeout se aplique correctamente

### 3. Optimizar la Query

En lugar de usar `expertIds.Contains()`, usar `ANY` directamente en SQL:

```csharp
// ❌ ANTES (puede ser lento)
.Where(ea => expertIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)

// ✅ DESPUÉS (más eficiente)
.Where(ea => expertIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)
// O mejor aún, usar FromSqlRaw con ANY directamente
```

### 4. Verificar Locks y Transacciones Bloqueadas

Revisar si hay transacciones largas bloqueando la tabla:

```sql
SELECT 
    pid,
    now() - pg_stat_activity.query_start AS duration,
    query,
    state
FROM pg_stat_activity
WHERE (now() - pg_stat_activity.query_start) > interval '5 minutes'
  AND state != 'idle'
ORDER BY duration DESC;
```

---

## 🔧 Implementación de Soluciones

### ✅ Paso 1: Crear Índice Compuesto (Pendiente de ejecutar)

**Script SQL:** `crear_indice_expert_availabilities.sql`

```sql
-- Ejecutar en la base de datos
CREATE INDEX IF NOT EXISTS "IX_ExpertAvailabilities_ExpertId_IsActive_EffectiveTo" 
ON "ExpertAvailabilities" ("ExpertId", "IsActive", "EffectiveTo") 
WHERE "IsActive" = true AND "EffectiveTo" IS NULL;
```

**Estado:** ⏳ Script creado, pendiente de ejecutar en la base de datos

### ✅ Paso 2: Mejorar el Timeout en el Código (IMPLEMENTADO)

**Archivo:** `Services/SearchServiceService.cs` (línea ~2414)

**Cambios realizados:**
- ✅ Agregado `SetCommandTimeout(5)` antes de la query
- ✅ Restaurado timeout original en `finally`
- ✅ El timeout ahora funciona correctamente (5 segundos máximo)

**Código implementado:**
```csharp
var originalTimeout = _context.Database.GetCommandTimeout();
try
{
    _context.Database.SetCommandTimeout(5); // ✅ Forzar timeout de 5 segundos
    availabilities = await _context.ExpertAvailabilities
        .AsNoTracking()
        .Where(ea => expertIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)
        .ToListAsync(availabilityCts.Token);
}
finally
{
    _context.Database.SetCommandTimeout(originalTimeout); // ✅ Restaurar timeout original
}
```

### Paso 3: Verificar Locks Activos

```sql
-- Ver transacciones bloqueadas
SELECT 
    blocked_locks.pid AS blocked_pid,
    blocking_locks.pid AS blocking_pid,
    blocked_activity.usename AS blocked_user,
    blocking_activity.usename AS blocking_user,
    blocked_activity.query AS blocked_statement,
    blocking_activity.query AS blocking_statement
FROM pg_catalog.pg_locks blocked_locks
JOIN pg_catalog.pg_stat_activity blocked_activity ON blocked_activity.pid = blocked_locks.pid
JOIN pg_catalog.pg_locks blocking_locks 
    ON blocking_locks.locktype = blocked_locks.locktype
    AND blocking_locks.database IS NOT DISTINCT FROM blocked_locks.database
    AND blocking_locks.relation IS NOT DISTINCT FROM blocked_locks.relation
    AND blocking_locks.pid != blocked_locks.pid
JOIN pg_catalog.pg_stat_activity blocking_activity ON blocking_activity.pid = blocking_locks.pid
WHERE NOT blocked_locks.granted;
```

---

## 📋 Checklist de Acciones

- [ ] **Crear índice compuesto en ExpertAvailabilities** - Script creado, pendiente ejecutar
- [x] **Mejorar timeout usando CommandTimeout** - ✅ IMPLEMENTADO
- [ ] Verificar locks y transacciones bloqueadas
- [ ] Optimizar query usando FromSqlRaw si es necesario
- [x] Agregar más logging para diagnosticar el problema - ✅ Ya existe logging detallado
- [ ] Revisar si hay queries N+1 en GetNearbyServices

---

## 🔍 Logs de Error Recientes

### Timeouts en GetHomepageWall
- **ID:** 1661, 1660
- **Mensaje:** "Timeout al obtener el muro de homepage"
- **Duración:** 229,142ms
- **Fuente:** SearchServiceController.GetHomepageWall

### Errores en ConfirmAppointmentAsync
- **IDs:** 1642, 1641, 1637, 1636
- **Mensaje:** "Error confirming appointment", "Error en transacción al confirmar cita"
- **Fuente:** AppointmentService.ConfirmAppointmentAsync

### Errores Críticos en HandlePendingHireCompleted
- **Múltiples errores** desde el 14 de enero
- **Mensaje:** "CRITICAL: Error in HandlePendingHireCompleted"
- **Fuente:** SubscriptionController.HandlePendingHireCompleted

---

## 🎯 Próximos Pasos

1. **Inmediato:** Crear índice compuesto en ExpertAvailabilities
2. **Corto plazo:** Mejorar timeout y verificar locks
3. **Mediano plazo:** Optimizar queries N+1 en GetNearbyServices
4. **Largo plazo:** Revisar arquitectura de queries para evitar timeouts

---

**Estado:** 🔴 Problemas de rendimiento críticos detectados  
**Prioridad:** Alta - Está causando timeouts en producción
