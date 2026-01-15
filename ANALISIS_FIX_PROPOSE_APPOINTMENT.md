# Análisis del Fix: ProposeAppointment No Guardaba Valores

## 🔍 Problema Identificado

El endpoint `POST /api/appointment/propose/{searchHireId}` devolvía 200 OK pero **no guardaba** los valores de `ProposedDate`, `ProposedTime`, y `Location` en la base de datos.

## 📊 Evidencia de los Logs

### ❌ **ANTES del Fix** (Logs 1718, 1715, 1711, 1708):
```
"Message": "Propuesta de cita enviada correctamente"
"Details": "Has propuesto una cita para el fecha pendiente a las hora pendiente..."
```
- Los valores eran `null` cuando se recargaba el appointment después del commit
- El appointment en la BD seguía con `ProposedDate: null`, `ProposedTime: null`, `Location: null`
- `UpdatedAt` no se actualizaba

### ✅ **DESPUÉS del Fix** (Logs 1723-1728):
```
Log 1723: "Valores asignados al Appointment antes de SaveChanges"
Details: "AppointmentId: 12, ProposedDate: 20/01/2026 0:00:00, ProposedTime: 10:00:00, 
         Location: Vía Sin Nombre, 50214 Campillo de Aragón, Zaragoza, España, 
         StatusId: 11, EntityState: Modified"

Log 1724: "SaveChanges ejecutado en ProposeAppointment"
Details: "SaveChangesResult: 2 entidades modificadas. AppointmentId: 12, 
         ProposedDate: 20/01/2026 0:00:00, ProposedTime: 10:00:00, 
         Location: Vía Sin Nombre, 50214 Campillo de Aragón, Zaragoza, España"

Log 1725: "Appointment recargado después del commit"
Details: "AppointmentId: 12, ProposedDate: 20/01/2026 0:00:00, ProposedTime: 10:00:00, 
         Location: Vía Sin Nombre, 50214 Campillo de Aragón, Zaragoza, España, 
         StatusId: 11, UpdatedAt: 15/01/2026 13:07:09"

Log 1727: "Propuesta de cita enviada correctamente"
Details: "Has propuesto una cita para el 20/01/2026 a las 10:00..."
```

## 🐛 Causa Raíz

El problema estaba en cómo Entity Framework Core maneja el **change tracking** cuando se carga una entidad con `FromSqlInterpolated`:

```csharp
// ❌ PROBLEMA: Carga con FromSqlInterpolated y FOR UPDATE
var appointment = await _context.Appointments
    .FromSqlInterpolated($"SELECT * FROM \"Appointments\" WHERE \"SearchHireId\" = {searchHireId} FOR UPDATE")
    .Include(a => a.SearchHire)
    .Include(a => a.Status)
    .FirstOrDefaultAsync();

// Luego se modifican los valores...
appointment.ProposedDate = DateTime.SpecifyKind(proposedDateUtc, DateTimeKind.Utc);
appointment.ProposedTime = proposedTimeUtc;
appointment.Location = dto.Location;
// ... más cambios

// ❌ PROBLEMA: EF Core puede no detectar estos cambios automáticamente
await _context.SaveChangesAsync(); // No guardaba los cambios
```

### ¿Por qué pasaba esto?

1. **`FromSqlInterpolated` con `FOR UPDATE`**: Cuando se carga una entidad usando SQL raw con `FromSqlInterpolated`, EF Core puede no inicializar correctamente el **change tracker** para esa entidad.

2. **Change Tracking Incompleto**: EF Core necesita comparar los valores originales con los nuevos para detectar cambios. Con `FromSqlInterpolated`, puede que no tenga los valores originales correctamente almacenados.

3. **Estado de la Entidad**: La entidad puede estar en estado `Unchanged` en lugar de `Modified`, por lo que `SaveChangesAsync()` no la incluye en el update.

## ✅ Solución Implementada

