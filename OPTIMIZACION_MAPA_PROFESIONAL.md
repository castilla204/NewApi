# 🚀 Optimización Profesional del Mapa - Arquitectura de 3 Niveles

## 🎯 Objetivo

Implementar una arquitectura similar a **Airbnb/Google Maps** con 3 niveles de carga:
1. **Nivel 1 (Ultra Ligero)**: Solo marcadores con precios en el mapa
2. **Nivel 2 (Medio)**: Información básica + imágenes para el sidebar
3. **Nivel 3 (Completo)**: Información completa al hacer clic

---

## 📊 Arquitectura Propuesta

### **Nivel 1: Marcadores Ultra Ligeros** (NUEVO)

**Endpoint**: `GET /api/SearchService/map-markers`

**Propósito**: Cargar solo lo mínimo necesario para mostrar marcadores con precios.

**Qué devuelve**: Solo coordenadas + precio + ID
```typescript
{
  markers: [
    {
      id: number,              // ID del servicio
      latitude: string,
      longitude: string,
      price: decimal,          // Precio para mostrar en label
      serviceId: number        // Para cargar detalles después
    }
  ],
  totalCount: number
}
```

**Tamaño**: ~100 bytes por marcador (vs 5-20 KB actual)
**Tiempo**: 0.1-0.5 segundos (vs 0.5-1.5 actual)
**Optimización**: Solo SELECT de 4 campos desde BD

---

### **Nivel 2: Información Básica para Sidebar** (NUEVO)

**Endpoint**: `GET /api/SearchService/map-sidebar`

**Propósito**: Cargar información básica + primera imagen para mostrar en el sidebar izquierdo.

**Parámetros**:
- `serviceIds[]` (array de IDs de servicios visibles en el mapa)
- `bounds` (opcional, para filtrar)

**Qué devuelve**: Información básica + primera imagen
```typescript
{
  services: [
    {
      id: number,
      price: decimal,
      serviceTypeName: string,
      expertName: string,
      expertProfilePictureUrl: string,
      averageRating: number,
      totalReviews: number,
      firstImageUrl: string,    // ✅ Solo la primera imagen
      latitude: string,
      longitude: string,
      distance?: number          // Si hay ubicación del usuario
    }
  ]
}
```

**Tamaño**: ~2-5 KB por servicio (vs 50-200 KB actual)
**Tiempo**: 0.3-1 segundo
**Optimización**: Solo primera imagen, sin reviews, sin todas las relaciones

---

### **Nivel 3: Detalle Completo** (EXISTENTE - Mejorado)

**Endpoint**: `GET /api/SearchService/{id}`

**Propósito**: Cargar toda la información cuando el usuario hace clic.

**Qué devuelve**: `SearchServiceDetailDto` completo (ya existe)

**Tamaño**: ~100-500 KB
**Tiempo**: 0.3-1 segundo

---

## 🔄 Flujo Optimizado

```
1. Usuario abre el mapa
   ↓
2. Frontend: GET /api/SearchService/map-markers
   ↓ Devuelve: Solo coordenadas + precios (ultra ligero)
   ↓
3. Frontend muestra: Marcadores con precios en el mapa
   ↓
4. Frontend detecta: Servicios visibles en el viewport
   ↓
5. Frontend: GET /api/SearchService/map-sidebar?serviceIds=[1,2,3...]
   ↓ Devuelve: Info básica + primera imagen (medio)
   ↓
6. Frontend muestra: Sidebar izquierdo con cards básicas
   ↓
7. Usuario hace clic en marcador o card
   ↓
8. Frontend: GET /api/SearchService/{id}
   ↓ Devuelve: Información completa (completo)
   ↓
9. Frontend muestra: Página de detalle completa
```

---

## 💻 Implementación Backend

### **1. Nuevo Endpoint: GetMapMarkers (Ultra Ligero)**

