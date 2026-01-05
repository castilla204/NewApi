# 📱 Documentación Frontend - Endpoint Homepage Wall

## 🎯 Resumen

El endpoint `GET /api/SearchService/homepage-wall` devuelve un **array plano** con secciones de servicios para mostrar en el homepage. Cada sección incluye servicios filtrados por categoría, con títulos descriptivos y paginación.

---

## 🔌 Endpoint: GET /api/SearchService/homepage-wall

### ⚠️ IMPORTANTE: `categoryId` es OBLIGATORIO

El parámetro `categoryId` es **requerido** y debe ser el primer parámetro en la URL.

### Parámetros de Query:

```typescript
{
  categoryId: number;           // ✅ REQUERIDO - ID de la categoría
  latitude?: string;            // OPCIONAL - Latitud del usuario (ej: "41.7919123")
  longitude?: string;           // OPCIONAL - Longitud del usuario (ej: "-2.5594214")
  countryCode?: string;         // OPCIONAL - Código ISO del país (ej: "ES", "DE", "GB")
  locationRange?: number;       // OPCIONAL - Rango en km (default: 50)
  nearbyPage?: number;          // OPCIONAL - Página para servicios cercanos (default: 1)
  nearbyPageSize?: number;      // OPCIONAL - Tamaño de página cercanos (default: 20, max: 50)
  popularPage?: number;         // OPCIONAL - Página para servicios populares (default: 1)
  popularPageSize?: number;     // OPCIONAL - Tamaño de página populares (default: 20, max: 50)
}
```

### Ejemplo de Request:

```typescript
// Con categoría Coches (ID: 1)
GET /api/SearchService/homepage-wall?categoryId=1&latitude=41.7919123&longitude=-2.5594214&countryCode=ES&locationRange=50

// Con categoría Motos (ID: 2)
GET /api/SearchService/homepage-wall?categoryId=2&countryCode=ES

// Con otra categoría (ej: Informática, ID: 9)
GET /api/SearchService/homepage-wall?categoryId=9&countryCode=ES
```

---

## 📦 Estructura de la Respuesta

La respuesta es un **array plano** de objetos, cada uno representa una sección:

```typescript
type HomepageWallResponse = Array<{
  title: string;                    // Título de la sección (ej: "Revisiones Coches cerca de mí")
  services: SearchServiceHomepageDto[];  // Array de servicios
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
```

---

## 🎨 Ejemplos de Respuesta

### Ejemplo 1: Categoría Coches (categoryId=1)

```json
[
  {
    "title": "Revisiones Coches cerca de mí",
    "services": [ /* ... */ ],
    "pagination": {
      "page": 1,
      "pageSize": 20,
      "totalCount": 15,
      "totalPages": 1,
      "hasNextPage": false,
      "hasPreviousPage": false
    }
  },
  {
    "title": "Revisiones Coches populares",
    "services": [ /* ... */ ],
    "pagination": { /* ... */ }
  },
  {
    "title": "Revisiones Coches en Alemania",
    "services": [ /* ... */ ],
    "categoryName": "Coches",
    "country": "DE",
    "pagination": { /* ... */ }
  },
  {
    "title": "Revisiones Coches en Reino Unido",
    "services": [ /* ... */ ],
    "categoryName": "Coches",
    "country": "GB",
    "pagination": { /* ... */ }
  }
]
```

### Ejemplo 2: Categoría Motos (categoryId=2)

```json
[
  {
    "title": "Revisiones Motos cerca de mí",
    "services": [ /* ... */ ],
    "pagination": { /* ... */ }
  },
  {
    "title": "Revisiones Motos populares",
    "services": [ /* ... */ ],
    "pagination": { /* ... */ }
  },
  {
    "title": "Revisiones Motos en Alemania",
    "services": [ /* ... */ ],
    "categoryName": "Motos",
    "country": "DE",
    "pagination": { /* ... */ }
  },
  {
    "title": "Revisiones Motos en Reino Unido",
    "services": [ /* ... */ ],
    "categoryName": "Motos",
    "country": "GB",
    "pagination": { /* ... */ }
  }
]
```

### Ejemplo 3: Otra categoría (ej: Informática, categoryId=9)

```json
[
  {
    "title": "Revisiones Informática cerca de mí",
    "services": [ /* ... */ ],
    "pagination": { /* ... */ }
  },
  {
    "title": "Revisiones Informática populares",
    "services": [ /* ... */ ],
    "pagination": { /* ... */ }
  }
]
```

**Nota**: Para categorías que NO son Coches o Motos, solo se devuelven las secciones "cerca de mí" y "populares".

---

## 💻 Implementación Frontend

### Ejemplo con TypeScript/React:

