# 🗺️ Explicación Completa: Cómo Funcionan las Llamadas del Mapa

## 📋 Resumen Ejecutivo

El sistema del mapa utiliza **una arquitectura de dos niveles** para optimizar el rendimiento:

1. **Carga Inicial (Ligera)**: `GetMapExperts` - Devuelve información básica para mostrar etiquetas con precios en el mapa
2. **Carga al Mover Mapa (Completa)**: `GetMapExpertsWithDetails` - Devuelve información completa cuando el usuario mueve el mapa
3. **Carga al Hacer Clic (Detalle Completo)**: `GetServiceById` - Devuelve toda la información del servicio para la vista de detalle/review

---

## 🎯 Flujo de Llamadas del Mapa

### **Escenario 1: Carga Inicial del Mapa (Ligera)**

**Endpoint**: `GET /api/SearchService/map-experts`

**Parámetros**:
- `categoryId` (requerido)
- `serviceTypeId` (requerido)
- `limit` (opcional, default: 100, max: 500)
- `zoom` (opcional, afecta el límite de resultados)

**Cuándo se usa**: Al cargar el mapa por primera vez, sin ubicación específica ni bounds.

**Método Backend**: `GetMapExperts()`

**Qué devuelve**: `ExpertMapResponseDto` con información **BÁSICA** para mostrar etiquetas en el mapa.

**Datos incluidos** (ExpertMapDto):
```typescript
{
  id: number,                    // ID del experto
  name: string,                  // Nombre del experto
  profilePictureUrl: string,     // URL de foto de perfil
  averageRating: number,         // Rating promedio
  totalReviews: number,          // Total de reviews
  completedSearches: number,     // Búsquedas completadas
  registeredSince: DateTime,     // Fecha de registro
  latitude: string,              // Coordenada latitud
  longitude: string,             // Coordenada longitud
  price: decimal,                // ✅ PRECIO (para mostrar en etiqueta)
  serviceDescription: string,    // Descripción del servicio
  serviceTypeName: string,       // Nombre del tipo de servicio
  serviceTypeDescription: string,// Descripción del tipo
  currentAvailability: {         // Horarios de disponibilidad
    daysOfWeek: string[],
    startTime: TimeSpan,
    endTime: TimeSpan
  }
}
```

**Optimizaciones**:
- ✅ Solo carga datos básicos necesarios para etiquetas
- ✅ Agrupa servicios por experto (evita duplicados)
- ✅ Usa `AsNoTracking()` para mejor rendimiento
- ✅ Carga disponibilidades en batch (una sola consulta)

**Tamaño estimado de respuesta**: ~5-20 KB por experto (dependiendo de la cantidad)

**Tiempo de carga esperado**: 0.5 - 1.5 segundos

---

### **Escenario 2: Mover el Mapa (Carga Completa con Bounds)**

**Endpoint**: `GET /api/SearchService/map-experts`

**Parámetros**:
- `categoryId` (requerido)
- `serviceTypeId` (requerido)
- `northeastLat` (requerido cuando hay bounds)
- `northeastLng` (requerido cuando hay bounds)
- `southwestLat` (requerido cuando hay bounds)
- `southwestLng` (requerido cuando hay bounds)
- `zoom` (opcional, afecta el límite)
- `limit` (opcional, default: 100)
- `page` (opcional, default: 1)
- `pageSize` (opcional, default: 50)

**Cuándo se usa**: Cuando el usuario mueve el mapa y se envían los bounds (noreste y suroeste) del área visible.

**Método Backend**: `GetMapExpertsWithDetails()`

**Qué devuelve**: `SearchServiceDetailDto[]` con información **COMPLETA** de los servicios.

**Datos incluidos** (SearchServiceDetailDto):
```typescript
{
  // Información del servicio
  id: number,
  categoryId: number,
  serviceTypeId: number,
  serviceTypeName: string,
  serviceTypeDescription: string,
  price: decimal,
  conditions: string,
  durationInHours: number,
  imageUrls: string[],              // ✅ TODAS las imágenes del servicio
  selectedDeliverableTypes: [...],  // Tipos de entregables
  
  // Información del experto (COMPLETA)
  expertProfile: {
    id: number,
    profilePictureUrl: string,
    description: string,
    latitude: string,
    longitude: string,
    stripeStatus: string,
    isOnVacation: boolean,
    currentAvailability: {...},     // Horarios
    
    // Información del usuario
    user: {
      name: string,
      email: string
    },
    
    // ✅ TODAS las reviews con detalles completos
    reviews: [
      {
        id: number,
        score: number,
        description: string,
        createdAt: DateTime,
        reviewer: {
          id: number,
          name: string,
          email: string
        },
        imageUrls: string[],        // ✅ Imágenes de la review
        country: string             // País donde se hizo la contratación
      }
    ],
    
    // Estadísticas
    averageRating: number,
    totalReviews: number,
    completedSearches: number
  }
}
```