```csharp
public async Task<MapMarkersResponseDto> GetMapMarkers(
    int categoryId, 
    int serviceTypeId,
    decimal? northeastLat = null,
    decimal? northeastLng = null,
    decimal? southwestLat = null,
    decimal? southwestLng = null,
    int? zoom = null,
    int limit = 500,
    CancellationToken cancellationToken = default)
{
    // ✅ OPTIMIZACIÓN CRÍTICA: Solo SELECT de campos mínimos
    var query = _context.SearchServices
        .AsNoTracking()
        .Where(ss => ss.CategoryId == categoryId 
            && ss.ServiceTypeId == serviceTypeId 
            && ss.IsActive 
            && !ss.ExpertProfile.IsOnVacation
            && (ss.ExpertProfile.StripeStatus == StripeStatus.Approved 
                && ss.ExpertProfile.OnboardingCompleted
                || ss.ExpertProfile.StripeStatus == StripeStatus.PendingVerification))
        .Where(ss => !string.IsNullOrEmpty(ss.ExpertProfile.Latitude) 
            && !string.IsNullOrEmpty(ss.ExpertProfile.Longitude));

    // ✅ Filtrar por bounds si se proporcionan (en SQL)
    if (northeastLat.HasValue && southwestLat.HasValue)
    {
        query = query.Where(ss => 
            decimal.Parse(ss.ExpertProfile.Latitude) >= southwestLat.Value
            && decimal.Parse(ss.ExpertProfile.Latitude) <= northeastLat.Value
            && decimal.Parse(ss.ExpertProfile.Longitude) >= southwestLng.Value
            && decimal.Parse(ss.ExpertProfile.Longitude) <= northeastLng.Value);
    }

    // ✅ Aplicar límite según zoom
    int maxResults = limit;
    if (zoom.HasValue)
    {
        maxResults = zoom.Value switch
        {
            >= 15 => Math.Min(limit, 500),
            >= 12 => Math.Min(limit, 200),
            _ => Math.Min(limit, 100)
        };
    }

    // ✅ SOLO SELECT de campos necesarios (ultra rápido)
    var markers = await query
        .Select(ss => new MapMarkerDto
        {
            Id = ss.Id,
            ServiceId = ss.Id,
            Latitude = ss.ExpertProfile.Latitude,
            Longitude = ss.ExpertProfile.Longitude,
            Price = ss.Price
        })
        .Take(maxResults)
        .ToListAsync(cancellationToken);

    return new MapMarkersResponseDto
    {
        Markers = markers,
        TotalCount = markers.Count
    };
}
```

**Ventajas**:
- ✅ Solo 4 campos desde BD (vs 20+ actual)
- ✅ Sin JOINs complejos
- ✅ Sin cargar imágenes ni relaciones
- ✅ 10-50x más rápido que GetMapExperts

---

### **2. Nuevo Endpoint: GetMapSidebar (Medio)**

```csharp
public async Task<MapSidebarResponseDto> GetMapSidebar(
    int[] serviceIds,
    CancellationToken cancellationToken = default)
{
    if (serviceIds == null || serviceIds.Length == 0)
    {
        return new MapSidebarResponseDto { Services = new List<MapSidebarServiceDto>() };
    }

    // ✅ OPTIMIZACIÓN: Solo campos básicos + primera imagen
    var services = await _context.SearchServices
        .AsNoTracking()
        .Where(ss => serviceIds.Contains(ss.Id))
        .Select(ss => new MapSidebarServiceDto
        {
            Id = ss.Id,
            Price = ss.Price,
            ServiceTypeName = ss.ServiceType.Name,
            ExpertName = ss.ExpertProfile.User.Name,
            ExpertProfilePictureUrl = ss.ExpertProfile.ProfilePictureUrl,
            ExpertProfilePictureObjectName = ss.ExpertProfile.ProfilePictureObjectName,
            AverageRating = ss.ExpertProfile.User.ReviewsReceived.Any()
                ? ss.ExpertProfile.User.ReviewsReceived.Average(r => (double)r.Score)
                : 0.0,
            TotalReviews = ss.ExpertProfile.User.ReviewsReceived.Count,
            // ✅ Solo primera imagen (no todas)
            FirstImageUrl = ss.Images
                .OrderBy(img => img.Id)
                .Select(img => img.ImageUrl)
                .FirstOrDefault(),
            FirstImageObjectName = ss.Images
                .OrderBy(img => img.Id)
                .Select(img => img.ImageObjectName)
                .FirstOrDefault(),
            Latitude = ss.ExpertProfile.Latitude,
            Longitude = ss.ExpertProfile.Longitude
        })
        .ToListAsync(cancellationToken);

    // ✅ Procesar URLs firmadas en memoria (solo primera imagen)
    var processedServices = services.Select(s => new MapSidebarServiceDto
    {
        Id = s.Id,
        Price = s.Price,
        ServiceTypeName = s.ServiceTypeName,
        ExpertName = s.ExpertName,
        ExpertProfilePictureUrl = !string.IsNullOrWhiteSpace(s.ExpertProfilePictureObjectName)
            ? _signedUrlService.GetSignedUrl(s.ExpertProfilePictureObjectName) ?? s.ExpertProfilePictureUrl
            : s.ExpertProfilePictureUrl,
        AverageRating = s.AverageRating,
        TotalReviews = s.TotalReviews,
        FirstImageUrl = !string.IsNullOrWhiteSpace(s.FirstImageObjectName)
            ? _signedUrlService.GetSignedUrl(s.FirstImageObjectName) ?? s.FirstImageUrl
            : s.FirstImageUrl,
        Latitude = s.Latitude,
        Longitude = s.Longitude
    }).ToList();

    return new MapSidebarResponseDto
    {
        Services = processedServices
    };
}
```

