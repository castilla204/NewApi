# 🗺️ **GUÍA COMPLETA FRONTEND - MAPA Y HOMEPAGE WALL**

## 📋 **ÍNDICE**
1. [Endpoints del Mapa](#endpoints-del-mapa)
2. [Homepage Wall](#homepage-wall)
3. [Ejemplos de Implementación](#ejemplos-de-implementación)
4. [Mejores Prácticas](#mejores-prácticas)

---

## 🗺️ **ENDPOINTS DEL MAPA**

### **1. GET `/api/SearchService/map-markers` - Marcadores Ultra Ligeros**

**Propósito:** Cargar marcadores mínimos (solo coordenadas + precio) para renderizar el mapa rápidamente.

**Parámetros:**
```typescript
{
  categoryId: number;           // REQUERIDO: ID de la categoría
  serviceTypeId: number;        // REQUERIDO: ID del tipo de servicio
  northeastLat?: number;         // OPCIONAL: Latitud noreste del bounds
  northeastLng?: number;         // OPCIONAL: Longitud noreste del bounds
  southwestLat?: number;         // OPCIONAL: Latitud suroeste del bounds
  southwestLng?: number;         // OPCIONAL: Longitud suroeste del bounds
  zoom?: number;                 // OPCIONAL: Nivel de zoom (1-20)
  limit?: number;                // OPCIONAL: Límite de resultados (default: 500, max: 500)
}
```

**Respuesta:**
```typescript
{
  markers: MapMarkerDto[];
  totalCount: number;
}

interface MapMarkerDto {
  id: number;                    // ID del servicio
  serviceId: number;             // ID del servicio (igual que id)
  latitude: string;              // Latitud del experto
  longitude: string;             // Longitud del experto
  price: number;                 // Precio del servicio
}
```

**Ejemplo de Llamada:**
```typescript
// Carga inicial del mapa (sin bounds)
const response = await fetch(
  `/api/SearchService/map-markers?categoryId=1&serviceTypeId=1&limit=500`
);
const data = await response.json();
// data.markers contiene todos los marcadores visibles

// Carga con bounds (cuando el usuario mueve el mapa)
const response = await fetch(
  `/api/SearchService/map-markers?categoryId=1&serviceTypeId=1&northeastLat=40.5&northeastLng=-3.6&southwestLat=40.3&southwestLng=-3.8&zoom=12&limit=200`
);
const data = await response.json();
// data.markers contiene solo marcadores dentro del viewport
```

**✅ Optimizaciones Automáticas:**
- **Sin bounds:** Devuelve hasta 500 marcadores (todos los disponibles)
- **Con bounds:** Filtra por viewport visible (más rápido)
- **Zoom bajo (< 10):** Límite automático de 100 marcadores
- **Zoom medio (10-14):** Límite automático de 300 marcadores
- **Zoom alto (> 14):** Límite automático de 500 marcadores

---

### **2. GET `/api/SearchService/map-sidebar` - Información Completa del Sidebar**

**Propósito:** Obtener información completa de servicios visibles para mostrar cards en el sidebar con toda la información necesaria.

**Parámetros:**
```typescript
{
  serviceIds: number[];  // REQUERIDO: Array de IDs de servicios (máximo recomendado: 20-30)
}
```

**Respuesta:**
```typescript
{
  services: MapSidebarServiceDto[];
  totalCount: number;
}

interface MapSidebarServiceDto {
  id: number;                              // ID del servicio
  price: number;                            // Precio del servicio
  serviceTypeName: string;                  // Nombre del tipo de servicio
  serviceDescription: string;                // ✅ NUEVO: Descripción del servicio (Conditions)
  expertName: string;                       // Nombre del experto
  expertProfilePictureUrl: string;          // URL de la foto de perfil del experto
  averageRating: number;                     // Puntuación promedio (0-5)
  totalReviews: number;                     // Total de reseñas
  imageUrls: string[];                      // ✅ NUEVO: Mínimo 3 imágenes (antes era solo firstImageUrl)
  latitude: string;                         // Latitud del experto
  longitude: string;                        // Longitud del experto
  distance?: number;                        // Distancia al centro del mapa (si aplica)
  currentAvailability?: CurrentExpertAvailabilityDto;  // ✅ NUEVO: Horario de disponibilidad
}

interface CurrentExpertAvailabilityDto {
  id: number;
  daysOfWeek: string[];                     // ["Monday", "Tuesday", "Wednesday", ...]
  startTime: string;                       // "09:00:00" (formato TimeSpan)
  endTime: string;                         // "18:00:00" (formato TimeSpan)
  effectiveFrom: string;                    // "2025-01-01T00:00:00Z" (ISO DateTime)
}
```

**Ejemplo de Llamada:**
```typescript
// Detectar servicios visibles en el viewport del mapa
const visibleServiceIds = getVisibleServiceIdsFromMap(); // [1, 2, 3, 4, 5]

const response = await fetch(
  `/api/SearchService/map-sidebar?serviceIds=${visibleServiceIds.join(',')}`
);
const data = await response.json();

// data.services contiene información completa para cada servicio
data.services.forEach(service => {
  console.log(service.expertName);           // Nombre del experto
  console.log(service.imageUrls);            // Array con mínimo 3 imágenes
  console.log(service.serviceDescription);   // Descripción del servicio
  console.log(service.currentAvailability);  // Horario de disponibilidad
});
```

**✅ Optimizaciones:**
- **Batch Loading:** Carga múltiples servicios en una sola llamada
- **Mínimo 3 imágenes:** Siempre devuelve al menos 3 imágenes por servicio
- **Horarios incluidos:** Disponibilidad del experto incluida automáticamente
- **URLs firmadas:** Todas las imágenes ya vienen con URLs firmadas (listas para usar)

---

## 🏠 **HOMEPAGE WALL**

### **GET `/api/SearchService/homepage-wall` - Muro de Inicio**

**Propósito:** Obtener servicios para mostrar en el homepage filtrados por categoría. Devuelve un array plano con secciones: cercanos, populares y secciones específicas por país (solo para Coches y Motos).

**⚠️ IMPORTANTE: `categoryId` es OBLIGATORIO**

**Parámetros:**
```typescript
{
  categoryId: number;           // ✅ REQUERIDO - ID de la categoría (debe ser el primer parámetro)
  latitude?: string;            // OPCIONAL: Latitud del usuario
  longitude?: string;           // OPCIONAL: Longitud del usuario
  countryCode?: string;         // OPCIONAL: Código de país (ES, FR, DE, etc.) - default: "ES"
  locationRange?: number;       // OPCIONAL: Rango en km (default: 50)
  nearbyPage?: number;          // OPCIONAL: Página para servicios cercanos (default: 1)
  nearbyPageSize?: number;      // OPCIONAL: Tamaño de página cercanos (default: 20, max: 50)
  popularPage?: number;         // OPCIONAL: Página para servicios populares (default: 1)
  popularPageSize?: number;     // OPCIONAL: Tamaño de página populares (default: 20, max: 50)
}
```

**Respuesta: Array Plano de Secciones**

```typescript
// ✅ La respuesta es un ARRAY, no un objeto con propiedades anidadas
type HomepageWallResponse = Array<{
  title: string;                    // Título de la sección (ej: "Revisiones Coches cerca de mí")
  services: SearchServiceHomepageDto[];
  categoryName?: string;            // Solo presente en secciones específicas por país
  country?: string;                 // Solo presente en secciones específicas por país
  pagination: {
    page: number;
    pageSize: number;
    totalCount: number;
    totalPages: number;
    hasNextPage: boolean;
    hasPreviousPage: boolean;
  };
}>;

interface SearchServiceHomepageDto {
  Id: number;
  CategoryId: number;
  CategoryName: string;
  ServiceTypeId: number;
  ServiceTypeName: string;
  Price: number;
  ImageUrls: string[];  // URLs firmadas, listas para usar
  Expert: {
    Id: number;
    Name: string;
    ProfilePictureUrl: string;
    Country: string;
  };
  CompletedSearches: number;
  AverageRating: number;
}
```

**Ejemplo de Llamada:**

```typescript
// ✅ CON CATEGORÍA COCHES (categoryId=1)
const response = await fetch(
  `/api/SearchService/homepage-wall?categoryId=1&latitude=40.4168&longitude=-3.7038&countryCode=ES&locationRange=50`
);
const sections = await response.json(); // Array de secciones

// sections es un array:
// [
//   { title: "Revisiones Coches cerca de mí", services: [...], pagination: {...} },
//   { title: "Revisiones Coches populares", services: [...], pagination: {...} },
//   { title: "Revisiones Coches en Alemania", services: [...], categoryName: "Coches", country: "DE", pagination: {...} },
//   { title: "Revisiones Coches en Reino Unido", services: [...], categoryName: "Coches", country: "GB", pagination: {...} }
// ]

// ✅ CON CATEGORÍA MOTOS (categoryId=2)
const response = await fetch(
  `/api/SearchService/homepage-wall?categoryId=2&countryCode=ES`
);
const sections = await response.json();

// sections es un array:
// [
//   { title: "Revisiones Motos cerca de mí", services: [...], pagination: {...} },
//   { title: "Revisiones Motos populares", services: [...], pagination: {...} },
//   { title: "Revisiones Motos en Alemania", services: [...], categoryName: "Motos", country: "DE", pagination: {...} },
//   { title: "Revisiones Motos en Reino Unido", services: [...], categoryName: "Motos", country: "GB", pagination: {...} }
// ]

// ✅ CON OTRA CATEGORÍA (ej: Informática, categoryId=9)
const response = await fetch(
  `/api/SearchService/homepage-wall?categoryId=9&countryCode=ES`
);
const sections = await response.json();

// sections es un array (solo 2 secciones, sin secciones específicas por país):
// [
//   { title: "Revisiones Informática cerca de mí", services: [...], pagination: {...} },
//   { title: "Revisiones Informática populares", services: [...], pagination: {...} }
// ]
```

**✅ REGLAS IMPORTANTES:**

1. **`categoryId` es OBLIGATORIO**: Sin él, la petición retornará error 400
2. **Respuesta es un ARRAY**: No hay objetos anidados, simplemente itera el array
3. **Títulos ya vienen formateados**: Usa `section.title` directamente, no necesitas formatear
4. **Secciones específicas solo para Coches y Motos**: 
   - Si `categoryId = 1` (Coches) → Incluye "Coches en Alemania" y "Coches en Reino Unido"
   - Si `categoryId = 2` (Motos) → Incluye "Motos en Alemania" y "Motos en Reino Unido"
   - Otras categorías → Solo "cerca de mí" y "populares"
5. **No necesitas conocer las claves**: Simplemente itera el array con `.map()` o `.forEach()`

**Ejemplo de Implementación React/TypeScript:**

```typescript
// 1. Definir tipos
interface HomepageSection {
  title: string;
  services: SearchServiceHomepageDto[];
  categoryName?: string;
  country?: string;
  pagination: {
    page: number;
    pageSize: number;
    totalCount: number;
    totalPages: number;
    hasNextPage: boolean;
    hasPreviousPage: boolean;
  };
}

interface SearchServiceHomepageDto {
  Id: number;
  CategoryId: number;
  CategoryName: string;
  ServiceTypeId: number;
  ServiceTypeName: string;
  Price: number;
  ImageUrls: string[];
  Expert: {
    Id: number;
    Name: string;
    ProfilePictureUrl: string;
    Country: string;
  };
  CompletedSearches: number;
  AverageRating: number;
}

// 2. Función para cargar el homepage wall
async function fetchHomepageWall(
  categoryId: number,
  latitude?: string,
  longitude?: string,
  countryCode: string = "ES"
): Promise<HomepageSection[]> {
  const params = new URLSearchParams({
    categoryId: categoryId.toString(), // ✅ OBLIGATORIO
  });
  
  if (latitude) params.append('latitude', latitude);
  if (longitude) params.append('longitude', longitude);
  params.append('countryCode', countryCode);
  
  const response = await fetch(
    `/api/SearchService/homepage-wall?${params.toString()}`
  );
  
  if (!response.ok) {
    if (response.status === 400) {
      throw new Error('categoryId es requerido');
    }
    if (response.status === 404) {
      throw new Error('Categoría no encontrada');
    }
    throw new Error(`Error ${response.status}: ${response.statusText}`);
  }
  
  return await response.json(); // ✅ Retorna array directamente
}

// 3. Componente React
function HomepageWall({ categoryId }: { categoryId: number }) {
  const [sections, setSections] = useState<HomepageSection[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  
  useEffect(() => {
    async function loadSections() {
      try {
        setLoading(true);
        setError(null);
        
        // Obtener ubicación del usuario (opcional)
        const position = await getCurrentPosition(); // Tu función de geolocalización
        
        const data = await fetchHomepageWall(
          categoryId,
          position?.latitude?.toString(),
          position?.longitude?.toString(),
          'ES'
        );
        
        setSections(data);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Error desconocido');
      } finally {
        setLoading(false);
      }
    }
    
    loadSections();
  }, [categoryId]);
  
  if (loading) return <LoadingSpinner />;
  if (error) return <ErrorMessage message={error} />;
  
  return (
    <div className="homepage-wall">
      {/* ✅ Simplemente itera el array - no necesitas conocer las claves */}
      {sections.map((section, index) => (
        <Section
          key={index}
          title={section.title} // ✅ Título ya viene formateado
          services={section.services}
          pagination={section.pagination}
        />
      ))}
    </div>
  );
}

// 4. Componente de Sección
function Section({ 
  title, 
  services, 
  pagination 
}: { 
  title: string; 
  services: SearchServiceHomepageDto[]; 
  pagination: HomepageSection['pagination'];
}) {
  return (
    <div className="section">
      <h2>{title}</h2>
      <div className="services-grid">
        {services.map(service => (
          <ServiceCard key={service.Id} service={service} />
        ))}
      </div>
      {pagination.hasNextPage && (
        <button>Cargar más</button>
      )}
    </div>
  );
}
```

**Ejemplo con JavaScript puro:**

```javascript
async function loadHomepageWall(categoryId, latitude, longitude, countryCode = 'ES') {
  // ✅ categoryId es obligatorio
  const params = new URLSearchParams({
    categoryId: categoryId.toString(),
  });
  
  if (latitude) params.append('latitude', latitude);
  if (longitude) params.append('longitude', longitude);
  params.append('countryCode', countryCode);
  
  try {
    const response = await fetch(
      `/api/SearchService/homepage-wall?${params.toString()}`
    );
    
    if (!response.ok) {
      const error = await response.json();
      throw new Error(error.message || `Error ${response.status}`);
    }
    
    // ✅ La respuesta es un array directamente
    const sections = await response.json();
    
    // ✅ Itera el array - no necesitas conocer las claves
    sections.forEach((section, index) => {
      renderSection({
        title: section.title, // ✅ Usa el título tal cual viene
        services: section.services,
        pagination: section.pagination
      });
    });
  } catch (error) {
    console.error('Error cargando homepage wall:', error);
    showError(error.message);
  }
}

// Uso
loadHomepageWall(1, '40.4168', '-3.7038', 'ES'); // Coches
loadHomepageWall(2, null, null, 'ES'); // Motos
loadHomepageWall(9, null, null, 'ES'); // Informática
```

---

## 💻 **EJEMPLOS DE IMPLEMENTACIÓN**

### **Ejemplo 1: Carga Inicial del Mapa**

```typescript
// 1. Cargar marcadores iniciales (sin bounds)
async function loadInitialMapMarkers(categoryId: number, serviceTypeId: number) {
  const response = await fetch(
    `/api/SearchService/map-markers?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}&limit=500`
  );
  const data = await response.json();
  
  // Renderizar marcadores en el mapa
  data.markers.forEach(marker => {
    addMarkerToMap({
      id: marker.id,
      position: { lat: parseFloat(marker.latitude), lng: parseFloat(marker.longitude) },
      price: marker.price
    });
  });
}

// 2. Cuando el usuario mueve el mapa, cargar marcadores del viewport
function onMapBoundsChanged(bounds: MapBounds) {
  const response = await fetch(
    `/api/SearchService/map-markers?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}&northeastLat=${bounds.northeast.lat}&northeastLng=${bounds.northeast.lng}&southwestLat=${bounds.southwest.lat}&southwestLng=${bounds.southwest.lng}&zoom=${map.getZoom()}&limit=200`
  );
  const data = await response.json();
  
  // Actualizar marcadores visibles
  updateVisibleMarkers(data.markers);
}
```

### **Ejemplo 2: Cargar Sidebar con Información Completa**

```typescript
// Detectar servicios visibles en el viewport
function getVisibleServiceIds(): number[] {
  const visibleMarkers = getMarkersInViewport();
  return visibleMarkers.map(marker => marker.id);
}

// Cargar información completa para el sidebar
async function loadSidebarInfo() {
  const visibleIds = getVisibleServiceIds();
  
  if (visibleIds.length === 0) {
    clearSidebar();
    return;
  }
  
  // Limitar a 30 servicios máximo (recomendado)
  const limitedIds = visibleIds.slice(0, 30);
  
  const response = await fetch(
    `/api/SearchService/map-sidebar?serviceIds=${limitedIds.join(',')}`
  );
  const data = await response.json();
  
  // Renderizar cards en el sidebar
  renderSidebarCards(data.services);
}

// Renderizar una card del sidebar
function renderSidebarCard(service: MapSidebarServiceDto) {
  return `
    <div class="service-card">
      <img src="${service.imageUrls[0]}" alt="${service.serviceTypeName}" />
      <h3>${service.expertName}</h3>
      <p>${service.serviceTypeName}</p>
      <p>${service.serviceDescription}</p>
      <p>€${service.price}</p>
      <div class="rating">
        ⭐ ${service.averageRating} (${service.totalReviews} reseñas)
      </div>
      ${service.currentAvailability ? `
        <div class="availability">
          Disponible: ${formatAvailability(service.currentAvailability)}
        </div>
      ` : ''}
    </div>
  `;
}

function formatAvailability(availability: CurrentExpertAvailabilityDto): string {
  const days = availability.daysOfWeek.map(day => day.substring(0, 3)).join(', ');
  const start = availability.startTime.substring(0, 5); // "09:00"
  const end = availability.endTime.substring(0, 5);   // "18:00"
  return `${days} ${start}-${end}`;
}
```

### **Ejemplo 3: Homepage Wall con Filtro de Categoría**

```typescript
// Cargar homepage wall SIN filtro (todas las categorías)
async function loadHomepageWallAllCategories() {
  const response = await fetch(
    `/api/SearchService/homepage-wall?latitude=40.4168&longitude=-3.7038&countryCode=ES&locationRange=50&nearbyPage=1&nearbyPageSize=20&popularPage=1&popularPageSize=20`
  );
  const data = await response.json();
  
  // Mostrar servicios cercanos
  renderServices(data.nearbyServices.services, 'nearby-section');
  
  // Mostrar servicios populares
  renderServices(data.popularServices.services, 'popular-section');
  
  // Mostrar secciones específicas
  Object.values(data.specificSections).forEach(section => {
    renderSection(section, section.categoryName + '_' + section.country);
  });
}

// Cargar homepage wall CON filtro de categoría (solo Coches)
async function loadHomepageWallCarsOnly() {
  const response = await fetch(
    `/api/SearchService/homepage-wall?categoryId=1&latitude=40.4168&longitude=-3.7038&countryCode=ES&locationRange=50`
  );
  const data = await response.json();
  
  // Solo servicios de Coches (categoría 1)
  renderServices(data.nearbyServices.services, 'nearby-section');
  renderServices(data.popularServices.services, 'popular-section');
  
  // Solo secciones de Coches
  Object.values(data.specificSections).forEach(section => {
    if (section.categoryName === 'Coches') {
      renderSection(section, section.categoryName + '_' + section.country);
    }
  });
}
```

---

## 🚀 **MEJORES PRÁCTICAS**

### **1. Mapa - Carga de Marcadores**

✅ **HACER:**
- Cargar marcadores iniciales sin bounds (más rápido)
- Usar debounce (300-500ms) al mover el mapa para evitar llamadas excesivas
- Limitar marcadores visibles según zoom (el backend ya lo hace automáticamente)
- Cargar sidebar solo cuando hay marcadores visibles

❌ **NO HACER:**
- Cargar marcadores en cada movimiento del mapa (usar debounce)
- Cargar sidebar para más de 30 servicios a la vez
- Cargar marcadores sin especificar bounds cuando el mapa está visible

### **2. Mapa - Sidebar**

✅ **HACER:**
- Detectar servicios visibles en el viewport antes de cargar
- Limitar a 20-30 servicios máximo por llamada
- Mostrar loading state mientras se carga
- Cachear resultados para evitar llamadas duplicadas

❌ **NO HACER:**
- Cargar sidebar para todos los marcadores del mapa
- Hacer llamadas individuales por cada servicio
- Ignorar el campo `imageUrls` (ahora es array, no string)

### **3. Homepage Wall**

✅ **HACER:**
- Usar `categoryId` cuando quieras mostrar solo una categoría específica
- Omitir `categoryId` cuando quieras mostrar todas las categorías
- Usar paginación para cargar más servicios
- Cachear resultados por categoría

❌ **NO HACER:**
- Asumir que siempre devuelve todas las categorías (ahora depende de `categoryId`)
- Ignorar la paginación
- Hacer múltiples llamadas cuando puedes usar una sola

### **4. Rendimiento General**

✅ **HACER:**
- Implementar debounce para llamadas del mapa
- Usar loading states
- Cachear respuestas cuando sea posible
- Lazy load de imágenes

❌ **NO HACER:**
- Hacer llamadas en cada evento de scroll/move
- Cargar todas las imágenes de una vez
- Ignorar los límites recomendados

---

## 📊 **RESUMEN DE CAMBIOS**

### **Mapa - Sidebar (`map-sidebar`)**
- ✅ **NUEVO:** `imageUrls` es ahora un array (mínimo 3 imágenes)
- ✅ **NUEVO:** `serviceDescription` - Descripción del servicio
- ✅ **NUEVO:** `currentAvailability` - Horario de disponibilidad del experto
- ❌ **ELIMINADO:** `firstImageUrl` (ahora usar `imageUrls[0]`)

### **Homepage Wall**
- ✅ **NUEVO:** Parámetro `categoryId` para filtrar por categoría
- ✅ **SIN `categoryId`:** Devuelve servicios de TODAS las categorías
- ✅ **CON `categoryId`:** Devuelve SOLO servicios de la categoría especificada

---

## 🔗 **ENDPOINTS COMPLETOS**

### **Mapa:**
- `GET /api/SearchService/map-markers` - Marcadores ligeros
- `GET /api/SearchService/map-sidebar` - Información completa del sidebar

### **Homepage:**
- `GET /api/SearchService/homepage-wall` - Muro de inicio completo

---

**Última actualización:** 2025-01-XX
**Versión API:** 1.0

