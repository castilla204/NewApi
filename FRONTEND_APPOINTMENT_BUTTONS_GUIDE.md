# 📋 GUÍA FRONTEND - BOTONES DE CITAS SEGÚN ESTADOS

Este documento explica **cuándo mostrar cada botón** de acciones de citas según el `statusValue` del `Appointment`.

---

## 🎯 ESTADOS DE CITAS (AppointmentStatus)

### Estados Principales:
- `awaiting_appointment` - Esperando propuesta del cliente
- `appointment_proposed` - Cliente propuso cita
- `appointment_confirmed` - Experto confirmó
- `appointment_rejected` - Experto rechazó (primera vez)
- `appointment_cancelled_by_client` - Primera cancelación del cliente
- `appointment_cancelled_by_client_second` - Segunda cancelación del cliente
- `appointment_cancelled_by_expert` - Primera cancelación del experto
- `appointment_cancelled_by_expert_second` - Segunda cancelación del experto
- `appointment_cancelled_by_expert_rejection` - Experto rechazó 2 veces
- `appointment_cancelled_by_client_no_proposal` - Cliente no propuso en 24h
- `appointment_cancelled_by_expert_no_response` - Experto no respondió en 24h
- `appointment_awaiting_report` - Esperando reporte del experto
- `appointment_cancelled_by_no_report` - Experto no envió reporte
- `appointment_report_sent` - Experto envió reporte
- `appointment_completed_without_client_approval` - Cliente no decidió en 24h

---

## 👤 BOTONES PARA EL CLIENTE

### ✅ **BOTÓN: "Proponer Cita"**
**Mostrar cuando:**
```typescript
const canPropose = [
  'awaiting_appointment',
  'appointment_rejected',
  'appointment_cancelled_by_client',      // Primera cancelación
  'appointment_cancelled_by_expert'       // Primera cancelación del experto
].includes(appointment.status);
```

**NO mostrar cuando:**
```typescript
const cannotPropose = [
  'appointment_proposed',                  // Ya propuso
  'appointment_confirmed',                // Ya confirmada
  'appointment_cancelled_by_client_second', // Segunda cancelación
  'appointment_cancelled_by_expert_second', // Segunda cancelación
  'appointment_cancelled_by_expert_rejection',
  'appointment_cancelled_by_client_no_proposal',
  'appointment_cancelled_by_expert_no_response',
  'appointment_awaiting_report',
  'appointment_cancelled_by_no_report',
  'appointment_report_sent',
  'appointment_completed_without_client_approval'
].includes(appointment.status);
```

**Validaciones adicionales:**
- ✅ El `SearchHire` NO debe estar finalizado (`isFinalizationStatus === false`)
- ✅ La cita debe tener al menos 24 horas de anticipación
- ✅ La ubicación debe estar dentro del rango del experto
- ✅ La fecha/hora debe estar dentro del horario de disponibilidad del experto

---

### ❌ **BOTÓN: "Cancelar Cita"**
**Mostrar cuando:**
```typescript
const canCancel = [
  'appointment_confirmed'  // Solo cuando está confirmada
].includes(appointment.status);
```

**NO mostrar cuando:**
```typescript
const cannotCancel = [
  'awaiting_appointment',                  // No hay propuesta aún
  'appointment_proposed',                  // El experto puede rechazar/aprobar
  'appointment_rejected',                  // Puede proponer nueva cita
  'appointment_cancelled_by_client',       // Ya cancelada
  'appointment_cancelled_by_client_second',
  'appointment_cancelled_by_expert',
  'appointment_cancelled_by_expert_second',
  'appointment_cancelled_by_expert_rejection',
  'appointment_cancelled_by_client_no_proposal',
  'appointment_cancelled_by_expert_no_response',
  'appointment_awaiting_report',
  'appointment_cancelled_by_no_report',
  'appointment_report_sent',
  'appointment_completed_without_client_approval'
].includes(appointment.status);
```

**Validaciones adicionales:**
- ✅ El `SearchHire` NO debe estar finalizado (`isFinalizationStatus === false`)

---

## 👨‍🔧 BOTONES PARA EL EXPERTO

### ✅ **BOTÓN: "Aceptar Cita" (Confirmar)**
**Mostrar cuando:**
```typescript
const canConfirm = [
  'appointment_proposed'  // Solo cuando el cliente propuso
].includes(appointment.status);
```

**NO mostrar cuando:**
```typescript
const cannotConfirm = [
  'awaiting_appointment',                  // No hay propuesta aún
  'appointment_confirmed',                 // Ya confirmada
  'appointment_rejected',                  // Ya rechazada
  'appointment_cancelled_by_client',
  'appointment_cancelled_by_client_second',
  'appointment_cancelled_by_expert',
  'appointment_cancelled_by_expert_second',
  'appointment_cancelled_by_expert_rejection',
  'appointment_cancelled_by_client_no_proposal',
  'appointment_cancelled_by_expert_no_response',
  'appointment_awaiting_report',
  'appointment_cancelled_by_no_report',
  'appointment_report_sent',
  'appointment_completed_without_client_approval'
].includes(appointment.status);
```

