# 🆕 Guía de Nuevas Mejoras - Frontend

## 📋 Resumen de Cambios Recientes

Se han agregado dos nuevas mejoras importantes a la API:

1. **País en Reseñas (Reviews)**: Cada reseña ahora muestra el país donde se realizó la contratación
2. **Precio en Mapa de Expertos**: El endpoint de mapa ahora incluye el precio del servicio

---

## 1. 🏳️ País en Reseñas (Reviews)

### ¿Qué cambió?

Todas las reseñas ahora incluyen el campo `Country` que indica el país donde se realizó la contratación. Esto permite mostrar la bandera correspondiente en cada reseña.

### Campos Nuevos

```typescript
interface ReviewDto {
  id: number;
  score: number;
  description: string;
  createdAt: Date;
  reviewer: UserDto;
  imageUrls: string[];
  
  // ✅ NUEVO
  country?: string;  // ISO 3166-1 alpha-2 (ej: "ES", "MX", "US")
}
```

### Ejemplo de Respuesta

```json
{
  "id": 1,
  "score": 5,
  "description": "Excelente servicio, muy profesional",
  "createdAt": "2025-01-10T10:00:00Z",
  "reviewer": {
    "id": 1,
    "name": "María García",
    "email": "maria@example.com"
  },
  "imageUrls": [],
  "country": "ES"  // ✅ España
}
```

### Endpoints Afectados

- ✅ `GET /api/reviews/expert/{expertId}` - Lista de reseñas del experto
- ✅ `GET /api/searchservice/{id}` - Detalles del servicio (incluye `expert.reviews[]`)
- ✅ `GET /api/searchservice` - Listado de servicios (incluye `expert.reviews[]`)
- ✅ `GET /api/searchhire/{id}/details-complete` - Detalles completos (incluye review)

### Implementación en Frontend

```typescript
// Función helper para obtener emoji de bandera
function getCountryFlag(countryCode: string): string {
  const flags: Record<string, string> = {
    'ES': '🇪🇸',  // España
    'MX': '🇲🇽',  // México
    'US': '🇺🇸',  // Estados Unidos
    'AR': '🇦🇷',  // Argentina
    'CO': '🇨🇴',  // Colombia
    'CL': '🇨🇱',  // Chile
    'PE': '🇵🇪',  // Perú
    // ... agregar más países según necesidad
  };
  return flags[countryCode] || '🌍';
}

// Componente de reseña
function ReviewCard({ review }: { review: ReviewDto }) {
  const flag = review.country ? getCountryFlag(review.country) : '🌍';
  
  return (
    <div className="review-card">
      <div className="review-header">
        <span className="flag">{flag}</span>
        <span className="reviewer-name">{review.reviewer.name}</span>
        <span className="score">⭐ {review.score}/5</span>
      </div>
      <p className="description">{review.description}</p>
      <span className="date">{formatDate(review.createdAt)}</span>
    </div>
  );
}
```

### Notas Importantes

- El campo `Country` proviene de `SearchHire.ExpertCountry` (snapshot al momento de crear la contratación)
- Si una reseña no tiene país (puede ser null), muestra una bandera genérica o no muestres bandera
- Cada reseña muestra el país donde se realizó ESA contratación específica, no el país actual del experto

---

## 2. 💰 Precio en Mapa de Expertos

### ¿Qué cambió?

El endpoint `GET /api/searchservice/map-experts` ahora incluye el precio del servicio para cada experto, permitiendo mostrar precios directamente en el mapa sin necesidad de que el usuario seleccione una ubicación.

### Campo Nuevo

```typescript
interface ExpertMapDto {
  id: number;
  name: string;
  profilePictureUrl: string;
  averageRating: number;
  totalReviews: number;
  completedSearches: number;
  registeredSince: Date;
  latitude: string;
  longitude: string;
  
  // ✅ NUEVO
  price: number;  // Precio en euros
}
```

### Ejemplo de Respuesta

```json
{
  "experts": [
    {
      "id": 40,
      "name": "Diego Castilla",
      "profilePictureUrl": "https://storage.googleapis.com/...",
      "averageRating": 0,
      "totalReviews": 0,
      "completedSearches": 0,
      "registeredSince": "2025-11-22T19:43:11.653346Z",
      "latitude": "41.54660957575336",
      "longitude": "-0.9480622482776679",
      "price": 150.00  // ✅ NUEVO
    },
    {
      "id": 34,
      "name": "Diego Castilla Abella",
      "profilePictureUrl": "https://storage.googleapis.com/...",
      "averageRating": 4,
      "totalReviews": 5,
      "completedSearches": 0,
      "registeredSince": "2025-09-14T17:55:59.171923Z",
      "latitude": "-28.155790867269413",
      "longitude": "132.78048084507316",
      "price": 200.00  // ✅ NUEVO
    }
  ],
  "totalCount": 2
}
```

### ⚠️ IMPORTANTE: ¿Qué Endpoint Usar?

#### ✅ USAR: `GET /api/searchservice/map-experts`

**Endpoint:** `GET /api/searchservice/map-experts?categoryId={id}&serviceTypeId={id}`

**Ventajas:**
- ✅ Muestra TODOS los expertos disponibles sin necesidad de seleccionar ubicación
- ✅ Incluye el precio del servicio
- ✅ Perfecto para la vista inicial del mapa
- ✅ Permite al usuario ver todos los expertos disponibles antes de filtrar por ubicación

