# 🎯 Eventos de Hangfire - Guía Completa

## 📋 Resumen

Este documento explica **todos los eventos/jobs de Hangfire** que se usan en el sistema, **cuándo se disparan** y **qué hacen**.

---

## 🔧 Tipos de Jobs de Hangfire

### 1. **BackgroundJob.Enqueue** ✅ (Ejecución Inmediata)
**Cuándo se dispara**: Inmediatamente cuando se llama
**Uso**: Para tareas que deben ejecutarse en segundo plano sin esperar

**Eventos que usan Enqueue:**

#### 1.1. Envío de Emails
- **Método**: `NotificationService.SendAppointmentConfirmationEmailJob`
- **Cuándo se dispara**: Cuando se confirma una cita (`ConfirmAppointmentAsync`)
- **Qué hace**: Envía email de confirmación al cliente y experto
- **Reintentos**: 3 intentos (60s, 5m, 10m)
- **Ubicación**: `Services/NotificationService.cs` línea 147

- **Método**: `NotificationService.SendWelcomeEmailJob`
- **Cuándo se dispara**: Cuando un nuevo usuario se registra
- **Qué hace**: Envía email de bienvenida
- **Reintentos**: 3 intentos (60s, 5m, 10m)

- **Método**: `NotificationService.SendGeneralNotificationEmailJob`
- **Cuándo se dispara**: Para notificaciones generales (cancelaciones, cambios de estado, etc.)
- **Qué hace**: Envía email de notificación
- **Reintentos**: 3 intentos (60s, 5m, 10m)

- **Método**: `NotificationService.SendServiceCompletionEmailJob`
- **Cuándo se dispara**: Cuando un servicio se completa
- **Qué hace**: Envía email de finalización de servicio
- **Reintentos**: 3 intentos (60s, 5m, 10m)

#### 1.2. Envío de Facturas
- **Método**: `InvoiceService.SendInvoiceByEmailBackgroundJob`
- **Cuándo se dispara**: 
  - Cuando se completa un SearchHire (`SearchHireController`)
  - Cuando se procesa un pago (`SubscriptionController`)
- **Qué hace**: Genera y envía factura por email
- **Reintentos**: 3 intentos (60s, 5m, 10m)
- **Ubicación**: `Services/InvoiceService.cs` línea 292

#### 1.3. Logging en Segundo Plano
- **Método**: `LoggingService.SendEmailBackgroundJob`
- **Cuándo se dispara**: Cuando se necesita enviar un email de log/notificación
- **Qué hace**: Envía email de notificación del sistema
- **Reintentos**: 3 intentos (60s, 5m, 10m)
- **Ubicación**: `Services/LoggingService.cs` línea 915

---

### 2. **BackgroundJob.Schedule** ✅ (Ejecución Diferida - Programada)
**Cuándo se dispara**: En un momento específico en el futuro (calculado al crear el job)
**Uso**: Para tareas que deben ejecutarse después de un tiempo determinado

**Eventos que usan Schedule:**

#### 2.1. Timer "proposal" - Cliente No Propone Cita en 24h
- **Método**: `AppointmentService.ProcessAppointmentTimerAsync(timerId)`
- **Cuándo se dispara**: 24 horas después de crear el timer
- **Cuándo se crea el timer**:
  - ✅ Cuando se crea un SearchHire nuevo (`CreateAppointmentAsync`)
  - ✅ Cuando el experto rechaza una propuesta (primer rechazo) (`RejectAppointmentAsync`)
  - ✅ Cuando se cancela una cita (primera cancelación) (`CancelAppointmentAsync`)
- **Qué hace cuando expira**:
  1. Valida que el timer no esté ya procesado
  2. Verifica que SearchHire esté en estado "pending"
  3. Verifica que Appointment esté en: `awaiting_appointment`, `appointment_rejected`, `appointment_cancelled_by_client`, o `appointment_cancelled_by_expert`
  4. Cambia estado a: `appointment_cancelled_by_client_no_proposal`
  5. Procesa dinero: Cliente 0%, Experto 100%, Plataforma 0%
- **Reintentos**: 5 intentos (1m, 5m, 10m, 15m, 20m)
- **Ubicaciones donde se programa**:
  - `AppointmentService.CreateAppointmentAsync` línea 758
  - `AppointmentService.RejectAppointmentAsync` línea 2484
  - `AppointmentService.CancelAppointmentAsync` línea 3137
  - `SearchHireController` línea 273
  - `SubscriptionController` línea 3610

