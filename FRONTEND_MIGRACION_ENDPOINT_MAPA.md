# 🔄 Guía de Migración Frontend: Consolidar 2 Endpoints en 1

## ⚠️ CAMBIO IMPORTANTE

**ANTES tenías 2 endpoints diferentes:**
1. `GET /api/SearchService` - Para búsqueda por ubicación
2. `GET /api/SearchService/map-experts` - Para el mapa

**AHORA solo hay 1 endpoint unificado:**
```
GET /api/SearchService/map-experts
```

Este endpoint hace **TODO** según los parámetros que le envíes.

---

## 🎯 Resumen Rápido

| Situación | Parámetros a Enviar | Qué Devuelve |
|-----------|-------------------|--------------|
| **Carga inicial del mapa** | Solo `categoryId` y `serviceTypeId` | `ExpertMapResponseDto` (info básica) |
| **Mover el mapa** | `categoryId`, `serviceTypeId` + **bounds** (`northeastLat`, `northeastLng`, `southwestLat`, `southwestLng`) | `SearchServiceDetailDto[]` (info completa) |
| **Buscar por ubicación** | `categoryId`, `serviceTypeId` + `latitude`, `longitude`, `locationRange` | `SearchServiceDetailDto[]` (info completa) |

---

## 📋 Caso 1: Carga Inicial del Mapa

### **Cuándo usar:**
- Al entrar al mapa por primera vez
- Para mostrar todos los expertos disponibles
- Para colocar marcadores iniciales

### **Qué ENVIAR:**
```typescript
const params = new URLSearchParams({
  categoryId: '2',
  serviceTypeId: '1'
  // ❌ NO enviar bounds
  // ❌ NO enviar latitude/longitude/locationRange
});

const response = await fetch(
  `/api/SearchService/map-experts?${params.toString()}`
);
```

### **Qué RECIBES:**
```typescript
{
  experts: [
    {
      id: 40,
      name: "Diego Castilla",
      profilePictureUrl: "https://...",
      averageRating: 4.5,
      totalReviews: 10,
      completedSearches: 5,
      latitude: "40.4168",
      longitude: "-3.7038",
      price: 150.00,
      serviceDescription: "Consulta especializada...",
      serviceTypeName: "Consulta",
      serviceTypeDescription: "...",
      currentAvailability: {
        daysOfWeek: ["Monday", "Tuesday"],
        startTime: "09:00",
        endTime: "18:00"
      }
    },
    // ... más expertos
  ],
  totalCount: 25
}
```

### **Tipo TypeScript:**
```typescript
interface ExpertMapResponseDto {
  experts: ExpertMapDto[];
  totalCount: number;
}

interface ExpertMapDto {
  id: number;
  name: string;
  profilePictureUrl: string;
  averageRating: number;
  totalReviews: number;
  completedSearches: number;
  latitude: string;
  longitude: string;
  price: number;
  serviceDescription: string;
  serviceTypeName: string;
  serviceTypeDescription: string;
  currentAvailability: CurrentExpertAvailabilityDto | null;
}
```

---

## 📋 Caso 2: Mover el Mapa (Desplazamiento Dinámico)

### **Cuándo usar:**
- Cuando el usuario mueve o hace zoom en el mapa
- Para cargar servicios del área visible
- **REEMPLAZA** el comportamiento anterior de mover el mapa

### **Qué ENVIAR:**
```typescript
// 1. Obtener bounds del mapa
const bounds = map.getBounds();
const northeast = bounds.getNorthEast();
const southwest = bounds.getSouthWest();
const zoom = map.getZoom();

// 2. Construir parámetros
const params = new URLSearchParams({
  categoryId: '2',
  serviceTypeId: '1',
  // ✅ Bounds del área visible (OBLIGATORIOS todos)
  northeastLat: northeast.lat().toString(),
  northeastLng: northeast.lng().toString(),
  southwestLat: southwest.lat().toString(),
  southwestLng: southwest.lng().toString(),
  // ✅ Zoom (opcional pero recomendado)
  zoom: zoom.toString(),
  // ✅ Límite de resultados (opcional, default: 100)
  limit: '50'
});

// 3. Llamar endpoint
const response = await fetch(
  `/api/SearchService/map-experts?${params.toString()}`
);
```