**Cuándo usarlo:**
- Vista inicial del mapa (sin filtros de ubicación)
- Cuando quieres mostrar todos los expertos disponibles
- Cuando necesitas mostrar precios en el mapa

#### ❌ NO USAR: `GET /api/searchservice` para el mapa

**Endpoint:** `GET /api/searchservice?categoryId={id}&serviceTypeId={id}&latitude={lat}&longitude={lon}&locationRange={range}`

**Desventajas:**
- ❌ Solo muestra expertos cuando el usuario selecciona una ubicación
- ❌ Si no hay expertos en el rango seleccionado, no muestra nada
- ❌ No es útil para la vista inicial del mapa
- ❌ El usuario debe seleccionar ubicación antes de ver expertos

**Cuándo usarlo:**
- Solo cuando el usuario ha seleccionado una ubicación específica
- Para filtrar expertos por proximidad después de la selección inicial
- Para mostrar servicios detallados dentro de un rango

### Estrategia Recomendada

```typescript
// 1. Vista inicial: Cargar todos los expertos con precios
async function loadMapExperts(categoryId: number, serviceTypeId: number) {
  const response = await fetch(
    `/api/searchservice/map-experts?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}`
  );
  const data: ExpertMapResponseDto = await response.json();
  
  // Mostrar todos los expertos en el mapa con sus precios
  data.experts.forEach(expert => {
    addMarkerToMap(expert, {
      showPrice: true,  // Mostrar precio en el marcador
      price: expert.price
    });
  });
}

// 2. Cuando el usuario selecciona una ubicación: Filtrar por proximidad
async function filterByLocation(
  categoryId: number, 
  serviceTypeId: number,
  latitude: number,
  longitude: number,
  range: number
) {
  const response = await fetch(
    `/api/searchservice?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}&latitude=${latitude}&longitude=${longitude}&locationRange=${range}`
  );
  const services: SearchServiceDetailDto[] = await response.data;
  
  // Mostrar solo servicios dentro del rango
  // (Estos ya tienen información más detallada)
}
```

### Implementación en Frontend

```typescript
// Componente de marcador en el mapa
function ExpertMarker({ expert }: { expert: ExpertMapDto }) {
  return (
    <div className="expert-marker">
      <img src={expert.profilePictureUrl} alt={expert.name} />
      <div className="marker-info">
        <h3>{expert.name}</h3>
        <div className="rating">
          ⭐ {expert.averageRating.toFixed(1)} ({expert.totalReviews})
        </div>
        {/* ✅ NUEVO: Mostrar precio */}
        <div className="price">
          {expert.price.toFixed(2)}€
        </div>
      </div>
    </div>
  );
}

// Componente de mapa
function ExpertsMap({ categoryId, serviceTypeId }: Props) {
  const [experts, setExperts] = useState<ExpertMapDto[]>([]);
  
  useEffect(() => {
    // ✅ Cargar todos los expertos con precios
    loadMapExperts(categoryId, serviceTypeId).then(setExperts);
  }, [categoryId, serviceTypeId]);
  
  return (
    <Map>
      {experts.map(expert => (
        <ExpertMarker 
          key={expert.id} 
          expert={expert}
          position={[parseFloat(expert.latitude), parseFloat(expert.longitude)]}
        />
      ))}
    </Map>
  );
}
```

### Notas Importantes

- El precio corresponde al primer servicio del experto que coincida con `categoryId` y `serviceTypeId`
- Si un experto tiene múltiples servicios del mismo tipo, se muestra el precio del primero encontrado
- El precio está en euros (EUR)
- Usa `map-experts` para la vista inicial y `GetAllServices` solo cuando el usuario filtre por ubicación

---

## 📊 Comparación de Endpoints

| Endpoint | Muestra Expertos | Incluye Precio | Requiere Ubicación | Uso Recomendado |
|----------|------------------|----------------|-------------------|-----------------|
| `GET /api/searchservice/map-experts` | ✅ Todos | ✅ Sí | ❌ No | Vista inicial del mapa |
| `GET /api/searchservice` | ⚠️ Solo en rango | ✅ Sí | ✅ Sí | Filtrado por proximidad |

---

## ✅ Checklist de Implementación

### País en Reseñas
- [ ] Agregar campo `Country` a la interfaz `ReviewDto`
- [ ] Crear función helper para obtener emoji de bandera desde código ISO
- [ ] Actualizar componente de reseña para mostrar bandera
- [ ] Probar con diferentes países (ES, MX, US, etc.)

### Precio en Mapa
- [ ] Agregar campo `Price` a la interfaz `ExpertMapDto`
- [ ] Cambiar endpoint de mapa de `GetAllServices` a `map-experts`
- [ ] Actualizar componente de marcador para mostrar precio
- [ ] Verificar que los precios se muestran correctamente
- [ ] Implementar filtrado por ubicación usando `GetAllServices` cuando el usuario seleccione ubicación

---

## 🐛 Troubleshooting

### Problema: No se muestra el precio en el mapa

**Solución:** Verifica que estás usando el endpoint `GET /api/searchservice/map-experts` y no `GET /api/searchservice`. El segundo requiere parámetros de ubicación.

### Problema: No se muestra la bandera en las reseñas

**Solución:** Verifica que el campo `Country` está presente en la respuesta. Si es `null`, muestra una bandera genérica o no muestres bandera.

### Problema: El precio es 0 o no aparece

**Solución:** Verifica que el experto tiene un servicio activo con el `categoryId` y `serviceTypeId` especificados. El precio corresponde al primer servicio encontrado.