#### 2.2. Timer "response" - Experto No Responde a Propuesta en 24h
- **Método**: `AppointmentService.ProcessAppointmentTimerAsync(timerId)`
- **Cuándo se dispara**: 24 horas después de que el cliente propone la cita
- **Cuándo se crea el timer**:
  - ✅ Cuando el cliente propone una cita (`ProposeAppointmentAsync`)
- **Qué hace cuando expira**:
  1. Valida que el timer no esté ya procesado
  2. Verifica que SearchHire esté en estado "pending"
  3. Verifica que Appointment esté en `appointment_proposed`
  4. Cambia estado a: `appointment_cancelled_by_expert_no_response`
  5. Procesa dinero: Cliente 100%, Experto 0%, Plataforma 0%
- **Reintentos**: 5 intentos (1m, 5m, 10m, 15m, 20m)
- **Ubicación donde se programa**: `AppointmentService.ProposeAppointmentAsync` línea 1267

#### 2.3. Timer "expert_report" - Experto No Envía Reporte en 24h
- **Método**: `AppointmentService.ProcessAppointmentTimerAsync(timerId)`
- **Cuándo se dispara**: 24 horas después de que se crea el timer
- **Cuándo se crea el timer**:
  - ✅ Cuando pasan 3 horas desde la hora de la cita confirmada (`ProcessAppointmentToAwaitingReportAsync`)
  - ✅ También se crea en `CheckAppointmentTimersAsync` para citas confirmadas que ya pasaron las 3 horas
- **Qué hace cuando expira**:
  1. Valida que el timer no esté ya procesado
  2. Verifica que SearchHire esté en estado "pending"
  3. Verifica que Appointment esté en `appointment_awaiting_report`
  4. Cambia estado a: `appointment_cancelled_by_no_report`
  5. Procesa dinero: Cliente 95%, Experto 0%, Plataforma 5%
- **Reintentos**: 5 intentos (1m, 5m, 10m, 15m, 20m)
- **Ubicaciones donde se programa**:
  - `AppointmentService.ProcessAppointmentToAwaitingReportAsync` línea 6225
  - `AppointmentService.CheckAppointmentTimersAsync` línea 4423

#### 2.4. Timer "client_decision" - Cliente No Decide (Aprueba/Disputa) en 24h
- **Método**: `AppointmentService.ProcessAppointmentTimerAsync(timerId)`
- **Cuándo se dispara**: 24 horas después de que el experto envía el reporte
- **Cuándo se crea el timer**:
  - ✅ Cuando el experto envía el reporte (`SubmitExpertReportAsync`)
- **Qué hace cuando expira**:
  1. Valida que el timer no esté ya procesado
  2. Verifica que SearchHire esté en estado "awaiting_client_decision"
  3. Verifica que Appointment esté en `appointment_report_sent`
  4. Cambia estado a: `completed_without_client_approval` (solo SearchHire)
  5. Procesa dinero: Cliente 0%, Experto 100%, Plataforma 0%
  6. Notifica al experto que el servicio se completó a su favor
- **Reintentos**: 5 intentos (1m, 5m, 10m, 15m, 20m)
- **Ubicación donde se programa**: `AppointmentService.SubmitExpertReportAsync` línea 6614

#### 2.5. Timer "awaiting_report_transition" - Transición a Awaiting Report
- **Método**: `AppointmentService.ProcessAppointmentToAwaitingReportAsync(appointmentId)`
- **Cuándo se dispara**: 3 horas después de la hora de la cita confirmada
- **Cuándo se crea el timer**:
  - ✅ Cuando el experto confirma la cita (`ConfirmAppointmentAsync`)
  - ⚠️ Solo si `timeUntil3HoursAfter.TotalSeconds > 0` (si aún no han pasado las 3 horas)
- **Qué hace cuando expira**:
  1. Valida que Appointment exista y esté en `appointment_confirmed`
  2. Valida que SearchHire exista y NO esté finalizado
  3. Valida que usuarios no estén bloqueados
  4. Cambia estado a: `appointment_awaiting_report`
  5. Crea nuevo timer "expert_report" de 24 horas
  6. Programa job de Hangfire para el timer "expert_report"
  7. Notifica al experto que debe enviar el reporte
  8. Notifica al cliente que se está esperando el reporte
  9. Marca el timer de transición como expirado
- **Reintentos**: 5 intentos (1m, 5m, 10m, 15m, 20m)
- **Ubicación donde se programa**: `AppointmentService.ConfirmAppointmentAsync` línea 1633

---