### **⚠️ IMPORTANTE: Debouncing**
```typescript
// ✅ OBLIGATORIO: Esperar 300ms después de que el usuario deje de mover
let debounceTimer: NodeJS.Timeout | null = null;

const handleMapMove = () => {
  if (debounceTimer) {
    clearTimeout(debounceTimer);
  }
  
  debounceTimer = setTimeout(() => {
    loadServicesInBounds();
  }, 300); // ⚠️ CRÍTICO: 300ms mínimo
};

// En Google Maps React
<GoogleMap
  onDragEnd={handleMapMove}      // ✅ Al terminar de arrastrar
  onZoomChanged={handleMapMove}  // ✅ Al cambiar zoom
/>
```

### **Qué RECIBES:**
```typescript
[
  {
    id: 123,
    categoryId: 2,
    serviceTypeId: 1,
    serviceTypeName: "Consulta",
    serviceTypeDescription: "Consulta especializada...",
    price: 150.00,
    conditions: "Consulta de 1 hora...",
    durationInHours: 1,
    imageUrls: [
      "https://storage.googleapis.com/...",
      "https://storage.googleapis.com/..."
    ],
    expert: {
      id: 40,
      name: "Diego Castilla",
      profilePictureUrl: "https://...",
      description: "Experto en...",
      latitude: "40.4168",
      longitude: "-3.7038",
      reviews: [
        {
          id: 1,
          score: 5,
          description: "Excelente servicio",
          createdAt: "2024-01-15",
          reviewer: {
            id: 10,
            name: "Juan Pérez"
          },
          imageUrls: []
        }
      ],
      currentAvailability: {
        daysOfWeek: ["Monday", "Tuesday"],
        startTime: "09:00",
        endTime: "18:00"
      }
    },
    selectedDeliverableTypes: [
      {
        id: 1,
        name: "Informe",
        displayName: "Informe escrito"
      }
    ],
    categoryName: "Consultoría",
    completedSearches: 5,
    averageRating: 4.5
  },
  // ... más servicios
]
```

### **Tipo TypeScript:**
```typescript
interface SearchServiceDetailDto {
  id: number;
  categoryId: number;
  serviceTypeId: number;
  serviceTypeName: string;
  serviceTypeDescription: string;
  price: number;
  conditions: string;
  durationInHours: number;
  imageUrls: string[];
  expert: ExpertProfileDto;
  selectedDeliverableTypes: DeliverableTypeDto[];
  categoryName: string;
  completedSearches: number;
  averageRating: number;
}
```

---

## 📋 Caso 3: Búsqueda por Ubicación

### **Cuándo usar:**
- Cuando quieres buscar servicios cerca de una ubicación específica
- **REEMPLAZA** el endpoint anterior `GET /api/SearchService?latitude=...&longitude=...&locationRange=...`

### **Qué ENVIAR:**
```typescript
const params = new URLSearchParams({
  categoryId: '2',
  serviceTypeId: '1',
  // ✅ Ubicación y rango (OBLIGATORIOS todos)
  latitude: '40.4168',
  longitude: '-3.7038',
  locationRange: '25'  // En kilómetros
});

const response = await fetch(
  `/api/SearchService/map-experts?${params.toString()}`
);
```

### **Qué RECIBES:**
```typescript
// ✅ MISMO formato que Caso 2 (SearchServiceDetailDto[])
[
  {
    id: 123,
    categoryId: 2,
    serviceTypeId: 1,
    // ... misma estructura que Caso 2
  },
  // ... más servicios ordenados por distancia
]
```

### **Tipo TypeScript:**
```typescript
// ✅ MISMO tipo que Caso 2
SearchServiceDetailDto[]
```

---

## 🔄 Migración Paso a Paso

### **Paso 1: Identificar tus llamadas actuales**

Busca en tu código:
```typescript
// ❌ ANTIGUO - Buscar por ubicación
fetch('/api/SearchService?categoryId=2&serviceTypeId=1&latitude=40.4168&longitude=-3.7038&locationRange=25')

// ❌ ANTIGUO - Mapa con bounds
fetch('/api/SearchService/map-experts?categoryId=2&serviceTypeId=1&northeastLat=...')
```

