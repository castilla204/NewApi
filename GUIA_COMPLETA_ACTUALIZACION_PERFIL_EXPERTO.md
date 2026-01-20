# 📘 Guía Completa: Actualización del Perfil de Experto

## 🎯 Resumen

Esta guía explica **exactamente** cómo funciona la actualización del perfil de experto en el ExpertPanel, incluyendo:
- ✅ Cómo **recibir** los datos del perfil
- ✅ Cómo **enviar** los datos para actualizar
- ✅ Estructura exacta de los DTOs
- ✅ Validaciones y reglas de negocio
- ✅ Ejemplos de código completos

---

## 📥 1. RECIBIR EL PERFIL DE EXPERTO

### **Endpoint: `GET /api/User/expert-profile`**

**Autenticación**: ✅ Requerida (Bearer Token)

**Headers**:
```http
Authorization: Bearer {token}
```

**Response (200 OK)**:
```json
{
  "id": 39,
  "profilePictureUrl": "https://storage.googleapis.com/atrapobucket/experts/abc123.jpg",
  "description": "Experto en diseño gráfico con 10 años de experiencia...",
  "stripeAccountId": "acct_1SrnmZByGqRHKYlM",
  "createdAt": "2025-01-20T22:52:14.400038Z",
  "user": {
    "id": 13,
    "email": "expert@example.com",
    "name": "Juan Pérez",
    "profilePictureUrl": null
  },
  "reviews": [],
  "latitude": "40.4168",
  "longitude": "-3.7038",
  "stripeStatus": "Approved",
  "stripeStatusDetails": null,
  "onboardingCompleted": true,
  "isOnVacation": false,
  "currentAvailability": {
    "id": 1,
    "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
    "startTime": "09:00:00",
    "endTime": "18:00:00",
    "effectiveFrom": "2025-01-15T10:30:00Z"
  },
  "stripeFutureRequirements": null,
  "stripeFutureDueAt": null,
  "timezone": "Europe/Madrid",
  "country": "ES",
  "city": "Madrid"
}
```

### **DTO TypeScript (Respuesta)**

```typescript
interface ExpertProfileDto {
  id: number;
  profilePictureUrl: string; // ✅ URL de la imagen de perfil del experto (nunca null)
  description: string;
  stripeAccountId: string | null;
  createdAt: string; // ISO 8601 DateTime
  user: {
    id: number;
    email: string;
    name: string;
    profilePictureUrl: null; // ✅ SIEMPRE null - el perfil de imagen está en el nivel superior
  };
  reviews: ReviewDto[]; // Array de reviews (puede estar vacío)
  latitude: string; // ✅ STRING, no number
  longitude: string; // ✅ STRING, no number
  stripeStatus: "NotRequested" | "PendingVerification" | "Approved" | "Rejected";
  stripeStatusDetails: string | null;
  onboardingCompleted: boolean;
  isOnVacation: boolean;
  currentAvailability: CurrentExpertAvailabilityDto | null;
  stripeFutureRequirements: string | null;
  stripeFutureDueAt: string | null; // ISO 8601 DateTime | null
  timezone: string | null; // IANA timezone (ej: "Europe/Madrid")
  country: string | null; // ISO 3166-1 alpha-2 (ej: "ES", "MX")
  city: string | null; // Nombre de la ciudad (ej: "Madrid")
}

interface CurrentExpertAvailabilityDto {
  id: number;
  daysOfWeek: string[]; // ["Monday", "Tuesday", ...]
  startTime: string; // "09:00:00" (formato HH:mm:ss)
  endTime: string; // "18:00:00" (formato HH:mm:ss)
  effectiveFrom: string; // ISO 8601 DateTime
}

interface ReviewDto {
  id: number;
  score: number; // 1-5
  description: string;
  createdAt: string; // ISO 8601 DateTime
  reviewer: UserDto | null;
  imageUrls: string[];
  country: string | null;
}
```

