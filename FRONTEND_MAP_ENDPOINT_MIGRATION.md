# 🔄 Migración de Endpoint: GET /api/SearchService → GET /api/SearchService/map-experts

## ⚠️ CAMBIO IMPORTANTE

**El endpoint anterior `GET /api/SearchService` con parámetros `latitude`, `longitude` y `locationRange` ya NO existe.**

Ahora **TODO** se maneja a través del endpoint unificado:
```
GET /api/SearchService/map-experts
```

---

## 🎯 Nuevo Endpoint Unificado

### Endpoint Base
```
GET /api/SearchService/map-experts
```

Este endpoint funciona en **3 modos diferentes** según los parámetros que envíes:

---

## 📋 Modo 1: Carga Inicial (Información Básica)

### **Cuándo usar:**
- Al cargar el mapa por primera vez
- Para mostrar todos los expertos disponibles
- Para colocar marcadores iniciales en el mapa

### **Llamada:**
```typescript
const response = await fetch(
  `/api/SearchService/map-experts?categoryId=2&serviceTypeId=1`
);
```

### **Parámetros:**
- ✅ `categoryId` (requerido)
- ✅ `serviceTypeId` (requerido)
- ❌ NO incluir bounds
- ❌ NO incluir latitude/longitude/locationRange

### **Qué devuelve:**
```typescript
{
  experts: ExpertMapDto[],  // Información básica
  totalCount: number
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

### **Uso:**
- Mostrar todos los marcadores en el mapa
- Seleccionar el primer servicio por defecto
- Información básica para el mapa

---

## 📋 Modo 2: Al Mover el Mapa (Información Completa)

### **Cuándo usar:**
- Cuando el usuario mueve o hace zoom en el mapa
- Para cargar servicios **dinámicamente** según el área visible
- **Carga automáticamente** todo lo que está visible en el mapa mientras te desplazas
- **REEMPLAZA** el comportamiento anterior de `GET /api/SearchService` con bounds

### **🎯 Funcionamiento:**
Cada vez que el usuario mueve el mapa, el frontend envía los **bounds** (límites del área visible) y el backend devuelve **solo los servicios que están dentro de esa área**, con **información completa**. Esto permite:
- ✅ Cargar servicios dinámicamente mientras te mueves
- ✅ Mostrar solo lo visible (optimización)
- ✅ Obtener información completa sin necesidad de otra llamada
- ✅ Funcionar como Airbnb: se carga todo según te desplazas

### **⚠️ IMPORTANTE: Usar Debouncing Optimizado**
```typescript
// ✅ DEBOUNCING INTELIGENTE (recomendado para mejor performance)
const debounceTimer = useRef<NodeJS.Timeout | null>(null);
const lastBounds = useRef<any>(null);

const handleMapMove = () => {
  if (debounceTimer.current) {
    clearTimeout(debounceTimer.current);
  }
  
  const currentBounds = map.getBounds();
  const currentZoom = map.getZoom();
  
  // Si el zoom cambió significativamente, cargar inmediatamente
  if (lastBounds.current && 
      Math.abs(currentZoom - lastBounds.current.zoom) > 2) {
    loadServicesInBounds();
    lastBounds.current = { bounds: currentBounds, zoom: currentZoom };
    return;
  }
  
  // Debounce adaptativo según velocidad de movimiento
  let debounceDelay = 300; // Default
  if (lastBounds.current) {
    const boundsChanged = calculateBoundsChange(currentBounds, lastBounds.current.bounds);
    if (boundsChanged > 0.5) {
      debounceDelay = 150; // Cambio grande: menos delay
    } else if (boundsChanged < 0.1) {
      debounceDelay = 500; // Cambio pequeño: más delay
    }
  }
  
  debounceTimer.current = setTimeout(() => {
    loadServicesInBounds();
    lastBounds.current = { bounds: currentBounds, zoom: currentZoom };
  }, debounceDelay);
};

