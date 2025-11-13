# 📋 VALIDACIONES DE ESTADO EN ACCIONES DE CONTRATACIÓN/CITA

Este documento detalla **todas las acciones** disponibles en el sistema de contrataciones/citas y **qué validaciones de estado se realizan** en cada una.

---

## 🎯 ACCIONES DE CITAS (APPOINTMENTS)

### 1. **CREAR CITA** (`CreateAppointmentAsync`)
**Método**: `AppointmentService.CreateAppointmentAsync`  
**Líneas**: 245-475

#### ✅ Validaciones de Estado:

1. **Protección contra race conditions**
   - ✅ Usa `Database.CreateExecutionStrategy()` para manejar reintentos
   - ✅ Transacción con `BeginTransactionAsync()`
   - ✅ Bloqueo de fila con `FOR UPDATE` en el SearchHire para evitar creación simultánea

2. **SearchHire NO finalizado**
   ```csharp
   if (searchHire.Status?.IsFinalizationStatus == true)
   ```
   - ❌ **BLOQUEA** si el SearchHire está en estado de finalización
   - Estados de finalización: `completed`, `cancelled`, `disputed`, `dispute-resolved-client`, `dispute-resolved-expert`, `transfer_failed`

3. **SearchHire existe y no tiene cita**
   - Verifica que el `SearchHire` exista
   - ✅ **Con bloqueo activo**: Verifica que no tenga ya una cita asociada (evita race conditions)

4. **Estado de la cita**
   - Crea la cita en estado `"awaiting_appointment"`

5. **Validaciones de negocio**:
   - ✅ **Anticipación mínima**: La cita debe tener al menos **24 horas** de anticipación
   - ✅ **Ubicación**: Verifica que la ubicación propuesta esté dentro del rango del experto
   - ✅ **Disponibilidad**: Verifica que la fecha/hora propuesta esté dentro del horario de disponibilidad del experto

---

### 2. **PROPONER CITA** (`ProposeAppointmentAsync`)
**Método**: `AppointmentService.ProposeAppointmentAsync`  
**Líneas**: 407-878  
**Usuario**: Cliente

#### ✅ Validaciones de Estado:

1. **SearchHire NO finalizado**
   ```csharp
   if (searchHire.Status?.IsFinalizationStatus == true)
   ```
   - ❌ **BLOQUEA** si el SearchHire está en estado de finalización
   - Estados de finalización: `completed`, `cancelled`, `disputed`, `dispute-resolved-client`, `dispute-resolved-expert`, `transfer_failed`

2. **Estado actual de la cita**
   - ✅ **Estados válidos para proponer**:
     - `"awaiting_appointment"` - No hay propuesta aún
     - `"appointment_rejected"` - Cita rechazada previamente
     - `"appointment_cancelled_by_client"` - Primera cancelación del cliente
     - `"appointment_cancelled_by_expert"` - Primera cancelación del experto
   - ❌ **NO permite** si está en: `appointment_proposed`, `appointment_confirmed`, `appointment_report_sent`, etc.

3. **Autorización**
   - Verifica que el usuario sea el **cliente** (`searchHire.ClientId == userId`)

4. **Validaciones de negocio**:
   - ✅ **Anticipación mínima**: Al menos **24 horas** de anticipación
   - ✅ **Ubicación**: Dentro del rango del experto
   - ✅ **Disponibilidad**: Dentro del horario del experto

5. **Stripe** (REMOVIDO)
   - ⚠️ **NO se valida** el estado de Stripe del experto (permite continuar el flujo incluso si cambia a `Deauthorized`)

---

### 3. **CONFIRMAR CITA** (`ConfirmAppointmentAsync`)
**Método**: `AppointmentService.ConfirmAppointmentAsync`  
**Líneas**: 879-1213  
**Usuario**: Experto

#### ✅ Validaciones de Estado:

1. **SearchHire NO finalizado**
   ```csharp
   if (appointment.SearchHire.Status?.IsFinalizationStatus == true)
   ```
   - ❌ **BLOQUEA** si el SearchHire está finalizado