### 3. **RecurringJob** ❌ (NO HAY - Jobs Periódicos)
**Estado**: NO CONFIGURADO

**Nota**: Los recurring jobs fueron eliminados según comentario en `Program.cs`:
> "Los scheduled jobs se programan cuando ocurre el evento (más eficiente)"

**Si necesitas agregar un RecurringJob** (ejemplo):
```csharp
// En Program.cs después de configurar Hangfire
RecurringJob.AddOrUpdate(
    "cleanup-old-logs", // ID único del job
    () => service.CleanupOldLogs(),
    Cron.Daily(3) // Ejecutar diariamente a las 3 AM
);
```

---

## 📊 Tabla Resumen de Eventos

| Evento | Tipo | Método | Cuándo se Dispara | Duración | Reintentos |
|--------|------|--------|-------------------|----------|------------|
| **Envío Email Confirmación** | Enqueue | `SendAppointmentConfirmationEmailJob` | Al confirmar cita | Inmediato | 3 (60s, 5m, 10m) |
| **Envío Email Bienvenida** | Enqueue | `SendWelcomeEmailJob` | Al registrar usuario | Inmediato | 3 (60s, 5m, 10m) |
| **Envío Email Notificación** | Enqueue | `SendGeneralNotificationEmailJob` | Varios eventos | Inmediato | 3 (60s, 5m, 10m) |
| **Envío Factura** | Enqueue | `SendInvoiceByEmailBackgroundJob` | Al completar SearchHire | Inmediato | 3 (60s, 5m, 10m) |
| **Timer "proposal"** | Schedule | `ProcessAppointmentTimerAsync` | 24h después de crear timer | 24 horas | 5 (1m, 5m, 10m, 15m, 20m) |
| **Timer "response"** | Schedule | `ProcessAppointmentTimerAsync` | 24h después de proponer cita | 24 horas | 5 (1m, 5m, 10m, 15m, 20m) |
| **Timer "expert_report"** | Schedule | `ProcessAppointmentTimerAsync` | 24h después de crear timer | 24 horas | 5 (1m, 5m, 10m, 15m, 20m) |
| **Timer "client_decision"** | Schedule | `ProcessAppointmentTimerAsync` | 24h después de enviar reporte | 24 horas | 5 (1m, 5m, 10m, 15m, 20m) |
| **Timer "awaiting_report_transition"** | Schedule | `ProcessAppointmentToAwaitingReportAsync` | 3h después de hora de cita | 3 horas | 5 (1m, 5m, 10m, 15m, 20m) |

---

## 🔄 Flujo Completo de Eventos en un Appointment

### Escenario 1: Flujo Exitoso ✅

1. **Cliente crea SearchHire**
   - ✅ Se crea Appointment con estado `awaiting_appointment`
   - ✅ Se crea Timer "proposal" (24h)
   - ✅ Se programa: `ProcessAppointmentTimerAsync` en 24h

2. **Cliente propone cita** (`ProposeAppointmentAsync`)
   - ✅ Se cancela Timer "proposal" (si existe)
   - ✅ Se cancela Job de Hangfire del timer "proposal"
   - ✅ Se crea Timer "response" (24h)
   - ✅ Se programa: `ProcessAppointmentTimerAsync` en 24h

3. **Experto confirma cita** (`ConfirmAppointmentAsync`)
   - ✅ Se cancela Timer "response"
   - ✅ Se cancela Job de Hangfire del timer "response"
   - ✅ Se crea Timer "awaiting_report_transition" (3h)
   - ✅ Se programa: `ProcessAppointmentToAwaitingReportAsync` en 3h
   - ✅ Se dispara: `SendAppointmentConfirmationEmailJob` (Enqueue - inmediato)

4. **Pasan 3 horas desde la hora de la cita**
   - ✅ Se ejecuta: `ProcessAppointmentToAwaitingReportAsync`
   - ✅ Cambia estado a: `appointment_awaiting_report`
   - ✅ Se crea Timer "expert_report" (24h)
   - ✅ Se programa: `ProcessAppointmentTimerAsync` en 24h
   - ✅ Se dispara: `SendGeneralNotificationEmailJob` (Enqueue - inmediato) al experto

5. **Experto envía reporte** (`SubmitExpertReportAsync`)
   - ✅ Se cancela Timer "expert_report"
   - ✅ Se cancela Job de Hangfire del timer "expert_report"
   - ✅ Se crea Timer "client_decision" (24h)
   - ✅ Se programa: `ProcessAppointmentTimerAsync` en 24h