### 1. **Forzar EntityState.Modified**
```csharp
// ✅ SOLUCIÓN: Marcar explícitamente como Modified
appointment.ProposedDate = DateTime.SpecifyKind(proposedDateUtc, DateTimeKind.Utc);
appointment.ProposedTime = proposedTimeUtc;
appointment.Location = dto.Location;
// ... más cambios

// ✅ CRÍTICO: Forzar el estado Modified
_context.Entry(appointment).State = EntityState.Modified;

await _context.SaveChangesAsync(); // Ahora SÍ guarda los cambios
```

### 2. **Detach después del Commit**
```csharp
await transaction.CommitAsync();

// ✅ Detach para evitar problemas de caché
_context.Entry(appointment).State = EntityState.Detached;
```

### 3. **AsNoTracking al Recargar**
```csharp
// ✅ Recargar con AsNoTracking para evitar problemas de caché
var updatedAppointment = await _context.Appointments
    .AsNoTracking()
    .Include(a => a.SearchHire)
    // ... más includes
    .FirstAsync(a => a.Id == appointment.Id);
```

### 4. **Logging para Diagnóstico**
Se agregaron logs en puntos clave:
- Antes de `SaveChanges`: Verificar valores asignados
- Después de `SaveChanges`: Verificar cuántas entidades se modificaron
- Después del commit: Verificar valores recargados desde BD

## 📈 Resultado

**Antes:**
- `SaveChangesResult: 0 entidades modificadas` (implícito, no se guardaba nada)
- Appointment en BD: `ProposedDate: null`, `ProposedTime: null`, `Location: null`
- Mensajes: "fecha pendiente", "hora pendiente"

**Después:**
- `SaveChangesResult: 2 entidades modificadas` (Appointment + Timer)
- Appointment en BD: Valores correctos guardados
- Mensajes: "20/01/2026 10:00", "Vía Sin Nombre, 50214 Campillo de Aragón..."

## 🎯 Lecciones Aprendidas

1. **`FromSqlInterpolated` requiere manejo especial**: Cuando se usa SQL raw, EF Core puede no detectar cambios automáticamente.

2. **Siempre verificar EntityState**: Si se modifica una entidad cargada con SQL raw, forzar `EntityState.Modified` es una buena práctica.

3. **Logging es crucial**: Los logs agregados permitieron identificar exactamente dónde fallaba el proceso.

4. **Change Tracking con SQL Raw**: EF Core funciona mejor con queries LINQ normales. Cuando se usa SQL raw, hay que ser explícito con el estado de las entidades.

## 🔧 Código Final (Resumen)

```csharp
// 1. Cargar con FromSqlInterpolated (necesario para FOR UPDATE)
var appointment = await _context.Appointments
    .FromSqlInterpolated($"SELECT * FROM \"Appointments\" WHERE \"SearchHireId\" = {searchHireId} FOR UPDATE")
    .Include(a => a.SearchHire)
    .Include(a => a.Status)
    .FirstOrDefaultAsync();

// 2. Modificar valores
appointment.ProposedDate = DateTime.SpecifyKind(proposedDateUtc, DateTimeKind.Utc);
appointment.ProposedTime = proposedTimeUtc;
appointment.Location = dto.Location;
// ... más cambios

// 3. ✅ CRÍTICO: Forzar estado Modified
_context.Entry(appointment).State = EntityState.Modified;

// 4. Guardar cambios
await _context.SaveChangesAsync();

// 5. Commit
await transaction.CommitAsync();

// 6. Detach para evitar problemas de caché
_context.Entry(appointment).State = EntityState.Detached;

// 7. Recargar con AsNoTracking
var updatedAppointment = await _context.Appointments
    .AsNoTracking()
    .Include(a => a.SearchHire)
    .FirstAsync(a => a.Id == appointment.Id);
```

## ✅ Estado Actual

**FUNCIONANDO CORRECTAMENTE** - Los valores se guardan y se recargan correctamente desde la base de datos.
