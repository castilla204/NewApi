# 🗺️ Guía Completa Frontend: Mapa Estilo Airbnb

## 📋 Resumen Rápido

El endpoint ahora soporta **búsqueda dinámica por bounds del mapa**. Al mover el mapa, solo cargas los servicios visibles en el área actual (como Airbnb).

---

## 🔌 Endpoint: Cómo Llamarlo

### **URL Base:**
```
GET /api/searchservice/map-experts
```

### **Parámetros:**

| Parámetro | Tipo | Requerido | Descripción |
|-----------|------|-----------|-------------|
| `categoryId` | `number` | ✅ **SÍ** | ID de la categoría |
| `serviceTypeId` | `number` | ✅ **SÍ** | ID del tipo de servicio |
| `northeastLat` | `number` | ❌ No | Latitud del punto **noreste** del mapa visible |
| `northeastLng` | `number` | ❌ No | Longitud del punto **noreste** del mapa visible |
| `southwestLat` | `number` | ❌ No | Latitud del punto **suroeste** del mapa visible |
| `southwestLng` | `number` | ❌ No | Longitud del punto **suroeste** del mapa visible |
| `zoom` | `number` | ❌ No | Nivel de zoom del mapa (afecta límite de resultados) |
| `limit` | `number` | ❌ No | Límite máximo (default: 100, max: 500) |

---

## 🎯 Dos Modos de Uso

### **1. Carga Inicial (SIN bounds) - Al Entrar al Mapa**

**Cuándo usar:** Cuando el usuario entra al mapa por primera vez.

**Llamada:**
```typescript
GET /api/searchservice/map-experts?categoryId=1&serviceTypeId=2
```

**Qué hace:**
- Devuelve **TODOS** los servicios disponibles
- Perfecto para mostrar el primer servicio por defecto
- Coloca todos los marcadores en el mapa

**Ejemplo de código:**
```typescript
const loadInitialServices = async (categoryId: number, serviceTypeId: number) => {
  const response = await fetch(
    `/api/searchservice/map-experts?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}`
  );
  const data = await response.json();
  
  // data.experts contiene todos los servicios
  // data.totalCount es el total
  
  // Mostrar primer servicio por defecto
  if (data.experts.length > 0) {
    setSelectedService(data.experts[0]);
  }
  
  // Colocar marcadores en el mapa
  addMarkersToMap(data.experts);
};
```

---

### **2. Búsqueda Dinámica (CON bounds) - Al Mover el Mapa**

**Cuándo usar:** Cuando el usuario mueve o hace zoom en el mapa.

**Llamada:**
```typescript
GET /api/searchservice/map-experts?
    categoryId=1&serviceTypeId=2&
    northeastLat=40.5&northeastLng=-3.6&
    southwestLat=40.3&southwestLng=-3.8&
    zoom=12&limit=50
```

**Qué hace:**
- Devuelve **SOLO** los servicios visibles en el área del mapa
- Mucho más rápido (no carga todos los servicios)
- Se actualiza automáticamente al mover el mapa

**Ejemplo de código:**
```typescript
const loadServicesInBounds = async (
  map: any, 
  categoryId: number, 
  serviceTypeId: number
) => {
  // 1. Obtener bounds del mapa visible
  const bounds = map.getBounds();
  const northeast = bounds.getNorthEast();
  const southwest = bounds.getSouthWest();
  const zoom = map.getZoom();
  
  // 2. Construir URL con todos los parámetros
  const params = new URLSearchParams({
    categoryId: categoryId.toString(),
    serviceTypeId: serviceTypeId.toString(),
    northeastLat: northeast.lat().toString(),
    northeastLng: northeast.lng().toString(),
    southwestLat: southwest.lat().toString(),
    southwestLng: southwest.lng().toString(),
    zoom: zoom.toString(),
    limit: '50' // Recomendado: 30-50 para móvil, 50-100 para desktop
  });
  
  // 3. Llamar al endpoint
  const response = await fetch(
    `/api/searchservice/map-experts?${params.toString()}`
  );
  const data = await response.json();
  
  // 4. Actualizar marcadores en el mapa
  updateMapMarkers(data.experts);
  
  // 5. En móvil: mostrar primer servicio del área
  if (data.experts.length > 0 && isMobile) {
    setSelectedService(data.experts[0]);
  }
};
```

---