### **Ejemplo de Código (React/TypeScript)**

```typescript
import { useState, useEffect } from 'react';

interface ExpertProfile {
  id: number;
  profilePictureUrl: string;
  description: string;
  latitude: string;
  longitude: string;
  currentAvailability: {
    id: number;
    daysOfWeek: string[];
    startTime: string;
    endTime: string;
    effectiveFrom: string;
  } | null;
  // ... otros campos
}

function ExpertPanelPage() {
  const [expertProfile, setExpertProfile] = useState<ExpertProfile | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetchExpertProfile();
  }, []);

  const fetchExpertProfile = async () => {
    try {
      setLoading(true);
      const response = await fetch('/api/User/expert-profile', {
        method: 'GET',
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
        }
      });

      if (!response.ok) {
        throw new Error('Failed to fetch expert profile');
      }

      const data: ExpertProfile = await response.json();
      setExpertProfile(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoading(false);
    }
  };

  if (loading) return <div>Cargando...</div>;
  if (error) return <div>Error: {error}</div>;
  if (!expertProfile) return <div>No se encontró el perfil</div>;

  return (
    <div>
      {/* ✅ USAR profilePictureUrl del nivel superior, NO user.profilePictureUrl */}
      <img src={expertProfile.profilePictureUrl || '/default-avatar.png'} alt="Profile" />
      <p>{expertProfile.description}</p>
      <p>Ubicación: {expertProfile.latitude}, {expertProfile.longitude}</p>
      {expertProfile.currentAvailability && (
        <div>
          <p>Disponibilidad: {expertProfile.currentAvailability.daysOfWeek.join(', ')}</p>
          <p>Horario: {expertProfile.currentAvailability.startTime} - {expertProfile.currentAvailability.endTime}</p>
        </div>
      )}
    </div>
  );
}
```

---

## 📤 2. ACTUALIZAR EL PERFIL DE EXPERTO

### **Endpoint: `PUT /api/User/expert-profile`**

**Autenticación**: ✅ Requerida (Bearer Token)

**Content-Type**: `multipart/form-data` (FormData)

**Headers**:
```http
Authorization: Bearer {token}
```

### **Request Body (FormData)**

| Campo | Tipo | Requerido | Descripción |
|-------|------|-----------|-------------|
| `ProfilePicture` | `File` | ❌ Opcional | Imagen de perfil (JPG, PNG, máximo 5MB) |
| `Description` | `string` | ✅ **REQUERIDO** | Descripción del experto |
| `Latitude` | `string` | ✅ **REQUERIDO** | Latitud (formato string, ej: "40.4168") |
| `Longitude` | `string` | ✅ **REQUERIDO** | Longitud (formato string, ej: "-3.7038") |
| `AvailabilityDaysOfWeek` | `string[]` | ⚠️ **CONDICIONAL** | Días de la semana (ver reglas abajo) |
| `AvailabilityStartTime` | `string` | ⚠️ **CONDICIONAL** | Hora de inicio (formato "HH:mm", ej: "09:00") |
| `AvailabilityEndTime` | `string` | ⚠️ **CONDICIONAL** | Hora de fin (formato "HH:mm", ej: "18:00") |

### **⚠️ REGLAS CRÍTICAS DE DISPONIBILIDAD**

1. **Si el experto YA TIENE disponibilidad activa**:
   - ✅ **DEBE** proporcionar `AvailabilityDaysOfWeek`, `AvailabilityStartTime` y `AvailabilityEndTime`
   - ❌ **NO puede omitir** estos campos (el backend rechazará la actualización)

2. **Si el experto NO TIENE disponibilidad activa**:
   - ✅ **DEBE** proporcionar `AvailabilityDaysOfWeek`, `AvailabilityStartTime` y `AvailabilityEndTime`
   - ❌ **NO puede omitir** estos campos (el backend rechazará la actualización)

