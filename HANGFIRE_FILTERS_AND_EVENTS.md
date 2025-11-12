# Hangfire Filters y Events - Documentación Completa

## 📋 Filtros y Atributos Actualmente Usados

### 1. **AutomaticRetry** ✅ (EN USO)
**Ubicación:**
- `Services/InvoiceService.cs` línea 408
- `Services/LoggingService.cs` línea 478

**Configuración actual:**
```csharp
[AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
```

**Comportamiento:**
- Reintenta 3 veces si el job falla
- Delays: 60 segundos, 5 minutos, 10 minutos

---

## 🔧 Filtros Disponibles en Hangfire (NO CONFIGURADOS)

### 1. **IServerFilter**
Intercepta la ejecución del job en el servidor.

**Eventos disponibles:**
- `OnPerforming(PerformingContext)` - Antes de ejecutar el job
- `OnPerformed(PerformedContext)` - Después de ejecutar el job (incluso si falla)
- `OnStateElection(ElectStateContext)` - Cuando se elige un nuevo estado

**Ejemplo:**
```csharp
public class LogServerFilter : IServerFilter
{
    public void OnPerforming(PerformingContext filterContext)
    {
        Console.WriteLine($"Job {filterContext.BackgroundJob.Id} está por ejecutarse");
    }

    public void OnPerformed(PerformedContext filterContext)
    {
        if (filterContext.Exception != null)
        {
            Console.WriteLine($"Job {filterContext.BackgroundJob.Id} falló: {filterContext.Exception.Message}");
        }
        else
        {
            Console.WriteLine($"Job {filterContext.BackgroundJob.Id} completado exitosamente");
        }
    }
}
```

### 2. **IElectStateFilter**
Intercepta cuando Hangfire elige un nuevo estado para el job.

**Eventos disponibles:**
- `OnStateElection(ElectStateContext)` - Cuando se elige un estado

**Ejemplo:**
```csharp
public class CustomStateFilter : IElectStateFilter
{
    public void OnStateElection(ElectStateContext context)
    {
        // Puedes cambiar el estado elegido aquí
        if (context.CandidateState is FailedState failedState)
        {
            // Personalizar el comportamiento cuando falla
            Console.WriteLine($"Job falló: {failedState.Exception.Message}");
        }
    }
}
```

### 3. **IApplyStateFilter**
Intercepta cuando se aplica un estado al job.

**Eventos disponibles:**
- `OnStateApplied(ApplyStateContext, IWriteOnlyTransaction)` - Cuando se aplica un estado
- `OnStateUnapplied(ApplyStateContext, IWriteOnlyTransaction)` - Cuando se desaplica un estado

**Ejemplo:**
```csharp
public class StateChangeFilter : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        Console.WriteLine($"Estado aplicado: {context.NewState.Name} al job {context.BackgroundJob.Id}");
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        Console.WriteLine($"Estado desaplicado: {context.OldState.Name} del job {context.BackgroundJob.Id}");
    }
}
```

### 4. **IClientFilter**
Intercepta la creación del job en el cliente.

**Eventos disponibles:**
- `OnCreating(CreatingContext)` - Antes de crear el job
- `OnCreated(CreatedContext)` - Después de crear el job

**Ejemplo:**
```csharp
public class ClientFilter : IClientFilter
{
    public void OnCreating(CreatingContext filterContext)
    {
        Console.WriteLine($"Creando job: {filterContext.Job.Method.Name}");
    }

    public void OnCreated(CreatedContext filterContext)
    {
        Console.WriteLine($"Job creado con ID: {filterContext.BackgroundJob.Id}");
    }
}
```

### 5. **IServerFilterProvider**
Proporciona filtros dinámicamente basados en el contexto.

**Ejemplo:**
```csharp
public class DynamicFilterProvider : IServerFilterProvider
{
    public IEnumerable<IServerFilter> GetFilters(JobContext jobContext)
    {
        // Retornar filtros basados en el contexto
        if (jobContext.BackgroundJob.Job.Method.Name.Contains("Email"))
        {
            yield return new EmailJobFilter();
        }
    }
}
```

