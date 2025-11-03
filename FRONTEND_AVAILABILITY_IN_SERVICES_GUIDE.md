# 🕐 **GUÍA FRONTEND - HORARIOS DE DISPONIBILIDAD EN SERVICIOS**

## 📋 **RESUMEN DE CAMBIOS**

Se han añadido los **horarios de disponibilidad del experto** en dos endpoints principales:

1. ✅ **`GET /api/SearchService`** - Lista de servicios con disponibilidad en cada servicio
2. ✅ **`GET /api/Search/{searchId}/details-complete`** - Detalles completos de búsqueda con disponibilidad del experto

---

## 🎯 **1. ENDPOINT: GET /api/SearchService**

### **Cambio:**
El campo `expert.currentAvailability` ahora está disponible en cada servicio de la lista.

### **Estructura Actualizada:**

```typescript
interface SearchServiceResponse {
  id: number;
  categoryId: number;
  serviceTypeId: number;
  serviceTypeName: string;
  price: number;
  conditions: string;
  durationInHours: number;
  createdAt: string;
  isActive: boolean;
  imageUrls: string[];
  selectedDeliverableTypes: DeliverableTypeDto[];
  expert: ExpertProfileDto | null; // ✅ NUEVO: Ahora incluye currentAvailability
  // ... otros campos
  categoryName?: string; // Solo en SearchServiceDetailDto
  completedSearches?: number;
  averageRating?: number;
}

interface ExpertProfileDto {
  id: number;
  profilePictureUrl: string;
  description: string;
  stripeAccountId: string | null;
  createdAt: string;
  user: UserDto | null;
  reviews: ReviewDto[];
  latitude: string;
  longitude: string;
  stripeStatus: StripeStatus;
  stripeStatusDetails: string | null;
  onboardingCompleted: boolean;
  isOnVacation: boolean;
  currentAvailability: CurrentExpertAvailabilityDto | null; // ✅ NUEVO CAMPO
}

interface CurrentExpertAvailabilityDto {
  id: number;
  daysOfWeek: string[]; // Ej: ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"]
  startTime: string; // Formato: "HH:mm:ss" (ej: "09:00:00")
  endTime: string; // Formato: "HH:mm:ss" (ej: "18:00:00")
  effectiveFrom: string; // ISO 8601 date (ej: "2025-01-15T00:00:00Z")
}
```

### **Ejemplo de Respuesta:**

```json
[
  {
    "id": 140,
    "categoryId": 2,
    "serviceTypeId": 1,
    "serviceTypeName": "Revisión presencial",
    "price": 200,
    "conditions": "ewrerewre",
    "durationInHours": 22,
    "expert": {
      "id": 52,
      "profilePictureUrl": "https://storage.googleapis.com/...",
      "description": "Experto en motos",
      "user": {
        "id": 52,
        "email": "expert@example.com",
        "name": "Juan Pérez"
      },
      "latitude": "42.17050547182959",
      "longitude": "-3.4035203733877717",
      "isOnVacation": false,
      "currentAvailability": {
        "id": 1,
        "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
        "startTime": "09:00:00",
        "endTime": "18:00:00",
        "effectiveFrom": "2025-01-01T00:00:00Z"
      }
    },
    "selectedDeliverableTypes": [...]
  }
]
```

### **Comportamiento:**
- Si el experto **tiene horarios configurados**: `currentAvailability` contendrá los datos
- Si el experto **no tiene horarios**: `currentAvailability` será `null`

---

## 🎯 **2. ENDPOINT: GET /api/Search/{searchId}/details-complete**

### **Cambio:**
Se ha añadido un nuevo campo `expertProfile` en la respuesta que contiene toda la información del experto, incluyendo los horarios de disponibilidad.

### **Estructura Actualizada:**

```typescript
interface SearchDetailsCompleteResponseDto {
  search: SearchListDto;
  moneyDistribution: MoneyDistributionConfigDto | null;
  category: CategoryDto | null;
  review: ReviewDto | null;
  appointment: AppointmentDto | null;
  deliverables: DeliverableDto[];
  disputes: DisputeDto[];
  expertProfile: ExpertProfileDto | null; // ✅ NUEVO CAMPO
}
```

### **Ejemplo de Respuesta:**