3. **Valores válidos para `AvailabilityDaysOfWeek`**:
   - `"Monday"`, `"Tuesday"`, `"Wednesday"`, `"Thursday"`, `"Friday"`, `"Saturday"`, `"Sunday"`
   - ✅ Case-insensitive (puede ser "monday" o "Monday")
   - ✅ Múltiples valores permitidos

4. **Formato de tiempos**:
   - `AvailabilityStartTime`: `"HH:mm"` (ej: `"09:00"`, `"14:30"`)
   - `AvailabilityEndTime`: `"HH:mm"` (ej: `"18:00"`, `"22:00"`)
   - ✅ `StartTime` **DEBE ser menor** que `EndTime`

### **DTO TypeScript (Request)**

```typescript
interface UpdateExpertProfileRequest {
  profilePicture?: File; // Opcional
  description: string; // ✅ REQUERIDO
  latitude: string; // ✅ REQUERIDO (string, no number)
  longitude: string; // ✅ REQUERIDO (string, no number)
  availabilityDaysOfWeek?: string[]; // ⚠️ CONDICIONAL (ver reglas)
  availabilityStartTime?: string; // ⚠️ CONDICIONAL (formato "HH:mm")
  availabilityEndTime?: string; // ⚠️ CONDICIONAL (formato "HH:mm")
}
```

### **Ejemplo de Código (React/TypeScript)**

