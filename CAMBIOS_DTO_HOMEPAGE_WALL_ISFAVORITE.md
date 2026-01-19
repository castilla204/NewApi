# 📋 Cambios en el DTO `SearchServiceHomepageDto` - Campo `IsFavorite`

## 🎯 Resumen

Se ha añadido el campo **`IsFavorite`** al DTO `SearchServiceHomepageDto` que se devuelve en el endpoint `/api/SearchService/homepage-wall`. Este campo indica si cada servicio es favorito del usuario autenticado.

---

## 📊 Estructura del DTO Antes y Después

### **ANTES** (DTO Original)

```csharp
public class SearchServiceHomepageDto
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } // ✅ Ya estaba incluido
    public int ServiceTypeId { get; set; }
    public string ServiceTypeName { get; set; }
    public decimal Price { get; set; }
    public List<string> ImageUrls { get; set; }
    public HomepageExpertDto Expert { get; set; }
    public int CompletedSearches { get; set; }
    public double AverageRating { get; set; }
}
```

### **DESPUÉS** (DTO Actualizado)

```csharp
public class SearchServiceHomepageDto
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } // ✅ Ya estaba incluido - se devuelve siempre
    public int ServiceTypeId { get; set; }
    public string ServiceTypeName { get; set; }
    public decimal Price { get; set; }
    public List<string> ImageUrls { get; set; }
    public HomepageExpertDto Expert { get; set; }
    public int CompletedSearches { get; set; }
    public double AverageRating { get; set; }
    public bool IsFavorite { get; set; } // ✅ NUEVO
}
```

---

## 🆕 Nuevo Campo Añadido

### **`IsFavorite`** (bool)

**Descripción**: Indica si el servicio es favorito del usuario autenticado.

**Valores posibles**:
- `true`: El servicio está en los favoritos del usuario
- `false`: El servicio NO está en los favoritos del usuario (o el usuario no está autenticado)

**Cuándo es `false`**:
- El usuario no está autenticado (el endpoint es público)
- El usuario está autenticado pero el servicio no está en sus favoritos
- El servicio no existe o fue eliminado

**Cuándo es `true`**:
- El usuario está autenticado Y el servicio está en sus favoritos

---

## ✅ Campos Existentes (Confirmación)

### **`CategoryName`** (string)

**Descripción**: Nombre de la categoría del servicio (ej: "Hogar", "Coches", "Motos").

**Nota**: Este campo **ya estaba incluido** en el DTO desde el inicio. Se devuelve siempre en todas las respuestas del endpoint `/api/SearchService/homepage-wall`.

**Ejemplos de valores**:
- `"Hogar"`
- `"Coches"`
- `"Motos"`
- `"Informática"`
- `"Jardín"`

---

## 📡 Ejemplo de Respuesta Completa

### **Request**:
```http
GET /api/SearchService/homepage-wall?categoryId=1&latitude=42.46855255601289&longitude=-2.42577606638114&countryCode=ES&locationRange=50&nearbyPage=1&nearbyPageSize=20&popularPage=1&popularPageSize=20
Authorization: Bearer {token} // ✅ Opcional: Si está autenticado, se verificarán favoritos
```

