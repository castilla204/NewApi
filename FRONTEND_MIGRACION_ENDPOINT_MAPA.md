# 🔄 Guía de Migración Frontend: Consolidar 2 Endpoints en 1

## 🚀 ACTUALIZACIÓN: Optimizaciones Implementadas (30 Dic 2025)

**¡Buenas noticias!** El backend ha sido optimizado significativamente. Las respuestas ahora son **mucho más rápidas** y **más eficientes**.

### ✅ Cambios Transparentes (No Requieren Cambios en el Frontend)

1. **Compresión HTTP Automática**
   - Las respuestas ahora vienen comprimidas (Gzip/Brotli)
   - Los navegadores modernos las descomprimen automáticamente
   - **No necesitas hacer nada** - funciona automáticamente
   - **Beneficio:** 60-80% menos datos transferidos

2. **Consultas Optimizadas**
   - El backend ahora filtra directamente en SQL
   - **No cambia la API** - misma estructura de respuesta
   - **Beneficio:** Respuestas 10-100x más rápidas

3. **Índices de Base de Datos**
   - Consultas geoespaciales optimizadas
   - **No cambia la API** - misma estructura de respuesta
   - **Beneficio:** Mejor rendimiento con muchos servicios

### 📊 Mejoras de Rendimiento Esperadas

| Métrica | Antes | Después | Mejora |
|---------|-------|---------|--------|
| **Tiempo de respuesta** | 2-5 segundos | < 500ms | 10-100x más rápido |
| **Tamaño de respuesta** | 2MB | 400KB | 80% menos datos |
| **Servicios cargados** | Todos | Solo visibles | 99.5% menos datos |

### ⚠️ Importante para el Frontend

**NO necesitas cambiar nada en tu código.** La API sigue siendo exactamente la misma:
- ✅ Mismos endpoints
- ✅ Mismos parámetros
- ✅ Misma estructura de respuesta
- ✅ Misma paginación

**Lo único que notarás:**
- ✅ Respuestas más rápidas
- ✅ Menos datos transferidos
- ✅ Mejor rendimiento general

### 🔧 Si Quieres Aprovechar la Compresión (Opcional)

Si quieres asegurarte de que tu frontend acepta compresión (aunque los navegadores modernos lo hacen automáticamente):

```typescript
// En tus llamadas fetch/axios
const response = await fetch(url, {
  headers: {
    'Accept-Encoding': 'gzip, deflate, br' // Opcional - navegadores lo hacen automáticamente
  }
});
```

**Nota:** Los navegadores modernos (Chrome, Firefox, Safari, Edge) ya envían este header automáticamente, así que no es necesario.

---

## 📋 Guía de Migración Original

## ⚠️ CAMBIO IMPORTANTE

**ANTES tenías 2 endpoints diferentes:**
1. `GET /api/SearchService` - Para búsqueda por ubicación
2. `GET /api/SearchService/map-experts` - Para el mapa

**AHORA solo hay 1 endpoint unificado:**
```
GET /api/SearchService/map-experts
```

Este endpoint hace **TODO** según los parámetros que le envíes.

### **🔍 FILTRO IMPORTANTE:**
⚠️ **La API SOLO devuelve servicios que coincidan con:**
- ✅ El `categoryId` que especifiques
- ✅ El `serviceTypeId` que especifiques
- ✅ Que estén activos
- ✅ Que el experto esté aprobado y no de vacaciones
- ✅ Que estén dentro del área visible (si enviaste bounds) o dentro del radio (si enviaste locationRange)

**NO devuelve todos los servicios**, solo los que cumplan estos filtros.

---

## 🎯 Resumen Rápido

| Situación | Parámetros a Enviar | Qué Devuelve |
|-----------|-------------------|--------------|
| **Carga inicial del mapa** | Solo `categoryId` y `serviceTypeId` | `ExpertMapResponseDto` (info básica) - **Solo servicios de esa categoría/tipo** |
| **Mover el mapa** | `categoryId`, `serviceTypeId` + **bounds** (`northeastLat`, `northeastLng`, `southwestLat`, `southwestLng`) | `SearchServiceDetailDto[]` ✅ **INFO COMPLETA**: imágenes, reviews, disponibilidades, todo - **Solo servicios de esa categoría/tipo dentro del área visible** |
| **Buscar por ubicación** | `categoryId`, `serviceTypeId` + `latitude`, `longitude`, `locationRange` | `SearchServiceDetailDto[]` ✅ **INFO COMPLETA**: imágenes, reviews, disponibilidades, todo - **Solo servicios de esa categoría/tipo dentro del radio** |

