# 🗺️ Guía de Implementación Frontend: Mapa Estilo Airbnb

## 📋 Resumen

El backend ahora soporta búsqueda dinámica por **bounds del mapa** (como Airbnb). Al mover el mapa, puedes cargar solo los servicios visibles en el área actual.

---

## 🔌 Endpoint: `GET /api/searchservice/map-experts`

### **URL Base:**
```
GET /api/searchservice/map-experts
```

### **Parámetros:**

| Parámetro | Tipo | Requerido | Descripción | Ejemplo |
|-----------|------|-----------|-------------|---------|
| `categoryId` | `number` | ✅ Sí | ID de la categoría | `1` |
| `serviceTypeId` | `number` | ✅ Sí | ID del tipo de servicio | `2` |
| `northeastLat` | `number` | ❌ No | Latitud del punto noreste del mapa | `40.5` |
| `northeastLng` | `number` | ❌ No | Longitud del punto noreste del mapa | `-3.6` |
| `southwestLat` | `number` | ❌ No | Latitud del punto suroeste del mapa | `40.3` |
| `southwestLng` | `number` | ❌ No | Longitud del punto suroeste del mapa | `-3.8` |
| `zoom` | `number` | ❌ No | Nivel de zoom del mapa | `12` |
| `limit` | `number` | ❌ No | Límite máximo de resultados (default: 100) | `50` |

---

## 🎯 Estrategia de Implementación

### **1. Carga Inicial (Sin Bounds)**

Cuando el usuario entra al mapa por primera vez:

```typescript
// Carga inicial - sin bounds
const loadInitialServices = async (categoryId: number, serviceTypeId: number) => {
  const response = await fetch(
    `/api/searchservice/map-experts?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}`
  );
  const data = await response.json();
  
  // data.experts contiene todos los servicios disponibles
  // Mostrar el primer servicio por defecto
  if (data.experts.length > 0) {
    setSelectedService(data.experts[0]);
    addMarkersToMap(data.experts);
  }
};
```

### **2. Búsqueda Dinámica (Con Bounds) - Al Mover el Mapa**

Cuando el usuario mueve o hace zoom en el mapa:

```typescript
// ⚠️ IMPORTANTE: Usar debouncing para evitar demasiadas llamadas
let debounceTimer: NodeJS.Timeout | null = null;

const handleMapMove = (map: any) => {
  // Limpiar timer anterior
  if (debounceTimer) {
    clearTimeout(debounceTimer);
  }
  
  // Esperar 300-500ms después de que el usuario deje de mover el mapa
  debounceTimer = setTimeout(async () => {
    await loadServicesInBounds(map, categoryId, serviceTypeId);
  }, 300); // 300ms de debounce
};

const loadServicesInBounds = async (
  map: any, 
  categoryId: number, 
  serviceTypeId: number
) => {
  // Obtener bounds del mapa visible
  const bounds = map.getBounds();
  const northeast = bounds.getNorthEast();
  const southwest = bounds.getSouthWest();
  const zoom = map.getZoom();
  
  // Construir URL con bounds
  const params = new URLSearchParams({
    categoryId: categoryId.toString(),
    serviceTypeId: serviceTypeId.toString(),
    northeastLat: northeast.lat.toString(),
    northeastLng: northeast.lng.toString(),
    southwestLat: southwest.lat.toString(),
    southwestLng: southwest.lng.toString(),
    zoom: zoom.toString(),
    limit: '50' // Límite recomendado para móvil
  });
  
  try {
    const response = await fetch(
      `/api/searchservice/map-experts?${params.toString()}`
    );
    const data = await response.json();
    
    // Actualizar marcadores en el mapa
    updateMapMarkers(data.experts);
    
    // En móvil: mostrar primer servicio
    if (data.experts.length > 0 && isMobile) {
      setSelectedService(data.experts[0]);
    }
    
    // En desktop: actualizar lista
    if (!isMobile) {
      setServicesList(data.experts);
    }
  } catch (error) {
    console.error('Error loading services:', error);
  }
};
```

---

## 📱 Implementación Completa: React + Google Maps

### **Componente Completo:**