**Optimizaciones**:
- ✅ Filtra por bounds directamente en SQL (muy rápido)
- ✅ Solo carga servicios dentro del área visible
- ✅ Aplica límite según zoom (más zoom = más servicios)
- ✅ Ordena por distancia al centro del bounds
- ✅ Paginación para no sobrecargar

**Tamaño estimado de respuesta**: ~50-200 KB por servicio (con todas las reviews e imágenes)

**Tiempo de carga esperado**: 1-3 segundos (depende del área y cantidad de servicios)

---

### **Escenario 3: Hacer Clic en un Servicio (Detalle Completo)**

**Endpoint**: `GET /api/SearchService/{id}`

**Parámetros**:
- `id` (requerido) - ID del servicio

**Cuándo se usa**: Cuando el usuario hace clic en un marcador o servicio específico para ver el detalle completo.

**Método Backend**: `GetServiceById()`

**Qué devuelve**: `SearchServiceDetailDto` con **TODA** la información del servicio.

**Datos incluidos**: Igual que `GetMapExpertsWithDetails`, pero para **UN SOLO** servicio.

**Optimizaciones**:
- ✅ Carga todas las relaciones necesarias
- ✅ Incluye todas las reviews con imágenes
- ✅ Incluye información completa del experto
- ✅ Carga disponibilidad actual del experto

**Tamaño estimado de respuesta**: ~100-500 KB (dependiendo de cantidad de reviews)

**Tiempo de carga esperado**: 0.3 - 1 segundo

---

## 🔄 Flujo Completo de Usuario

```
1. Usuario abre el mapa
   ↓
2. Frontend llama: GET /api/SearchService/map-experts?categoryId=X&serviceTypeId=Y
   ↓
3. Backend devuelve: ExpertMapResponseDto (info básica)
   ↓
4. Frontend muestra: Marcadores en el mapa con precios
   ↓
5. Usuario mueve el mapa
   ↓
6. Frontend llama: GET /api/SearchService/map-experts?categoryId=X&serviceTypeId=Y&northeastLat=...&southwestLat=...
   ↓
7. Backend devuelve: SearchServiceDetailDto[] (info completa con paginación)
   ↓
8. Frontend muestra: Lista de servicios con imágenes y detalles
   ↓
9. Usuario hace clic en un servicio
   ↓
10. Frontend llama: GET /api/SearchService/{id}
   ↓
11. Backend devuelve: SearchServiceDetailDto (detalle completo de UN servicio)
   ↓
12. Frontend muestra: Vista de detalle/review completa
```

---

## 📊 Comparación de Datos

| Característica | GetMapExperts (Ligera) | GetMapExpertsWithDetails (Completa) | GetServiceById (Detalle) |
|----------------|------------------------|-------------------------------------|--------------------------|
| **Uso** | Carga inicial | Mover mapa | Clic en servicio |
| **Datos** | Básicos | Completos | Completos |
| **Imágenes** | ❌ No | ✅ Sí (todas) | ✅ Sí (todas) |
| **Reviews** | ❌ No | ✅ Sí (todas con imágenes) | ✅ Sí (todas con imágenes) |
| **Precio** | ✅ Sí | ✅ Sí | ✅ Sí |
| **Disponibilidad** | ✅ Sí | ✅ Sí | ✅ Sí |
| **Paginación** | ❌ No | ✅ Sí | ❌ No |
| **Tamaño respuesta** | 5-20 KB/experto | 50-200 KB/servicio | 100-500 KB |
| **Tiempo carga** | 0.5-1.5s | 1-3s | 0.3-1s |

---

## 🎨 Mejores Prácticas para el Frontend

### **1. Carga Inicial (GetMapExperts)**

```typescript
// ✅ Cargar solo cuando se abre el mapa
const loadMapMarkers = async () => {
  const response = await fetch(
    `/api/SearchService/map-experts?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}&limit=100`
  );
  const data: ExpertMapResponseDto = await response.json();
  
  // Mostrar solo marcadores con precio
  data.experts.forEach(expert => {
    showMarker(expert.latitude, expert.longitude, expert.price);
  });
};
```

### **2. Mover Mapa (GetMapExpertsWithDetails)**

```typescript
// ✅ Cargar cuando el usuario mueve el mapa (debounce recomendado)
const handleMapMove = debounce(async (bounds) => {
  const response = await fetch(
    `/api/SearchService/map-experts?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}` +
    `&northeastLat=${bounds.northeast.lat}&northeastLng=${bounds.northeast.lng}` +
    `&southwestLat=${bounds.southwest.lat}&southwestLng=${bounds.southwest.lng}` +
    `&zoom=${map.getZoom()}&page=1&pageSize=20`
  );
  const data = await response.json();
  
  // Mostrar lista de servicios con imágenes
  displayServiceList(data.services);
}, 500); // Debounce de 500ms
```