```typescript
import { useState } from 'react';

function UpdateExpertProfileForm() {
  const [formData, setFormData] = useState({
    description: '',
    latitude: '',
    longitude: '',
    availabilityDaysOfWeek: [] as string[],
    availabilityStartTime: '',
    availabilityEndTime: ''
  });
  const [profilePicture, setProfilePicture] = useState<File | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Días de la semana válidos
  const validDays = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setLoading(true);
    setError(null);

    try {
      // ✅ Validaciones del frontend
      if (!formData.description.trim()) {
        throw new Error('La descripción es requerida');
      }

      if (!formData.latitude || !formData.longitude) {
        throw new Error('Latitud y Longitud son requeridas');
      }

      // ✅ Validar disponibilidad (SIEMPRE requerida)
      if (!formData.availabilityDaysOfWeek || formData.availabilityDaysOfWeek.length === 0) {
        throw new Error('Debe seleccionar al menos un día de disponibilidad');
      }

      if (!formData.availabilityStartTime || !formData.availabilityEndTime) {
        throw new Error('Hora de inicio y fin son requeridas');
      }

      // ✅ Validar formato de tiempos
      const startTime = formData.availabilityStartTime.split(':');
      const endTime = formData.availabilityEndTime.split(':');
      
      if (startTime.length !== 2 || endTime.length !== 2) {
        throw new Error('Formato de tiempo inválido. Use HH:mm (ej: 09:00)');
      }

      // ✅ Validar que startTime < endTime
      const startMinutes = parseInt(startTime[0]) * 60 + parseInt(startTime[1]);
      const endMinutes = parseInt(endTime[0]) * 60 + parseInt(endTime[1]);
      
      if (startMinutes >= endMinutes) {
        throw new Error('La hora de inicio debe ser menor que la hora de fin');
      }

      // ✅ Validar días válidos
      const invalidDays = formData.availabilityDaysOfWeek.filter(
        day => !validDays.includes(day)
      );
      
      if (invalidDays.length > 0) {
        throw new Error(`Días inválidos: ${invalidDays.join(', ')}`);
      }

      // ✅ Crear FormData
      const formDataToSend = new FormData();
      
      // Campos requeridos
      formDataToSend.append('Description', formData.description);
      formDataToSend.append('Latitude', formData.latitude); // ✅ STRING
      formDataToSend.append('Longitude', formData.longitude); // ✅ STRING
      
      // Imagen de perfil (opcional)
      if (profilePicture) {
        formDataToSend.append('ProfilePicture', profilePicture);
      }
      
      // ✅ Disponibilidad (SIEMPRE requerida)
      formData.availabilityDaysOfWeek.forEach(day => {
        formDataToSend.append('AvailabilityDaysOfWeek', day);
      });
      formDataToSend.append('AvailabilityStartTime', formData.availabilityStartTime);
      formDataToSend.append('AvailabilityEndTime', formData.availabilityEndTime);

      // ✅ Enviar al backend
      const response = await fetch('/api/User/expert-profile', {
        method: 'PUT',
        headers: {
          'Authorization': `Bearer ${token}`
          // ❌ NO incluir 'Content-Type' - el navegador lo establece automáticamente para FormData
        },
        body: formDataToSend
      });

      if (!response.ok) {
        const errorData = await response.json();
        throw new Error(errorData.message || 'Error al actualizar el perfil');
      }

      const result = await response.json();
      console.log('✅ Perfil actualizado:', result);
      
      // ✅ Actualizar estado local con la respuesta
      // result.expertProfile contiene el perfil actualizado
      
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoading(false);
    }
  };

  return (
    <form onSubmit={handleSubmit}>
      {/* Descripción */}
      <div>
        <label>Descripción *</label>
        <textarea
          value={formData.description}
          onChange={(e) => setFormData({ ...formData, description: e.target.value })}
          required
        />
      </div>

      {/* Ubicación */}
      <div>
        <label>Latitud *</label>
        <input
          type="text"
          value={formData.latitude}
          onChange={(e) => setFormData({ ...formData, latitude: e.target.value })}
          required
        />
      </div>

      <div>
        <label>Longitud *</label>
        <input
          type="text"
          value={formData.longitude}
          onChange={(e) => setFormData({ ...formData, longitude: e.target.value })}
          required
        />
      </div>

      {/* Imagen de perfil */}
      <div>
        <label>Imagen de Perfil (opcional)</label>
        <input
          type="file"
          accept="image/jpeg,image/png,image/jpg"
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file) {
              // ✅ Validar tamaño (5MB máximo)
              if (file.size > 5 * 1024 * 1024) {
                alert('La imagen debe ser menor a 5MB');
                return;
              }
              setProfilePicture(file);
            }
          }}
        />
      </div>

      {/* Disponibilidad - Días de la semana */}
      <div>
        <label>Días de Disponibilidad *</label>
        {validDays.map(day => (
          <label key={day}>
            <input
              type="checkbox"
              checked={formData.availabilityDaysOfWeek.includes(day)}
              onChange={(e) => {
                if (e.target.checked) {
                  setFormData({
                    ...formData,
                    availabilityDaysOfWeek: [...formData.availabilityDaysOfWeek, day]
                  });
                } else {
                  setFormData({
                    ...formData,
                    availabilityDaysOfWeek: formData.availabilityDaysOfWeek.filter(d => d !== day)
                  });
                }
              }}
            />
            {day}
          </label>
        ))}
      </div>

      {/* Disponibilidad - Horario */}
      <div>
        <label>Hora de Inicio *</label>
        <input
          type="time"
          value={formData.availabilityStartTime}
          onChange={(e) => setFormData({ ...formData, availabilityStartTime: e.target.value })}
          required
        />
      </div>

      <div>
        <label>Hora de Fin *</label>
        <input
          type="time"
          value={formData.availabilityEndTime}
          onChange={(e) => setFormData({ ...formData, availabilityEndTime: e.target.value })}
          required
        />
      </div>

      {error && <div style={{ color: 'red' }}>{error}</div>}

      <button type="submit" disabled={loading}>
        {loading ? 'Guardando...' : 'Guardar Cambios'}
      </button>
    </form>
  );
}
```

---

## 📋 3. ESTRUCTURA EXACTA DE LOS DTOs

### **UpdateExpertProfileRequestDto (Backend C#)**