**Ventajas**:
- ✅ Solo primera imagen (no todas)
- ✅ Sin reviews completas
- ✅ Sin todas las relaciones
- ✅ 5-10x más rápido que GetMapExpertsWithDetails

---

## 📈 Comparación de Rendimiento

| Métrica | Actual (GetMapExperts) | Propuesto (GetMapMarkers) | Mejora |
|---------|------------------------|---------------------------|--------|
| **Campos BD** | 20+ campos | 4 campos | **5x menos** |
| **JOINs** | 5-7 JOINs | 1 JOIN | **5-7x menos** |
| **Tamaño respuesta** | 5-20 KB/experto | 100 bytes/marcador | **50-200x menos** |
| **Tiempo carga** | 0.5-1.5s | 0.1-0.5s | **3-5x más rápido** |
| **Consultas SQL** | 2-3 queries | 1 query | **2-3x menos** |

---

## 🎨 Flujo Frontend Optimizado

```typescript
// 1. Carga inicial: Solo marcadores
const loadMapMarkers = async () => {
  const response = await fetch(
    `/api/SearchService/map-markers?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}`
  );
  const data = await response.json();
  
  // Mostrar solo marcadores con precios
  data.markers.forEach(marker => {
    addMarker(marker.latitude, marker.longitude, marker.price, marker.id);
  });
};

// 2. Cargar sidebar cuando hay servicios visibles
const loadSidebar = async (visibleServiceIds: number[]) => {
  if (visibleServiceIds.length === 0) return;
  
  const response = await fetch(
    `/api/SearchService/map-sidebar?serviceIds=${visibleServiceIds.join(',')}`
  );
  const data = await response.json();
  
  // Mostrar cards en sidebar
  displaySidebarCards(data.services);
};

// 3. Cargar detalle completo al hacer clic
const loadServiceDetail = async (serviceId: number) => {
  const response = await fetch(`/api/SearchService/${serviceId}`);
  const service = await response.json();
  
  // Navegar a página de detalle
  navigate(`/service/${serviceId}`, { state: service });
};

// Lógica del mapa
useEffect(() => {
  // Cargar marcadores al inicio
  loadMapMarkers();
  
  // Detectar servicios visibles en viewport
  const handleMapMove = debounce(() => {
    const visibleIds = getVisibleServiceIds();
    loadSidebar(visibleIds);
  }, 300);
  
  map.on('moveend', handleMapMove);
  
  // Click en marcador
  markers.forEach(marker => {
    marker.on('click', () => {
      loadServiceDetail(marker.serviceId);
    });
  });
}, []);
```

---

## 📊 Nuevos DTOs Necesarios

### **MapMarkerDto**
```csharp
public class MapMarkerDto
{
    public int Id { get; set; }
    public int ServiceId { get; set; }
    public string Latitude { get; set; }
    public string Longitude { get; set; }
    public decimal Price { get; set; }
}

public class MapMarkersResponseDto
{
    public List<MapMarkerDto> Markers { get; set; } = new();
    public int TotalCount { get; set; }
}
```

### **MapSidebarServiceDto**
```csharp
public class MapSidebarServiceDto
{
    public int Id { get; set; }
    public decimal Price { get; set; }
    public string ServiceTypeName { get; set; }
    public string ExpertName { get; set; }
    public string ExpertProfilePictureUrl { get; set; }
    public double AverageRating { get; set; }
    public int TotalReviews { get; set; }
    public string FirstImageUrl { get; set; }  // Solo primera imagen
    public string Latitude { get; set; }
    public string Longitude { get; set; }
    public decimal? Distance { get; set; }  // Si hay ubicación usuario
}

public class MapSidebarResponseDto
{
    public List<MapSidebarServiceDto> Services { get; set; } = new();
}
```

---

## ⚡ Optimizaciones Adicionales

### **1. Caché de Marcadores**
```csharp
// Cachéar marcadores por 5 minutos (cambian poco)
[ResponseCache(Duration = 300, VaryByQueryKeys = new[] { "categoryId", "serviceTypeId" })]
public async Task<MapMarkersResponseDto> GetMapMarkers(...)
```