```json
{
  "search": {
    "id": 123,
    "title": "Búsqueda de servicio",
    "searchHire": {
      "expert": {
        "id": 52,
        "name": "Juan Pérez",
        "email": "expert@example.com"
      }
    }
  },
  "moneyDistribution": { ... },
  "category": { ... },
  "review": null,
  "appointment": { ... },
  "deliverables": [],
  "disputes": [],
  "expertProfile": {
    "id": 52,
    "profilePictureUrl": "https://storage.googleapis.com/...",
    "description": "Experto en motos con 10 años de experiencia",
    "stripeAccountId": null,
    "createdAt": "2025-09-14T17:55:59.171923Z",
    "user": {
      "id": 52,
      "email": "expert@example.com",
      "name": "Juan Pérez",
      "profilePictureUrl": null
    },
    "reviews": [],
    "latitude": "42.17050547182959",
    "longitude": "-3.4035203733877717",
    "stripeStatus": 0,
    "stripeStatusDetails": null,
    "onboardingCompleted": false,
    "isOnVacation": false,
    "currentAvailability": {
      "id": 1,
      "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
      "startTime": "09:00:00",
      "endTime": "18:00:00",
      "effectiveFrom": "2025-01-01T00:00:00Z"
    }
  }
}
```

### **Comportamiento:**
- Si el servicio tiene un experto asociado: `expertProfile` contendrá los datos completos
- Si no hay experto o no tiene disponibilidad: `expertProfile.currentAvailability` será `null`
- Si no hay SearchHire: `expertProfile` será `null`

---

## 🔧 **IMPLEMENTACIÓN FRONTEND**

### **1. Tipos TypeScript:**

```typescript
// types/expert.ts
export interface CurrentExpertAvailabilityDto {
  id: number;
  daysOfWeek: string[]; // ["Monday", "Tuesday", ...]
  startTime: string; // "HH:mm:ss"
  endTime: string; // "HH:mm:ss"
  effectiveFrom: string; // ISO 8601
}

export interface ExpertProfileDto {
  id: number;
  profilePictureUrl: string;
  description: string;
  stripeAccountId: string | null;
  createdAt: string;
  user: UserDto | null;
  reviews: ReviewDto[];
  latitude: string;
  longitude: string;
  stripeStatus: StripeStatus;
  stripeStatusDetails: string | null;
  onboardingCompleted: boolean;
  isOnVacation: boolean;
  currentAvailability: CurrentExpertAvailabilityDto | null; // ✅ NUEVO
}

// types/service.ts
export interface SearchServiceDetailDto {
  id: number;
  categoryId: number;
  serviceTypeId: number;
  serviceTypeName: string;
  price: number;
  // ... otros campos
  expert: ExpertProfileDto | null; // ✅ Ahora incluye currentAvailability
}

// types/search.ts
export interface SearchDetailsCompleteResponseDto {
  search: SearchListDto;
  moneyDistribution: MoneyDistributionConfigDto | null;
  category: CategoryDto | null;
  review: ReviewDto | null;
  appointment: AppointmentDto | null;
  deliverables: DeliverableDto[];
  disputes: DisputeDto[];
  expertProfile: ExpertProfileDto | null; // ✅ NUEVO
}
```

### **2. Utilidades para Formatear Horarios:**