```csharp
public class UpdateExpertProfileRequestDto
{
    public IFormFile? ProfilePicture { get; set; } // Opcional
    public string Description { get; set; } // ✅ REQUERIDO
    public string Latitude { get; set; } // ✅ REQUERIDO (string)
    public string Longitude { get; set; } // ✅ REQUERIDO (string)
    
    public List<string>? AvailabilityDaysOfWeek { get; set; } // ⚠️ CONDICIONAL
    public string? AvailabilityStartTime { get; set; } // ⚠️ CONDICIONAL (formato "HH:mm")
    public string? AvailabilityEndTime { get; set; } // ⚠️ CONDICIONAL (formato "HH:mm")
}
```

### **UpdateExpertProfileResponseDto (Backend C#)**

```csharp
public class UpdateExpertProfileResponseDto
{
    public string Message { get; set; } // "Expert profile updated successfully"
    public ExpertProfileDto ExpertProfile { get; set; } // Perfil actualizado
}
```

### **ExpertProfileDto (Backend C#)**

```csharp
public class ExpertProfileDto
{
    public int Id { get; set; }
    public string ProfilePictureUrl { get; set; }
    public string Description { get; set; }
    public string? StripeAccountId { get; set; }
    public DateTime CreatedAt { get; set; }
    public UserDto User { get; set; }
    public List<ReviewDto> Reviews { get; set; }
    public string Latitude { get; set; } // ✅ STRING
    public string Longitude { get; set; } // ✅ STRING
    public StripeStatus StripeStatus { get; set; }
    public string? StripeStatusDetails { get; set; }
    public bool OnboardingCompleted { get; set; }
    public bool IsOnVacation { get; set; }
    public CurrentExpertAvailabilityDto? CurrentAvailability { get; set; }
    public string? StripeFutureRequirements { get; set; }
    public DateTime? StripeFutureDueAt { get; set; }
    public string? Timezone { get; set; } // IANA timezone
    public string? Country { get; set; } // ISO 3166-1 alpha-2
    public string? City { get; set; } // Nombre de la ciudad
}
```

---

## ⚠️ 4. VALIDACIONES Y REGLAS DE NEGOCIO

### **Validaciones del Backend**

1. **Descripción**:
   - ✅ No puede estar vacía o ser solo espacios en blanco
   - ✅ Campo requerido

2. **Coordenadas**:
   - ✅ `Latitude` y `Longitude` son requeridas
   - ✅ `Latitude` debe estar entre -90 y 90
   - ✅ `Longitude` debe estar entre -180 y 180
   - ✅ Se envían como **strings** (no numbers)

3. **Imagen de Perfil**:
   - ✅ Opcional
   - ✅ Tamaño máximo: 5MB
   - ✅ Formatos permitidos: `.jpg`, `.jpeg`, `.png`
   - ✅ Se redimensiona automáticamente a 200x200px

4. **Disponibilidad**:
   - ⚠️ **SIEMPRE requerida** (tanto si el experto ya tiene disponibilidad como si no)
   - ✅ `AvailabilityDaysOfWeek` debe tener al menos un día
   - ✅ Días válidos: `"Monday"`, `"Tuesday"`, `"Wednesday"`, `"Thursday"`, `"Friday"`, `"Saturday"`, `"Sunday"`
   - ✅ `AvailabilityStartTime` y `AvailabilityEndTime` son requeridos
   - ✅ Formato: `"HH:mm"` (ej: `"09:00"`, `"18:00"`)
   - ✅ `StartTime` **DEBE ser menor** que `EndTime`

5. **Detección Automática**:
   - ✅ Si cambian las coordenadas (`Latitude` o `Longitude`), el backend detecta automáticamente:
     - `Timezone` (IANA, ej: "Europe/Madrid")
     - `Country` (ISO 3166-1 alpha-2, ej: "ES")
     - `City` (nombre de la ciudad, ej: "Madrid")

---

## 🔄 5. FLUJO COMPLETO DE ACTUALIZACIÓN

### **Paso 1: Cargar Perfil Actual**

```typescript
GET /api/User/expert-profile
→ Devuelve ExpertProfileDto con todos los datos actuales
```