2. **Estado actual de la cita**
   - ✅ **Estado requerido**: `"appointment_proposed"` (cita propuesta por el cliente)
   - ❌ **NO permite** si está en:
     - `"appointment_confirmed"` - Ya confirmada
     - `"appointment_rejected"` - Ya rechazada
     - `"appointment_cancelled_by_expert_rejection"` - Cancelada por rechazo
     - `"appointment_cancelled_by_client"` - Cancelada por cliente
     - `"appointment_cancelled_by_client_second"` - Segunda cancelación del cliente
     - `"appointment_cancelled_by_expert"` - Cancelada por experto
     - `"appointment_cancelled_by_expert_second"` - Segunda cancelación del experto

3. **Autorización**
   - Verifica que el usuario sea el **experto** (`searchHire.ExpertId == userId`)

4. **Protección contra doble procesamiento**
   - Verifica que no se haya procesado ya (evita doble click)

---

### 4. **RECHAZAR CITA** (`RejectAppointmentAsync`)
**Método**: `AppointmentService.RejectAppointmentAsync`  
**Líneas**: 1214-1857  
**Usuario**: Experto

#### ✅ Validaciones de Estado:

1. **SearchHire NO finalizado**
   ```csharp
   if (appointment.SearchHire.Status?.IsFinalizationStatus == true)
   ```
   - ❌ **BLOQUEA** si el SearchHire está finalizado

2. **Estado actual de la cita**
   - ✅ **Estado requerido**: `"appointment_proposed"` (cita propuesta por el cliente)
   - ❌ **NO permite** si está en:
     - `"appointment_rejected"` - Ya rechazada
     - `"appointment_cancelled_by_expert_rejection"` - Cancelada por rechazo
     - `"appointment_cancelled_by_client"` - Cancelada por cliente
     - `"appointment_cancelled_by_client_second"` - Segunda cancelación del cliente
     - `"appointment_cancelled_by_expert"` - Cancelada por experto
     - `"appointment_cancelled_by_expert_second"` - Segunda cancelación del experto
     - `"appointment_confirmed"` - Ya confirmada

3. **Autorización**
   - Verifica que el usuario sea el **experto** (`searchHire.ExpertId == userId`)

4. **Lógica de rechazos múltiples**:
   - Si es el **primer rechazo** → Estado: `"appointment_rejected"`
   - Si es el **segundo rechazo o más** → Estado: `"appointment_cancelled_by_expert_rejection"`

---

### 5. **CANCELAR CITA** (`CancelAppointmentAsync`)
**Método**: `AppointmentService.CancelAppointmentAsync`  
**Líneas**: 1858-2512  
**Usuario**: Cliente o Experto

#### ✅ Validaciones de Estado:

1. **SearchHire NO finalizado**
   ```csharp
   if (appointment.SearchHire.Status?.IsFinalizationStatus == true)
   ```
   - ❌ **BLOQUEA** si el SearchHire está finalizado

2. **Estado actual de la cita**
   - ❌ **NO permite** si está en `"awaiting_appointment"` (no hay propuesta aún)
   - ❌ **NO permite** si está en estados finales:
     - `"appointment_cancelled_by_client"` - Ya cancelada por cliente
     - `"appointment_cancelled_by_client_second"` - Segunda cancelación del cliente
     - `"appointment_cancelled_by_expert"` - Ya cancelada por experto
     - `"appointment_cancelled_by_expert_second"` - Segunda cancelación del experto
     - `"appointment_cancelled_by_expert_rejection"` - Cancelada por rechazo
     - `"appointment_cancelled_by_no_response"` - Cancelada por falta de respuesta
     - `"appointment_report_sent"` - Reporte ya enviado
   - ✅ **Estados válidos para cancelar**:
     - `"appointment_proposed"` - Cita propuesta
     - `"appointment_confirmed"` - Cita confirmada
     - `"appointment_rejected"` - Cita rechazada (para reprogramar)

3. **Anticipación mínima**
   - ✅ **NO permite cancelar** si quedan menos de **12 horas** antes de la cita
   - Solo aplica para citas en estado `"appointment_proposed"` o `"appointment_confirmed"`
   - ❌ **NO permite** si la cita ya pasó

4. **Autorización**
   - Verifica que el usuario sea el **cliente** o el **experto**