### **3. Clic en Servicio (GetServiceById)**

```typescript
// ✅ Cargar solo cuando el usuario hace clic
const handleServiceClick = async (serviceId: number) => {
  const response = await fetch(`/api/SearchService/${serviceId}`);
  const service: SearchServiceDetailDto = await response.json();
  
  // Mostrar vista de detalle completa
  showServiceDetail(service);
};
```

---

## ⚡ Optimizaciones Implementadas

### **GetMapExperts (Ligera)**
1. ✅ Solo carga campos básicos necesarios
2. ✅ Agrupa servicios por experto (evita duplicados)
3. ✅ Usa `AsNoTracking()` para mejor rendimiento
4. ✅ Carga disponibilidades en batch
5. ✅ No carga imágenes ni reviews

### **GetMapExpertsWithDetails (Completa)**
1. ✅ Filtra por bounds directamente en SQL (100-1000x más rápido)
2. ✅ Solo carga servicios dentro del área visible
3. ✅ Aplica límite según zoom
4. ✅ Ordena por distancia al centro
5. ✅ Paginación para no sobrecargar
6. ✅ Carga todas las relaciones necesarias

### **GetServiceById (Detalle)**
1. ✅ Carga todas las relaciones en una sola consulta
2. ✅ Incluye todas las reviews con imágenes
3. ✅ Carga disponibilidad actual del experto
4. ✅ Optimizado para un solo servicio

---

## 🚨 Consideraciones Importantes

### **1. No usar GetAllServices para el mapa inicial**
- ❌ `GetAllServices` requiere `latitude`, `longitude`, `locationRange`
- ✅ Usa `GetMapExperts` para carga inicial sin ubicación

### **2. Bounds del mapa**
- Cuando el usuario mueve el mapa, siempre enviar los 4 parámetros de bounds
- El backend filtra automáticamente por el área visible

### **3. Zoom del mapa**
- El zoom afecta el límite de resultados
- Zoom alto (15+) = más servicios (hasta 200)
- Zoom bajo (<12) = menos servicios (30-50)

### **4. Paginación**
- `GetMapExpertsWithDetails` soporta paginación
- Usar `page` y `pageSize` para cargar más servicios
- Recomendado: 20-50 servicios por página

### **5. Caché**
- Considerar caché de `GetMapExperts` (carga inicial)
- No cachéar `GetMapExpertsWithDetails` (cambia con bounds)
- Cachéar `GetServiceById` por ID (cambia poco)

---

## 📈 Métricas de Rendimiento

### **GetMapExperts (Ligera)**
- **Consultas SQL**: 2-3 queries
- **Datos transferidos**: ~100-500 KB (para 20-50 expertos)
- **Tiempo**: 0.5-1.5 segundos

### **GetMapExpertsWithDetails (Completa)**
- **Consultas SQL**: 3-4 queries
- **Datos transferidos**: ~1-5 MB (para 20 servicios con reviews)
- **Tiempo**: 1-3 segundos

### **GetServiceById (Detalle)**
- **Consultas SQL**: 2-3 queries
- **Datos transferidos**: ~100-500 KB (un servicio completo)
- **Tiempo**: 0.3-1 segundo

---

## ✅ Checklist de Implementación Frontend

- [ ] Usar `GetMapExperts` para carga inicial (sin bounds)
- [ ] Usar `GetMapExpertsWithDetails` cuando el usuario mueve el mapa
- [ ] Implementar debounce en el movimiento del mapa (500ms recomendado)
- [ ] Usar `GetServiceById` cuando el usuario hace clic en un servicio
- [ ] Mostrar precios en los marcadores del mapa
- [ ] Implementar paginación para `GetMapExpertsWithDetails`
- [ ] Cachéar `GetMapExperts` (carga inicial)
- [ ] No cachéar `GetMapExpertsWithDetails` (cambia con bounds)
- [ ] Mostrar loading states durante las cargas
- [ ] Manejar errores y timeouts (30 segundos máximo)

---

## 🔍 Ejemplos de Uso

### **Ejemplo 1: Carga Inicial**
```typescript
// Cargar marcadores básicos al abrir el mapa
GET /api/SearchService/map-experts?categoryId=1&serviceTypeId=2&limit=100
```

### **Ejemplo 2: Mover Mapa**
```typescript
// Cargar servicios completos cuando el usuario mueve el mapa
GET /api/SearchService/map-experts?categoryId=1&serviceTypeId=2
  &northeastLat=41.8&northeastLng=-2.5
  &southwestLat=41.7&southwestLng=-2.6
  &zoom=15&page=1&pageSize=20
```

### **Ejemplo 3: Clic en Servicio**
```typescript
// Cargar detalle completo de un servicio
GET /api/SearchService/123
```

---

**Última actualización**: Enero 2025
**Versión**: v1 (optimizado)