## ⚠️ IMPORTANTE: Debouncing (CRÍTICO)

**NUNCA llames al endpoint en cada movimiento del mapa.** Usa debouncing para esperar 300-500ms después de que el usuario deje de mover el mapa.

### **❌ INCORRECTO (Demasiadas llamadas):**
```typescript
// ❌ MAL: Llamada en cada movimiento
map.on('move', () => {
  loadServicesInBounds(map); // ❌ Llamará cientos de veces
});
```

### **✅ CORRECTO (Con debouncing):**
```typescript
// ✅ BIEN: Esperar 300ms después de mover
let debounceTimer: NodeJS.Timeout | null = null;

map.on('moveend', () => {
  // Limpiar timer anterior
  if (debounceTimer) {
    clearTimeout(debounceTimer);
  }
  
  // Esperar 300ms después de que el usuario deje de mover
  debounceTimer = setTimeout(() => {
    loadServicesInBounds(map, categoryId, serviceTypeId);
  }, 300); // 300ms de debounce
});
```

---

## 📱 Implementación Completa: React + Google Maps

### **Componente Completo Funcional:**

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
  const [mapCenter, setMapCenter] = useState({ lat: 40.4168, lng: -3.7038 }); // Madrid
  const [isLoading, setIsLoading] = useState(false);
  const mapRef = useRef<google.maps.Map | null>(null);
  const debounceTimerRef = useRef<NodeJS.Timeout | null>(null);
  const isMobile = window.innerWidth < 768;

  // ✅ 1. CARGA INICIAL (sin bounds) - Al montar el componente
  useEffect(() => {
    loadInitialServices();
  }, [categoryId, serviceTypeId]);

  const loadInitialServices = async () => {
    setIsLoading(true);
    try {
      // Llamada SIN bounds
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

  // ✅ 2. CARGA DINÁMICA (con bounds) - Al mover el mapa
  const handleMapMove = () => {
    // Limpiar timer anterior
    if (debounceTimerRef.current) {
      clearTimeout(debounceTimerRef.current);
    }
    
    // ⚠️ DEBOUNCING: Esperar 300ms después de que el usuario deje de mover
    debounceTimerRef.current = setTimeout(() => {
      loadServicesInBounds();
    }, 300);
  };

  const loadServicesInBounds = async () => {
    if (!mapRef.current) return;
    
    setIsLoading(true);
    
    try {
      // Obtener bounds del mapa visible
      const bounds = mapRef.current.getBounds();
      if (!bounds) return;
      
      const northeast = bounds.getNorthEast();
      const southwest = bounds.getSouthWest();
      const zoom = mapRef.current.getZoom() || 12;
      
      // Construir parámetros
      const params = new URLSearchParams({
        categoryId: categoryId.toString(),
        serviceTypeId: serviceTypeId.toString(),
        northeastLat: northeast.lat().toString(),
        northeastLng: northeast.lng().toString(),
        southwestLat: southwest.lat().toString(),
        southwestLng: southwest.lng().toString(),
        zoom: zoom.toString(),
        limit: isMobile ? '30' : '50' // Menos resultados en móvil
      });
      
      // Llamar endpoint CON bounds
      const response = await fetch(
        `/api/searchservice/map-experts?${params.toString()}`
      );
      const data: MapExpertsResponse = await response.json();
      
      // Actualizar marcadores
      setExperts(data.experts);
      
      // En móvil: mostrar primer servicio si no hay uno seleccionado
      if (data.experts.length > 0 && !selectedExpert && isMobile) {
        setSelectedExpert(data.experts[0]);
      }
    } catch (error) {
      console.error('Error loading services in bounds:', error);
    } finally {
      setIsLoading(false);
    }
  };

  // ✅ 3. Click en marcador de precio
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
          onDragEnd={handleMapMove}      // ✅ Al terminar de arrastrar
          onZoomChanged={handleMapMove}  // ✅ Al cambiar zoom
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
      
      {/* Card de servicio (móvil) */}
      {isMobile && selectedExpert && (
        <div style={{
          position: 'absolute',
          bottom: '20px',
          left: '20px',
          right: '20px',
          background: 'white',
          borderRadius: '12px',
          padding: '16px',
          boxShadow: '0 4px 12px rgba(0,0,0,0.15)'
        }}>
          <img
            src={selectedExpert.profilePictureUrl}
            alt={selectedExpert.name}
            style={{ width: '100%', borderRadius: '8px', marginBottom: '12px' }}
          />
          <h3 style={{ margin: '0 0 8px 0' }}>{selectedExpert.name}</h3>
          <p style={{ margin: '0 0 8px 0', color: '#666' }}>
            ⭐ {selectedExpert.averageRating.toFixed(1)} ({selectedExpert.totalReviews} reseñas)
          </p>
          <p style={{ margin: '0', fontSize: '20px', fontWeight: 'bold', color: '#FF385C' }}>
            €{selectedExpert.price.toFixed(2)}
          </p>
        </div>
      )}
    </div>
  );
};