// Función auxiliar para calcular cambio de bounds
const calculateBoundsChange = (bounds1, bounds2) => {
  const latDiff = Math.abs(bounds1.getNorth() - bounds2.getNorth()) + 
                  Math.abs(bounds1.getSouth() - bounds2.getSouth());
  const lngDiff = Math.abs(bounds1.getEast() - bounds2.getEast()) + 
                  Math.abs(bounds1.getWest() - bounds2.getWest());
  return (latDiff + lngDiff) / 2;
};

// ✅ DEBOUNCING SIMPLE (mínimo requerido)
let timer;
map.on('moveend', () => {
  clearTimeout(timer);
  timer = setTimeout(() => {
    loadServicesInBounds();
  }, 300); // ⚠️ CRÍTICO: Mínimo 300ms de debounce
});
```

### **Llamada:**
```typescript
// 1. Obtener bounds del mapa
const bounds = map.getBounds();
const northeast = bounds.getNorthEast();
const southwest = bounds.getSouthWest();
const zoom = map.getZoom();

// 2. Construir URL
const params = new URLSearchParams({
  categoryId: '2',
  serviceTypeId: '1',
  northeastLat: northeast.lat().toString(),
  northeastLng: northeast.lng().toString(),
  southwestLat: southwest.lat().toString(),
  southwestLng: southwest.lng().toString(),
  zoom: zoom.toString(),
  limit: '50'
});

// 3. Llamar endpoint
const response = await fetch(
  `/api/SearchService/map-experts?${params.toString()}`
);
```

### **Parámetros:**
- ✅ `categoryId` (requerido)
- ✅ `serviceTypeId` (requerido)
- ✅ `northeastLat` (latitud del punto noreste del mapa visible)
- ✅ `northeastLng` (longitud del punto noreste del mapa visible)
- ✅ `southwestLat` (latitud del punto suroeste del mapa visible)
- ✅ `southwestLng` (longitud del punto suroeste del mapa visible)
- ✅ `zoom` (nivel de zoom del mapa, opcional pero recomendado)
- ✅ `limit` (máximo de resultados, recomendado: 30-50)

### **Qué devuelve:**
```typescript
SearchServiceDetailDto[]  // Información COMPLETA

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
  expert: ExpertProfileDto;  // Información completa del experto
  selectedDeliverableTypes: DeliverableTypeDto[];
  categoryName: string;
  completedSearches: number;
  averageRating: number;
  // ... más campos
}
```

### **Uso:**
- ✅ Filtrar servicios por el área visible del mapa
- ✅ Cargar información completa cuando el usuario se mueve
- ✅ Actualizar marcadores dinámicamente
- ✅ **Cargar automáticamente todo lo visible** mientras te desplazas por el mapa
- ✅ Funciona como Airbnb: se carga todo según te mueves

---

## 📋 Modo 3: Búsqueda por Ubicación (Información Completa)

### **Cuándo usar:**
- Cuando quieres buscar servicios cerca de una ubicación específica
- **REEMPLAZA** el endpoint anterior `GET /api/SearchService?latitude=...&longitude=...&locationRange=...`
- Para búsquedas basadas en coordenadas y radio

### **Llamada:**
```typescript
const params = new URLSearchParams({
  categoryId: '2',
  serviceTypeId: '1',
  latitude: '40.4168',
  longitude: '-3.7038',
  locationRange: '25'
});

const response = await fetch(
  `/api/SearchService/map-experts?${params.toString()}`
);
```

### **Parámetros:**
- ✅ `categoryId` (requerido)
- ✅ `serviceTypeId` (requerido)
- ✅ `latitude` (latitud del punto de búsqueda)
- ✅ `longitude` (longitud del punto de búsqueda)
- ✅ `locationRange` (rango de búsqueda en km)

### **Qué devuelve:**
```typescript
SearchServiceDetailDto[]  // Información COMPLETA (igual que Modo 2)
```

### **Uso:**
- Buscar servicios cerca de una ubicación específica
- Filtrar por distancia desde un punto
- Reemplaza completamente el endpoint anterior

---

## 🔄 Migración desde el Endpoint Anterior

### ❌ Endpoint Anterior (OBSOLETO)
```typescript
// ❌ YA NO FUNCIONA
const response = await fetch(
  `/api/SearchService?categoryId=2&serviceTypeId=1&latitude=40.4168&longitude=-3.7038&locationRange=25`
);
```

### ✅ Nuevo Endpoint (ACTUAL)
```typescript
// ✅ USA ESTE
const response = await fetch(
  `/api/SearchService/map-experts?categoryId=2&serviceTypeId=1&latitude=40.4168&longitude=-3.7038&locationRange=25`
);
```

**Los parámetros son exactamente los mismos**, solo cambia la URL del endpoint.

---

## 💻 Ejemplo Completo de Implementación

```typescript
import { useState, useEffect, useRef } from 'react';

