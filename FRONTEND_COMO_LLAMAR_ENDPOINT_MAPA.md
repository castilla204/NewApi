# 📱 Frontend: Cómo Llamar al Endpoint del Mapa

## ⚠️ IMPORTANTE: Cambio de Endpoint

**El endpoint anterior `GET /api/SearchService` con `latitude`, `longitude` y `locationRange` ya NO existe.**

Ahora **TODO** se maneja a través del endpoint unificado:
```
GET /api/SearchService/map-experts
```

📖 **Ver documentación completa de migración:** `FRONTEND_MAP_ENDPOINT_MIGRATION.md`

---

## 🎯 Resumen Rápido

El endpoint funciona en **3 modos**:
1. **Carga inicial** (sin parámetros) → Información básica de todos los expertos
2. **Al mover mapa** (con bounds) → Información completa de servicios visibles
3. **Búsqueda por ubicación** (con latitude/longitude/locationRange) → Información completa filtrada por distancia

---

## 🔌 Endpoint

```
GET /api/SearchService/map-experts
```

---

## 📋 Modo 1: Carga Inicial (SIN bounds)

### **Cuándo:** Al entrar al mapa por primera vez

### **Llamada:**
```typescript
const response = await fetch(
  `/api/searchservice/map-experts?categoryId=1&serviceTypeId=2`
);
```

### **Parámetros:**
- ✅ `categoryId` (requerido)
- ✅ `serviceTypeId` (requerido)
- ❌ NO incluir bounds

### **Qué devuelve:**
```typescript
{
  experts: ExpertMapDto[],  // Información básica
  totalCount: number
}
```
- **TODOS** los expertos disponibles (información básica)
- Úsalo para mostrar el primer servicio por defecto
- Coloca todos los marcadores en el mapa

---

## 📋 Modo 2: Al Mover el Mapa (CON bounds)

### **Cuándo:** Cuando el usuario mueve o hace zoom en el mapa

### **🎯 Funcionamiento:**
Cada vez que mueves el mapa, el endpoint recibe los **bounds** (límites del área visible) y devuelve **solo los servicios que están dentro de esa área**, con **información completa**. Esto permite cargar dinámicamente todo lo visible mientras te desplazas, igual que Airbnb.

### **⚠️ IMPORTANTE: Usar Debouncing**
```typescript
// Esperar 300ms después de que el usuario deje de mover
let timer;
map.on('moveend', () => {
  clearTimeout(timer);
  timer = setTimeout(() => {
    loadServicesInBounds();
  }, 300); // ⚠️ CRÍTICO: 300ms de debounce
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
  categoryId: '1',
  serviceTypeId: '2',
  northeastLat: northeast.lat().toString(),
  northeastLng: northeast.lng().toString(),
  southwestLat: southwest.lat().toString(),
  southwestLng: southwest.lng().toString(),
  zoom: zoom.toString(),
  limit: '50'
});

// 3. Llamar endpoint
const response = await fetch(
  `/api/searchservice/map-experts?${params.toString()}`
);
```

### **Parámetros:**
- ✅ `categoryId` (requerido)
- ✅ `serviceTypeId` (requerido)
- ✅ `northeastLat` (latitud del punto noreste)
- ✅ `northeastLng` (longitud del punto noreste)
- ✅ `southwestLat` (latitud del punto suroeste)
- ✅ `southwestLng` (longitud del punto suroeste)
- ✅ `zoom` (nivel de zoom)
- ✅ `limit` (máximo de resultados, recomendado: 30-50)

### **Qué devuelve:**
```typescript
SearchServiceDetailDto[]  // Información COMPLETA
```
- **SOLO** los servicios visibles en el área del mapa
- Máximo según el `limit` especificado
- Ordenados por distancia al centro del mapa
- **Información completa** (imágenes, reviews, disponibilidades, etc.)

---

## 💻 Código Completo de Ejemplo

```typescript
import { useState, useEffect, useRef } from 'react';

const ExpertsMap = ({ categoryId, serviceTypeId }) => {
  const [experts, setExperts] = useState([]);
  const [selectedExpert, setSelectedExpert] = useState(null);
  const mapRef = useRef(null);
  const debounceTimer = useRef(null);

  // ✅ 1. CARGA INICIAL (sin bounds)
  useEffect(() => {
    loadInitialServices();
  }, [categoryId, serviceTypeId]);

  const loadInitialServices = async () => {
    const response = await fetch(
      `/api/searchservice/map-experts?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}`
    );
    const data = await response.json();
    
    setExperts(data.experts);
    
    // Mostrar primer servicio por defecto
    if (data.experts.length > 0) {
      setSelectedExpert(data.experts[0]);
    }
  };

  // ✅ 2. AL MOVER EL MAPA (con bounds)
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
    const data = await response.json();
    
    // Actualizar marcadores
    setExperts(data.experts);
    
    // En móvil: mostrar primer servicio del área
    if (data.experts.length > 0 && window.innerWidth < 768) {
      setSelectedExpert(data.experts[0]);
    }
  };

  return (
    <GoogleMap
      onLoad={(map) => { mapRef.current = map; }}
      onDragEnd={handleMapMove}      // ✅ Al terminar de arrastrar
      onZoomChanged={handleMapMove}  // ✅ Al cambiar zoom
    >
      {/* Marcadores */}
      {experts.map((expert) => (
        <Marker
          key={expert.id}
          position={{
            lat: parseFloat(expert.latitude),
            lng: parseFloat(expert.longitude)
          }}
          label={`€${expert.price.toFixed(0)}`}
          onClick={() => setSelectedExpert(expert)}
        />
      ))}
    </GoogleMap>
  );
};
```