---

## 📦 Atributos Disponibles (NO TODOS EN USO)

### 1. **AutomaticRetry** ✅ (EN USO)
```csharp
[AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
```

### 2. **DisableConcurrentExecution** ❌ (NO USADO)
Evita que múltiples instancias del mismo job se ejecuten simultáneamente.

```csharp
[DisableConcurrentExecution(timeoutInSeconds: 60)]
public void ProcessData(int id) { }
```

### 3. **Queue** ❌ (NO USADO)
Especifica en qué cola se ejecuta el job.

```csharp
[Queue("critical")]
public void CriticalJob() { }
```

### 4. **JobDisplayName** ❌ (NO USADO)
Personaliza el nombre del job en el dashboard.

```csharp
[JobDisplayName("Enviar Email a {0}")]
public void SendEmail(string email) { }
```

### 5. **QueueFromParameter** ❌ (NO USADO)
Especifica la cola desde un parámetro del método.

```csharp
[QueueFromParameter("queueName")]
public void ProcessInQueue(string queueName, int id) { }
```

---

## 🎯 Jobs Actualmente en Uso

### 1. **BackgroundJob.Enqueue** ✅ (Ejecución Inmediata - UNA VEZ)
**Ubicaciones:**
- `Controllers/SearchHireController.cs` línea 190
- `Controllers/SubscriptionController.cs` línea 2129
- `Services/LoggingService.cs` línea 448

**Uso:**
```csharp
Hangfire.BackgroundJob.Enqueue<IInvoiceService>(service => 
    service.SendInvoiceByEmailBackgroundJob(searchHireId, toEmail));
```

### 2. **BackgroundJob.Schedule** ✅ (Ejecución Diferida - UNA VEZ)
**Ubicaciones:**
- `Services/AppointmentService.cs` - Múltiples usos (líneas 199, 299, 367, 442, 599, 1702)
- `Services/AppointmentService.cs` línea 1817

**Uso:**
```csharp
BackgroundJob.Schedule<IAppointmentService>(
    service => service.ProcessAppointmentTimeout(appointmentId),
    delay);
```

### 3. **RecurringJob** ❌ (NO HAY - Jobs Periódicos)
**Estado:** NO CONFIGURADO

**Nota:** Los recurring jobs fueron eliminados según comentario en `Program.cs` líneas 415-420:
> "Los recurring jobs fueron eliminados porque:
> 1. Los scheduled jobs se programan cuando ocurre el evento (más eficiente)
> 2. Hangfire tiene reintentos automáticos para scheduled jobs que fallan
> 3. Evita verificar periódicamente cuando no hay nada que procesar
> 4. Mejor práctica: programar jobs cuando ocurre el evento, no verificar periódicamente"

**Si necesitas agregar un RecurringJob, ejemplo:**
```csharp
// En Program.cs después de configurar Hangfire
RecurringJob.AddOrUpdate(
    "cleanup-old-logs", // ID único del job
    () => Console.WriteLine("Limpiando logs antiguos"),
    Cron.Daily // Ejecutar diariamente a medianoche
);

// O con expresión CRON personalizada
RecurringJob.AddOrUpdate(
    "check-appointments",
    () => service.CheckPendingAppointments(),
    "*/5 * * * *" // Cada 5 minutos
);

// Ejemplos de expresiones CRON:
// "0 */1 * * *" - Cada hora
// "0 0 * * *" - Diariamente a medianoche
// "0 0 * * 0" - Cada domingo a medianoche
// "*/30 * * * *" - Cada 30 minutos
```

**Expresiones CRON comunes:**
- `Cron.Minutely()` - Cada minuto
- `Cron.Hourly()` - Cada hora
- `Cron.Daily()` - Diariamente a medianoche
- `Cron.Weekly()` - Semanalmente
- `Cron.Monthly()` - Mensualmente
- `"*/5 * * * *"` - Cada 5 minutos
- `"0 */2 * * *"` - Cada 2 horas