```typescript
// utils/availability.ts

/**
 * Formatea los días de la semana en español
 */
export function formatDaysOfWeek(days: string[]): string {
  const daysMap: Record<string, string> = {
    'Monday': 'Lunes',
    'Tuesday': 'Martes',
    'Wednesday': 'Miércoles',
    'Thursday': 'Jueves',
    'Friday': 'Viernes',
    'Saturday': 'Sábado',
    'Sunday': 'Domingo'
  };
  
  return days.map(day => daysMap[day] || day).join(', ');
}

/**
 * Formatea TimeSpan a hora legible (ej: "09:00:00" -> "09:00")
 */
export function formatTimeSpan(timeSpan: string): string {
  const [hours, minutes] = timeSpan.split(':');
  return `${hours}:${minutes}`;
}

/**
 * Formatea el rango horario completo (ej: "Lunes a Viernes, 09:00 - 18:00")
 */
export function formatAvailabilityRange(
  availability: CurrentExpertAvailabilityDto | null
): string {
  if (!availability) {
    return 'Horarios no disponibles';
  }
  
  const days = formatDaysOfWeek(availability.daysOfWeek);
  const startTime = formatTimeSpan(availability.startTime);
  const endTime = formatTimeSpan(availability.endTime);
  
  return `${days}, ${startTime} - ${endTime}`;
}

/**
 * Verifica si un día específico está disponible
 */
export function isDayAvailable(
  availability: CurrentExpertAvailabilityDto | null,
  day: string
): boolean {
  if (!availability) return false;
  return availability.daysOfWeek.includes(day);
}

/**
 * Verifica si un experto está disponible ahora mismo
 */
export function isExpertAvailableNow(
  availability: CurrentExpertAvailabilityDto | null
): boolean {
  if (!availability) return false;
  
  const now = new Date();
  const currentDay = now.toLocaleDateString('en-US', { weekday: 'long' });
  const currentTime = now.toTimeString().slice(0, 8); // "HH:mm:ss"
  
  if (!isDayAvailable(availability, currentDay)) {
    return false;
  }
  
  return currentTime >= availability.startTime && currentTime <= availability.endTime;
}
```

### **3. Componente de Ejemplo - Mostrar Disponibilidad:**

```typescript
// components/ExpertAvailability.tsx
import React from 'react';
import { CurrentExpertAvailabilityDto } from '@/types/expert';
import { formatAvailabilityRange, formatDaysOfWeek, formatTimeSpan } from '@/utils/availability';

interface ExpertAvailabilityProps {
  availability: CurrentExpertAvailabilityDto | null;
}

export const ExpertAvailability: React.FC<ExpertAvailabilityProps> = ({ availability }) => {
  if (!availability) {
    return (
      <div className="availability-unavailable">
        <span className="text-gray-500">Horarios no disponibles</span>
      </div>
    );
  }

  return (
    <div className="expert-availability">
      <h3 className="font-semibold mb-2">Horarios de Disponibilidad</h3>
      
      <div className="availability-info">
        <div className="days">
          <strong>Días:</strong> {formatDaysOfWeek(availability.daysOfWeek)}
        </div>
        
        <div className="hours">
          <strong>Horario:</strong> {formatTimeSpan(availability.startTime)} - {formatTimeSpan(availability.endTime)}
        </div>
        
        <div className="range-text text-sm text-gray-600 mt-2">
          {formatAvailabilityRange(availability)}
        </div>
      </div>
    </div>
  );
};
```

### **4. Uso en Lista de Servicios:**

```typescript
// components/ServiceCard.tsx
import { SearchServiceDetailDto } from '@/types/service';
import { ExpertAvailability } from './ExpertAvailability';

interface ServiceCardProps {
  service: SearchServiceDetailDto;
}

export const ServiceCard: React.FC<ServiceCardProps> = ({ service }) => {
  return (
    <div className="service-card">
      <h2>{service.serviceTypeName}</h2>
      <p>Precio: €{service.price}</p>
      
      {service.expert && (
        <>
          <p>Experto: {service.expert.user?.name}</p>
          {/* ✅ NUEVO: Mostrar horarios de disponibilidad */}
          <ExpertAvailability 
            availability={service.expert.currentAvailability} 
          />
        </>
      )}
    </div>
  );
};
```

### **5. Uso en Detalles de Búsqueda:**

```typescript
// components/SearchDetails.tsx
import { useSearchDetailsComplete } from '@/hooks/useSearchDetailsComplete';
import { ExpertAvailability } from './ExpertAvailability';

export const SearchDetails: React.FC<{ searchId: number }> = ({ searchId }) => {
  const { data, isLoading, error } = useSearchDetailsComplete(searchId);

  if (isLoading) return <LoadingSpinner />;
  if (error) return <ErrorMessage error={error} />;
  if (!data) return <NotFound />;

  return (
    <div className="search-details">
      <h1>{data.search.title}</h1>
      
      {/* ✅ NUEVO: Mostrar perfil completo del experto con disponibilidad */}
      {data.expertProfile && (
        <div className="expert-section">
          <h2>Información del Experto</h2>
          <p>{data.expertProfile.description}</p>
          
          {/* Horarios de disponibilidad */}
          <ExpertAvailability 
            availability={data.expertProfile.currentAvailability} 
          />
        </div>
      )}
      
      {data.appointment && <AppointmentDetails appointment={data.appointment} />}
      {/* ... otros componentes */}
    </div>
  );
};
```