### **Paso 2: Mostrar Formulario**

```typescript
// Pre-llenar formulario con datos actuales
setFormData({
  description: expertProfile.description,
  latitude: expertProfile.latitude, // ✅ STRING
  longitude: expertProfile.longitude, // ✅ STRING
  availabilityDaysOfWeek: expertProfile.currentAvailability?.daysOfWeek || [],
  availabilityStartTime: expertProfile.currentAvailability?.startTime?.substring(0, 5) || '', // "09:00:00" → "09:00"
  availabilityEndTime: expertProfile.currentAvailability?.endTime?.substring(0, 5) || '' // "18:00:00" → "18:00"
});
```

### **Paso 3: Usuario Modifica Datos**

```typescript
// Usuario cambia descripción, ubicación, disponibilidad, etc.
// Validaciones del frontend antes de enviar
```

### **Paso 4: Enviar Actualización**

```typescript
PUT /api/User/expert-profile
FormData:
  - Description: "Nueva descripción..."
  - Latitude: "40.4168"
  - Longitude: "-3.7038"
  - ProfilePicture: [File] (opcional)
  - AvailabilityDaysOfWeek: ["Monday", "Tuesday", ...]
  - AvailabilityStartTime: "09:00"
  - AvailabilityEndTime: "18:00"
```

### **Paso 5: Respuesta del Backend**

```json
{
  "message": "Expert profile updated successfully",
  "expertProfile": {
    "id": 39,
    "profilePictureUrl": "https://...",
    "description": "Nueva descripción...",
    "latitude": "40.4168",
    "longitude": "-3.7038",
    "currentAvailability": {
      "id": 2,
      "daysOfWeek": ["Monday", "Tuesday"],
      "startTime": "09:00:00",
      "endTime": "18:00:00",
      "effectiveFrom": "2025-01-20T10:30:00Z"
    },
    "timezone": "Europe/Madrid",
    "country": "ES",
    "city": "Madrid"
  }
}
```

### **Paso 6: Actualizar UI**

```typescript
// Actualizar estado local con la respuesta
setExpertProfile(result.expertProfile);
// Mostrar mensaje de éxito
```

---

## 🎯 6. EJEMPLO COMPLETO Y FUNCIONAL

```typescript
import { useState, useEffect } from 'react';

interface ExpertProfile {
  id: number;
  profilePictureUrl: string;
  description: string;
  latitude: string;
  longitude: string;
  currentAvailability: {
    id: number;
    daysOfWeek: string[];
    startTime: string;
    endTime: string;
    effectiveFrom: string;
  } | null;
  timezone: string | null;
  country: string | null;
  city: string | null;
}

function ExpertProfileEditor() {
  const [expertProfile, setExpertProfile] = useState<ExpertProfile | null>(null);
  const [formData, setFormData] = useState({
    description: '',
    latitude: '',
    longitude: '',
    availabilityDaysOfWeek: [] as string[],
    availabilityStartTime: '',
    availabilityEndTime: ''
  });
  const [profilePicture, setProfilePicture] = useState<File | null>(null);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);

  const validDays = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

  // Cargar perfil al montar
  useEffect(() => {
    loadProfile();
  }, []);

  const loadProfile = async () => {
    try {
      setLoading(true);
      const response = await fetch('/api/User/expert-profile', {
        headers: { 'Authorization': `Bearer ${token}` }
      });
      
      if (!response.ok) throw new Error('Failed to load profile');
      
      const data: ExpertProfile = await response.json();
      setExpertProfile(data);
      
      // Pre-llenar formulario
      setFormData({
        description: data.description,
        latitude: data.latitude,
        longitude: data.longitude,
        availabilityDaysOfWeek: data.currentAvailability?.daysOfWeek || [],
        availabilityStartTime: data.currentAvailability?.startTime?.substring(0, 5) || '',
        availabilityEndTime: data.currentAvailability?.endTime?.substring(0, 5) || ''
      });
    } catch (err) {
      console.error('Error loading profile:', err);
    } finally {
      setLoading(false);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);

    try {
      const formDataToSend = new FormData();
      formDataToSend.append('Description', formData.description);
      formDataToSend.append('Latitude', formData.latitude);
      formDataToSend.append('Longitude', formData.longitude);
      
      if (profilePicture) {
        formDataToSend.append('ProfilePicture', profilePicture);
      }
      
      formData.availabilityDaysOfWeek.forEach(day => {
        formDataToSend.append('AvailabilityDaysOfWeek', day);
      });
      formDataToSend.append('AvailabilityStartTime', formData.availabilityStartTime);
      formDataToSend.append('AvailabilityEndTime', formData.availabilityEndTime);

      const response = await fetch('/api/User/expert-profile', {
        method: 'PUT',
        headers: { 'Authorization': `Bearer ${token}` },
        body: formDataToSend
      });

      if (!response.ok) {
        const error = await response.json();
        throw new Error(error.message || 'Error al actualizar');
      }

      const result = await response.json();
      setExpertProfile(result.expertProfile);
      alert('✅ Perfil actualizado correctamente');
    } catch (err) {
      alert(`❌ Error: ${err instanceof Error ? err.message : 'Unknown error'}`);
    } finally {
      setSaving(false);
    }
  };

  if (loading) return <div>Cargando perfil...</div>;

  return (
    <form onSubmit={handleSubmit}>
      {/* Campos del formulario... */}
      <button type="submit" disabled={saving}>
        {saving ? 'Guardando...' : 'Guardar Cambios'}
      </button>
    </form>
  );
}
```