5. **Lógica de cancelaciones múltiples**:
   - **Cliente cancela**:
     - Primera cancelación → `"appointment_cancelled_by_client"`
     - Segunda cancelación → `"appointment_cancelled_by_client_second"`
   - **Experto cancela**:
     - Primera cancelación → `"appointment_cancelled_by_expert"`
     - Segunda cancelación → `"appointment_cancelled_by_expert_second"`

---

### 6. **ENVIAR REPORTE DEL EXPERTO** (`SubmitExpertReportAsync`)
**Método**: `AppointmentService.SubmitExpertReportAsync`  
**Líneas**: 3741-4183  
**Usuario**: Experto

#### ✅ Validaciones de Estado:

1. **SearchHire NO finalizado**
   ```csharp
   if (appointment.SearchHire.Status?.IsFinalizationStatus == true)
   ```
   - ❌ **BLOQUEA** si el SearchHire está finalizado

2. **Estado actual de la cita**
   - ✅ **Estado requerido**: `"appointment_awaiting_report"` (esperando reporte del experto)
   - ❌ **NO permite** si está en:
     - `"appointment_report_sent"` - Reporte ya enviado
     - `"appointment_cancelled_by_client"` - Cancelada por cliente
     - `"appointment_cancelled_by_client_second"` - Segunda cancelación del cliente
     - `"appointment_cancelled_by_expert"` - Cancelada por experto
     - `"appointment_cancelled_by_expert_second"` - Segunda cancelación del experto
     - `"appointment_cancelled_by_expert_rejection"` - Cancelada por rechazo
     - `"appointment_cancelled_by_no_response"` - Cancelada por falta de respuesta

3. **Autorización**
   - Verifica que el usuario sea el **experto** (`searchHire.ExpertId == expertId`)

4. **Validación de entregables**:
   - ✅ Verifica que se hayan subido todos los archivos obligatorios (PDF, video si está configurado, etc.)

---

### 7. **PROCESAR TRANSICIÓN A AWAITING_REPORT** (`ProcessAppointmentToAwaitingReportAsync`)
**Método**: `AppointmentService.ProcessAppointmentToAwaitingReportAsync`  
**Líneas**: 3630-3740  
**Automático**: Se ejecuta 3 horas después de la cita confirmada

#### ✅ Validaciones de Estado:

1. **Cita existe y está confirmada**
   - Verifica que la cita exista
   - Verifica que esté en estado `"appointment_confirmed"`

2. **SearchHire existe**
   - Verifica que el SearchHire no haya sido eliminado

3. **SearchHire NO finalizado**
   ```csharp
   if (searchHire.Status?.IsFinalizationStatus == true)
   ```
   - ❌ **NO procesa** si el SearchHire está finalizado

4. **Estado del SearchHire**
   - ✅ **Estados válidos**:
     - `"pending"` - Pendiente
     - `"awaiting_client_decision"` - Esperando decisión del cliente
   - ❌ **NO procesa** si está en otros estados

5. **Usuarios válidos**
   - Verifica que el cliente exista y no esté bloqueado
   - Verifica que el experto exista y no esté bloqueado (si tiene experto asignado)

---

### 8. **PROCESAR TIMER DE CITA** (`ProcessAppointmentTimerAsync`)
**Método**: `AppointmentService.ProcessAppointmentTimerAsync`  
**Líneas**: 3372-3629  
**Automático**: Se ejecuta cuando expira un timer

#### ✅ Validaciones de Estado:

1. **Timer existe y no está expirado**
   - Verifica que el timer exista
   - Verifica que no esté ya expirado

2. **SearchHire y Appointment existen**
   - Verifica que ambos existan

3. **SearchHire NO finalizado**
   ```csharp
   if (searchHire.Status?.IsFinalizationStatus == true)
   ```
   - ❌ **NO procesa** si el SearchHire está finalizado

4. **Usuarios válidos**
   - Verifica que el cliente exista y no esté bloqueado
   - Verifica que el experto exista y no esté bloqueado

5. **Estado del SearchHire según tipo de timer**
   - Valida el estado según el tipo de timer (proposal, response, etc.)

6. **Estado de la cita**
   - Verifica el estado de la cita antes de procesar