interface SearchServiceDetailDto {
  id: number;
  categoryId: number;
  serviceTypeId: number;
  price: number;
  imageUrls: string[];
  expert: {
    id: number;
    name: string;
    profilePictureUrl: string;
    latitude: string;
    longitude: string;
    reviews: any[];
    // ... más campos
  };
  // ... más campos
}

interface ExpertMapDto {
  id: number;
  name: string;
  latitude: string;
  longitude: string;
  price: number;
  // ... más campos
}

const ExpertsMap = ({ categoryId, serviceTypeId }) => {
  const [services, setServices] = useState<SearchServiceDetailDto[]>([]);
  const [experts, setExperts] = useState<ExpertMapDto[]>([]);
  const [selectedService, setSelectedService] = useState<SearchServiceDetailDto | null>(null);
  const mapRef = useRef<any>(null);
  const debounceTimer = useRef<NodeJS.Timeout | null>(null);

  // ✅ 1. CARGA INICIAL (Modo 1: Información Básica)
  useEffect(() => {
    loadInitialServices();
  }, [categoryId, serviceTypeId]);

  const loadInitialServices = async () => {
    try {
      const response = await fetch(
        `/api/SearchService/map-experts?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}`
      );
      const data = await response.json();
      
      // Devuelve ExpertMapResponseDto
      setExperts(data.experts);
      
      // Mostrar primer servicio por defecto
      if (data.experts.length > 0) {
        // Cargar información completa del primer servicio
        loadServiceDetails(data.experts[0].id);
      }
    } catch (error) {
      console.error('Error loading initial services:', error);
    }
  };

  // ✅ 2. AL MOVER EL MAPA (Modo 2: Información Completa con Bounds)
  const handleMapMove = () => {
    // Limpiar timer anterior
    if (debounceTimer.current) {
      clearTimeout(debounceTimer.current);
    }
    
    // ⚠️ DEBOUNCING: Esperar 300ms
    debounceTimer.current = setTimeout(() => {
      loadServicesInBounds();
    }, 300);
  };

  const loadServicesInBounds = async () => {
    if (!mapRef.current) return;
    
    try {
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
      
      // Devuelve SearchServiceDetailDto[]
      setServices(data);
      
      // Actualizar marcadores
      updateMarkers(data);
      
      // En móvil: mostrar primer servicio del área
      if (data.length > 0 && window.innerWidth < 768) {
        setSelectedService(data[0]);
      }
    } catch (error) {
      console.error('Error loading services in bounds:', error);
    }
  };

  // ✅ 3. BÚSQUEDA POR UBICACIÓN (Modo 3: Información Completa con Location)
  const searchByLocation = async (latitude: string, longitude: string, locationRange: number) => {
    try {
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
      
      // Devuelve SearchServiceDetailDto[]
      setServices(data);
      updateMarkers(data);
    } catch (error) {
      console.error('Error searching by location:', error);
    }
  };

  // ✅ 4. CARGAR DETALLES DE UN SERVICIO ESPECÍFICO
  const loadServiceDetails = async (serviceId: number) => {
    try {
      const response = await fetch(`/api/SearchService/${serviceId}`);
      const data = await response.json();
      setSelectedService(data);
    } catch (error) {
      console.error('Error loading service details:', error);
    }
  };

  return (
    <GoogleMap
      onLoad={(map) => { mapRef.current = map; }}
      onDragEnd={handleMapMove}      // ✅ Al terminar de arrastrar
      onZoomChanged={handleMapMove}  // ✅ Al cambiar zoom
    >
      {/* Marcadores de expertos (carga inicial) */}
      {experts.map((expert) => (
        <Marker
          key={expert.id}
          position={{
            lat: parseFloat(expert.latitude),
            lng: parseFloat(expert.longitude)
          }}
          label={`€${expert.price.toFixed(0)}`}
          onClick={() => loadServiceDetails(expert.id)}
        />
      ))}
      
      {/* Marcadores de servicios (al mover mapa) */}
      {services.map((service) => (
        <Marker
          key={service.id}
          position={{
            lat: parseFloat(service.expert.latitude),
            lng: parseFloat(service.expert.longitude)
          }}
          label={`€${service.price.toFixed(0)}`}
          onClick={() => setSelectedService(service)}
        />
      ))}
    </GoogleMap>
  );
};
```

---

## 📊 Resumen de los 3 Modos

| Modo | Parámetros | Devuelve | Uso |
|------|-----------|----------|-----|
| **1. Carga Inicial** | `categoryId`, `serviceTypeId` | `ExpertMapResponseDto` (básico) | Mostrar todos los expertos al inicio |
| **2. Mover Mapa** | `categoryId`, `serviceTypeId`, `northeastLat`, `northeastLng`, `southwestLat`, `southwestLng`, `zoom`, `limit` | `SearchServiceDetailDto[]` (completo) | Cargar servicios del área visible |
| **3. Búsqueda por Ubicación** | `categoryId`, `serviceTypeId`, `latitude`, `longitude`, `locationRange` | `SearchServiceDetailDto[]` (completo) | Buscar servicios cerca de un punto |

---

## ⚠️ Puntos Críticos

### 1. **Debouncing OBLIGATORIO** (Modo 2)
```typescript
// ✅ CORRECTO
setTimeout(() => loadServicesInBounds(), 300);

