# Flujo de Appointment - Guía para Frontend

## 📋 Lógica Actual del Backend

### 1. **Creación Automática del Appointment**

Cuando se crea una **contratación (SearchHire)**, el backend **automáticamente** crea un `Appointment` con:

- ✅ **Status**: `"awaiting_appointment"` 
- ✅ **ProposedDate**: `NULL` (se asigna cuando el cliente propone)
- ✅ **ProposedTime**: `NULL` (se asigna cuando el cliente propone)
- ✅ **Location**: `NULL` (se asigna cuando el cliente propone)
- ✅ **Timer de 24 horas**: Se crea automáticamente para que el cliente proponga la cita

**Código relevante:**
- `SearchHireController.cs` (líneas 226-256)
- `SubscriptionController.cs` (líneas 3563-3590)

### 2. **Proponer la Cita (Cliente)**

El cliente debe usar el endpoint `POST /api/appointments/propose/{searchHireId}` para asignar:
- `ProposedDate` (fecha en hora local del experto)
- `ProposedTime` (hora en hora local del experto)
- `Location` (dirección de la cita)
- `Latitude` / `Longitude` (opcional)
- `DoorNumber`, `OwnerPhone`, `SiteDetails` (opcional)
- `Timezone` (opcional, si no se envía usa el del experto)

**Esto cambia el status a**: `"appointment_proposed"`

### 3. **Confirmar/Rechazar (Experto)**

El experto puede:
- **Confirmar**: `POST /api/appointments/confirm` → Status: `"appointment_confirmed"`
- **Rechazar**: `POST /api/appointments/reject` → Status: `"appointment_rejected"` o vuelve a `"awaiting_appointment"`

---

## 🔄 Cambios Necesarios en el Frontend

### ❌ **NO HACER** (Ya no es necesario)

1. **NO crear el Appointment manualmente** - Ya se crea automáticamente al crear la contratación
2. **NO usar el endpoint `POST /api/appointments/create`** - Este endpoint todavía existe pero ya no es necesario en el flujo normal

### ✅ **SÍ HACER** (Nuevo flujo)

#### 1. **Obtener el Appointment después de crear la contratación**

```typescript
// Después de crear la contratación (SearchHire)
const appointment = await fetch(`/api/appointments/by-search-hire/${searchHireId}`)
  .then(res => res.json());

// El appointment ya existe con status "awaiting_appointment"
// pero ProposedDate, ProposedTime y Location son NULL
```

#### 2. **Verificar si necesita proponer la cita**

```typescript
// Verificar si el cliente necesita proponer la cita
const needsProposal = 
  appointment.status === "awaiting_appointment" && 
  (!appointment.proposedDate || !appointment.proposedTime || !appointment.location);
```

#### 3. **Mostrar formulario de propuesta si es necesario**

Si `needsProposal === true`, mostrar un formulario con:
- Campo de fecha (`ProposedDate`)
- Campo de hora (`ProposedTime`)
- Campo de ubicación (`Location`)
- Campos opcionales: `Latitude`, `Longitude`, `DoorNumber`, `OwnerPhone`, `SiteDetails`
- Campo opcional: `Timezone` (si no se envía, el backend usa el del experto)

#### 4. **Enviar la propuesta**

```typescript
// Proponer la cita
const response = await fetch(`/api/appointments/propose/${searchHireId}`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    proposedDate: "2025-01-20", // Fecha en hora LOCAL del experto
    proposedTime: "14:30:00",   // Hora en hora LOCAL del experto
    location: "Calle Principal 123, Madrid",
    latitude: 40.4168,          // Opcional
    longitude: -3.7038,         // Opcional
    doorNumber: "2º B",         // Opcional
    ownerPhone: "+34612345678", // Opcional
    siteDetails: "Entrada por la puerta trasera", // Opcional
    timezone: "Europe/Madrid"   // Opcional (si no se envía, usa el del experto)
  })
});

const updatedAppointment = await response.json();
// Ahora el status es "appointment_proposed"
```

#### 5. **Manejar estados del Appointment**