---

## 📝 7. NOTAS IMPORTANTES

### **✅ Puntos Clave**

1. **Latitude/Longitude son STRINGS**: No uses `number`, usa `string`
2. **Disponibilidad SIEMPRE requerida**: Incluso si el experto ya tiene disponibilidad, debe enviarla
3. **Formato de tiempos**: `"HH:mm"` (ej: `"09:00"`), no `"HH:mm:ss"`
4. **Días de la semana**: Case-insensitive, pero usa mayúscula inicial (ej: `"Monday"`)
5. **Imagen opcional**: Si no se envía, se mantiene la imagen actual
6. **Detección automática**: Timezone, Country y City se detectan automáticamente si cambian las coordenadas
7. **⚠️ ProfilePictureUrl**: 
   - ✅ **USA**: `expertProfile.profilePictureUrl` (nivel superior del DTO)
   - ❌ **NO USES**: `expertProfile.user.profilePictureUrl` (siempre será `null`)
   - El perfil de imagen del experto está en el nivel superior, no dentro de `user`

### **❌ Errores Comunes**

1. ❌ Enviar `Latitude`/`Longitude` como `number` → Debe ser `string`
2. ❌ Omitir disponibilidad si el experto ya la tiene → **SIEMPRE requerida**
3. ❌ Formato de tiempo incorrecto → Debe ser `"HH:mm"`, no `"HH:mm:ss"`
4. ❌ Incluir `Content-Type: application/json` con FormData → El navegador lo establece automáticamente
5. ❌ No validar que `StartTime < EndTime` → El backend rechazará la petición
6. ❌ Usar `expertProfile.user.profilePictureUrl` → **SIEMPRE será `null`**, usa `expertProfile.profilePictureUrl` en su lugar

---

## 🎯 Resumen Final

- **GET `/api/User/expert-profile`**: Recibe `ExpertProfileDto` completo
- **PUT `/api/User/expert-profile`**: Envía `UpdateExpertProfileRequestDto` como FormData
- **Campos requeridos**: `Description`, `Latitude`, `Longitude`, `AvailabilityDaysOfWeek`, `AvailabilityStartTime`, `AvailabilityEndTime`
- **Campos opcionales**: `ProfilePicture`
- **Validaciones**: Backend valida todo, pero el frontend debe validar antes de enviar para mejor UX
