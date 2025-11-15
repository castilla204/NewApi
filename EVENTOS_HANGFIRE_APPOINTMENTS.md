# Eventos de Hangfire en Appointments - Análisis Completo

## 📋 Resumen

Este documento explica qué sucede cuando cada evento de Hangfire se ejecuta en el sistema de appointments. Hay dos métodos principales que procesan los timers:

1. **`CheckAppointmentTimersAsync()`** - Revisa timers expirados periódicamente (método legacy)
2. **`ProcessAppointmentTimerAsync(int timerId)`** - Procesa un timer específico cuando expira (método principal con reintentos)

---

## 🔄 Métodos de Procesamiento

### 1. `CheckAppointmentTimersAsync()` - Método Legacy

**Cuándo se ejecuta**: Recurring job que revisa periódicamente timers expirados

**Qué hace**:
- Busca timers con `EndTime <= DateTime.UtcNow` y `IsExpired = false`
- Procesa cada timer según su tipo
- **Problema**: No tiene reintentos automáticos de Hangfire

**Ubicación**: `AppointmentService.cs` línea 2684

---

### 2. `ProcessAppointmentTimerAsync(int timerId)` - Método Principal ✅

**Cuándo se ejecuta**: Job programado con `BackgroundJob.Schedule` cuando el timer expira

**Características**:
- ✅ Tiene `[AutomaticRetry]` con 5 intentos y delays progresivos (1m, 5m, 10m, 15m, 20m)
- ✅ Validaciones exhaustivas antes de procesar
- ✅ Manejo robusto de errores con fallbacks

**Ubicación**: `AppointmentService.cs` línea 3492

---

## ⏰ Tipos de Timers y Qué Sucede en Cada Uno

### 1. Timer "proposal" - Cliente No Propone Cita en 24h

**Cuándo se crea**:
- Cuando se crea un SearchHire nuevo
- Cuando el experto rechaza una propuesta (primer rechazo)
- Cuando se cancela una cita (primera cancelación)

**Duración**: 24 horas desde la creación

**Qué sucede cuando expira** (`ProcessAppointmentTimerAsync`):

1. **Validaciones**:
   - ✅ Verifica que el timer no esté ya expirado
   - ✅ Verifica que SearchHire y Appointment existan
   - ✅ Verifica que SearchHire NO esté finalizado
   - ✅ Verifica que usuarios no estén bloqueados
   - ✅ Verifica que SearchHire esté en estado "pending"
   - ✅ Verifica que Appointment esté en: `awaiting_appointment`, `appointment_rejected`, `appointment_cancelled_by_client`, o `appointment_cancelled_by_expert`

2. **Cambios de Estado**:
   - **AppointmentStatus**: → `appointment_cancelled_by_no_response`
   - **SearchHireStatus**: → `cancelled_by_client_no_proposal` (requerido, lanza excepción si no existe)

3. **Distribución de Dinero**:
   - **Estado usado**: `cancelled_by_client_no_proposal` (requerido)
   - **Porcentajes**: Cliente 0%, Experto 100%, Plataforma 0%
   - **Mensaje**: "Client did not propose within 24h - automatic cancellation"

4. **Notificaciones**:
   - ❌ No hay notificaciones específicas en este método (se procesa en CheckAppointmentTimersAsync)

**Ubicación en código**: `AppointmentService.cs` líneas 3641-3694

---

### 2. Timer "response" - Experto No Responde a Propuesta en 24h

**Cuándo se crea**:
- Cuando el cliente propone una cita (`ProposeAppointmentAsync`)

**Duración**: 24 horas desde la propuesta

**Qué sucede cuando expira** (`ProcessAppointmentTimerAsync`):

1. **Validaciones**:
   - ✅ Verifica que el timer no esté ya expirado
   - ✅ Verifica que SearchHire y Appointment existan
   - ✅ Verifica que SearchHire NO esté finalizado
   - ✅ Verifica que usuarios no estén bloqueados
   - ✅ Verifica que SearchHire esté en estado "pending"
   - ✅ Verifica que Appointment esté en `appointment_proposed`

2. **Cambios de Estado**:
   - **AppointmentStatus**: → `appointment_cancelled_by_no_response`
   - **SearchHireStatus**: → `cancelled_by_expert_no_response` (requerido, lanza excepción si no existe)

3. **Distribución de Dinero**:
   - **Estado usado**: `cancelled_by_expert_no_response` (requerido)
   - **Porcentajes**: Cliente 100%, Experto 0%, Plataforma 0%
   - **Mensaje**: "Expert did not respond within 24h - automatic cancellation"

4. **Notificaciones**:
   - ❌ No hay notificaciones específicas en este método (se procesa en CheckAppointmentTimersAsync)

**Ubicación en código**: `AppointmentService.cs` líneas 3696-3749

