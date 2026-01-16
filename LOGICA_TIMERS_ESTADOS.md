# LÓGICA DE TIMERS POR ESTADO DE APPOINTMENT

## Estados y Timers Correctos

### 1. **awaiting_appointment** (Esperando propuesta del cliente)
- ✅ Timer "proposal" ACTIVO (24h para que el cliente proponga)
- ❌ NO debe haber timer "response"
- Si expira → `appointment_cancelled_by_client_no_proposal`

### 2. **appointment_proposed** (Cliente propuso, esperando respuesta del experto)
- ❌ NO debe haber timer "proposal" (ya se propuso)
- ✅ Timer "response" ACTIVO (24h para que el experto responda)
- Si expira → `appointment_cancelled_by_expert_no_response`

### 3. **appointment_rejected** (Experto rechazó, cliente puede proponer otra vez)
- ✅ Timer "proposal" ACTIVO (24h para nueva propuesta)
- ❌ NO debe haber timer "response"

### 4. **appointment_confirmed** (Cita confirmada)
- ❌ NO debe haber timers activos (se cancelan todos)

## PROBLEMA DETECTADO

**Appointment 23 - Estado: appointment_proposed**
- ❌ Timer 40: "proposal" con HangfireJobId "151" - NO debería estar activo
- ❌ Timer 41: "response" sin HangfireJobId - Debería tener HangfireJobId

**Causa raíz:**
1. El timer "proposal" no se está marcando como expirado correctamente en `ProposeAppointmentAsync`
2. El timer "response" no se está guardando con el HangfireJobId correctamente
