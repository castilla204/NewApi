# Fix: EntityState.Modified para FromSqlInterpolated

## 🔍 Problema

Cuando se carga una entidad con `FromSqlInterpolated` y luego se modifican sus propiedades, Entity Framework Core puede no detectar los cambios automáticamente, resultando en que `SaveChangesAsync()` no guarde las modificaciones.

## ✅ Solución Aplicada

Se agregó `_context.Entry(appointment).State = EntityState.Modified;` explícitamente después de modificar entidades cargadas con `FromSqlInterpolated`.

## 📍 Métodos Corregidos

### 1. ✅ `ProposeAppointmentAsync` (Línea ~1183)
```csharp
appointment.ProposedDate = DateTime.SpecifyKind(proposedDateUtc, DateTimeKind.Utc);
appointment.ProposedTime = proposedTimeUtc;
appointment.Location = dto.Location;
// ... más cambios
appointment.UpdatedAt = DateTime.UtcNow;

// ✅ CRÍTICO: Marcar la entidad como Modified explícitamente
_context.Entry(appointment).State = EntityState.Modified;
```

### 2. ✅ `ConfirmAppointmentAsync` (Línea ~1576)
```csharp
appointment.StatusId = confirmedStatus.Id;
appointment.LastResponseAt = DateTime.UtcNow;
appointment.UpdatedAt = DateTime.UtcNow;

// ✅ CRÍTICO: Marcar la entidad como Modified explícitamente
_context.Entry(appointment).State = EntityState.Modified;
```

### 3. ✅ `RejectAppointmentAsync` (Línea ~2253)
```csharp
appointment.StatusId = newStatus.Id;
appointment.RejectionCount++;
appointment.LastRejectionAt = DateTime.UtcNow;
appointment.LastResponseAt = DateTime.UtcNow;
appointment.UpdatedAt = DateTime.UtcNow;

// ✅ CRÍTICO: Marcar la entidad como Modified explícitamente
_context.Entry(appointment).State = EntityState.Modified;
```

### 4. ✅ `CancelAppointmentAsync` (Línea ~3072)
```csharp
appointment.StatusId = cancelledStatus.Id;
appointment.ClientCancellationCount++; // o ExpertCancellationCount++
appointment.LastClientCancellationAt = DateTime.UtcNow; // o LastExpertCancellationAt
appointment.UpdatedAt = DateTime.UtcNow;

// ✅ CRÍTICO: Marcar la entidad como Modified explícitamente
_context.Entry(appointment).State = EntityState.Modified;
```

### 5. ✅ `SubmitExpertReportAsync` (Línea ~6330)
```csharp
appointment.StatusId = appointmentReportSentStatus.Id;
appointment.UpdatedAt = DateTime.UtcNow;

// ✅ CRÍTICO: Marcar la entidad como Modified explícitamente
_context.Entry(appointment).State = EntityState.Modified;
```

## 🎯 Patrón a Seguir

**SIEMPRE** que se cargue una entidad con `FromSqlInterpolated` y luego se modifiquen sus propiedades, agregar:

```csharp
// Después de modificar las propiedades
_context.Entry(entidad).State = EntityState.Modified;
```

## ⚠️ Nota Importante

Este fix es necesario porque:
1. `FromSqlInterpolated` carga entidades usando SQL raw
2. EF Core puede no inicializar correctamente el change tracker para estas entidades
3. Sin el estado `Modified` explícito, `SaveChangesAsync()` puede no detectar los cambios

## ✅ Estado

**TODOS LOS MÉTODOS CORREGIDOS** - Los 5 métodos que cargan con `FromSqlInterpolated` y modifican entidades ahora tienen el fix aplicado.
