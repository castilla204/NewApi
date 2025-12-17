# ✅ Logging Detallado Agregado a ProcessAppointmentTimerAsync

## 📋 Resumen

He agregado **logging detallado** en todos los puntos críticos del método `ProcessAppointmentTimerAsync` para diagnosticar por qué no cancela las citas cuando se ejecuta manualmente.

**NO se cambió la lógica** - solo se agregó logging para entender qué está pasando.

---

## 🔍 Puntos de Logging Agregados

### 1. Timer No Encontrado
```csharp
if (timer == null)
{
    // ✅ LOG: Timer no encontrado
    await _loggingService.LogWarningAsync(...);
    return;
}
```

### 2. Timer Ya Expirado
```csharp
if (timer.IsExpired)
{
    // ✅ LOG: Registrar por qué se retorna
    await _loggingService.LogInfoAsync(
        message: "Timer ya expirado - retornando sin procesar",
        details: $"Timer {timerId} ya está marcado como expirado (IsExpired=true). " +
                $"AppointmentId: {timer.AppointmentId}, TimerType: {timer.TimerType}, " +
                $"AppointmentStatus: {timer.Appointment?.Status?.StatusValue ?? "null"}"
    );
    return;
}
```

### 3. Appointment o SearchHire Eliminados
```csharp
if (timer.Appointment == null || timer.Appointment.SearchHire == null)
{
    // ✅ LOG: Appointment o SearchHire eliminados
    await _loggingService.LogWarningAsync(...);
    return;
}
```

### 4. SearchHire Ya Finalizado
```csharp
if (searchHire.Status?.IsFinalizationStatus == true)
{
    // ✅ LOG: SearchHire ya finalizado
    await _loggingService.LogInfoAsync(...);
    return;
}
```

### 5. SearchHire No Está en "pending"
```csharp
if (searchHireStatus != "pending")
{
    // ✅ LOG: SearchHire no está en pending
    await _loggingService.LogInfoAsync(
        message: "SearchHire no está en pending - retornando sin procesar",
        details: $"Timer {timerId}, TimerType: {timer.TimerType}. SearchHireId: {searchHire.Id}, " +
                $"SearchHireStatus actual: '{searchHireStatus}', esperado: 'pending'"
    );
    return;
}
```

### 6. Estado de Cita No Válido para Timer de Response
```csharp
if (timer.TimerType == "response" && appointmentStatus != "appointment_proposed")
{
    // ✅ LOG: Estado de cita no válido
    await _loggingService.LogInfoAsync(
        message: "Estado de cita no válido para timer de response - retornando sin procesar",
        details: $"Timer {timerId}, TimerType: response. AppointmentId: {timer.AppointmentId}, " +
                $"AppointmentStatus actual: '{appointmentStatus}', esperado: 'appointment_proposed'"
    );
    return;
}
```

### 7. Inicio de Procesamiento
```csharp
// ✅ LOG: Inicio de procesamiento del timer
await _loggingService.LogInfoAsync(
    message: "Iniciando procesamiento de timer",
    details: $"Timer {timerId}, TimerType: {timer.TimerType}, AppointmentId: {timer.AppointmentId}, " +
            $"AppointmentStatus: {appointmentStatus}, SearchHireId: {searchHire.Id}, " +
            $"SearchHireStatus: {searchHireStatus}, EndTime: {timer.EndTime}, Now: {DateTime.UtcNow}"
);
```

### 8. Procesamiento de Timer "response"
```csharp
case "response":
    // ✅ LOG: Inicio de cancelación
    await _loggingService.LogInfoAsync(
        message: "Iniciando cancelación de cita por falta de respuesta",
        details: $"Timer {timerId} expirado. TimerType: response, AppointmentId: {timer.AppointmentId}, " +
                $"AppointmentStatus actual: {appointmentStatus}, SearchHireId: {timer.Appointment.SearchHireId}, " +
                $"SearchHireStatus: {searchHireStatus}"
    );
    
    // ✅ LOG: Estado cambiado
    await _loggingService.LogInfoAsync(
        message: "Estado de cita cambiado",
        details: $"Cambiando estado de cita {timer.AppointmentId} a 'appointment_cancelled_by_expert_no_response'"
    );
    
    // ✅ LOG: Iniciando procesamiento de dinero
    await _loggingService.LogInfoAsync(
        message: "Iniciando procesamiento de distribución de dinero",
        details: $"Llamando a ProcessMoneyDistributionAsync para SearchHireId: {timer.Appointment.SearchHireId}"
    );
    
    // ... procesar dinero ...
    
    // ✅ LOG: Procesamiento de dinero completado
    await _loggingService.LogInfoAsync(
        message: "Distribución de dinero procesada",
        details: $"ProcessMoneyDistributionAsync completado para SearchHireId: {timer.Appointment.SearchHireId}"
    );
```