**Validaciones adicionales:**
- ✅ El `SearchHire` NO debe estar finalizado (`isFinalizationStatus === false`)

---

### ❌ **BOTÓN: "Rechazar Cita"**
**Mostrar cuando:**
```typescript
const canReject = [
  'appointment_proposed'  // Solo cuando el cliente propuso
].includes(appointment.status);
```

**NO mostrar cuando:**
```typescript
const cannotReject = [
  'awaiting_appointment',                  // No hay propuesta aún
  'appointment_confirmed',                 // Ya confirmada
  'appointment_rejected',                  // Ya rechazada
  'appointment_cancelled_by_expert_rejection', // Ya cancelada por rechazos
  'appointment_cancelled_by_client',
  'appointment_cancelled_by_client_second',
  'appointment_cancelled_by_expert',
  'appointment_cancelled_by_expert_second',
  'appointment_cancelled_by_client_no_proposal',
  'appointment_cancelled_by_expert_no_response',
  'appointment_awaiting_report',
  'appointment_cancelled_by_no_report',
  'appointment_report_sent',
  'appointment_completed_without_client_approval'
].includes(appointment.status);
```

**Validaciones adicionales:**
- ✅ El `SearchHire` NO debe estar finalizado (`isFinalizationStatus === false`)
- ⚠️ **IMPORTANTE**: Si es el segundo rechazo (`rejectionCount >= 1`), la cita se cancelará automáticamente con estado `appointment_cancelled_by_expert_rejection`

---

### ❌ **BOTÓN: "Cancelar Cita" (Experto)**
**Mostrar cuando:**
```typescript
const canCancel = [
  'appointment_confirmed'  // Solo cuando está confirmada
].includes(appointment.status);
```

**NO mostrar cuando:**
```typescript
const cannotCancel = [
  'awaiting_appointment',                  // No hay propuesta aún
  'appointment_proposed',                  // Puede rechazar/aprobar
  'appointment_rejected',                  // Ya rechazada
  'appointment_cancelled_by_client',
  'appointment_cancelled_by_client_second',
  'appointment_cancelled_by_expert',       // Ya cancelada
  'appointment_cancelled_by_expert_second',
  'appointment_cancelled_by_expert_rejection',
  'appointment_cancelled_by_client_no_proposal',
  'appointment_cancelled_by_expert_no_response',
  'appointment_awaiting_report',
  'appointment_cancelled_by_no_report',
  'appointment_report_sent',
  'appointment_completed_without_client_approval'
].includes(appointment.status);
```

**Validaciones adicionales:**
- ✅ El `SearchHire` NO debe estar finalizado (`isFinalizationStatus === false`)
- ⚠️ **IMPORTANTE**: Si es la segunda cancelación (`expertCancellationCount >= 1`), el estado será `appointment_cancelled_by_expert_second`

---

## 📊 TABLA RESUMEN DE BOTONES

| Estado | Cliente: Proponer | Cliente: Cancelar | Experto: Aceptar | Experto: Rechazar | Experto: Cancelar |
|--------|-------------------|-------------------|------------------|-------------------|-------------------|
| `awaiting_appointment` | ✅ | ❌ | ❌ | ❌ | ❌ |
| `appointment_proposed` | ❌ | ❌ | ✅ | ✅ | ❌ |
| `appointment_confirmed` | ❌ | ✅ | ❌ | ❌ | ✅ |
| `appointment_rejected` | ✅ | ❌ | ❌ | ❌ | ❌ |
| `appointment_cancelled_by_client` | ✅ | ❌ | ❌ | ❌ | ❌ |
| `appointment_cancelled_by_client_second` | ❌ | ❌ | ❌ | ❌ | ❌ |
| `appointment_cancelled_by_expert` | ✅ | ❌ | ❌ | ❌ | ❌ |
| `appointment_cancelled_by_expert_second` | ❌ | ❌ | ❌ | ❌ | ❌ |
| `appointment_cancelled_by_expert_rejection` | ❌ | ❌ | ❌ | ❌ | ❌ |
| `appointment_cancelled_by_client_no_proposal` | ❌ | ❌ | ❌ | ❌ | ❌ |
| `appointment_cancelled_by_expert_no_response` | ❌ | ❌ | ❌ | ❌ | ❌ |
| `appointment_awaiting_report` | ❌ | ❌ | ❌ | ❌ | ❌ |
| `appointment_cancelled_by_no_report` | ❌ | ❌ | ❌ | ❌ | ❌ |
| `appointment_report_sent` | ❌ | ❌ | ❌ | ❌ | ❌ |
| `appointment_completed_without_client_approval` | ❌ | ❌ | ❌ | ❌ | ❌ |