export default ExpertsMap;
```

---

## 🔄 Flujo Completo de Funcionamiento

### **Paso 1: Usuario Entra al Mapa**
```
1. Componente se monta
2. Llama: GET /api/searchservice/map-experts?categoryId=1&serviceTypeId=2
3. Recibe: TODOS los servicios
4. Muestra: Primer servicio por defecto
5. Coloca: Todos los marcadores en el mapa
```

### **Paso 2: Usuario Mueve el Mapa**
```
1. Usuario arrastra el mapa
2. Se dispara: onDragEnd
3. Espera: 300ms (debouncing)
4. Llama: GET /api/searchservice/map-experts?
           categoryId=1&serviceTypeId=2&
           northeastLat=40.5&northeastLng=-3.6&
           southwestLat=40.3&southwestLng=-3.8&
           zoom=12&limit=50
5. Recibe: Solo servicios visibles (máximo 50)
6. Actualiza: Marcadores en el mapa
7. Muestra: Primer servicio del área (móvil)
```

### **Paso 3: Usuario Hace Click en Marcador**
```
1. Usuario click en pin de precio
2. Muestra: InfoWindow con detalles
3. Centra: Mapa en el servicio
4. Actualiza: Servicio seleccionado
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
  latitude: string;        // ✅ Coordenada como string
  longitude: string;       // ✅ Coordenada como string
  price: number;           // ✅ Precio del servicio
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

## ⚡ Optimizaciones Recomendadas

### **1. Límite según Dispositivo:**
```typescript
const limit = isMobile ? 30 : 50; // Menos en móvil
```

### **2. Límite según Zoom:**
```typescript
const zoom = map.getZoom();
const limit = zoom >= 15 ? 100 : zoom >= 12 ? 50 : 30;
```

### **3. Cache de Resultados:**
```typescript
const cache = new Map<string, Expert[]>();

const getCacheKey = (bounds: any) => {
  const ne = bounds.getNorthEast();
  const sw = bounds.getSouthWest();
  return `${ne.lat()}_${ne.lng()}_${sw.lat()}_${sw.lng()}`;
};

// Antes de llamar, verificar cache
const cacheKey = getCacheKey(bounds);
if (cache.has(cacheKey)) {
  return cache.get(cacheKey);
}
```

---

## 🎯 Checklist de Implementación

- [ ] Implementar carga inicial (sin bounds)
- [ ] Implementar debouncing (300ms)
- [ ] Obtener bounds del mapa (northeast, southwest)
- [ ] Llamar endpoint con bounds al mover mapa
- [ ] Actualizar marcadores con nuevos servicios
- [ ] Mostrar servicio por defecto al cargar
- [ ] Manejar click en marcadores
- [ ] Diferenciar móvil vs desktop
- [ ] Agregar loading indicators
- [ ] Manejar errores de red

---

## ✅ Ejemplo de URL Completa

### **Carga Inicial:**
```
GET /api/searchservice/map-experts?categoryId=1&serviceTypeId=2
```

### **Al Mover Mapa:**
```
GET /api/searchservice/map-experts?
    categoryId=1&
    serviceTypeId=2&
    northeastLat=40.4168&
    northeastLng=-3.7038&
    southwestLat=40.3900&
    southwestLng=-3.7200&
    zoom=13&
    limit=50
```

---

## 🚀 Listo para Implementar

El backend está **100% listo**. Solo necesitas:
1. ✅ Llamar al endpoint con los parámetros correctos
2. ✅ Usar debouncing (300ms)
3. ✅ Obtener bounds del mapa
4. ✅ Actualizar la UI según la respuesta

**¡Todo funcionará como Airbnb!** 🎉