### **Response**:
```json
[
  {
    "title": "Revisiones cerca de mí",
    "services": [
      {
        "id": 123,
        "categoryId": 1,
        "categoryName": "Hogar",
        "serviceTypeId": 5,
        "serviceTypeName": "Fontanería",
        "price": 50.00,
        "imageUrls": [
          "https://storage.googleapis.com/.../image1.jpg",
          "https://storage.googleapis.com/.../image2.jpg"
        ],
        "expert": {
          "id": 10,
          "name": "Juan Pérez",
          "profilePictureUrl": "https://storage.googleapis.com/.../profile.jpg",
          "country": "España",
          "city": "Madrid",
          "availability": {
            "daysOfWeek": ["Monday", "Tuesday", "Wednesday"],
            "startTime": "09:00:00",
            "endTime": "18:00:00"
          }
        },
        "completedSearches": 15,
        "averageRating": 4.8,
        "isFavorite": true // ✅ NUEVO
      },
      {
        "id": 456,
        "categoryId": 1,
        "categoryName": "Hogar",
        "serviceTypeId": 6,
        "serviceTypeName": "Electricidad",
        "price": 75.00,
        "imageUrls": [
          "https://storage.googleapis.com/.../image1.jpg"
        ],
        "expert": {
          "id": 11,
          "name": "María García",
          "profilePictureUrl": "https://storage.googleapis.com/.../profile.jpg",
          "country": "España",
          "city": "Barcelona"
        },
        "completedSearches": 8,
        "averageRating": 4.5,
        "isFavorite": false // ✅ NUEVO: No está en favoritos
      }
    ],
    "pagination": {
      "page": 1,
      "pageSize": 20,
      "totalCount": 45,
      "totalPages": 3,
      "hasNextPage": true,
      "hasPreviousPage": false
    }
  },
  {
    "title": "Revisiones populares",
    "services": [
      {
        "id": 789,
        "categoryId": 1,
        "categoryName": "Hogar",
        "serviceTypeId": 7,
        "serviceTypeName": "Carpintería",
        "price": 100.00,
        "imageUrls": [],
        "expert": {
          "id": 12,
          "name": "Pedro López",
          "profilePictureUrl": "https://storage.googleapis.com/.../profile.jpg",
          "country": "España",
          "city": "Valencia"
        },
        "completedSearches": 25,
        "averageRating": 4.9,
        "isFavorite": true // ✅ NUEVO
      }
    ],
    "pagination": {
      "page": 1,
      "pageSize": 20,
      "totalCount": 30,
      "totalPages": 2,
      "hasNextPage": true,
      "hasPreviousPage": false
    }
  }
]
```

---

## 🔄 Cambios en el Backend

### **Controlador** (`SearchServiceController.cs` - método `GetHomepageWall`)

#### 1. **Inyección de dependencia añadida**:
```csharp
private readonly IFavoriteService _favoriteService;

public SearchServiceController(
    // ... otros servicios ...
    IFavoriteService favoriteService)
{
    // ...
    _favoriteService = favoriteService;
}
```

#### 2. **Verificación de favoritos optimizada**:
```csharp
// ✅ Obtener userId del usuario autenticado (si existe)
int? userId = null;
var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out int parsedUserId))
{
    userId = parsedUserId;
}

// ✅ Recolectar todos los IDs de servicios
var allServiceIds = new List<int>();
allServiceIds.AddRange(nearbyServicesList.Select(s => s.Id));
allServiceIds.AddRange(popularServicesList.Select(s => s.Id));

// ✅ Verificar favoritos de forma eficiente (una sola consulta a la BD)
Dictionary<int, bool> favoritesMap = new Dictionary<int, bool>();
if (userId.HasValue && allServiceIds.Any())
{
    var favoriteServiceIds = await _context.SearchServiceFavorites
        .AsNoTracking()
        .Where(f => f.UserId == userId.Value && allServiceIds.Contains(f.SearchServiceId))
        .Select(f => f.SearchServiceId)
        .ToListAsync(cts.Token);
    
    // Crear mapa de favoritos
    foreach (var serviceId in allServiceIds)
    {
        favoritesMap[serviceId] = favoriteServiceIds.Contains(serviceId);
    }
}

// ✅ Aplicar IsFavorite a cada servicio
foreach (var service in nearbyServicesList)
{
    service.IsFavorite = favoritesMap.TryGetValue(service.Id, out var isFavorite) ? isFavorite : false;
}

foreach (var service in popularServicesList)
{
    service.IsFavorite = favoritesMap.TryGetValue(service.Id, out var isFavorite) ? isFavorite : false;
}
```

---

## 💡 Beneficios para el Frontend

### **Antes**:
- El frontend tenía que hacer llamadas adicionales para verificar si cada servicio es favorito
- Para 20 servicios, necesitaba 20 llamadas HTTP adicionales (o usar `check-multiple`)