**Nota**: También se procesa en `CheckAppointmentTimersAsync` (líneas 2722-3003) con notificaciones al cliente y experto.

---

### 3. Timer "expert_report" - Experto No Envía Reporte en 24h

**Cuándo se crea**:
- Cuando pasan 3 horas desde la hora de la cita confirmada (`ProcessAppointmentToAwaitingReportAsync`)
- Cuando el experto confirma la cita y ya pasaron las 3 horas

**Duración**: 24 horas desde que se crea el timer (3 horas después de la cita)

**Qué sucede cuando expira** (`ProcessAppointmentTimerAsync`):

1. **Validaciones**:
   - ✅ Verifica que el timer no esté ya expirado
   - ✅ Verifica que SearchHire y Appointment existan
   - ✅ Verifica que SearchHire NO esté finalizado
   - ✅ Verifica que usuarios no estén bloqueados
   - ✅ Verifica que SearchHire esté en estado "pending"
   - ✅ Verifica que Appointment esté en `appointment_awaiting_report`

2. **Cambios de Estado**:
   - **AppointmentStatus**: → `appointment_cancelled_by_no_report`
   - **SearchHireStatus**: → `cancelled_by_expert_no_report` (si existe) o `cancelled` (fallback)

3. **Distribución de Dinero**:
   - **Estado usado**: `cancelled_by_expert_no_report` o `appointment_cancelled_by_no_report`
   - **Porcentajes**: Cliente 95%, Experto 0%, Plataforma 5%
   - **Mensaje**: "Expert did not submit report within 24h - automatic cancellation"

4. **Notificaciones**:
   - ❌ No hay notificaciones específicas en este método (se procesa en CheckAppointmentTimersAsync)

**Ubicación en código**: `AppointmentService.cs` líneas 3751-3804

**Nota**: También se procesa en `CheckAppointmentTimersAsync` (líneas 3007-3306) con validación de archivos y notificaciones.

---

### 4. Timer "client_decision" - Cliente No Decide (Aprueba/Disputa) en 24h

**Cuándo se crea**:
- Cuando el experto envía el reporte (`SubmitExpertReportAsync`)

**Duración**: 24 horas desde que se envía el reporte

**Qué sucede cuando expira** (`ProcessAppointmentTimerAsync`):

1. **Validaciones**:
   - ✅ Verifica que el timer no esté ya expirado
   - ✅ Verifica que SearchHire y Appointment existan
   - ✅ Verifica que SearchHire NO esté finalizado
   - ✅ Verifica que usuarios no estén bloqueados
   - ✅ Verifica que SearchHire esté en estado "awaiting_client_decision"
   - ✅ Verifica que Appointment esté en `appointment_report_sent`

2. **Cambios de Estado**:
   - **AppointmentStatus**: No cambia (permanece en `appointment_report_sent`)
   - **SearchHireStatus**: → `completed_without_client_approval`

3. **Distribución de Dinero**:
   - **Estado usado**: `completed_without_client_approval`
   - **Porcentajes**: Cliente 0%, Experto 100%, Plataforma 0%
   - **Mensaje**: "Client did not respond within 24h - automatic completion in favor of expert"

4. **Notificaciones**:
   - ✅ **Experto**: "Servicio completado automáticamente a tu favor" (con `notifyUser: true`)
   - ✅ **Cliente**: Solo log INFO (sin notificación)

5. **Fallback en caso de error**:
   - Si falla `ProcessMoneyDistributionAsync`, intenta cambiar el estado manualmente a "completed"
   - Log crítico si ambos fallan

**Ubicación en código**: `AppointmentService.cs` líneas 3806-4069

---

### 5. Timer "awaiting_report_transition" - Transición a Awaiting Report

**Cuándo se crea**:
- Cuando el experto confirma la cita (`ConfirmAppointmentAsync`)

**Duración**: 3 horas desde la hora de la cita confirmada

**Qué sucede cuando expira** (`ProcessAppointmentToAwaitingReportAsync`):

1. **Validaciones**:
   - ✅ Verifica que Appointment y SearchHire existan
   - ✅ Verifica que usuarios no estén bloqueados
   - ✅ Verifica que la cita esté confirmada

2. **Cambios de Estado**:
   - **AppointmentStatus**: → `appointment_awaiting_report`
   - **SearchHireStatus**: No cambia (permanece en "pending")

3. **Crea nuevo timer**:
   - Crea timer "expert_report" de 24 horas
   - Programa job de Hangfire para cuando expire

4. **Notificaciones**:
   - ✅ **Experto**: "Debes enviar el reporte de la cita" (con `notifyUser: true`)
   - Mensaje: "Han pasado 3 horas desde la cita. Tienes 24 horas para enviar el reporte..."

5. **Limpieza**:
   - Marca el timer de transición como expirado