---

## 🔄 Estados de Jobs en Hangfire

Hangfire maneja los siguientes estados automáticamente:

1. **Enqueued** - Job encolado, esperando ejecución
2. **Processing** - Job en ejecución
3. **Succeeded** - Job completado exitosamente
4. **Failed** - Job falló
5. **Deleted** - Job eliminado
6. **Awaiting** - Job esperando una continuación
7. **Scheduled** - Job programado para ejecutarse más tarde

---

## 📝 Cómo Agregar Filtros Globales

Para agregar filtros globales a todos los jobs, se debe configurar en `Program.cs`:

```csharp
// Configure Hangfire
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(builder.Configuration.GetConnectionString("PostgresConnection"), new PostgreSqlStorageOptions
    {
        QueuePollInterval = TimeSpan.FromSeconds(15),
        InvisibilityTimeout = TimeSpan.FromMinutes(30),
        DistributedLockTimeout = TimeSpan.FromMinutes(10),
        PrepareSchemaIfNecessary = true
    })
    .UseDefaultTypeResolver()
    .UseDefaultTypeSerializer()
    .UseFilter(new LogServerFilter()) // ✅ Agregar filtro global
    .UseFilter(new CustomStateFilter()) // ✅ Agregar otro filtro
);
```

---

## 🎯 Recomendaciones

### Filtros Útiles para Implementar:

1. **LogServerFilter** - Para logging de todos los jobs
2. **PerformanceFilter** - Para medir tiempo de ejecución
3. **ErrorNotificationFilter** - Para notificar errores críticos
4. **DisableConcurrentExecution** - Para jobs que no deben ejecutarse simultáneamente

### Ejemplo de Filtro de Performance:

```csharp
public class PerformanceFilter : IServerFilter
{
    public void OnPerforming(PerformingContext filterContext)
    {
        filterContext.Items["StartTime"] = DateTime.UtcNow;
    }

    public void OnPerformed(PerformedContext filterContext)
    {
        var startTime = (DateTime)filterContext.Items["StartTime"];
        var duration = DateTime.UtcNow - startTime;
        
        Console.WriteLine($"Job {filterContext.BackgroundJob.Id} tomó {duration.TotalSeconds} segundos");
        
        if (duration.TotalSeconds > 60)
        {
            // Log warning para jobs lentos
            Console.WriteLine($"⚠️ Job lento detectado: {filterContext.BackgroundJob.Job.Method.Name}");
        }
    }
}
```

---

## 📊 Resumen

| Tipo | Estado | Ubicación |
|------|--------|-----------|
| `AutomaticRetry` | ✅ En uso | InvoiceService, LoggingService |
| `IServerFilter` | ❌ No configurado | - |
| `IElectStateFilter` | ❌ No configurado | - |
| `IApplyStateFilter` | ❌ No configurado | - |
| `IClientFilter` | ❌ No configurado | - |
| `IServerFilterProvider` | ❌ No configurado | - |
| `DisableConcurrentExecution` | ❌ No usado | - |
| `Queue` | ❌ No usado | - |

**Total de jobs activos:**
- `Enqueue` (inmediato, una vez): 3 usos
- `Schedule` (diferido, una vez): 8+ usos
- `RecurringJob` (periódico, repetitivo): **0 usos** ❌

## ⚠️ IMPORTANTE: No hay Jobs Recurrentes

**Confirmado:** No hay ningún `RecurringJob` configurado en el código. Todos los jobs son:
- **Una sola ejecución** (Enqueue o Schedule)
- **Programados cuando ocurre un evento específico** (no periódicamente)

Si necesitas jobs que se ejecuten periódicamente (cada X tiempo), debes agregar `RecurringJob.AddOrUpdate()` en `Program.cs`.