### **Después**:
- ✅ **Todo viene en una sola respuesta** - `IsFavorite` ya está incluido
- ✅ **Menos llamadas HTTP** = mejor rendimiento
- ✅ **Cards más completas** sin esperar datos adicionales
- ✅ **Funciona sin autenticación** - si el usuario no está autenticado, `IsFavorite` será `false`

---

## 🎨 Uso en el Frontend

### **Ejemplo React/TypeScript**:

```typescript
interface SearchServiceHomepageDto {
  id: number;
  categoryId: number;
  categoryName: string;
  serviceTypeId: number;
  serviceTypeName: string;
  price: number;
  imageUrls: string[];
  expert: {
    id: number;
    name: string;
    profilePictureUrl: string;
    country?: string;
    city?: string;
    availability?: {
      daysOfWeek: string[];
      startTime: string;
      endTime: string;
    };
  };
  completedSearches: number;
  averageRating: number;
  isFavorite: boolean; // ✅ NUEVO
}

interface HomepageSection {
  title: string;
  services: SearchServiceHomepageDto[];
  pagination: {
    page: number;
    pageSize: number;
    totalCount: number;
    totalPages: number;
    hasNextPage: boolean;
    hasPreviousPage: boolean;
  };
}

function ServiceCard({ service }: { service: SearchServiceHomepageDto }) {
  const [isFavorite, setIsFavorite] = useState(service.isFavorite);
  
  const handleToggleFavorite = async () => {
    try {
      const response = await fetch(`${API_URL}/api/Favorites/toggle`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${token}`
        },
        body: JSON.stringify({ searchServiceId: service.id })
      });
      
      const result = await response.json();
      setIsFavorite(result.isFavorite); // Actualizar estado local
    } catch (error) {
      console.error('Error al actualizar favorito', error);
    }
  };
  
  return (
    <div className="service-card">
      {/* Imagen del servicio */}
      {service.imageUrls.length > 0 && (
        <img 
          src={service.imageUrls[0]} 
          alt={service.serviceTypeName}
          className="service-image"
        />
      )}
      
      <h3>{service.serviceTypeName}</h3>
      <p>€{service.price.toFixed(2)}</p>
      
      {/* Botón de favorito */}
      <button 
        onClick={handleToggleFavorite}
        className={isFavorite ? 'favorite-active' : 'favorite-inactive'}
        aria-label={isFavorite ? 'Eliminar de favoritos' : 'Agregar a favoritos'}
      >
        {isFavorite ? '❤️' : '🤍'}
      </button>
      
      {/* Información del experto */}
      <div className="expert-info">
        <img 
          src={service.expert.profilePictureUrl} 
          alt={service.expert.name}
          className="expert-avatar"
        />
        <div>
          <p>{service.expert.name}</p>
          {service.expert.city && <p>📍 {service.expert.city}</p>}
          <p>⭐ {service.averageRating.toFixed(1)} ({service.completedSearches} trabajos)</p>
        </div>
      </div>
    </div>
  );
}

