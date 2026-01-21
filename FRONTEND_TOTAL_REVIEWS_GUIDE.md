# 📊 Guía Frontend: Campo `TotalReviews` en `map-experts`

## ✅ Cambio Implementado

Se ha agregado el campo `TotalReviews` al DTO `SearchServiceDetailDto` que se devuelve cuando llamas al endpoint `map-experts` con parámetros de ubicación (`latitude`, `longitude`, `locationRange`).

---

## 🎯 Endpoint Afectado

**Endpoint:** `GET /api/SearchService/map-experts`

**Cuándo se devuelve `TotalReviews`:**
- ✅ Cuando se proporcionan `latitude`, `longitude` y `locationRange`
- ✅ Cuando se proporcionan `northeastLat`, `northeastLng`, `southwestLat`, `southwestLng` (bounds del mapa)

**Cuándo NO se devuelve `TotalReviews`:**
- ❌ Cuando solo se proporcionan `categoryId` y `serviceTypeId` (carga inicial sin ubicación) - en este caso se devuelve `ExpertMapResponseDto` que ya tiene `TotalReviews`

---

## 📋 Estructura de Respuesta Actualizada

### **Con parámetros de ubicación:**

```typescript
GET /api/SearchService/map-experts?categoryId=2&serviceTypeId=1&latitude=42.4685225&longitude=-2.4257682499999995&locationRange=25
```

**Respuesta:**
```json
{
  "services": [
    {
      "id": 1,
      "categoryId": 2,
      "serviceTypeId": 1,
      "serviceTypeName": "Inspección Técnica",
      "price": 150.00,
      "conditions": "...",
      "durationInHours": 2,
      "imageUrls": ["https://..."],
      "categoryName": "Automoción",
      "averageRating": 4.5,
      "totalReviews": 10,  // ✅ NUEVO: Total de reseñas del experto
      "completedSearches": 25,
      "expert": {
        "id": 1,
        "profilePictureUrl": "https://...",
        "description": "...",
        "user": {
          "id": 1,
          "name": "Juan Pérez",
          "email": "juan@example.com"
        },
        "reviews": [  // ⚠️ Puede estar paginada o limitada
          {
            "id": 1,
            "score": 5,
            "description": "Excelente servicio",
            "createdAt": "2024-01-15T10:00:00Z",
            "reviewer": {...},
            "imageUrls": [...],
            "country": "ES"
          }
          // ... más reviews (puede que no sean todas)
        ],
        "latitude": "42.4685225",
        "longitude": "-2.4257682499999995",
        "country": "ES",
        "city": "Logroño",
        "timezone": "Europe/Madrid"
      }
    }
  ],
  "pagination": {
    "page": 1,
    "pageSize": 50,
    "totalCount": 1,
    "totalPages": 1,
    "hasNextPage": false,
    "hasPreviousPage": false
  }
}
```

---

## 🔍 Diferencia Importante: `TotalReviews` vs `Expert.Reviews.length`

### ❌ **NO uses `Expert.Reviews.length` para el total:**

```typescript
// ❌ INCORRECTO - Puede ser incorrecto si hay paginación
const totalReviews = service.expert.reviews.length;
```

**Problema:** La lista `Expert.Reviews` puede estar:
- Paginada (solo muestra algunas reviews)
- Limitada (solo muestra las últimas N reviews)
- Vacía (si no se cargaron las reviews)

### ✅ **Usa el campo `TotalReviews` directamente:**

```typescript
// ✅ CORRECTO - Siempre muestra el total real
const totalReviews = service.totalReviews;
```

---

## 💻 Ejemplos de Código

### **Ejemplo 1: Mostrar total de reseñas en una card**

```typescript
interface SearchServiceDetailDto {
  id: number;
  averageRating: number;
  totalReviews: number;  // ✅ NUEVO
  completedSearches: number;
  expert: {
    reviews: ReviewDto[];  // ⚠️ Puede estar limitada
  };
  // ... otros campos
}

function ServiceCard({ service }: { service: SearchServiceDetailDto }) {
  return (
    <div className="service-card">
      <div className="rating-section">
        <span className="stars">⭐ {service.averageRating.toFixed(1)}</span>
        <span className="reviews-count">
          ({service.totalReviews} {service.totalReviews === 1 ? 'reseña' : 'reseñas'})
        </span>
      </div>
      <div className="completed-searches">
        {service.completedSearches} contrataciones completadas
      </div>
    </div>
  );
}
```

### **Ejemplo 2: Llamada al endpoint con ubicación**

```typescript
async function getServicesWithLocation(
  categoryId: number,
  serviceTypeId: number,
  latitude: number,
  longitude: number,
  locationRange: number
) {
  const params = new URLSearchParams({
    categoryId: categoryId.toString(),
    serviceTypeId: serviceTypeId.toString(),
    latitude: latitude.toString(),
    longitude: longitude.toString(),
    locationRange: locationRange.toString(),
  });

  const response = await fetch(
    `/api/SearchService/map-experts?${params.toString()}`
  );

  if (!response.ok) {
    throw new Error('Error al obtener servicios');
  }

  const data = await response.json();
  
  // ✅ Usar totalReviews directamente
  data.services.forEach((service: SearchServiceDetailDto) => {
    console.log(`Servicio ${service.id}:`);
    console.log(`  - Rating: ${service.averageRating}`);
    console.log(`  - Total reseñas: ${service.totalReviews}`);  // ✅ NUEVO
    console.log(`  - Reseñas cargadas: ${service.expert.reviews.length}`);  // Puede ser menor
  });

  return data;
}
```