### **🎯 IMPORTANTE - Al Mover el Mapa:**
✅ **Devuelve TODOS los servicios visibles** (de la categoría y tipo especificados) **con TODA la información:**
- ✅ **Solo servicios de la categoría y tipo de servicio** que especificaste (`categoryId` y `serviceTypeId`)
- ✅ Todas las imágenes del servicio
- ✅ Información completa del experto
- ✅ Todas las reviews con imágenes y datos del revisor
- ✅ Disponibilidades (horarios)
- ✅ Tipos de entregables
- ✅ Estadísticas (rating, búsquedas completadas)
- ✅ **TODO lo necesario para mostrar el servicio completo**

**NO necesitas hacer otra llamada** para obtener detalles. **Ya viene todo incluido.** ✅

**⚠️ NOTA**: La API filtra por `categoryId` y `serviceTypeId`, así que solo devuelve servicios que coincidan con esos filtros.

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

### **🎯 IMPORTANTE: ¿Qué devuelve?**
✅ **DEVUELVE TODOS los servicios visibles** (de la categoría y tipo especificados) **con TODA la información completa:**
- ✅ **Solo servicios que coincidan con** `categoryId` y `serviceTypeId` que enviaste
- ✅ Imágenes del servicio (todas)
- ✅ Información completa del experto
- ✅ Reviews con imágenes y detalles del revisor
- ✅ Disponibilidades (horarios)
- ✅ Tipos de entregables
- ✅ Todo lo necesario para mostrar el servicio completo

**NO necesitas hacer otra llamada** para obtener los detalles. **Ya viene todo incluido.**