```typescript
import React, { useState, useEffect, useRef } from 'react';
import { GoogleMap, LoadScript, Marker, InfoWindow } from '@react-google-maps/api';

interface Expert {
  id: number;
  name: string;
  profilePictureUrl: string;
  averageRating: number;
  totalReviews: number;
  latitude: string;
  longitude: string;
  price: number;
  serviceDescription: string;
  serviceTypeName: string;
}

interface MapExpertsResponse {
  experts: Expert[];
  totalCount: number;
}

const ExpertsMap: React.FC<{
  categoryId: number;
  serviceTypeId: number;
}> = ({ categoryId, serviceTypeId }) => {
  const [experts, setExperts] = useState<Expert[]>([]);
  const [selectedExpert, setSelectedExpert] = useState<Expert | null>(null);
  const [mapCenter, setMapCenter] = useState({ lat: 40.4168, lng: -3.7038 }); // Madrid por defecto
  const [isLoading, setIsLoading] = useState(false);
  const mapRef = useRef<google.maps.Map | null>(null);
  const debounceTimerRef = useRef<NodeJS.Timeout | null>(null);

  // ✅ 1. Carga inicial (sin bounds)
  useEffect(() => {
    loadInitialServices();
  }, [categoryId, serviceTypeId]);

  const loadInitialServices = async () => {
    setIsLoading(true);
    try {
      const response = await fetch(
        `/api/searchservice/map-experts?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}`
      );
      const data: MapExpertsResponse = await response.json();
      
      setExperts(data.experts);
      
      // Mostrar primer servicio por defecto
      if (data.experts.length > 0) {
        setSelectedExpert(data.experts[0]);
        // Centrar mapa en el primer servicio
        setMapCenter({
          lat: parseFloat(data.experts[0].latitude),
          lng: parseFloat(data.experts[0].longitude)
        });
      }
    } catch (error) {
      console.error('Error loading initial services:', error);
    } finally {
      setIsLoading(false);
    }
  };

  // ✅ 2. Carga dinámica al mover el mapa (con bounds)
  const handleMapMove = () => {
    // Limpiar timer anterior
    if (debounceTimerRef.current) {
      clearTimeout(debounceTimerRef.current);
    }
    
    // Esperar 300ms después de que el usuario deje de mover el mapa
    debounceTimerRef.current = setTimeout(() => {
      loadServicesInBounds();
    }, 300);
  };

  const loadServicesInBounds = async () => {
    if (!mapRef.current) return;
    
    setIsLoading(true);
    
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
        `/api/searchservice/map-experts?${params.toString()}`
      );
      const data: MapExpertsResponse = await response.json();
      
      setExperts(data.experts);
      
      // En móvil: mostrar primer servicio si no hay uno seleccionado
      if (data.experts.length > 0 && !selectedExpert && window.innerWidth < 768) {
        setSelectedExpert(data.experts[0]);
      }
    } catch (error) {
      console.error('Error loading services in bounds:', error);
    } finally {
      setIsLoading(false);
    }
  };

  // ✅ 3. Manejar click en marcador
  const handleMarkerClick = (expert: Expert) => {
    setSelectedExpert(expert);
    // Centrar mapa en el servicio seleccionado
    setMapCenter({
      lat: parseFloat(expert.latitude),
      lng: parseFloat(expert.longitude)
    });
  };

  return (
    <div style={{ position: 'relative', width: '100%', height: '100vh' }}>
      <LoadScript googleMapsApiKey="TU_API_KEY">
        <GoogleMap
          mapContainerStyle={{ width: '100%', height: '100%' }}
          center={mapCenter}
          zoom={12}
          onLoad={(map) => {
            mapRef.current = map;
          }}
          onDragEnd={handleMapMove}
          onZoomChanged={handleMapMove}
        >
          {/* Marcadores de precio */}
          {experts.map((expert) => (
            <Marker
              key={expert.id}
              position={{
                lat: parseFloat(expert.latitude),
                lng: parseFloat(expert.longitude)
              }}
              onClick={() => handleMarkerClick(expert)}
              label={{
                text: `€${expert.price.toFixed(0)}`,
                color: 'white',
                fontSize: '12px',
                fontWeight: 'bold'
              }}
              icon={{
                url: 'data:image/svg+xml;charset=UTF-8,' + encodeURIComponent(`
                  <svg width="60" height="60" xmlns="http://www.w3.org/2000/svg">
                    <circle cx="30" cy="30" r="25" fill="#FF385C" stroke="white" stroke-width="2"/>
                  </svg>
                `),
                scaledSize: new google.maps.Size(60, 60),
                anchor: new google.maps.Point(30, 30)
              }}
            />
          ))}
          
          {/* InfoWindow al hacer click */}
          {selectedExpert && (
            <InfoWindow
              position={{
                lat: parseFloat(selectedExpert.latitude),
                lng: parseFloat(selectedExpert.longitude)
              }}
              onCloseClick={() => setSelectedExpert(null)}
            >
              <div style={{ padding: '10px', maxWidth: '200px' }}>
                <img
                  src={selectedExpert.profilePictureUrl}
                  alt={selectedExpert.name}
                  style={{ width: '100%', borderRadius: '8px', marginBottom: '8px' }}
                />
                <h3 style={{ margin: '0 0 4px 0', fontSize: '16px' }}>
                  {selectedExpert.name}
                </h3>
                <p style={{ margin: '0 0 4px 0', color: '#666', fontSize: '14px' }}>
                  ⭐ {selectedExpert.averageRating.toFixed(1)} ({selectedExpert.totalReviews})
                </p>
                <p style={{ margin: '0', fontSize: '18px', fontWeight: 'bold', color: '#FF385C' }}>
                  €{selectedExpert.price.toFixed(2)}
                </p>
              </div>
            </InfoWindow>
          )}
        </GoogleMap>
      </LoadScript>
      
      {/* Loading indicator */}
      {isLoading && (
        <div style={{
          position: 'absolute',
          top: '20px',
          left: '50%',
          transform: 'translateX(-50%)',
          padding: '10px 20px',
          background: 'white',
          borderRadius: '8px',
          boxShadow: '0 2px 8px rgba(0,0,0,0.1)'
        }}>
          Cargando servicios...
        </div>
      )}
    </div>
  );
};

export default ExpertsMap;
```

