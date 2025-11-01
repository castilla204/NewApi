# 📅 Guía Completa: Sistema de Disponibilidad Horaria para Expertos

## 📋 Índice
1. [Introducción](#introducción)
2. [Estructura de Datos](#estructura-de-datos)
3. [Endpoints Disponibles](#endpoints-disponibles)
4. [Flujos de Uso](#flujos-de-uso)
5. [Ejemplos de Código](#ejemplos-de-código)
6. [Casos de Uso](#casos-de-uso)

---

## 🎯 Introducción

Se ha implementado un sistema completo de gestión de disponibilidad horaria para expertos. Este sistema permite:
- ✅ Definir días de la semana en que el experto trabaja
- ✅ Establecer franjas horarias de trabajo (misma para todos los días)
- ✅ Gestionar cambios históricos (se mantiene el historial completo)
- ✅ Incluir la disponibilidad en las respuestas de perfil y contrataciones

**Importante:** La disponibilidad se define con una única franja horaria que aplica para todos los días seleccionados (ej: Lunes a Viernes de 9:00 a 18:00).

---

## 📊 Estructura de Datos

### 1. DTOs Principales

#### `CurrentExpertAvailabilityDto` (Disponibilidad Actual)
```typescript
interface CurrentExpertAvailabilityDto {
  id: number;
  daysOfWeek: string[];        // ["Monday", "Tuesday", "Wednesday", ...]
  startTime: string;            // "09:00:00" (formato TimeSpan)
  endTime: string;              // "18:00:00" (formato TimeSpan)
  effectiveFrom: string;        // "2025-01-01T00:00:00Z" (ISO DateTime)
}
```

#### `CreateOrUpdateExpertAvailabilityDto` (Para crear/actualizar)
```typescript
interface CreateOrUpdateExpertAvailabilityDto {
  daysOfWeek: string[];         // ["Monday", "Tuesday", ...] - REQUERIDO
  startTime: string;            // "09:00" (formato "HH:mm") - REQUERIDO
  endTime: string;              // "18:00" (formato "HH:mm") - REQUERIDO
}
```

#### `ExpertAvailabilityDto` (Historial completo)
```typescript
interface ExpertAvailabilityDto {
  id: number;
  expertId: number;
  daysOfWeek: string[];
  startTime: string;
  endTime: string;
  effectiveFrom: string;
  effectiveTo: string | null;   // null = disponibilidad actual activa
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}
```

#### Días de la semana válidos
```typescript
const VALID_DAYS = [
  "Monday",
  "Tuesday", 
  "Wednesday",
  "Thursday",
  "Friday",
  "Saturday",
  "Sunday"
];
```

---

## 🔌 Endpoints Disponibles

### ⚠️ IMPORTANTE: La disponibilidad NO se puede crear o actualizar de forma independiente

La disponibilidad horaria **SOLO** se puede crear o actualizar junto con el perfil de experto. No existen endpoints independientes para modificar la disponibilidad.

---

### 1. **Obtener Disponibilidad Actual** (Solo lectura)
```http
GET /api/ExpertAvailability/current
Authorization: Bearer {token}
```

**Response (200 OK):**
```json
{
  "id": 1,
  "daysOfWeek": ["Monday", "Tuesday", "Wednesday"],
  "startTime": "09:00:00",
  "endTime": "18:00:00",
  "effectiveFrom": "2025-01-15T10:30:00Z"
}
```

**Si no hay disponibilidad:**
```json
{
  "daysOfWeek": [],
  "startTime": "00:00:00",
  "endTime": "00:00:00",
  "effectiveFrom": "2025-01-15T10:30:00Z"
}
```

---

### 2. **Obtener Historial Completo** (Solo lectura)
```http
GET /api/ExpertAvailability/history
Authorization: Bearer {token}
```

**Response (200 OK):**
```json
[
  {
    "id": 2,
    "expertId": 52,
    "daysOfWeek": ["Monday", "Tuesday", "Wednesday"],
    "startTime": "09:00:00",
    "endTime": "18:00:00",
    "effectiveFrom": "2025-01-15T10:30:00Z",
    "effectiveTo": null,
    "isActive": true,
    "createdAt": "2025-01-15T10:30:00Z",
    "updatedAt": "2025-01-15T10:30:00Z"
  },
  {
    "id": 1,
    "expertId": 52,
    "daysOfWeek": ["Monday", "Friday"],
    "startTime": "10:00:00",
    "endTime": "16:00:00",
    "effectiveFrom": "2025-01-01T00:00:00Z",
    "effectiveTo": "2025-01-15T10:30:00Z",
    "isActive": false,
    "createdAt": "2025-01-01T00:00:00Z",
    "updatedAt": "2025-01-15T10:30:00Z"
  }
]
```

---

### 3. **Crear Perfil de Experto con Disponibilidad** ⭐ ÚNICA FORMA DE CREAR DISPONIBILIDAD
```http
POST /api/User/become-expert
Authorization: Bearer {token}
Content-Type: multipart/form-data
```

**Form Data:**
```javascript
const formData = new FormData();
formData.append('ProfilePicture', file);
formData.append('Description', 'Descripción del experto');
formData.append('Latitude', '42.49739255062159');
formData.append('Longitude', '-2.2609422483877717');

// Disponibilidad horaria (OPCIONAL)
formData.append('AvailabilityDaysOfWeek', 'Monday');
formData.append('AvailabilityDaysOfWeek', 'Tuesday');
formData.append('AvailabilityDaysOfWeek', 'Wednesday');
formData.append('AvailabilityStartTime', '09:00');
formData.append('AvailabilityEndTime', '18:00');
```

**✅ Ventaja:** Puedes crear el perfil y la disponibilidad en una sola petición.

---

### 4. **Actualizar Perfil de Experto con Disponibilidad** ⭐ ÚNICA FORMA DE ACTUALIZAR DISPONIBILIDAD
```http
PUT /api/User/expert-profile
Authorization: Bearer {token}
Content-Type: multipart/form-data
```

**Form Data:**
```javascript
const formData = new FormData();
formData.append('Description', 'Nueva descripción');
formData.append('Latitude', '42.49739255062159');
formData.append('Longitude', '-2.2609422483877717');

// Si quieres actualizar la disponibilidad también
// ⚠️ IMPORTANTE: Debes enviar TODOS los campos de disponibilidad juntos
formData.append('AvailabilityDaysOfWeek', 'Monday');
formData.append('AvailabilityDaysOfWeek', 'Tuesday');
formData.append('AvailabilityStartTime', '10:00');
formData.append('AvailabilityEndTime', '19:00');
```

**⚠️ Nota:** 
- Si no envías los campos de disponibilidad, no se actualizará la disponibilidad (el perfil sí se actualiza normalmente).
- Solo se actualiza la disponibilidad si envías **todos** los campos: `AvailabilityDaysOfWeek`, `AvailabilityStartTime`, `AvailabilityEndTime`.
- **NO existe un endpoint separado para actualizar solo la disponibilidad.** Debe hacerse siempre junto con el perfil.

---

### 5. **Obtener Perfil de Experto (incluye disponibilidad)** ⭐ RECOMENDADO
```http
GET /api/User/expert-profile
Authorization: Bearer {token}
```

**Response (200 OK):**
```json
{
  "id": 52,
  "profilePictureUrl": "https://storage.googleapis.com/...",
  "description": "Descripción del experto",
  "stripeAccountId": "acct_1S7K9dR92l5GeyCp",
  "createdAt": "2025-09-14T17:55:59.171923Z",
  "user": {
    "id": 0,
    "email": "a26865@svalero.com",
    "name": "Diego Castilla Abella",
    "profilePictureUrl": null
  },
  "reviews": [],
  "latitude": "42.49739255062159",
  "longitude": "-2.2609422483877717",
  "stripeStatus": 2,
  "stripeStatusDetails": "✅ **Cuenta Aprobada**: ...",
  "onboardingCompleted": true,
  "isOnVacation": false,
  "currentAvailability": {
    "id": 1,
    "daysOfWeek": ["Monday", "Tuesday", "Wednesday"],
    "startTime": "09:00:00",
    "endTime": "18:00:00",
    "effectiveFrom": "2025-01-15T10:30:00Z"
  }
}
```

**✅ Ya no necesitas llamar a `/api/ExpertAvailability/current` por separado!**

---

### 6. **Obtener Contrataciones del Experto (incluye disponibilidad)** ⭐ NUEVO
```http
GET /api/SearchHire/expert
Authorization: Bearer {token}
```

**Response (200 OK):**
```json
[
  {
    "id": 112,
    "clientId": 38,
    "expertId": 34,
    "service": {
      "id": 133,
      "categoryId": 2,
      "serviceTypeId": 1,
      "expert": {
        "id": 52,
        "profilePictureUrl": "https://...",
        "description": "...",
        "currentAvailability": {
          "id": 1,
          "daysOfWeek": ["Monday", "Tuesday", "Wednesday"],
          "startTime": "09:00:00",
          "endTime": "18:00:00",
          "effectiveFrom": "2025-01-15T10:30:00Z"
        }
      }
    },
    "status": "pending",
    ...
  }
]
```

**✅ La disponibilidad está en `service.expert.currentAvailability`**

---

## 🔄 Flujos de Uso

### Flujo 1: Crear Experto con Disponibilidad Inicial
```
1. Usuario se registra/autentica
2. POST /api/User/become-expert
   - Incluye: ProfilePicture, Description, Lat/Lng
   - Incluye: AvailabilityDaysOfWeek, AvailabilityStartTime, AvailabilityEndTime (OPCIONAL)
3. ✅ Perfil creado + Disponibilidad creada (si se proporcionó)
```

### Flujo 2: Actualizar Disponibilidad ⚠️ SOLO JUNTO CON EL PERFIL
```
1. Usuario está autenticado como experto
2. PUT /api/User/expert-profile
   - FormData incluye: Description, Lat/Lng (requeridos)
   - FormData incluye: AvailabilityDaysOfWeek, AvailabilityStartTime, AvailabilityEndTime (OPCIONAL)
3. ✅ Si se incluyen campos de disponibilidad, se actualiza la disponibilidad
4. ✅ Disponibilidad anterior marcada como inactiva
5. ✅ Nueva disponibilidad creada y activa
```

**⚠️ IMPORTANTE:** 
- La disponibilidad **NO se puede crear o actualizar por separado**.
- **NO existe** un endpoint `POST /api/ExpertAvailability/set` para actualizar solo la disponibilidad.
- La disponibilidad **SOLO** se puede actualizar junto con el perfil del experto usando `PUT /api/User/expert-profile`.
- Los endpoints `GET /api/ExpertAvailability/current` y `GET /api/ExpertAvailability/history` son **solo lectura**.

### Flujo 3: Obtener Perfil Completo (Recomendado) ⭐
```
1. Usuario está autenticado como experto
2. GET /api/User/expert-profile
3. ✅ Respuesta incluye: Perfil completo + currentAvailability
```

### Flujo 4: Obtener Contrataciones con Disponibilidad
```
1. Usuario está autenticado como experto
2. GET /api/SearchHire/expert
3. ✅ Cada contratación incluye: service.expert.currentAvailability
```

---

## 💻 Ejemplos de Código

### ⚠️ IMPORTANTE: Ya NO existe el endpoint para actualizar disponibilidad por separado

**NO uses:** `POST /api/ExpertAvailability/set` (este endpoint ha sido eliminado)

**Usa en su lugar:** `PUT /api/User/expert-profile` con los campos de disponibilidad incluidos en el FormData.

---

### Ejemplo 1: Obtener Disponibilidad Actual (Solo lectura)

```typescript
async function getCurrentAvailability(): Promise<CurrentExpertAvailabilityDto> {
  const response = await fetch('/api/ExpertAvailability/current', {
    headers: {
      'Authorization': `Bearer ${token}`
    }
  });

  if (!response.ok) {
    throw new Error('Error al obtener disponibilidad');
  }

  return await response.json();
}
```

---

### Ejemplo 2: Crear Experto con Disponibilidad (FormData)

```typescript
async function becomeExpert(
  profilePicture: File,
  description: string,
  latitude: string,
  longitude: string,
  availability?: AvailabilityFormData
): Promise<BecomeExpertResponse> {
  const formData = new FormData();
  formData.append('ProfilePicture', profilePicture);
  formData.append('Description', description);
  formData.append('Latitude', latitude);
  formData.append('Longitude', longitude);

  // Disponibilidad opcional
  if (availability) {
    availability.daysOfWeek.forEach(day => {
      formData.append('AvailabilityDaysOfWeek', day);
    });
    formData.append('AvailabilityStartTime', availability.startTime);
    formData.append('AvailabilityEndTime', availability.endTime);
  }

  const response = await fetch('/api/User/become-expert', {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${token}`
      // NO incluyas 'Content-Type', el navegador lo hará automáticamente con FormData
    },
    body: formData
  });

  if (!response.ok) {
    const error = await response.json();
    throw new Error(error.message || 'Error al crear perfil de experto');
  }

  return await response.json();
}

// Uso:
const result = await becomeExpert(
  file,
  'Experto en motos',
  '42.49739255062159',
  '-2.2609422483877717',
  {
    daysOfWeek: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'],
    startTime: '09:00',
    endTime: '18:00'
  }
);
```

---

### Ejemplo 3: Actualizar Perfil con Disponibilidad ⭐ ÚNICA FORMA DE ACTUALIZAR DISPONIBILIDAD

```typescript
async function updateExpertProfile(
  description: string,
  latitude: string,
  longitude: string,
  profilePicture?: File,
  availability?: AvailabilityFormData
): Promise<UpdateExpertProfileResponse> {
  const formData = new FormData();
  formData.append('Description', description);
  formData.append('Latitude', latitude);
  formData.append('Longitude', longitude);

  if (profilePicture) {
    formData.append('ProfilePicture', profilePicture);
  }

  // Disponibilidad opcional
  if (availability) {
    availability.daysOfWeek.forEach(day => {
      formData.append('AvailabilityDaysOfWeek', day);
    });
    formData.append('AvailabilityStartTime', availability.startTime);
    formData.append('AvailabilityEndTime', availability.endTime);
  }

  const response = await fetch('/api/User/expert-profile', {
    method: 'PUT',
    headers: {
      'Authorization': `Bearer ${token}`
    },
    body: formData
  });

  if (!response.ok) {
    const error = await response.json();
    throw new Error(error.message || 'Error al actualizar perfil');
  }

  return await response.json();
}
```

---

### Ejemplo 4: Obtener Perfil Completo (con disponibilidad incluida)

```typescript
interface ExpertProfileDto {
  id: number;
  profilePictureUrl: string;
  description: string;
  stripeAccountId: string | null;
  createdAt: string;
  user: {
    id: number;
    email: string;
    name: string;
    profilePictureUrl: string | null;
  };
  reviews: any[];
  latitude: string;
  longitude: string;
  stripeStatus: number;
  stripeStatusDetails: string | null;
  onboardingCompleted: boolean;
  isOnVacation: boolean;
  currentAvailability: CurrentExpertAvailabilityDto | null;  // ⭐ NUEVO
}

async function getExpertProfile(): Promise<ExpertProfileDto> {
  const response = await fetch('/api/User/expert-profile', {
    headers: {
      'Authorization': `Bearer ${token}`
    }
  });

  if (!response.ok) {
    throw new Error('Error al obtener perfil de experto');
  }

  return await response.json();
}

// Uso:
const profile = await getExpertProfile();
if (profile.currentAvailability) {
  console.log('Días disponibles:', profile.currentAvailability.daysOfWeek);
  console.log('Horario:', profile.currentAvailability.startTime, '-', profile.currentAvailability.endTime);
} else {
  console.log('No hay disponibilidad configurada');
}
```

---

### Ejemplo 5: Obtener Contrataciones con Disponibilidad

```typescript
interface SearchHireResponseDto {
  id: number;
  service: {
    id: number;
    expert: {
      id: number;
      currentAvailability: CurrentExpertAvailabilityDto | null;  // ⭐ NUEVO
      // ... otros campos
    };
    // ... otros campos
  };
  // ... otros campos
}

async function getExpertHires(): Promise<SearchHireResponseDto[]> {
  const response = await fetch('/api/SearchHire/expert', {
    headers: {
      'Authorization': `Bearer ${token}`
    }
  });

  if (!response.ok) {
    throw new Error('Error al obtener contrataciones');
  }

  return await response.json();
}

// Uso:
const hires = await getExpertHires();
hires.forEach(hire => {
  if (hire.service.expert?.currentAvailability) {
    console.log('Disponibilidad del experto:', hire.service.expert.currentAvailability);
  }
});
```

---

## 🎯 Casos de Uso

### Caso 1: Primera vez que un experto configura su horario
```typescript
// El experto acaba de registrarse y quiere configurar su horario
// Debe hacerse al crear el perfil de experto o actualizarlo
const formData = new FormData();
formData.append('Description', 'Descripción del experto');
formData.append('Latitude', '42.49739255062159');
formData.append('Longitude', '-2.2609422483877717');

// Incluir disponibilidad
formData.append('AvailabilityDaysOfWeek', 'Monday');
formData.append('AvailabilityDaysOfWeek', 'Tuesday');
formData.append('AvailabilityDaysOfWeek', 'Wednesday');
formData.append('AvailabilityDaysOfWeek', 'Thursday');
formData.append('AvailabilityDaysOfWeek', 'Friday');
formData.append('AvailabilityStartTime', '09:00');
formData.append('AvailabilityEndTime', '18:00');

// Si es primera vez, usar POST /api/User/become-expert
// Si ya es experto, usar PUT /api/User/expert-profile
```

### Caso 2: Experto cambia su horario (ej: ahora trabaja menos días)
```typescript
// Cambió de L-V a solo L-M-W
// ⚠️ DEBE actualizar el perfil completo
const formData = new FormData();
formData.append('Description', expertProfile.description); // Descripción actual
formData.append('Latitude', expertProfile.latitude);
formData.append('Longitude', expertProfile.longitude);

// Nueva disponibilidad
formData.append('AvailabilityDaysOfWeek', 'Monday');
formData.append('AvailabilityDaysOfWeek', 'Tuesday');
formData.append('AvailabilityDaysOfWeek', 'Wednesday');
formData.append('AvailabilityStartTime', '09:00');
formData.append('AvailabilityEndTime', '18:00');

const response = await fetch('/api/User/expert-profile', {
  method: 'PUT',
  headers: {
    'Authorization': `Bearer ${token}`
  },
  body: formData
});

// ✅ La disponibilidad anterior queda en el historial como inactiva
```

### Caso 3: Mostrar horario actual en el perfil (Recomendado usar GET /api/User/expert-profile)
```typescript
const profile = await getExpertProfile();

// En tu componente React:
{profile.currentAvailability ? (
  <div>
    <p>Días: {profile.currentAvailability.daysOfWeek.join(', ')}</p>
    <p>Horario: {formatTime(profile.currentAvailability.startTime)} - {formatTime(profile.currentAvailability.endTime)}</p>
  </div>
) : (
  <p>No hay horario configurado</p>
)}
```

### Caso 4: Validar disponibilidad antes de mostrar servicios (Recomendado usar GET /api/User/expert-profile)
```typescript
const profile = await getExpertProfile();

if (!profile.currentAvailability || profile.currentAvailability.daysOfWeek.length === 0) {
  // Mostrar mensaje: "Por favor configura tu horario de disponibilidad"
  showNotification('Configura tu horario para recibir contrataciones');
}
```

### Caso 5: Formatear tiempo para mostrar al usuario
```typescript
function formatTime(timeSpan: string): string {
  // timeSpan viene como "09:00:00" o "18:00:00"
  const [hours, minutes] = timeSpan.split(':');
  return `${hours}:${minutes}`;
}

function formatDaysOfWeek(days: string[]): string {
  const dayNames: Record<string, string> = {
    'Monday': 'Lunes',
    'Tuesday': 'Martes',
    'Wednesday': 'Miércoles',
    'Thursday': 'Jueves',
    'Friday': 'Viernes',
    'Saturday': 'Sábado',
    'Sunday': 'Domingo'
  };
  
  return days.map(day => dayNames[day] || day).join(', ');
}
```

---

## ⚠️ Notas Importantes

1. **⚠️ ACTUALIZACIÓN DE DISPONIBILIDAD:**
   - **NO existe** un endpoint separado para crear/actualizar disponibilidad
   - La disponibilidad **SOLO** se puede crear o actualizar junto con el perfil usando:
     - `POST /api/User/become-expert` (al crear el perfil)
     - `PUT /api/User/expert-profile` (al actualizar el perfil)
   - Si intentas usar `POST /api/ExpertAvailability/set`, ese endpoint **NO existe**

2. **Formato de Tiempo:**
   - Para enviar: `"09:00"` (formato "HH:mm")
   - Para recibir: `"09:00:00"` (formato TimeSpan)

3. **Días de la Semana:**
   - Siempre en inglés: `"Monday"`, `"Tuesday"`, etc.
   - El frontend debe traducir al español para mostrar

4. **Múltiples Valores en FormData:**
   - Cuando uses FormData, debes enviar cada día por separado:
   ```javascript
   formData.append('AvailabilityDaysOfWeek', 'Monday');
   formData.append('AvailabilityDaysOfWeek', 'Tuesday');
   ```

5. **Actualización de Disponibilidad:**
   - Cada vez que actualizas, se crea un nuevo registro
   - El anterior queda en el historial marcado como inactivo
   - Esto permite mantener un registro completo de cambios
   - **DEBES enviar todos los campos de disponibilidad juntos** (DaysOfWeek, StartTime, EndTime)

6. **Disponibilidad en Respuestas:**
   - `GET /api/User/expert-profile` → Incluye `currentAvailability`
   - `GET /api/SearchHire/expert` → Incluye `currentAvailability` en `service.expert`
   - Ya no necesitas llamar a `/api/ExpertAvailability/current` por separado en estos casos

7. **Endpoints de Solo Lectura:**
   - `GET /api/ExpertAvailability/current` → Solo lectura, muestra disponibilidad actual
   - `GET /api/ExpertAvailability/history` → Solo lectura, muestra historial completo

---

## 🔗 Resumen de Endpoints

| Método | Endpoint | Descripción | Incluye Disponibilidad | Modificar Disponibilidad |
|--------|----------|-------------|------------------------|--------------------------|
| ~~`POST`~~ | ~~`/api/ExpertAvailability/set`~~ | ~~Crear/Actualizar disponibilidad~~ | ❌ | **❌ ELIMINADO** |
| `GET` | `/api/ExpertAvailability/current` | Obtener disponibilidad actual | ✅ | ❌ Solo lectura |
| `GET` | `/api/ExpertAvailability/history` | Obtener historial completo | ✅ | ❌ Solo lectura |
| `POST` | `/api/User/become-expert` | Crear experto (con disponibilidad opcional) | ⚠️ Opcional | ✅ Si se incluyen campos |
| `PUT` | `/api/User/expert-profile` | Actualizar perfil (con disponibilidad opcional) | ⚠️ Opcional | ✅ Si se incluyen campos |
| `GET` | `/api/User/expert-profile` | Obtener perfil completo | ✅ | ❌ Solo lectura |
| `GET` | `/api/SearchHire/expert` | Obtener contrataciones | ✅ | ❌ Solo lectura |

**⚠️ NOTA:** La disponibilidad **SOLO** se puede crear o actualizar a través de `POST /api/User/become-expert` o `PUT /api/User/expert-profile` incluyendo los campos de disponibilidad en el FormData.

---

## ✅ Checklist de Implementación Frontend

- [ ] **NO usar** `POST /api/ExpertAvailability/set` (endpoint eliminado)
- [ ] Crear interfaz para configurar disponibilidad (días + horario)
- [ ] Validar días de la semana antes de enviar
- [ ] Validar que hora inicio < hora fin
- [ ] Mostrar disponibilidad actual en el perfil del experto
- [ ] **Actualizar disponibilidad usando** `PUT /api/User/expert-profile` (no endpoint separado)
- [ ] Incluir disponibilidad al crear perfil de experto usando `POST /api/User/become-expert` (opcional)
- [ ] Formatear días al español para mostrar
- [ ] Formatear horas de TimeSpan a formato legible
- [ ] Manejar casos donde no hay disponibilidad configurada
- [ ] Mostrar historial de cambios (opcional)
- [ ] Asegurarse de enviar todos los campos de disponibilidad juntos (DaysOfWeek, StartTime, EndTime)

---

**¿Preguntas?** Revisa los ejemplos de código arriba o consulta con el equipo de backend.