```typescript
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

async function fetchHomepageWall(
  categoryId: number,
  latitude?: string,
  longitude?: string,
  countryCode: string = "ES"
): Promise<HomepageSection[]> {
  const params = new URLSearchParams({
    categoryId: categoryId.toString(),
  });
  
  if (latitude) params.append('latitude', latitude);
  if (longitude) params.append('longitude', longitude);
  if (countryCode) params.append('countryCode', countryCode);
  
  const response = await fetch(
    `/api/SearchService/homepage-wall?${params.toString()}`
  );
  
  if (!response.ok) {
    throw new Error(`Error ${response.status}: ${response.statusText}`);
  }
  
  return await response.json();
}

// Uso en componente React
function HomepageWall({ categoryId }: { categoryId: number }) {
  const [sections, setSections] = useState<HomepageSection[]>([]);
  const [loading, setLoading] = useState(true);
  
  useEffect(() => {
    async function loadSections() {
      try {
        setLoading(true);
        const data = await fetchHomepageWall(categoryId);
        setSections(data);
      } catch (error) {
        console.error('Error cargando homepage wall:', error);
      } finally {
        setLoading(false);
      }
    }
    
    loadSections();
  }, [categoryId]);
  
  if (loading) return <LoadingSpinner />;
  
  return (
    <div>
      {sections.map((section, index) => (
        <Section
          key={index}
          title={section.title}
          services={section.services}
          pagination={section.pagination}
        />
      ))}
    </div>
  );
}
```

### Ejemplo con JavaScript puro:

```javascript
async function loadHomepageWall(categoryId, latitude, longitude, countryCode = 'ES') {
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
      throw new Error(`Error ${response.status}`);
    }
    
    const sections = await response.json();
    
    // Renderizar secciones
    sections.forEach((section, index) => {
      renderSection({
        title: section.title,
        services: section.services,
        pagination: section.pagination
      });
    });
  } catch (error) {
    console.error('Error:', error);
  }
}
```

---

## 📋 Reglas de Negocio

### 1. Secciones siempre presentes:
- ✅ **"Revisiones [Categoría] cerca de mí"**: Servicios cercanos a la ubicación del usuario
- ✅ **"Revisiones [Categoría] populares"**: Servicios más populares de la categoría

### 2. Secciones específicas por país (solo Coches y Motos):
- ✅ **"Revisiones Coches en Alemania"**: Solo si `categoryId = 1` (Coches)
- ✅ **"Revisiones Coches en Reino Unido"**: Solo si `categoryId = 1` (Coches)
- ✅ **"Revisiones Motos en Alemania"**: Solo si `categoryId = 2` (Motos)
- ✅ **"Revisiones Motos en Reino Unido"**: Solo si `categoryId = 2` (Motos)

### 3. Otras categorías:
- ❌ **NO** incluyen secciones específicas por país
- ✅ Solo incluyen "cerca de mí" y "populares"

---

## ⚠️ Manejo de Errores

### Error 400 Bad Request:
```json
{
  "message": "categoryId es requerido y debe ser mayor a 0"
}
```
**Causa**: `categoryId` no se proporcionó o es inválido (≤ 0)

### Error 404 Not Found:
```json
{
  "message": "Categoría con ID {categoryId} no encontrada o no está activa"
}
```
**Causa**: La categoría especificada no existe o está inactiva

### Error 408 Request Timeout:
```json
{
  "message": "Request timeout. Please try again.",
  "detail": "The request took too long to complete"
}
```
**Causa**: La petición excedió 30 segundos. El frontend debe permitir reintentar.

---

## 🎯 Mejores Prácticas

1. **Siempre incluir `categoryId`**: Es obligatorio, sin él la petición fallará
2. **Usar el título directamente**: El campo `title` ya viene formateado, úsalo tal cual
3. **Iterar el array**: No necesitas conocer las claves, simplemente itera el array
4. **Manejar paginación**: Usa `pagination.hasNextPage` y `pagination.hasPreviousPage` para navegación
5. **Cargar ubicación del usuario**: Si está disponible, incluye `latitude` y `longitude` para mejores resultados

---

## 📊 Estructura de SearchServiceHomepageDto

Cada servicio en el array `services` tiene esta estructura:

```typescript
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

---

## ✅ Resumen de Cambios

### ✅ Cambios Implementados:
1. **`categoryId` es obligatorio** - Debe ser el primer parámetro
2. **Respuesta es array plano** - No hay objetos anidados
3. **Títulos incluyen categoría** - Formato: "Revisiones [Categoría] [Tipo]"
4. **Secciones específicas solo para Coches y Motos** - En Alemania (DE) y Reino Unido (GB)
5. **Validación de categoría** - Retorna 404 si la categoría no existe

### ❌ Cambios que NO aplican:
- No hay opción sin `categoryId`
- No se devuelven múltiples categorías juntas
- No hay secciones específicas para categorías que no sean Coches o Motos