### **2. Batch Loading del Sidebar**
```typescript
// Cargar sidebar solo para servicios visibles en viewport
const visibleIds = markers
  .filter(m => isInViewport(m.latitude, m.longitude))
  .map(m => m.serviceId);
  
loadSidebar(visibleIds);
```

### **3. Prefetching Inteligente**
```typescript
// Pre-cargar detalles de servicios cercanos al cursor
const prefetchNearby = debounce((lat, lng) => {
  const nearbyIds = getNearbyServiceIds(lat, lng, radius: 100);
  prefetchServiceDetails(nearbyIds);
}, 1000);
```

### **4. Lazy Loading de Imágenes**
```typescript
// Cargar imágenes del sidebar solo cuando están visibles
<LazyImage 
  src={service.firstImageUrl} 
  placeholder="/placeholder.jpg"
  loading="lazy"
/>
```

---

## 🎯 Beneficios de la Nueva Arquitectura

### **Rendimiento**
- ✅ **10-50x más rápido** la carga inicial
- ✅ **5-10x menos datos** transferidos
- ✅ **Mejor experiencia** de usuario (carga instantánea)

### **Escalabilidad**
- ✅ Soporta **miles de marcadores** sin problemas
- ✅ Carga **solo lo visible** en el viewport
- ✅ Reduce **carga del servidor** significativamente

### **UX**
- ✅ Mapa carga **instantáneamente**
- ✅ Sidebar se carga **progresivamente**
- ✅ Detalle completo **solo cuando se necesita**

---

## 📝 Plan de Implementación

### **Fase 1: Backend (Prioridad Alta)**
1. ✅ Crear `GetMapMarkers()` - Ultra ligero
2. ✅ Crear `GetMapSidebar()` - Medio
3. ✅ Crear DTOs necesarios
4. ✅ Agregar endpoints al controlador
5. ✅ Agregar caché de marcadores

### **Fase 2: Frontend (Prioridad Alta)**
1. ✅ Implementar carga de marcadores
2. ✅ Implementar detección de viewport
3. ✅ Implementar carga de sidebar
4. ✅ Implementar navegación a detalle

### **Fase 3: Optimizaciones (Prioridad Media)**
1. ✅ Implementar prefetching
2. ✅ Implementar lazy loading de imágenes
3. ✅ Implementar caché en frontend
4. ✅ Implementar virtualización del sidebar

---

## 🔍 Comparación con Airbnb/Google Maps

| Característica | Airbnb | Google Maps | Nuestra Propuesta |
|----------------|--------|-------------|-------------------|
| **Marcadores iniciales** | Ultra ligeros | Ultra ligeros | ✅ Ultra ligeros |
| **Info al mover** | Carga progresiva | Carga progresiva | ✅ Carga progresiva |
| **Detalle al clic** | Página completa | InfoWindow | ✅ Página completa |
| **Caché** | Sí | Sí | ✅ Sí (propuesto) |
| **Lazy loading** | Sí | Sí | ✅ Sí (propuesto) |

---

## ✅ Checklist de Implementación

### **Backend**
- [ ] Crear `MapMarkerDto` y `MapMarkersResponseDto`
- [ ] Crear `MapSidebarServiceDto` y `MapSidebarResponseDto`
- [ ] Implementar `GetMapMarkers()` en `SearchServiceService`
- [ ] Implementar `GetMapSidebar()` en `SearchServiceService`
- [ ] Agregar endpoints al controlador
- [ ] Agregar caché de marcadores (5 minutos)
- [ ] Agregar tests unitarios

### **Frontend**
- [ ] Crear componente `MapMarkers`
- [ ] Crear componente `MapSidebar`
- [ ] Implementar detección de viewport
- [ ] Implementar carga progresiva
- [ ] Implementar navegación a detalle
- [ ] Agregar loading states
- [ ] Agregar error handling
- [ ] Implementar lazy loading de imágenes

---

## 🚀 Resultado Esperado

Con esta optimización:
- **Carga inicial**: 0.1-0.5s (vs 0.5-1.5s actual) - **3-5x más rápido**
- **Datos iniciales**: 50-200 KB (vs 500 KB-2 MB actual) - **10-20x menos**
- **Experiencia**: Similar a Airbnb/Google Maps
- **Escalabilidad**: Soporta miles de servicios sin problemas

---

**Última actualización**: Enero 2025
**Versión**: v2 (Optimización Profesional)