---

## 💻 EJEMPLO DE IMPLEMENTACIÓN EN TYPESCRIPT/REACT

```typescript
interface Appointment {
  id: number;
  status: string;
  rejectionCount: number;
  clientCancellationCount: number;
  expertCancellationCount: number;
  searchHire: {
    status: {
      isFinalizationStatus: boolean;
    };
  };
}

// Función helper para determinar qué botones mostrar
export function getAppointmentButtons(appointment: Appointment, userRole: 'client' | 'expert') {
  const status = appointment.status;
  const isFinalized = appointment.searchHire?.status?.isFinalizationStatus === true;
  
  // Si está finalizado, no mostrar ningún botón
  if (isFinalized) {
    return {
      showPropose: false,
      showCancel: false,
      showAccept: false,
      showReject: false
    };
  }

  if (userRole === 'client') {
    return {
      showPropose: [
        'awaiting_appointment',
        'appointment_rejected',
        'appointment_cancelled_by_client',
        'appointment_cancelled_by_expert'
      ].includes(status),
      showCancel: status === 'appointment_confirmed',
      showAccept: false,
      showReject: false
    };
  } else { // expert
    return {
      showPropose: false,
      showCancel: status === 'appointment_confirmed',
      showAccept: status === 'appointment_proposed',
      showReject: status === 'appointment_proposed'
    };
  }
}

// Uso en componente React
function AppointmentActions({ appointment, userRole }: Props) {
  const buttons = getAppointmentButtons(appointment, userRole);
  
  return (
    <div>
      {buttons.showPropose && (
        <button onClick={handlePropose}>Proponer Cita</button>
      )}
      {buttons.showCancel && (
        <button onClick={handleCancel}>Cancelar Cita</button>
      )}
      {buttons.showAccept && (
        <button onClick={handleAccept}>Aceptar Cita</button>
      )}
      {buttons.showReject && (
        <button onClick={handleReject}>Rechazar Cita</button>
      )}
    </div>
  );
}
```

---

## ⚠️ VALIDACIONES IMPORTANTES

### 1. **Verificar SearchHire NO finalizado**
```typescript
if (appointment.searchHire?.status?.isFinalizationStatus === true) {
  // NO mostrar ningún botón de acción
  return;
}
```

### 2. **Validar anticipación mínima (solo para Proponer)**
```typescript
if (action === 'propose') {
  const proposedDateTime = new Date(proposedDate + 'T' + proposedTime);
  const hoursUntilAppointment = (proposedDateTime.getTime() - Date.now()) / (1000 * 60 * 60);
  
  if (hoursUntilAppointment < 24) {
    // Mostrar error: "Las citas deben proponerse con al menos 24 horas de anticipación"
    return;
  }
}
```

### 3. **Mostrar advertencia en segundo rechazo**
```typescript
if (action === 'reject' && appointment.rejectionCount >= 1) {
  // Mostrar advertencia: "Esta es tu segunda vez rechazando. La cita se cancelará automáticamente."
}
```

### 4. **Mostrar advertencia en segunda cancelación**
```typescript
if (action === 'cancel') {
  const isSecondCancellation = userRole === 'client' 
    ? appointment.clientCancellationCount >= 1
    : appointment.expertCancellationCount >= 1;
    
  if (isSecondCancellation) {
    // Mostrar advertencia: "Esta es tu segunda cancelación. No podrás cancelar más veces."
  }
}
```

---

## 🔄 FLUJO DE ESTADOS

```
awaiting_appointment
    ↓ (Cliente propone)
appointment_proposed
    ↓ (Experto acepta)              ↓ (Experto rechaza)
appointment_confirmed          appointment_rejected
    ↓ (Cliente/Experto cancela)         ↓ (Cliente puede proponer nueva)
appointment_cancelled_by_*     awaiting_appointment
```

---

## 📝 NOTAS IMPORTANTES

1. **Estados finales**: Una vez que la cita está en un estado final (cancelada, reporte enviado, etc.), NO se pueden realizar más acciones.

2. **Segunda cancelación**: Después de la segunda cancelación, NO se puede proponer/cancelar más.

3. **Segundo rechazo**: Si el experto rechaza 2 veces, la cita se cancela automáticamente con estado `appointment_cancelled_by_expert_rejection`.

4. **Timers**: Algunos estados cambian automáticamente por timers (24h sin propuesta, 24h sin respuesta, etc.). El frontend debe refrescar el estado periódicamente.

5. **SearchHire finalizado**: Si el `SearchHire` está finalizado (`isFinalizationStatus === true`), NO se pueden realizar acciones de citas, independientemente del estado de la cita.