function HomepageWall({ categoryId }: { categoryId: number }) {
  const [sections, setSections] = useState<HomepageSection[]>([]);
  const token = getAuthToken(); // Obtener token si existe
  
  useEffect(() => {
    loadHomepageWall();
  }, [categoryId]);
  
  async function loadHomepageWall() {
    const params = new URLSearchParams({
      categoryId: categoryId.toString(),
      // ... otros parámetros ...
    });
    
    const headers: HeadersInit = {
      'Content-Type': 'application/json'
    };
    
    // ✅ Añadir token si el usuario está autenticado
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    }
    
    const response = await fetch(
      `/api/SearchService/homepage-wall?${params.toString()}`,
      { headers }
    );
    
    const data = await response.json();
    setSections(data);
  }
  
  return (
    <div className="homepage-wall">
      {sections.map((section, index) => (
        <div key={index} className="homepage-section">
          <h2>{section.title}</h2>
          <div className="services-grid">
            {section.services.map(service => (
              <ServiceCard 
                key={service.id} 
                service={service} 
              />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}
```

---

## ⚠️ Notas Importantes

### 1. **Endpoint Público**
El endpoint `/api/SearchService/homepage-wall` es **público** (`[AllowAnonymous]`), lo que significa:
- ✅ Funciona **sin autenticación** - si no hay token, `IsFavorite` será `false` para todos
- ✅ Funciona **con autenticación** - si hay token, se verificarán los favoritos del usuario

### 2. **Token Opcional pero Recomendado**
Aunque el endpoint es público, es **recomendado** enviar el token si el usuario está autenticado:
```typescript
const headers: HeadersInit = {
  'Content-Type': 'application/json'
};

// ✅ Añadir token si existe
if (token) {
  headers['Authorization'] = `Bearer ${token}`;
}
```

### 3. **Actualización del Estado**
Cuando el usuario hace toggle de favorito:
- ✅ Actualizar el estado local inmediatamente (`setIsFavorite(result.isFavorite)`)
- ✅ No es necesario recargar toda la página
- ✅ El backend ya devuelve el estado actualizado en la respuesta del toggle

### 4. **Optimización**
- ✅ El backend verifica **todos los favoritos de una vez** con una sola consulta SQL
- ✅ No hay múltiples llamadas HTTP para verificar favoritos
- ✅ El rendimiento es óptimo incluso con muchos servicios

---

## 🔄 Flujo Recomendado

### Al cargar el homepage:
1. Obtener token del usuario (si está autenticado)
2. Llamar a `GET /api/SearchService/homepage-wall?categoryId=1&...` con el token en headers
3. Mostrar los servicios con el estado de favorito (`isFavorite`) que viene en la respuesta
4. No necesitas hacer llamadas adicionales para verificar favoritos

### Al hacer toggle de favorito:
1. Llamar a `POST /api/Favorites/toggle` con `{ searchServiceId: 123 }`
2. Actualizar el estado local con `result.isFavorite` de la respuesta
3. Opcional: Recargar la sección si quieres asegurar consistencia

---

## 📝 Resumen de Cambios

| Campo | Tipo | Estado | Descripción | Valores |
|-------|------|--------|-------------|---------|
| `CategoryName` | `string` | ✅ Ya existía | Nombre de la categoría del servicio | Ej: "Hogar", "Coches", "Motos" |
| `IsFavorite` | `bool` | 🆕 Nuevo | Indica si el servicio es favorito del usuario autenticado | `true` = es favorito<br>`false` = no es favorito o usuario no autenticado |

---

## 🎯 Comparación Antes/Después

### **ANTES**:
```typescript
// 1. Cargar servicios
const services = await fetch('/api/SearchService/homepage-wall?...');

// 2. Verificar favoritos (20 llamadas adicionales)
const serviceIds = services.map(s => s.id);
const favorites = await fetch('/api/Favorites/check-multiple', {
  method: 'POST',
  body: JSON.stringify(serviceIds)
});

// 3. Mapear favoritos a servicios
services.forEach(service => {
  service.isFavorite = favorites.data[service.id] || false;
});
```

### **DESPUÉS**:
```typescript
// ✅ Todo en una sola llamada
const services = await fetch('/api/SearchService/homepage-wall?...', {
  headers: {
    'Authorization': `Bearer ${token}` // ✅ Opcional pero recomendado
  }
});

// ✅ isFavorite ya viene en cada servicio
services.forEach(service => {
  // service.isFavorite ya está disponible
  renderServiceCard(service);
});
```

---

## ✅ Ventajas

1. **Menos llamadas HTTP**: De N+1 llamadas a 1 sola llamada
2. **Mejor rendimiento**: Carga más rápida del homepage
3. **Código más simple**: No necesitas verificar favoritos por separado
4. **Funciona sin autenticación**: El endpoint es público, `IsFavorite` será `false` si no hay token
5. **Consistente**: Mismo DTO que se usa en otros endpoints

---

¿Tienes alguna pregunta sobre los cambios? 🚀
