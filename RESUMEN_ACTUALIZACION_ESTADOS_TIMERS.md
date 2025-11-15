# Resumen: Actualización de Estados en Timers de Appointments

## ✅ **PROBLEMA IDENTIFICADO**

Cuando los timers de appointments expiran, se actualizaba el estado del `Appointment` pero **NO** se actualizaba el estado del `SearchHire`, dejando la contratación en un estado inconsistente.

---

## 🔧 **SOLUCIONES IMPLEMENTADAS**

### **1. Timer "proposal" (Cliente no propone en 24h)**

**Ubicación**: `AppointmentService.cs` línea 3618-3657

**Estado Appointment**: `appointment_cancelled_by_no_response` (Id: 16)
**Estado SearchHire**: `cancelled_by_no_response` (Id: 25) ✅

**Cambio**:
- ✅ Agregada actualización del `SearchHire.StatusId` a `cancelled_by_no_response`
- ✅ Actualiza `SearchHire.UpdatedAt`

---

### **2. Timer "response" (Experto no responde en 24h)**

**Ubicación**: `AppointmentService.cs` línea 3659-3700

**Estado Appointment**: `appointment_cancelled_by_no_response` (Id: 16)
**Estado SearchHire**: `cancelled_by_no_response` (Id: 25) ✅

**Cambio**:
- ✅ Agregada actualización del `SearchHire.StatusId` a `cancelled_by_no_response`
- ✅ Actualiza `SearchHire.UpdatedAt`

---

### **3. Timer "expert_report" (Experto no envía reporte en 24h)**

**Ubicación**: `AppointmentService.cs` línea 3700-3739

**Estado Appointment**: `appointment_cancelled_by_no_report` (Id: 20)
**Estado SearchHire**: `cancelled` (Id: 5) ✅

**Cambio**:
- ✅ Agregada actualización del `SearchHire.StatusId` a `cancelled`
- ✅ Actualiza `SearchHire.UpdatedAt`

**Nota**: El mapeo en la BD apunta a "cancelled" genérico, no hay estado específico para "no report".

---

### **4. Timer "client_decision" (Cliente no decide en 24h)**

**Ubicación**: `AppointmentService.cs` línea 3741-3762

**Estado Appointment**: No cambia (permanece en `appointment_report_sent`)
**Estado SearchHire**: `completed_without_client_approval` (Id: 27) ✅

**Cambio**:
- ✅ Agregada actualización del `SearchHire.StatusId` a `completed_without_client_approval`
- ✅ Actualiza `SearchHire.UpdatedAt`
- ✅ Cambiado `updateState: false` porque ya actualizamos el estado manualmente

---

## 📊 **MAPEOS EN LA BASE DE DATOS**

| AppointmentStatus (Source) | SearchHireStatus (Target) | Estado en BD |
|----------------------------|---------------------------|--------------|
| `appointment_cancelled_by_no_response` (Id: 16) | `cancelled` (Id: 5) | ⚠️ Mapea a genérico, pero código usa específico `cancelled_by_no_response` (Id: 25) |
| `appointment_cancelled_by_client_second` (Id: 14) | `cancelled` (Id: 5) | ✅ Correcto |
| `appointment_cancelled_by_expert_second` (Id: 22) | `cancelled` (Id: 5) | ✅ Correcto |
| `appointment_cancelled_by_expert_rejection` (Id: 17) | `cancelled` (Id: 5) | ✅ Correcto |
| `appointment_report_sent` (Id: 23) | `awaiting_client_decision` (Id: 2) | ✅ Correcto |

**Nota**: No hay mapeo para `appointment_cancelled_by_no_report` ni para `completed_without_client_approval` porque estos son estados directos de SearchHire, no AppointmentStatus.

---

## ✅ **VERIFICACIÓN**

Todos los casos de timers ahora actualizan correctamente el estado del `SearchHire`:

1. ✅ **"proposal"** → `cancelled_by_no_response`
2. ✅ **"response"** → `cancelled_by_no_response`
3. ✅ **"expert_report"** → `cancelled`
4. ✅ **"client_decision"** → `completed_without_client_approval`

---

## 🔍 **RECOMENDACIÓN OPCIONAL**

Actualizar el mapeo en la BD para que `appointment_cancelled_by_no_response` apunte a `cancelled_by_no_response` en lugar de `cancelled` genérico:

```sql
UPDATE "StatusMappings" 
SET "TargetStatusId" = 25 
WHERE "SourceStatusId" = 16 AND "Id" = 5;
```

Esto haría que el mapeo sea consistente con lo que el código hace directamente.

---

## ✅ **RESULTADO**

Ahora todos los eventos de timers actualizan correctamente tanto el estado del `Appointment` como el estado del `SearchHire`, manteniendo la consistencia de datos.