### **Paso 2: Reemplazar por el nuevo endpoint**

```typescript
// ✅ NUEVO - Buscar por ubicación (mismos parámetros, diferente URL)
fetch('/api/SearchService/map-experts?categoryId=2&serviceTypeId=1&latitude=40.4168&longitude=-3.7038&locationRange=25')

// ✅ NUEVO - Mapa con bounds (igual que antes)
fetch('/api/SearchService/map-experts?categoryId=2&serviceTypeId=1&northeastLat=...')
```

### **Paso 3: Actualizar tipos TypeScript**

```typescript
// ✅ Agregar estos tipos si no los tienes
interface ExpertMapResponseDto {
  experts: ExpertMapDto[];
  totalCount: number;
}

interface SearchServiceDetailDto {
  id: number;
  categoryId: number;
  serviceTypeId: number;
  // ... (ver estructura completa arriba)
}
```

### **Paso 4: Manejar diferentes respuestas**

```typescript
const loadMapData = async (params: URLSearchParams) => {
  const response = await fetch(`/api/SearchService/map-experts?${params.toString()}`);
  const data = await response.json();
  
  // ✅ Detectar tipo de respuesta
  if (Array.isArray(data)) {
    // Es SearchServiceDetailDto[] (Caso 2 o 3)
    setServices(data);
  } else if (data.experts) {
    // Es ExpertMapResponseDto (Caso 1)
    setExperts(data.experts);
  }
};
```

---

## 💻 Ejemplo Completo de Implementación

```typescript
import { useState, useEffect, useRef } from 'react';

interface ExpertMapDto {
  id: number;
  name: string;
  latitude: string;
  longitude: string;
  price: number;
  // ... más campos
}

interface SearchServiceDetailDto {
  id: number;
  price: number;
  expert: {
    latitude: string;
    longitude: string;
    // ... más campos
  };
  // ... más campos
}

const ExpertsMap = ({ categoryId, serviceTypeId }) => {
  const [experts, setExperts] = useState<ExpertMapDto[]>([]);
  const [services, setServices] = useState<SearchServiceDetailDto[]>([]);
  const mapRef = useRef<any>(null);
  const debounceTimer = useRef<NodeJS.Timeout | null>(null);

  // ✅ 1. CARGA INICIAL (Caso 1)
  useEffect(() => {
    loadInitialServices();
  }, [categoryId, serviceTypeId]);

  const loadInitialServices = async () => {
    const params = new URLSearchParams({
      categoryId: categoryId.toString(),
      serviceTypeId: serviceTypeId.toString()
    });
    
    const response = await fetch(
      `/api/SearchService/map-experts?${params.toString()}`
    );
    const data = await response.json();
    
    // ✅ Recibe ExpertMapResponseDto
    setExperts(data.experts);
  };

  // ✅ 2. AL MOVER EL MAPA (Caso 2)
  const handleMapMove = () => {
    if (debounceTimer.current) {
      clearTimeout(debounceTimer.current);
    }
    
    debounceTimer.current = setTimeout(() => {
      loadServicesInBounds();
    }, 300);
  };

  const loadServicesInBounds = async () => {
    if (!mapRef.current) return;
    
    const bounds = mapRef.current.getBounds();
    if (!bounds) return;
    
    const northeast = bounds.getNorthEast();
    const southwest = bounds.getSouthWest();
    const zoom = mapRef.current.getZoom() || 12;
    
    const params = new URLSearchParams({
      categoryId: categoryId.toString(),
      serviceTypeId: serviceTypeId.toString(),
      northeastLat: northeast.lat().toString(),
      northeastLng: northeast.lng().toString(),
      southwestLat: southwest.lat().toString(),
      southwestLng: southwest.lng().toString(),
      zoom: zoom.toString(),
      limit: '50'
    });
    
    const response = await fetch(
      `/api/SearchService/map-experts?${params.toString()}`
    );
    const data = await response.json();
    
    // ✅ Recibe SearchServiceDetailDto[]
    setServices(data);
  };

  // ✅ 3. BÚSQUEDA POR UBICACIÓN (Caso 3)
  const searchByLocation = async (
    latitude: string, 
    longitude: string, 
    locationRange: number
  ) => {
    const params = new URLSearchParams({
      categoryId: categoryId.toString(),
      serviceTypeId: serviceTypeId.toString(),
      latitude: latitude,
      longitude: longitude,
      locationRange: locationRange.toString()
    });
    
    const response = await fetch(
      `/api/SearchService/map-experts?${params.toString()}`
    );
    const data = await response.json();
    
    // ✅ Recibe SearchServiceDetailDto[]
    setServices(data);
  };

  return (
    <GoogleMap
      onLoad={(map) => { mapRef.current = map; }}
      onDragEnd={handleMapMove}
      onZoomChanged={handleMapMove}
    >
      {/* Marcadores de carga inicial (Caso 1) */}
      {experts.map((expert) => (
        <Marker
          key={expert.id}
          position={{
            lat: parseFloat(expert.latitude),
            lng: parseFloat(expert.longitude)
          }}
          label={`€${expert.price.toFixed(0)}`}
        />
      ))}
      
      {/* Marcadores al mover mapa (Caso 2) */}
      {services.map((service) => (
        <Marker
          key={service.id}
          position={{
            lat: parseFloat(service.expert.latitude),
            lng: parseFloat(service.expert.longitude)
          }}
          label={`€${service.price.toFixed(0)}`}
        />
      ))}
    </GoogleMap>
  );
};
```