### 9. Guardando Cambios
```csharp
// ✅ LOG: Guardando cambios en base de datos
await _loggingService.LogInfoAsync(
    message: "Guardando cambios en base de datos",
    details: $"Timer {timerId} procesado. Guardando cambios: IsExpired=true, AppointmentStatusId={timer.Appointment?.StatusId}"
);

await _context.SaveChangesAsync();

// ✅ LOG: Cambios guardados exitosamente
await _loggingService.LogInfoAsync(
    message: "Timer procesado exitosamente",
    details: $"Timer {timerId} procesado y cambios guardados en base de datos. " +
            $"AppointmentId: {timer.AppointmentId}, AppointmentStatus: {timer.Appointment?.Status?.StatusValue ?? "null"}"
);
```

### 10. Errores
- ✅ Logging de errores en `ProcessMoneyDistributionAsync`
- ✅ Logging de errores generales en el catch principal
- ✅ Logging cuando el estado no se encuentra

---

## 🔍 Cómo Usar el Logging

### Ver Logs en Base de Datos

```sql
SELECT 
    "Id",
    "Message",
    "Details",
    "CreatedAt",
    "LogTypeId",
    "Source"
FROM "Logs"
WHERE "Source" = 'AppointmentService.ProcessAppointmentTimerAsync'
AND "RelatedEntityId" = 218  -- ⚠️ Reemplaza con el timerId que quieres diagnosticar
ORDER BY "CreatedAt" DESC;
```

### Ver Todos los Logs de un Timer Específico

```sql
SELECT 
    l."Id",
    l."Message",
    l."Details",
    l."CreatedAt",
    lt."Name" as log_type,
    l."Source"
FROM "Logs" l
INNER JOIN "LogTypes" lt ON l."LogTypeId" = lt."Id"
WHERE l."Source" = 'AppointmentService.ProcessAppointmentTimerAsync'
AND l."RelatedEntityId" = 218  -- ⚠️ Reemplaza con el timerId
ORDER BY l."CreatedAt" DESC;
```

---

## 📊 Qué Verás en los Logs

Cuando ejecutes el job manualmente, verás una secuencia de logs como:

1. **"Iniciando procesamiento de timer"** - El método comenzó
2. **"Iniciando cancelación de cita por falta de respuesta"** - Entró al case "response"
3. **"Estado de cita cambiado"** - Cambió el estado de la cita
4. **"Iniciando procesamiento de distribución de dinero"** - Llamó a ProcessMoneyDistributionAsync
5. **"Distribución de dinero procesada"** - ProcessMoneyDistributionAsync completó
6. **"Guardando cambios en base de datos"** - Guardando cambios
7. **"Timer procesado exitosamente"** - Todo completado

**O si hay un problema, verás:**
- **"Timer ya expirado - retornando sin procesar"** - El timer ya estaba expirado
- **"Estado de cita no válido para timer de response"** - El estado no es correcto
- **"SearchHire no está en pending"** - El SearchHire no está en el estado correcto
- **"Error procesando distribución de dinero"** - Hubo un error en ProcessMoneyDistributionAsync

---

## ✅ Próximos Pasos

1. **Ejecutar el job manualmente** desde Hangfire Dashboard
2. **Revisar los logs** en la base de datos con la query de arriba
3. **Identificar en qué punto se detiene** el procesamiento
4. **Corregir el problema** según el log que aparezca

---

## 📝 Nota

**NO se cambió la lógica del código** - solo se agregó logging detallado para diagnosticar el problema. El código funciona exactamente igual que antes, pero ahora podrás ver exactamente qué está pasando en cada paso.