### **Ejemplo 3: Componente de lista de servicios**

```typescript
function ServiceList({ services }: { services: SearchServiceDetailDto[] }) {
  return (
    <div className="service-list">
      {services.map((service) => (
        <div key={service.id} className="service-item">
          <h3>{service.expert.user.name}</h3>
          
          {/* ✅ Mostrar rating y total de reseñas */}
          <div className="rating-info">
            <span className="rating">
              ⭐ {service.averageRating.toFixed(1)}
            </span>
            <span className="reviews">
              {service.totalReviews} {service.totalReviews === 1 ? 'reseña' : 'reseñas'}
            </span>
          </div>

          {/* Mostrar algunas reviews (si están cargadas) */}
          {service.expert.reviews.length > 0 && (
            <div className="reviews-preview">
              <p>Últimas {service.expert.reviews.length} reseñas:</p>
              {service.expert.reviews.map((review) => (
                <div key={review.id} className="review-item">
                  <p>{review.description}</p>
                  <span>⭐ {review.score}/5</span>
                </div>
              ))}
            </div>
          )}

          {/* Mostrar que hay más reseñas si el total es mayor */}
          {service.totalReviews > service.expert.reviews.length && (
            <p className="more-reviews">
              Ver todas las {service.totalReviews} reseñas
            </p>
          )}
        </div>
      ))}
    </div>
  );
}
```

### **Ejemplo 4: Validación y manejo de casos edge**

```typescript
function displayReviewInfo(service: SearchServiceDetailDto) {
  // ✅ Validar que totalReviews existe
  const totalReviews = service.totalReviews ?? 0;
  const averageRating = service.averageRating ?? 0;
  
  // Casos especiales
  if (totalReviews === 0) {
    return "Sin reseñas aún";
  }
  
  if (totalReviews === 1) {
    return `⭐ ${averageRating.toFixed(1)} (1 reseña)`;
  }
  
  return `⭐ ${averageRating.toFixed(1)} (${totalReviews} reseñas)`;
}
```

---

## 🔄 Migración desde Código Existente

### **Antes (Incorrecto):**

```typescript
// ❌ Código antiguo - puede ser incorrecto
const totalReviews = service.expert?.reviews?.length ?? 0;
```

### **Después (Correcto):**

```typescript
// ✅ Código nuevo - siempre correcto
const totalReviews = service.totalReviews ?? 0;
```

### **Búsqueda y reemplazo recomendado:**

Busca en tu código:
```typescript
service.expert.reviews.length
service.expert?.reviews?.length
expert.reviews.length
```

Reemplaza por:
```typescript
service.totalReviews
service.totalReviews ?? 0
```

---

## 📊 Comparación de Campos

| Campo | Tipo | Descripción | Cuándo usar |
|-------|------|-------------|-------------|
| `totalReviews` | `number` | **Total real de reseñas** del experto | ✅ **SIEMPRE** para mostrar el total |
| `expert.reviews.length` | `number` | Cantidad de reseñas **cargadas** en la respuesta | ⚠️ Solo para mostrar las reseñas visibles |
| `averageRating` | `number` | Promedio de todas las reseñas | ✅ Para mostrar el rating |

---

## ⚠️ Notas Importantes

1. **`TotalReviews` siempre refleja el total real**, incluso si `Expert.Reviews` está vacía o limitada.

2. **`Expert.Reviews` puede estar paginada o limitada**, por lo que su longitud puede ser menor que `TotalReviews`.

3. **Si `TotalReviews` es 0**, significa que el experto no tiene reseñas aún.

4. **Si `TotalReviews` > `Expert.Reviews.length`**, significa que hay más reseñas disponibles (posiblemente en otra página o endpoint).

---

## 🧪 Casos de Prueba

### **Caso 1: Experto con 10 reseñas, pero solo se cargan 3**

```json
{
  "totalReviews": 10,  // ✅ Total real
  "expert": {
    "reviews": [/* solo 3 reseñas */]  // ⚠️ Solo algunas cargadas
  }
}
```

**Frontend debe mostrar:** "⭐ 4.5 (10 reseñas)" y "Mostrando 3 de 10 reseñas"

### **Caso 2: Experto sin reseñas**

```json
{
  "totalReviews": 0,  // ✅ Sin reseñas
  "averageRating": 0,
  "expert": {
    "reviews": []  // ⚠️ Vacío
  }
}
```

**Frontend debe mostrar:** "Sin reseñas aún"

### **Caso 3: Experto con 1 reseña**

```json
{
  "totalReviews": 1,  // ✅ Una reseña
  "averageRating": 5.0,
  "expert": {
    "reviews": [/* 1 reseña */]
  }
}
```

**Frontend debe mostrar:** "⭐ 5.0 (1 reseña)"

---

## ✅ Checklist de Implementación

- [ ] Actualizar interfaces TypeScript para incluir `totalReviews: number`
- [ ] Reemplazar `service.expert.reviews.length` por `service.totalReviews`
- [ ] Actualizar componentes que muestran el número de reseñas
- [ ] Agregar validación para casos edge (0 reseñas, 1 reseña)
- [ ] Probar con servicios que tienen muchas reseñas
- [ ] Probar con servicios sin reseñas
- [ ] Verificar que el texto sea correcto (singular/plural)

---

## 📞 Soporte

Si tienes dudas o encuentras algún problema con el campo `TotalReviews`, contacta al equipo de backend.

---

**Última actualización:** 2025-01-21
**Versión del endpoint:** `GET /api/SearchService/map-experts`