```typescript
// Estados posibles:
switch (appointment.status) {
  case "awaiting_appointment":
    // Mostrar formulario para proponer cita (si no tiene fecha/hora)
    if (!appointment.proposedDate || !appointment.proposedTime) {
      showProposalForm();
    }
    break;
    
  case "appointment_proposed":
    // Mostrar información de la cita propuesta
    // El experto puede confirmar o rechazar
    showProposedAppointmentInfo();
    break;
    
  case "appointment_confirmed":
    // Cita confirmada, mostrar detalles
    showConfirmedAppointmentInfo();
    break;
    
  case "appointment_rejected":
    // Cita rechazada, el cliente puede proponer otra
    showRejectionMessage();
    // Opcionalmente, permitir proponer otra cita
    break;
}
```

---

## 📊 Estructura del AppointmentDto

```typescript
interface AppointmentDto {
  id: number;
  searchHireId: number;
  status: string; // "awaiting_appointment", "appointment_proposed", etc.
  
  // ✅ Campos nullable - pueden ser NULL al crear la contratación
  proposedDate?: string | null;        // Fecha en UTC (guardada en BD)
  proposedTime?: string | null;        // Hora en UTC (guardada en BD)
  location?: string | null;            // Dirección de la cita
  
  // ✅ Campos convertidos a hora local (para mostrar en frontend)
  proposedDateLocal?: string | null;  // Fecha en hora local del experto
  proposedTimeLocal?: string | null;   // Hora en hora local del experto
  timezone?: string | null;            // Timezone usado (ej: "Europe/Madrid")
  country?: string | null;             // País del experto (ej: "ES")
  
  // Campos opcionales adicionales
  latitude?: number | null;
  longitude?: number | null;
  doorNumber?: string | null;
  ownerPhone?: string | null;
  siteDetails?: string | null;
  
  // Información adicional
  clientName?: string;
  expertName?: string;
  amount: number;
  timers: AppointmentTimerDto[];
  // ... otros campos
}
```

---

## 🎯 Resumen del Flujo Completo

```
1. Cliente crea contratación (SearchHire)
   ↓
2. Backend crea automáticamente Appointment con:
   - Status: "awaiting_appointment"
   - ProposedDate: NULL
   - ProposedTime: NULL
   - Location: NULL
   - Timer de 24 horas
   ↓
3. Frontend obtiene el Appointment
   ↓
4. Frontend verifica: ¿Tiene ProposedDate/ProposedTime?
   - NO → Mostrar formulario para proponer cita
   - SÍ → Mostrar información de la cita propuesta
   ↓
5. Cliente completa formulario y envía propuesta
   POST /api/appointments/propose/{searchHireId}
   ↓
6. Backend actualiza Appointment:
   - ProposedDate: asignado
   - ProposedTime: asignado
   - Location: asignado
   - Status: "appointment_proposed"
   ↓
7. Experto recibe notificación
   ↓
8. Experto confirma o rechaza
   - Confirmar → Status: "appointment_confirmed"
   - Rechazar → Status: "appointment_rejected" o "awaiting_appointment"
```

---

## ⚠️ Puntos Importantes

1. **El Appointment se crea automáticamente** - No necesitas llamar a `POST /api/appointments/create`
2. **Los campos son nullable** - `ProposedDate`, `ProposedTime` y `Location` pueden ser `null` inicialmente
3. **Usar el endpoint correcto** - Usa `POST /api/appointments/propose/{searchHireId}` para proponer la cita
4. **Fechas en hora local** - El frontend envía fechas en hora local del experto, el backend las convierte a UTC
5. **Timer de 24 horas** - El cliente tiene 24 horas para proponer la cita desde que se crea la contratación

---

## 🔍 Endpoints Relevantes

- `GET /api/appointments/by-search-hire/{searchHireId}` - Obtener appointment por SearchHireId
- `POST /api/appointments/propose/{searchHireId}` - Proponer cita (Cliente)
- `POST /api/appointments/confirm` - Confirmar cita (Experto)
- `POST /api/appointments/reject` - Rechazar cita (Experto)
- `GET /api/appointments/my-appointments` - Obtener todas las citas del usuario