---

## 🎯 ACCIONES DE CONTRATACIÓN (SEARCHHIRE)

### 1. **CAMBIAR ESTADO DE CONTRATACIÓN** (`UpdateStatusAsync`)
**Método**: `SearchHireService.UpdateStatusAsync`  
**Líneas**: 75-92

#### ✅ Validaciones de Estado:

1. **SearchHire existe**
   - Verifica que el SearchHire exista
   - Verifica permisos del usuario

2. **Validación de entregables (si se completa)**
   - Si el estado es `"completed"`:
     - ✅ Verifica que se hayan subido todos los archivos obligatorios
     - ✅ Verifica PDF obligatorio
     - ✅ Verifica video si está configurado

---

## 📊 RESUMEN DE VALIDACIONES COMUNES

### ✅ **Validación Crítica en TODAS las acciones de citas**:
```csharp
if (searchHire.Status?.IsFinalizationStatus == true)
```
- **BLOQUEA** cualquier acción si el SearchHire está en estado de finalización
- **Estados de finalización**:
  - `completed` - Completado
  - `cancelled` - Cancelado
  - `disputed` - En disputa
  - `dispute-resolved-client` - Disputa resuelta a favor del cliente
  - `dispute-resolved-expert` - Disputa resuelta a favor del experto
  - `transfer_failed` - Transferencia fallida

### ✅ **Validaciones de Autorización**:
- **Cliente**: Solo puede proponer y cancelar citas
- **Experto**: Solo puede confirmar, rechazar, cancelar y enviar reportes

### ✅ **Validaciones de Anticipación**:
- **Crear/Proponer cita**: Mínimo **24 horas** de anticipación
- **Cancelar cita**: Mínimo **12 horas** de anticipación (no se puede cancelar con menos tiempo)

### ✅ **Validaciones de Ubicación y Disponibilidad**:
- Ubicación dentro del rango del experto
- Fecha/hora dentro del horario de disponibilidad del experto

### ✅ **Protección contra doble procesamiento y race conditions**:
- **TODAS las acciones críticas** usan:
  - `Database.CreateExecutionStrategy()` para manejar reintentos automáticos
  - Transacciones con `BeginTransactionAsync()`
  - Bloqueo de fila con `FOR UPDATE` para evitar race conditions
- Validación de estados inválidos para evitar doble procesamiento
- **Acciones protegidas**: Crear, Proponer, Confirmar, Rechazar, Cancelar, Enviar reporte

---

## 🔄 FLUJO DE ESTADOS DE CITA

```
awaiting_appointment
    ↓ (Cliente propone)
appointment_proposed
    ↓ (Experto confirma)          ↓ (Experto rechaza)
appointment_confirmed         appointment_rejected
    ↓ (3 horas después)              ↓ (Cliente puede reproponer)
appointment_awaiting_report    awaiting_appointment
    ↓ (Experto envía reporte)
appointment_report_sent
```

**Estados de cancelación**:
- `appointment_cancelled_by_client` / `appointment_cancelled_by_client_second`
- `appointment_cancelled_by_expert` / `appointment_cancelled_by_expert_second`
- `appointment_cancelled_by_expert_rejection` (2+ rechazos)
- `appointment_cancelled_by_no_response` (timeout)

---

## 📝 NOTAS IMPORTANTES

1. **Stripe**: La validación de Stripe se **removió** de "Proponer cita" para permitir continuar el flujo incluso si el experto cambia a `Deauthorized` después de crear la contratación.

2. **Estados finales**: Una vez que una cita está en un estado final (cancelada, reporte enviado, etc.), **NO se pueden realizar más acciones** sobre ella.

3. **Transacciones**: **TODAS las acciones críticas** (incluyendo `CreateAppointmentAsync` después de la corrección) usan:
   - Estrategia de ejecución con reintentos (`NpgsqlRetryingExecutionStrategy`)
   - Transacciones con `BeginTransactionAsync()`
   - Bloqueo de fila (`FOR UPDATE`) para evitar condiciones de carrera
   - Rollback automático en caso de error

4. **Timers**: Los timers automáticos también validan el estado antes de procesar transiciones automáticas.