---

## 📊 Tabla de Comparación: Antes vs Ahora

| Situación | ANTES | AHORA |
|-----------|-------|-------|
| **Carga inicial** | `GET /api/SearchService/map-experts` | `GET /api/SearchService/map-experts` (igual) |
| **Mover mapa** | `GET /api/SearchService/map-experts` con bounds | `GET /api/SearchService/map-experts` con bounds (igual) |
| **Buscar por ubicación** | `GET /api/SearchService?latitude=...` | `GET /api/SearchService/map-experts?latitude=...` (cambia URL) |

---

## ⚠️ Puntos Críticos

### 1. **Debouncing OBLIGATORIO** (Caso 2)
```typescript
// ✅ CORRECTO
setTimeout(() => loadServicesInBounds(), 300);

// ❌ INCORRECTO (demasiadas llamadas)
map.on('move', () => loadServicesInBounds());
```

### 2. **Todos los Bounds o Ninguno** (Caso 2)
```typescript
// ✅ CORRECTO: Todos los bounds
northeastLat, northeastLng, southwestLat, southwestLng

// ❌ INCORRECTO: Bounds parciales
northeastLat, northeastLng // ❌ Falta southwest
```

### 3. **LocationRange Requerido** (Caso 3)
```typescript
// ✅ CORRECTO: Todos los parámetros de ubicación
latitude, longitude, locationRange

// ❌ INCORRECTO: Faltan parámetros
latitude, longitude // ❌ Falta locationRange
```

### 4. **Diferentes Tipos de Respuesta**
```typescript
// ✅ Detectar tipo de respuesta
if (Array.isArray(data)) {
  // Es SearchServiceDetailDto[] (Caso 2 o 3)
} else if (data.experts) {
  // Es ExpertMapResponseDto (Caso 1)
}
```

---

## ✅ Checklist de Migración

- [ ] Reemplazar `GET /api/SearchService` por `GET /api/SearchService/map-experts`
- [ ] Mantener mismos parámetros para búsqueda por ubicación
- [ ] Implementar carga inicial (Caso 1)
- [ ] Implementar carga con bounds al mover mapa (Caso 2) con debouncing
- [ ] Actualizar tipos TypeScript
- [ ] Manejar diferentes tipos de respuesta (array vs objeto)
- [ ] Probar los 3 casos de uso

---

## 🚀 Listo

Ahora tienes **1 solo endpoint** que maneja **TODO**:
- ✅ Carga inicial del mapa
- ✅ Desplazamiento dinámico por el mapa
- ✅ Búsqueda por ubicación

**¡Ya no necesitas el endpoint anterior!** 🎉

---

## 📚 Optimizaciones Adicionales

Para manejar **miles de servicios** sin problemas, consulta:
- **`MAP_PERFORMANCE_OPTIMIZATION_GUIDE.md`** - Guía completa de optimizaciones