6. **Cliente aprueba/disputa**
   - ✅ Se cancela Timer "client_decision"
   - ✅ Se cancela Job de Hangfire del timer "client_decision"
   - ✅ Finalizado

---

### Escenario 2: Cliente No Propone ❌

1. **Cliente crea SearchHire**
   - ✅ Se crea Timer "proposal" (24h)
   - ✅ Se programa: `ProcessAppointmentTimerAsync` en 24h

2. **Pasan 24 horas**
   - ✅ Se ejecuta: `ProcessAppointmentTimerAsync`
   - ✅ Cambia estado a: `appointment_cancelled_by_client_no_proposal`
   - ✅ Procesa dinero: Cliente 0%, Experto 100%, Plataforma 0%

---

### Escenario 3: Experto No Responde ❌

1. **Cliente propone cita**
   - ✅ Se crea Timer "response" (24h)
   - ✅ Se programa: `ProcessAppointmentTimerAsync` en 24h

2. **Pasan 24 horas**
   - ✅ Se ejecuta: `ProcessAppointmentTimerAsync`
   - ✅ Cambia estado a: `appointment_cancelled_by_expert_no_response`
   - ✅ Procesa dinero: Cliente 100%, Experto 0%, Plataforma 0%

---

### Escenario 4: Experto No Envía Reporte ❌

1. **Pasan 3 horas desde cita confirmada**
   - ✅ Se ejecuta: `ProcessAppointmentToAwaitingReportAsync`
   - ✅ Se crea Timer "expert_report" (24h)
   - ✅ Se programa: `ProcessAppointmentTimerAsync` en 24h

2. **Pasan 24 horas más**
   - ✅ Se ejecuta: `ProcessAppointmentTimerAsync`
   - ✅ Cambia estado a: `appointment_cancelled_by_no_report`
   - ✅ Procesa dinero: Cliente 95%, Experto 0%, Plataforma 5%

---

### Escenario 5: Cliente No Decide ❌

1. **Experto envía reporte**
   - ✅ Se crea Timer "client_decision" (24h)
   - ✅ Se programa: `ProcessAppointmentTimerAsync` en 24h

2. **Pasan 24 horas**
   - ✅ Se ejecuta: `ProcessAppointmentTimerAsync`
   - ✅ Cambia estado a: `completed_without_client_approval`
   - ✅ Procesa dinero: Cliente 0%, Experto 100%, Plataforma 0%
   - ✅ Notifica al experto

---

## 🎯 Métodos que Procesan los Timers

### 1. `ProcessAppointmentTimerAsync(int timerId)` ✅ (Principal)

**Cuándo se ejecuta**: Cuando expira un timer programado con `BackgroundJob.Schedule`

**Qué hace según el tipo de timer**:

| Timer Type | Validaciones | Cambios de Estado | Distribución de Dinero |
|------------|--------------|-------------------|------------------------|
| **"proposal"** | SearchHire="pending", Appointment en estados válidos | → `appointment_cancelled_by_client_no_proposal` | Cliente 0%, Experto 100% |
| **"response"** | SearchHire="pending", Appointment="appointment_proposed" | → `appointment_cancelled_by_expert_no_response` | Cliente 100%, Experto 0% |
| **"expert_report"** | SearchHire="pending", Appointment="appointment_awaiting_report" | → `appointment_cancelled_by_no_report` | Cliente 95%, Experto 0%, Plataforma 5% |
| **"client_decision"** | SearchHire="awaiting_client_decision", Appointment="appointment_report_sent" | → `completed_without_client_approval` | Cliente 0%, Experto 100% |

**Reintentos**: 5 intentos (1m, 5m, 10m, 15m, 20m)
**Ubicación**: `Services/AppointmentService.cs` línea 4494

---

### 2. `ProcessAppointmentToAwaitingReportAsync(int appointmentId)` ✅

**Cuándo se ejecuta**: 3 horas después de la hora de la cita confirmada

**Qué hace**:
1. Valida que Appointment esté en `appointment_confirmed`
2. Valida que SearchHire exista y NO esté finalizado
3. Valida que usuarios no estén bloqueados
4. Cambia Appointment a: `appointment_awaiting_report`
5. Crea Timer "expert_report" (24h)
6. Programa: `ProcessAppointmentTimerAsync` para el timer "expert_report"
7. Notifica al experto y cliente
8. Marca timer de transición como expirado

**Reintentos**: 5 intentos (1m, 5m, 10m, 15m, 20m)
**Ubicación**: `Services/AppointmentService.cs` línea 6005

