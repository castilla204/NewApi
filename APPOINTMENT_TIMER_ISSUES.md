# Problemas con Timers y Jobs de Hangfire en Appointments

## 🔴 Problemas Identificados

### 1. **Jobs de Hangfire NO se cancelan cuando se cancela/rechaza la cita**

**Problema:**
- Cuando se cancela una cita (`CancelAppointmentAsync`), se marcan los timers como expirados en la BD
- PERO los jobs de Hangfire programados con `BackgroundJob.Schedule` **NO se cancelan**
- El job se ejecutará aunque el timer esté marcado como expirado
- Aunque `ProcessAppointmentTimerAsync` verifica si el timer está expirado y retorna temprano, el job ya se ejecutó innecesariamente

**Ubicación del problema:**
- `Services/AppointmentService.cs` línea 1124-1133: Marca timers como expirados pero NO cancela jobs
- `Services/AppointmentService.cs` línea 760-771: Marca timers de respuesta como expirados pero NO cancela jobs

**Código actual:**
```csharp
// CancelAppointmentAsync - línea 1124
// Marcar todos los timers activos como expirados
var activeTimers = await _context.AppointmentTimers
    .Where(t => t.AppointmentId == appointment.Id && !t.IsExpired)
    .ToListAsync();

foreach (var timer in activeTimers)
{
    timer.IsExpired = true;
    timer.ExpiredAt = DateTime.UtcNow;
}
// ❌ PROBLEMA: No se cancelan los jobs de Hangfire programados
```

---

### 2. **Cuando el experto rechaza (primer rechazo), NO se crea un nuevo timer de 24h para el cliente**

**Problema:**
- Cuando el experto rechaza una propuesta (`RejectAppointmentAsync`), el estado cambia a `appointment_rejected`
- Se marcan los timers de respuesta como expirados
- PERO **NO se crea un nuevo timer de 24h** para que el cliente proponga otra vez
- El cliente puede proponer manualmente (línea 328 permite `appointment_rejected`), pero no hay timer automático

**Ubicación del problema:**
- `Services/AppointmentService.cs` línea 760-771: Solo marca timers como expirados, no crea nuevo timer
- `Services/AppointmentService.cs` línea 736: Estado cambia a `appointment_rejected` pero no se crea timer

**Código actual:**
```csharp
// RejectAppointmentAsync - línea 760
// Marcar timers de respuesta como expirados (experto ya respondió)
var responseTimers = await _context.AppointmentTimers
    .Where(t => t.AppointmentId == appointment.Id && 
               t.TimerType == "response" && 
               !t.IsExpired)
    .ToListAsync();

foreach (var timer in responseTimers)
{
    timer.IsExpired = true;
    timer.ExpiredAt = DateTime.UtcNow;
}
// ❌ PROBLEMA: No se crea un nuevo timer de 24h para el cliente
// ❌ PROBLEMA: No se cancelan los jobs de Hangfire programados
```

**Comportamiento esperado:**
- Cuando el experto rechaza (primer rechazo), debería:
  1. Cancelar el timer de respuesta del experto (24h para aceptar/rechazar)
  2. Cancelar el job de Hangfire programado para ese timer
  3. Crear un nuevo timer de 24h para que el cliente proponga otra vez
  4. Programar un nuevo job de Hangfire para el nuevo timer

---

## ✅ Soluciones Propuestas

### Solución 1: Cancelar Jobs de Hangfire cuando se cancelan/rechazan citas

**Necesitamos:**
1. Guardar el `JobId` de Hangfire cuando se programa un job
2. Cancelar el job cuando se cancela/rechaza la cita

**Implementación:**

#### Paso 1: Agregar campo `HangfireJobId` a `AppointmentTimer`

```sql
ALTER TABLE "AppointmentTimers" 
ADD COLUMN "HangfireJobId" VARCHAR(255) NULL;
```

#### Paso 2: Guardar JobId cuando se programa un job

```csharp
// En AppointmentService.cs - cuando se crea un timer y se programa un job
var jobId = BackgroundJob.Schedule<IAppointmentService>(
    service => service.ProcessAppointmentTimerAsync(proposalTimer.Id),
    proposalTimer.EndTime - DateTime.UtcNow
);

// Guardar el JobId en el timer
proposalTimer.HangfireJobId = jobId;
await _context.SaveChangesAsync();
```

#### Paso 3: Cancelar jobs cuando se cancelan/rechazan citas