### **6. Ejemplo con Iconos y Badges:**

```typescript
// components/AvailabilityBadge.tsx
import { CurrentExpertAvailabilityDto } from '@/types/expert';
import { isExpertAvailableNow } from '@/utils/availability';
import { Clock, Calendar, CheckCircle, XCircle } from 'lucide-react';

export const AvailabilityBadge: React.FC<{
  availability: CurrentExpertAvailabilityDto | null;
}> = ({ availability }) => {
  if (!availability) {
    return (
      <div className="availability-badge unavailable">
        <XCircle className="w-4 h-4" />
        <span>No disponible</span>
      </div>
    );
  }

  const isAvailableNow = isExpertAvailableNow(availability);

  return (
    <div className={`availability-badge ${isAvailableNow ? 'available' : 'scheduled'}`}>
      {isAvailableNow ? (
        <>
          <CheckCircle className="w-4 h-4" />
          <span>Disponible ahora</span>
        </>
      ) : (
        <>
          <Clock className="w-4 h-4" />
          <span>Disponible en horario</span>
        </>
      )}
      
      <div className="availability-details">
        <Calendar className="w-3 h-3" />
        <span className="text-xs">
          {availability.daysOfWeek.length} días/semana
        </span>
      </div>
    </div>
  );
};
```

---

## 📝 **NOTAS IMPORTANTES**

### **Valores Null:**
- `currentAvailability` puede ser `null` si el experto no ha configurado horarios
- `expertProfile` puede ser `null` si no hay SearchHire o no hay experto asociado

### **Formato de Tiempo:**
- Los tiempos vienen en formato `TimeSpan`: `"HH:mm:ss"` (ej: `"09:00:00"`)
- Recomendación: extraer solo `HH:mm` para mostrar al usuario

### **Días de la Semana:**
- Los días vienen en inglés: `["Monday", "Tuesday", ...]`
- Recomendación: crear un mapeo para traducir al español

### **Validaciones:**
- Siempre verificar si `currentAvailability` existe antes de acceder a sus propiedades
- El campo `daysOfWeek` siempre será un array (puede estar vacío)

---

## ✅ **CHECKLIST DE IMPLEMENTACIÓN**

- [ ] Actualizar tipos TypeScript con `CurrentExpertAvailabilityDto`
- [ ] Añadir `currentAvailability` a `ExpertProfileDto` en tipos
- [ ] Añadir `expertProfile` a `SearchDetailsCompleteResponseDto` en tipos
- [ ] Crear utilidades para formatear horarios (`formatDaysOfWeek`, `formatTimeSpan`)
- [ ] Crear componente `ExpertAvailability` para mostrar horarios
- [ ] Integrar componente en lista de servicios
- [ ] Integrar componente en detalles de búsqueda
- [ ] Añadir validaciones para valores `null`
- [ ] Probar con expertos que tienen horarios configurados
- [ ] Probar con expertos que NO tienen horarios configurados
- [ ] Añadir estilos CSS para los componentes de disponibilidad

---

## 🎨 **EJEMPLOS DE UI SUGERIDOS**

### **Opción 1: Badge Simple**
```
🕐 Disponible: Lunes a Viernes, 09:00 - 18:00
```

### **Opción 2: Card Expandible**
```
📅 Horarios de Disponibilidad
   ▼ Lunes a Viernes
   09:00 - 18:00
```

### **Opción 3: Lista de Días**
```
✅ Lunes: 09:00 - 18:00
✅ Martes: 09:00 - 18:00
✅ Miércoles: 09:00 - 18:00
...
```

---

## 🔗 **ENDPOINTS AFECTADOS**

1. **`GET /api/SearchService`**
   - Query params: `categoryId`, `serviceTypeId`, `latitude`, `longitude`, `locationRange`
   - ✅ **NUEVO**: Campo `expert.currentAvailability` en cada servicio

2. **`GET /api/Search/{searchId}/details-complete`**
   - ✅ **NUEVO**: Campo `expertProfile` con información completa del experto incluyendo `currentAvailability`

---

¡Listo para implementar! 🚀