// ❌ INCORRECTO (demasiadas llamadas)
map.on('move', () => loadServicesInBounds());
```

### 2. **Todos los Bounds o Ninguno** (Modo 2)
```typescript
// ✅ CORRECTO: Todos los bounds
northeastLat, northeastLng, southwestLat, southwestLng

// ❌ INCORRECTO: Bounds parciales
northeastLat, northeastLng // ❌ Falta southwest
```

### 3. **LocationRange Requerido** (Modo 3)
```typescript
// ✅ CORRECTO: Todos los parámetros de ubicación
latitude, longitude, locationRange

// ❌ INCORRECTO: Faltan parámetros
latitude, longitude // ❌ Falta locationRange
```

---

## ✅ Checklist de Migración

- [ ] Reemplazar todas las llamadas a `GET /api/SearchService` por `GET /api/SearchService/map-experts`
- [ ] Mantener los mismos parámetros (`categoryId`, `serviceTypeId`, `latitude`, `longitude`, `locationRange`)
- [ ] Implementar carga inicial sin parámetros (Modo 1)
- [ ] Implementar carga con bounds al mover mapa (Modo 2) con debouncing
- [ ] Actualizar tipos TypeScript según el modo de respuesta
- [ ] Probar los 3 modos de funcionamiento

---

## 🚀 Listo

El endpoint unificado `map-experts` ahora maneja **TODO**:
- ✅ Carga inicial del mapa
- ✅ Desplazamiento dinámico por el mapa
- ✅ Búsqueda por ubicación

**¡Ya no necesitas el endpoint anterior!** 🎉

---

## 📚 Optimizaciones Adicionales

Para manejar **miles de servicios** sin problemas de rendimiento, consulta:
- **`MAP_PERFORMANCE_OPTIMIZATION_GUIDE.md`** - Guía completa de optimizaciones profesionales
  - Índices espaciales PostGIS (100-1000x más rápido)
  - Clustering de marcadores
  - Caché Redis
  - Y muchas más optimizaciones

**Recomendación**: Implementar al menos las optimizaciones de la **Fase 1** (críticas) antes de producción con muchos datos.