**Ubicación en código**: `Services/AppointmentService.cs` líneas ~4140-4253

---

## 📊 Tabla Resumen de Eventos

| Timer Type | Duración | AppointmentStatus → | SearchHireStatus → | Cliente % | Experto % | Plataforma % | Notificaciones |
|------------|----------|---------------------|-------------------|-----------|-----------|--------------|----------------|
| **proposal** | 24h | `appointment_cancelled_by_no_response` | `cancelled_by_client_no_proposal` (requerido) | 0% | 100% | 0% | ❌ No (en ProcessAppointmentTimerAsync) |
| **response** | 24h | `appointment_cancelled_by_no_response` | `cancelled_by_expert_no_response` (requerido) | 100% | 0% | 0% | ❌ No (en ProcessAppointmentTimerAsync) |
| **expert_report** | 24h | `appointment_cancelled_by_no_report` | `cancelled_by_expert_no_report` | 95% | 0% | 5% | ❌ No (en ProcessAppointmentTimerAsync) |
| **client_decision** | 24h | `appointment_report_sent` (no cambia) | `completed_without_client_approval` | 0% | 100% | 0% | ✅ Experto: "Servicio completado..." |
| **awaiting_report_transition** | 3h | `appointment_awaiting_report` | No cambia | - | - | - | ✅ Experto: "Debes enviar el reporte..." |

---

## 🔄 Flujo Completo de un Appointment

### Escenario 1: Flujo Exitoso

1. **Cliente crea SearchHire** → Timer "proposal" (24h)
2. **Cliente propone cita** → Timer "proposal" se cancela, Timer "response" (24h)
3. **Experto confirma cita** → Timer "response" se cancela, Timer "awaiting_report_transition" (3h)
4. **Pasan 3 horas** → Timer "awaiting_report_transition" expira → Timer "expert_report" (24h)
5. **Experto envía reporte** → Timer "expert_report" se cancela, Timer "client_decision" (24h)
6. **Cliente aprueba/disputa** → Timer "client_decision" se cancela → Finalizado

### Escenario 2: Cliente No Propone

1. **Cliente crea SearchHire** → Timer "proposal" (24h)
2. **Timer expira** → `cancelled_by_client_no_proposal` → Cliente 0%, Experto 100%

### Escenario 3: Experto No Responde

1. **Cliente propone cita** → Timer "response" (24h)
2. **Timer expira** → `cancelled_by_expert_no_response` → Cliente 100%, Experto 0%

### Escenario 4: Experto No Envía Reporte

1. **Pasan 3 horas desde cita** → Timer "expert_report" (24h)
2. **Timer expira** → `cancelled_by_expert_no_report` → Cliente 95%, Experto 0%, Plataforma 5%

### Escenario 5: Cliente No Decide

1. **Experto envía reporte** → Timer "client_decision" (24h)
2. **Timer expira** → `completed_without_client_approval` → Cliente 0%, Experto 100%

---

## ⚠️ Diferencias Entre Métodos

### `CheckAppointmentTimersAsync()` vs `ProcessAppointmentTimerAsync()`

| Característica | CheckAppointmentTimersAsync | ProcessAppointmentTimerAsync |
|----------------|----------------------------|----------------------------|
| **Reintentos** | ❌ No | ✅ Sí (5 intentos) |
| **Notificaciones** | ✅ Sí | ❌ No (solo en client_decision) |
| **Validaciones** | ⚠️ Básicas | ✅ Exhaustivas |
| **Uso** | Legacy/Backup | Principal ✅ |

**Recomendación**: El sistema usa principalmente `ProcessAppointmentTimerAsync` con jobs programados. `CheckAppointmentTimersAsync` actúa como backup.

---

## 🚨 Manejo de Errores

### Reintentos Automáticos (ProcessAppointmentTimerAsync)

- **5 intentos** con delays: 1m, 5m, 10m, 15m, 20m
- Si todos fallan, se marca como fallido en Hangfire

### Fallbacks

- Si `ProcessMoneyDistributionAsync` falla, intenta cambiar estado manualmente
- Logs críticos para intervención manual si es necesario

---

## 📝 Notas Importantes

1. **Estados específicos**: El código requiere estados específicos (`cancelled_by_client_no_proposal`, `cancelled_by_expert_no_response`, `cancelled_by_expert_no_report`). Si no existen, lanza excepción en lugar de hacer fallback a genéricos.

2. **Validaciones exhaustivas**: `ProcessAppointmentTimerAsync` tiene muchas validaciones para evitar procesar timers en estados incorrectos.

3. **Cancelación de jobs**: Cuando se cancela/rechaza una cita, se cancelan los jobs de Hangfire programados.

4. **Doble procesamiento**: El sistema tiene protección contra doble procesamiento con validaciones de estado.