---

## 📱 Diferencias Móvil vs Desktop

### **Móvil:**
```typescript
const isMobile = window.innerWidth < 768;

// Mostrar UN servicio a la vez (card inferior)
if (isMobile && experts.length > 0) {
  // Card inferior con servicio seleccionado
  return (
    <div>
      <Map />
      <ServiceCard service={selectedExpert} />
    </div>
  );
}
```

### **Desktop:**
```typescript
// Mostrar LISTA de servicios (sidebar izquierdo)
if (!isMobile) {
  return (
    <div style={{ display: 'flex' }}>
      <ServiceList services={experts} style={{ width: '400px' }} />
      <Map style={{ flex: 1 }} />
    </div>
  );
}
```

---

## ⚡ Optimizaciones Importantes

### **1. Debouncing (CRÍTICO)**
```typescript
// ✅ CORRECTO: Esperar 300ms después de mover el mapa
setTimeout(() => loadServices(), 300);

// ❌ INCORRECTO: Llamar en cada movimiento
map.on('move', () => loadServices()); // ❌ Demasiadas llamadas
```

### **2. Límite de Resultados**
```typescript
// Móvil: menos resultados
const limit = isMobile ? 30 : 50;

// Zoom alto: más resultados
const limit = zoom >= 15 ? 100 : zoom >= 12 ? 50 : 30;
```

### **3. Cache de Resultados**
```typescript
// Cachear resultados por área para evitar llamadas repetidas
const cache = new Map<string, Expert[]>();

const getCacheKey = (bounds: any) => {
  return `${bounds.getNorthEast().lat()}_${bounds.getNorthEast().lng()}_${bounds.getSouthWest().lat()}_${bounds.getSouthWest().lng()}`;
};
```

---

## 📊 Estructura de Respuesta

```typescript
interface MapExpertsResponse {
  experts: Expert[];
  totalCount: number;
}

interface Expert {
  id: number;
  name: string;
  profilePictureUrl: string;
  averageRating: number;
  totalReviews: number;
  completedSearches: number;
  registeredSince: string;
  latitude: string;
  longitude: string;
  price: number; // ✅ Precio del servicio
  serviceDescription: string;
  serviceTypeName: string;
  serviceTypeDescription: string;
  currentAvailability?: {
    id: number;
    daysOfWeek: string[];
    startTime: string;
    endTime: string;
    effectiveFrom: string;
  };
}
```

---

## 🎯 Checklist de Implementación

- [ ] Implementar carga inicial (sin bounds)
- [ ] Implementar debouncing (300ms)
- [ ] Obtener bounds del mapa al mover
- [ ] Llamar API con bounds
- [ ] Actualizar marcadores en el mapa
- [ ] Mostrar servicio por defecto al cargar
- [ ] Manejar click en marcadores
- [ ] Diferenciar móvil vs desktop
- [ ] Agregar loading indicators
- [ ] Manejar errores

---

## 🚀 Ejemplo de Uso Completo

```typescript
// 1. Carga inicial
await loadInitialServices(categoryId, serviceTypeId);

// 2. Al mover el mapa (con debounce)
map.on('moveend', debounce(() => {
  const bounds = map.getBounds();
  await loadServicesInBounds(bounds, categoryId, serviceTypeId);
}, 300));

// 3. Click en marcador
marker.on('click', () => {
  showServiceDetails(expert);
});
```

---

## ✅ Listo para Implementar

El backend está optimizado y listo. Solo necesitas:
1. Implementar debouncing
2. Obtener bounds del mapa
3. Llamar al endpoint con los parámetros correctos
4. Actualizar la UI según la respuesta

¡Todo funcionará como Airbnb! 🎉