---

## 🔄 Flujo de Funcionamiento

### **Paso 1: Usuario Entra al Mapa**
```
1. Componente se monta
2. Llama: GET /api/searchservice/map-experts?categoryId=1&serviceTypeId=2
3. Recibe: TODOS los servicios
4. Muestra: Primer servicio por defecto
5. Coloca: Todos los marcadores
```

### **Paso 2: Usuario Mueve el Mapa**
```
1. Usuario arrastra el mapa
2. Se dispara: onDragEnd
3. Espera: 300ms (debouncing) ⚠️ IMPORTANTE
4. Obtiene: Bounds del mapa (northeast, southwest)
5. Llama: GET /api/searchservice/map-experts?
           categoryId=1&serviceTypeId=2&
           northeastLat=40.5&northeastLng=-3.6&
           southwestLat=40.3&southwestLng=-3.8&
           zoom=12&limit=50
6. Recibe: Solo servicios visibles (máx 50)
7. Actualiza: Marcadores en el mapa
```

### **Paso 3: Usuario Click en Marcador**
```
1. Usuario click en pin de precio
2. Muestra: Detalles del servicio
3. Centra: Mapa en el servicio
```

---

## ⚠️ Puntos Críticos

### **1. Debouncing (OBLIGATORIO)**
```typescript
// ✅ CORRECTO
setTimeout(() => loadServices(), 300);

// ❌ INCORRECTO (demasiadas llamadas)
map.on('move', () => loadServices());
```

### **2. Obtener Bounds Correctamente**
```typescript
// ✅ CORRECTO
const bounds = map.getBounds();
const northeast = bounds.getNorthEast();
const southwest = bounds.getSouthWest();

// ❌ INCORRECTO
const bounds = map.getCenter(); // ❌ No es bounds
```

### **3. Todos los Bounds o Ninguno**
```typescript
// ✅ CORRECTO: Todos los bounds
northeastLat, northeastLng, southwestLat, southwestLng

// ❌ INCORRECTO: Bounds parciales
northeastLat, northeastLng // ❌ Falta southwest
```

---

## 📊 Estructura de Respuesta

### Modo 1: Carga Inicial (sin bounds)
```typescript
{
  "experts": [
    {
      "id": 40,
      "name": "Diego Castilla",
      "profilePictureUrl": "https://...",
      "averageRating": 4.5,
      "totalReviews": 10,
      "latitude": "41.54660957575336",
      "longitude": "-0.9480622482776679",
      "price": 150.00,
      "serviceDescription": "...",
      "serviceTypeName": "Consulta"
    }
  ],
  "totalCount": 1
}
```

### Modo 2 y 3: Con bounds o ubicación (información completa)
```typescript
[
  {
    "id": 123,
    "categoryId": 2,
    "serviceTypeId": 1,
    "price": 150.00,
    "imageUrls": ["https://..."],
    "expert": {
      "id": 40,
      "name": "Diego Castilla",
      "profilePictureUrl": "https://...",
      "latitude": "41.54660957575336",
      "longitude": "-0.9480622482776679",
      "reviews": [...],
      // ... más información completa
    },
    // ... más campos completos
  }
]
```

📖 **Ver documentación completa:** `FRONTEND_MAP_ENDPOINT_MIGRATION.md`

---

## ✅ Checklist Rápido

- [ ] Carga inicial sin bounds
- [ ] Debouncing de 300ms
- [ ] Obtener bounds (northeast, southwest)
- [ ] Llamar endpoint con bounds al mover mapa
- [ ] Actualizar marcadores
- [ ] Mostrar servicio por defecto
- [ ] Manejar click en marcadores

---

## 🔄 Migración desde Endpoint Anterior

### ❌ Endpoint Anterior (OBSOLETO)
```typescript
// ❌ YA NO FUNCIONA
GET /api/SearchService?categoryId=2&serviceTypeId=1&latitude=40.4168&longitude=-3.7038&locationRange=25
```

### ✅ Nuevo Endpoint (ACTUAL)
```typescript
// ✅ USA ESTE (mismos parámetros, diferente URL)
GET /api/SearchService/map-experts?categoryId=2&serviceTypeId=1&latitude=40.4168&longitude=-3.7038&locationRange=25
```

📖 **Ver documentación completa de migración:** `FRONTEND_MAP_ENDPOINT_MIGRATION.md`

---

## 🚀 Listo

El backend está listo. Solo implementa:
1. Carga inicial (sin bounds) → Modo 1
2. Debouncing (300ms) → Modo 2
3. Obtener bounds al mover mapa → Modo 2
4. Búsqueda por ubicación → Modo 3

**¡Funcionará como Airbnb!** 🎉