```csharp
// En CancelAppointmentAsync - después de marcar timers como expirados
foreach (var timer in activeTimers)
{
    timer.IsExpired = true;
    timer.ExpiredAt = DateTime.UtcNow;
    
    // ✅ CANCELAR job de Hangfire si existe
    if (!string.IsNullOrEmpty(timer.HangfireJobId))
    {
        BackgroundJob.Delete(timer.HangfireJobId);
        timer.HangfireJobId = null; // Limpiar referencia
    }
}

// En RejectAppointmentAsync - después de marcar timers como expirados
foreach (var timer in responseTimers)
{
    timer.IsExpired = true;
    timer.ExpiredAt = DateTime.UtcNow;
    
    // ✅ CANCELAR job de Hangfire si existe
    if (!string.IsNullOrEmpty(timer.HangfireJobId))
    {
        BackgroundJob.Delete(timer.HangfireJobId);
        timer.HangfireJobId = null; // Limpiar referencia
    }
}
```

---

### Solución 2: Crear nuevo timer cuando el experto rechaza (primer rechazo)

**Implementación:**

```csharp
// En RejectAppointmentAsync - después de marcar timers de respuesta como expirados
// Si es primer rechazo (no segundo), crear nuevo timer para el cliente
if (!isSecondRejection)
{
    // Cambiar estado a awaiting_appointment para permitir nueva propuesta
    var awaitingStatus = await _context.SystemStatuses
        .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                s.StatusValue == "awaiting_appointment");
    
    if (awaitingStatus != null)
    {
        appointment.StatusId = awaitingStatus.Id;
        
        // Crear nuevo timer para propuesta del cliente (24 horas)
        var proposalTimer = new AppointmentTimer
        {
            AppointmentId = appointment.Id,
            TimerType = "proposal",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(24),
            IsExpired = false,
            CreatedAt = DateTime.UtcNow
        };
        
        _context.AppointmentTimers.Add(proposalTimer);
        await _context.SaveChangesAsync();
        
        // Programar scheduled job para cuando expire el timer (24 horas)
        var jobId = BackgroundJob.Schedule<IAppointmentService>(
            service => service.ProcessAppointmentTimerAsync(proposalTimer.Id),
            proposalTimer.EndTime - DateTime.UtcNow
        );
        
        // Guardar el JobId en el timer
        proposalTimer.HangfireJobId = jobId;
        await _context.SaveChangesAsync();
    }
}
```

---

## 📋 Resumen de Cambios Necesarios

### 1. **Migración de Base de Datos**
- Agregar columna `HangfireJobId` a tabla `AppointmentTimers`

### 2. **Modificar AppointmentService.cs**

#### a) Guardar JobId cuando se programa un job (múltiples lugares):
- Línea 199-202: Timer de propuesta inicial
- Línea 299-302: Timer de propuesta en UpdateAppointmentAsync
- Línea 367-369: Timer de propuesta en cancelación (reprogramación)
- Línea 442-445: Timer de respuesta cuando cliente propone
- Línea 599: Timer de reporte
- Línea 1702: Timer de reporte
- Línea 1817: Timer de reporte

#### b) Cancelar jobs en CancelAppointmentAsync:
- Línea 1124-1133: Agregar cancelación de jobs

#### c) Cancelar jobs y crear nuevo timer en RejectAppointmentAsync:
- Línea 760-771: Agregar cancelación de jobs
- Después de línea 791: Agregar lógica para crear nuevo timer si es primer rechazo

---

## 🎯 Flujo Correcto Esperado

### Escenario 1: Cliente propone y luego cancela
1. Cliente propone → Se crea timer de 24h para respuesta del experto
2. Cliente cancela → Se marca timer como expirado + **Se cancela job de Hangfire**
3. ✅ No se ejecuta job innecesariamente

### Escenario 2: Cliente propone y experto rechaza (primer rechazo)
1. Cliente propone → Se crea timer de 24h para respuesta del experto
2. Experto rechaza → Se marca timer de respuesta como expirado + **Se cancela job de Hangfire**
3. **Se crea nuevo timer de 24h para que el cliente proponga otra vez**
4. **Se programa nuevo job de Hangfire para el nuevo timer**
5. ✅ Cliente tiene 24h para proponer otra vez

### Escenario 3: Cliente propone y experto rechaza (segundo rechazo)
1. Cliente propone → Se crea timer de 24h para respuesta del experto
2. Experto rechaza (segundo) → Se marca timer como expirado + **Se cancela job de Hangfire**
3. Se procesa refund automático
4. ✅ No se crea nuevo timer (es cancelación final)

---

## ⚠️ Nota Importante

Actualmente, `ProcessAppointmentTimerAsync` verifica si el timer está expirado antes de procesar (línea 1330-1332), lo cual previene procesamiento incorrecto, pero **el job ya se ejecutó innecesariamente**. Es mejor cancelar el job desde el principio para evitar ejecuciones innecesarias.































