# 🔍 ANÁLISIS DEL FALLO: Timer #97 Activo

## 📊 SITUACIÓN ACTUAL

### SearchHires Involucrados

| SearchHireId | CreatedAt | Estado | SearchServiceId | ClientId | Appointment Status | Timer #97 |
|--------------|-----------|--------|------------------|----------|-------------------|-----------|
| **54** | 22:28:22 | `pending` | 230 | 1 | `awaiting_appointment` | ✅ **ACTIVO** |
| **55** | 22:29:23 | `cancelled` | 230 | 1 | `appointment_cancelled_by_client_second` | ❌ N/A |

### Problema Identificado

El timer #97 pertenece al **SearchHireId 54**, que:
- Está en estado `pending` (no finalizado)
- Tiene un Appointment en estado `awaiting_appointment`
- Tiene un timer "proposal" activo con HangfireJobId "334"
- **NO debería estar activo** porque se creó el SearchHire 55 para el mismo servicio/cliente

---

## 🔴 ORIGEN DEL FALLO

### 1. **Validación de Duplicados NO Funcionó**

En `SubscriptionController.HireService` (línea 1595-1605) hay una validación que debería prevenir crear múltiples SearchHires activos:

```csharp
var existingHire = await _context.SearchHires
    .FirstOrDefaultAsync(sh => sh.ClientId == userId && 
                              sh.SearchServiceId == service.Id && 
                              (sh.StatusId == pendingStatusId || 
                               sh.StatusId == awaitingStatusId ||
                               sh.StatusId == disputedStatusId));
    
if (existingHire != null)
{
    return BadRequest(new { message = "Ya tienes una contratación activa para este servicio" });
}
```

**Problema**: Esta validación solo funciona si el SearchHire anterior está en `pending`, `awaiting_client_decision` o `disputed`. El SearchHire 54 está en `pending`, así que **debería haber bloqueado** la creación del 55.

**Posibles causas**:
1. El SearchHire 55 se creó de otra manera (no a través de `HireService`)
2. La validación no se ejecutó correctamente
3. Hubo una condición de carrera (race condition)

### 2. **Falta Lógica de Limpieza de Timers**

Cuando se crea un nuevo SearchHire, **NO hay lógica que cancele los timers activos** de SearchHires anteriores para el mismo servicio/cliente.

**Ubicación del problema**:
- `SearchHireController.CreateSearchHire` (línea 226-280): Crea Appointment y timer, pero NO cancela timers anteriores
- `SubscriptionController.HandlePendingHireCompleted` (línea 3694): Crea Appointment y timer, pero NO cancela timers anteriores

### 3. **El Timer #97 NO se Cancela Automáticamente**

El código en `ProcessAppointmentTimerAsync` (línea 3826) verifica si el SearchHire está finalizado:

```csharp
if (searchHire.Status?.IsFinalizationStatus == true)
{
    timer.IsExpired = true;
    timer.ExpiredAt = DateTime.UtcNow;
    await _context.SaveChangesAsync();
    return; // SearchHire ya finalizado, no procesar
}
```

**Problema**: El SearchHire 54 está en `pending` (no finalizado), así que el timer es válido según esta lógica. **PERO** debería haberse cancelado cuando se creó el SearchHire 55.

---

## ✅ SOLUCIÓN PROPUESTA

### Opción 1: Cancelar Timers al Crear Nuevo SearchHire (RECOMENDADA)

Agregar lógica en `SearchHireController.CreateSearchHire` y `SubscriptionController.HandlePendingHireCompleted` para cancelar timers activos de SearchHires anteriores:

```csharp
// ✅ CANCELAR timers activos de SearchHires anteriores para el mismo servicio/cliente
var previousSearchHires = await _context.SearchHires
    .Where(sh => sh.ClientId == searchHire.ClientId && 
                 sh.SearchServiceId == searchHire.SearchServiceId && 
                 sh.Id != searchHire.Id &&
                 sh.Status.StatusValue == "pending")
    .Include(sh => sh.Appointment)
        .ThenInclude(a => a.Timers)
    .ToListAsync();

foreach (var prevSearchHire in previousSearchHires)
{
    if (prevSearchHire.Appointment != null)
    {
        var activeTimers = prevSearchHire.Appointment.Timers
            .Where(t => !t.IsExpired)
            .ToList();
        
        foreach (var timer in activeTimers)
        {
            timer.IsExpired = true;
            timer.ExpiredAt = DateTime.UtcNow;
            
            // Cancelar job de Hangfire si existe
            if (!string.IsNullOrEmpty(timer.HangfireJobId))
            {
                try
                {
                    BackgroundJob.Delete(timer.HangfireJobId);
                    timer.HangfireJobId = null;
                }
                catch
                {
                    timer.HangfireJobId = null;
                }
            }
        }
    }
}
```

### Opción 2: Mejorar Validación de Duplicados

Asegurar que la validación en `SubscriptionController.HireService` funcione correctamente y bloquee la creación de SearchHires duplicados.

### Opción 3: Cancelar SearchHires Anteriores

En lugar de solo cancelar timers, cancelar completamente los SearchHires anteriores en estado `pending` cuando se crea uno nuevo.

---

## 🎯 RECOMENDACIÓN

**Implementar Opción 1** porque:
1. ✅ No cambia la lógica de negocio (solo limpia timers huérfanos)
2. ✅ Es la solución más segura (no cancela SearchHires, solo timers)
3. ✅ Evita que queden timers activos cuando se crean nuevos SearchHires
4. ✅ Es consistente con la lógica existente de cancelación de timers

---

## 📝 IMPLEMENTACIÓN

Agregar la lógica de cancelación de timers en:
1. `SearchHireController.CreateSearchHire` (después de línea 224, antes de crear Appointment)
2. `SubscriptionController.HandlePendingHireCompleted` (después de crear SearchHire, antes de crear Appointment)