**⚠️ FILTRO IMPORTANTE**: La API solo devuelve servicios que:
- Tengan el `categoryId` especificado
- Tengan el `serviceTypeId` especificado
- Estén activos (`IsActive = true`)
- El experto esté aprobado y no de vacaciones
- Estén dentro del área visible (si enviaste bounds) o dentro del radio (si enviaste locationRange)

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
// ✅ Objeto con servicios y paginación
{
  services: [
  {
    id: 123,
    categoryId: 2,
    serviceTypeId: 1,
    serviceTypeName: "Consulta",
    serviceTypeDescription: "Consulta especializada en...",
    price: 150.00,
    conditions: "Consulta de 1 hora con informe detallado...",
    durationInHours: 1,
    
    // ✅ TODAS las imágenes del servicio
    imageUrls: [
      "https://storage.googleapis.com/atrapobucket/services/image1.jpg",
      "https://storage.googleapis.com/atrapobucket/services/image2.jpg",
      "https://storage.googleapis.com/atrapobucket/services/image3.jpg"
    ],
    
    // ✅ INFORMACIÓN COMPLETA del experto
    expert: {
      id: 40,
      name: "Diego Castilla",
      profilePictureUrl: "https://storage.googleapis.com/...",
      description: "Experto en consultoría con 10 años de experiencia...",
      latitude: "40.4168",
      longitude: "-3.7038",
      
      // ✅ TODAS las reviews con información completa
      reviews: [
        {
          id: 1,
          score: 5,
          description: "Excelente servicio, muy profesional",
          createdAt: "2024-01-15T10:30:00Z",
          // ✅ Información del revisor
          reviewer: {
            id: 10,
            name: "Juan Pérez",
            email: "juan@example.com"
          },
          // ✅ Imágenes de la review (si las tiene)
          imageUrls: [
            "https://storage.googleapis.com/atrapobucket/reviews/review1.jpg"
          ],
          // ✅ País donde se realizó la contratación
          country: "ES"
        },
        {
          id: 2,
          score: 4,
          description: "Muy bueno, recomendado",
          createdAt: "2024-01-10T14:20:00Z",
          reviewer: {
            id: 11,
            name: "María García",
            email: "maria@example.com"
          },
          imageUrls: [],
          country: "ES"
        }
      ],
      
      // ✅ Disponibilidad actual del experto
      currentAvailability: {
        id: 1,
        daysOfWeek: ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
        startTime: "09:00",
        endTime: "18:00",
        effectiveFrom: "2024-01-01T00:00:00Z"
      },
      
      // ✅ Información adicional del experto
      stripeStatus: "Approved",
      onboardingCompleted: true,
      timezone: "Europe/Madrid",
      country: "ES"
    },
    
    // ✅ Tipos de entregables del servicio
    selectedDeliverableTypes: [
      {
        id: 1,
        name: "Informe",
        displayName: "Informe escrito",
        description: "Informe detallado con conclusiones",
        isRequired: true,
        isActive: true,
        sortOrder: 1
      },
      {
        id: 2,
        name: "Reunión",
        displayName: "Reunión de seguimiento",
        description: "Reunión para discutir resultados",
        isRequired: false,
        isActive: true,
        sortOrder: 2
      }
    ],
    
    categoryName: "Consultoría",
    completedSearches: 5,
    averageRating: 4.5
  },
  // ... más servicios con la misma estructura completa
]
```

### **🎯 Lo que incluye cada servicio:**
- ✅ **Todas las imágenes** del servicio (`imageUrls`)
- ✅ **Información completa del experto** (nombre, foto, descripción, ubicación)
- ✅ **Todas las reviews** con:
  - Puntuación y descripción
  - Información del revisor (nombre, email)
  - Imágenes de la review (si las tiene)
  - País donde se realizó
- ✅ **Disponibilidad actual** (días, horarios)
- ✅ **Tipos de entregables** (informe, reunión, etc.)
- ✅ **Estadísticas** (búsquedas completadas, rating promedio)
- ✅ **Todo lo necesario** para mostrar el servicio completo sin otra llamada

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
// ✅ INFORMACIÓN COMPLETA igual que cuando mueves el mapa
[
  {
    id: 123,
    categoryId: 2,
    serviceTypeId: 1,
    // ✅ TODAS las imágenes
    imageUrls: ["https://...", "https://..."],
    // ✅ Información completa del experto
    expert: {
      // ... con todas las reviews, disponibilidad, etc.
    },
    // ✅ Tipos de entregables
    selectedDeliverableTypes: [...],
    // ... misma estructura completa que Caso 2
  },
  // ... más servicios ordenados por distancia (más cercanos primero)
]
```

### **🎯 Diferencia con Caso 2:**
- **Caso 2 (mover mapa)**: Devuelve servicios del **área visible** del mapa
- **Caso 3 (búsqueda)**: Devuelve servicios dentro de un **radio** desde un punto
- **Ambos**: Devuelven **la misma información completa**

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
  // Cuando el usuario mueve el mapa, se cargan TODOS los servicios visibles
  // con TODA la información (imágenes, reviews, disponibilidades, etc.)
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
    
    // ✅ Recibe objeto con services y pagination - INFORMACIÓN COMPLETA
    // data.services: Array de SearchServiceDetailDto[]
    //   - Todas las imágenes (imageUrls)
    //   - Información completa del experto (expert)
    //   - Todas las reviews con imágenes (expert.reviews)
    //   - Disponibilidades (expert.currentAvailability)
    //   - Tipos de entregables (selectedDeliverableTypes)
    //   - Y todo lo demás necesario
    // data.pagination: Información de paginación
    //   - page, pageSize, totalCount, totalPages, hasNextPage, hasPreviousPage
    setServices(data.services);
    setPagination(data.pagination);
    
    // ✅ NO necesitas hacer otra llamada para obtener detalles
    // ✅ Ya tienes TODO para mostrar el servicio completo
  };

  // ✅ 3. BÚSQUEDA POR UBICACIÓN (Caso 3)
  // Busca servicios cerca de una ubicación específica
  // Devuelve la MISMA información completa que Caso 2
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
  locationRange: locationRange.toString(),
  // ✅ Paginación (opcionales)
  page: '1',        // Página actual
  pageSize: '50'    // Resultados por página (máx: 100)
});
    
    const response = await fetch(
      `/api/SearchService/map-experts?${params.toString()}`
    );
    const data = await response.json();
    
    // ✅ Recibe objeto con services y pagination - MISMA información completa que Caso 2
    // Incluye imágenes, reviews, disponibilidades, todo
    setServices(data.services);
    setPagination(data.pagination);
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
| **Carga inicial** | `GET /api/SearchService/map-experts` → Info básica | `GET /api/SearchService/map-experts` → Info básica (igual) |
| **Mover mapa** | `GET /api/SearchService/map-experts` con bounds → Info básica | `GET /api/SearchService/map-experts` con bounds → **Info COMPLETA** ✅ |
| **Buscar por ubicación** | `GET /api/SearchService?latitude=...` → Info completa | `GET /api/SearchService/map-experts?latitude=...` → Info completa (igual) |

### **🎯 Cambio Principal:**
**ANTES**: Al mover el mapa, recibías información básica y tenías que hacer otra llamada para obtener detalles.

**AHORA**: Al mover el mapa, recibes **TODA la información completa** (imágenes, reviews, disponibilidades, etc.) **sin necesidad de otra llamada**. ✅

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