---

### 3. `CheckAppointmentTimersAsync()` ⚠️ (Legacy/Backup)

**Cuándo se ejecuta**: NO está configurado como RecurringJob (fue eliminado)

**Qué hace**: Busca timers expirados y los procesa (método legacy)

**Nota**: Este método NO se ejecuta automáticamente. Solo se puede llamar manualmente.

**Ubicación**: `Services/AppointmentService.cs` línea 3649

---

## 🚨 Filtros de Hangfire

### 1. `HangfireFailedJobNotificationFilter` ✅ (Activo)

**Tipo**: `IElectStateFilter`

**Cuándo se dispara**: Cuando un job falla definitivamente (después de agotar todos los reintentos)

**Qué hace**:
1. Detecta cuando un job pasa a estado "Failed"
2. Verifica si es un job crítico (procesa dinero o estados)
3. Crea log crítico en la base de datos
4. Incluye información completa del error

**Jobs críticos detectados**:
- `ProcessAppointmentTimerAsync`
- `ProcessAppointmentToAwaitingReportAsync`
- `ProcessMoneyDistributionAsync`

**Ubicación**: `Services/HangfireFailedJobNotificationFilter.cs`

---

## 📝 Estados de Jobs en Hangfire

Hangfire maneja automáticamente estos estados:

1. **Scheduled** - Job programado para ejecutarse más tarde
2. **Enqueued** - Job encolado, esperando ejecución
3. **Processing** - Job en ejecución
4. **Succeeded** - Job completado exitosamente
5. **Failed** - Job falló (después de reintentos)
6. **Deleted** - Job eliminado

---

## ⚠️ Importante: Cancelación de Jobs

Cuando se cancela un timer (rechazo, cancelación, etc.), se debe:

1. ✅ Marcar el timer como expirado en la BD
2. ✅ Cancelar el job de Hangfire con `BackgroundJob.Delete(jobId)`
3. ✅ Limpiar `HangfireJobId` del timer

**Ubicaciones donde se cancela**:
- `AppointmentService.ConfirmAppointmentAsync` - cancela timers "response"
- `AppointmentService.RejectAppointmentAsync` - cancela timers "response"
- `AppointmentService.CancelAppointmentAsync` - cancela timers "awaiting_report_transition"
- `AppointmentService.ProposeAppointmentAsync` - cancela timers "proposal"
- `AppointmentService.SubmitExpertReportAsync` - cancela timers "expert_report"

---

## 🎯 Resumen de Cuándo se Dispara Cada Evento

| Evento | Se Dispara Cuando... | Tipo |
|--------|---------------------|------|
| **Envío Emails** | Se llama al método de notificación | Enqueue (inmediato) |
| **Envío Factura** | Se completa un SearchHire o se procesa pago | Enqueue (inmediato) |
| **Timer "proposal"** | 24h después de crear SearchHire/rechazo/cancelación | Schedule (24h) |
| **Timer "response"** | 24h después de proponer cita | Schedule (24h) |
| **Timer "awaiting_report_transition"** | 3h después de la hora de la cita confirmada | Schedule (3h) |
| **Timer "expert_report"** | 24h después de pasar a awaiting_report | Schedule (24h) |
| **Timer "client_decision"** | 24h después de enviar reporte | Schedule (24h) |

---

## ✅ Mejores Prácticas

1. **Siempre cancelar jobs** cuando se cancela un timer
2. **Guardar HangfireJobId** en el timer para poder cancelarlo
3. **Usar AutomaticRetry** para jobs críticos (procesamiento de dinero)
4. **Logging completo** en todos los métodos que procesan timers
5. **Validaciones exhaustivas** antes de procesar (evitar doble procesamiento)

---

## 🔍 Cómo Verificar Jobs en Hangfire

1. **Dashboard**: `https://tu-dominio.com/hangfire`
2. **Tabla PostgreSQL**: `hangfire.job` - ver todos los jobs
3. **Logs**: Buscar en `Logs` tabla con `source LIKE 'Hangfire.%'`

---

## 📌 Notas Finales

- ✅ **NO hay RecurringJobs** configurados (fueron eliminados)
- ✅ Todos los jobs son **una sola ejecución** (Enqueue o Schedule)
- ✅ Los jobs se programan **cuando ocurre el evento**, no periódicamente
- ✅ Los jobs críticos tienen **5 reintentos** con delays progresivos
- ✅ Los jobs de email tienen **3 reintentos** con delays progresivos
