# 📘 Guía de Favoritos de Servicios - Frontend

## 🎯 Resumen

El sistema de favoritos permite a los usuarios guardar servicios (`SearchService`) que les interesan. Todos los endpoints requieren **autenticación JWT** (token en el header `Authorization: Bearer {token}`).

---

## 🔐 Autenticación

**IMPORTANTE**: Todos los endpoints (excepto `GET /api/Favorites/service/{id}/count`) requieren autenticación.

El usuario se identifica automáticamente desde el token JWT. No necesitas enviar el `userId` en el body.

---

## 📡 Endpoints Disponibles

### 1. **Toggle Favorito** (Recomendado) ⭐

**Agrega o elimina un favorito en una sola llamada.**

```http
POST /api/Favorites/toggle
Content-Type: application/json
Authorization: Bearer {token}
```

#### 📤 Request Body:
```json
{
  "searchServiceId": 123
}
```

#### ✅ Response 200 OK:
```json
{
  "success": true,
  "message": "Servicio agregado a favoritos", // o "Servicio eliminado de favoritos"
  "isFavorite": true, // true = ahora es favorito, false = ya no es favorito
  "searchServiceId": 123
}
```

#### ❌ Response 400 Bad Request:
```json
{
  "success": false,
  "message": "Servicio no encontrado o no está activo"
}
```

#### ❌ Response 401 Unauthorized:
```json
{
  "success": false,
  "message": "Usuario no autenticado"
}
```

---

### 2. **Agregar Favorito**

```http
POST /api/Favorites
Content-Type: application/json
Authorization: Bearer {token}
```

#### 📤 Request Body:
```json
{
  "searchServiceId": 123
}
```

#### ✅ Response 201 Created:
```json
{
  "success": true,
  "message": "Servicio agregado a favoritos",
  "data": {
    "id": 456,
    "userId": 1,
    "searchServiceId": 123,
    "createdAt": "2026-01-16T12:00:00Z"
  }
}
```

#### ❌ Response 400 Bad Request:
```json
{
  "success": false,
  "message": "El servicio ya está en favoritos" // o "Servicio no encontrado o no está activo"
}
```

---

### 3. **Eliminar Favorito**

```http
DELETE /api/Favorites/{searchServiceId}
Authorization: Bearer {token}
```

#### 📤 Request:
- No requiere body
- El `searchServiceId` va en la URL

#### ✅ Response 200 OK:
```json
{
  "success": true,
  "message": "Servicio eliminado de favoritos"
}
```

#### ❌ Response 404 Not Found:
```json
{
  "success": false,
  "message": "El servicio no está en favoritos"
}
```

---

### 4. **Verificar si un Servicio es Favorito**

```http
GET /api/Favorites/check/{searchServiceId}
Authorization: Bearer {token}
```

#### 📤 Request:
- No requiere body
- El `searchServiceId` va en la URL

#### ✅ Response 200 OK:
```json
{
  "success": true,
  "data": {
    "isFavorite": true,
    "favoriteId": 456 // null si no es favorito
  }
}
```

---

### 5. **Verificar Múltiples Servicios** (Para listas)

```http
POST /api/Favorites/check-multiple
Content-Type: application/json
Authorization: Bearer {token}
```

#### 📤 Request Body:
```json
[123, 456, 789]
```

#### ✅ Response 200 OK:
```json
{
  "success": true,
  "data": {
    "123": true,
    "456": false,
    "789": true
  }
}
```

**💡 Uso recomendado**: Cuando muestras una lista de servicios, envía todos los IDs de una vez para verificar cuáles son favoritos del usuario.

---

### 6. **Obtener Favoritos del Usuario** (Con paginación)

```http
GET /api/Favorites?page=1&pageSize=20
Authorization: Bearer {token}
```

#### 📤 Query Parameters:
- `page` (opcional, default: 1): Número de página
- `pageSize` (opcional, default: 20, máximo: 50): Elementos por página

#### ✅ Response 200 OK:
```json
{
  "success": true,
  "data": [
    {
      "id": 456,
      "createdAt": "2026-01-16T12:00:00Z",
      "service": {
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
          "availability": null // Disponibilidad no incluida en favoritos
        },
        "completedSearches": 15,
        "averageRating": 4.8
      }
    }
  ],
  "pagination": {
    "currentPage": 1,
    "pageSize": 20,
    "totalCount": 5,
    "totalPages": 1
  }
}
```

**✅ IMPORTANTE**: El objeto `service` dentro de cada favorito es exactamente el mismo DTO (`SearchServiceHomepageDto`) que se usa en las cards de la homepage. Esto significa que puedes **reutilizar el mismo componente de card** que usas en la homepage para mostrar los favoritos.

---

### 7. **Obtener Cantidad de Favoritos de un Servicio** (Público)

```http
GET /api/Favorites/service/{searchServiceId}/count
```

#### 📤 Request:
- **NO requiere autenticación** (público)
- El `searchServiceId` va en la URL

#### ✅ Response 200 OK:
```json
{
  "success": true,
  "searchServiceId": 123,
  "favoritesCount": 42
}
```

**💡 Uso**: Muestra cuántas personas han guardado este servicio como favorito.

---

## 🎨 Ejemplos de Uso en Frontend

### React/TypeScript - Toggle Favorito

```typescript
interface ToggleFavoriteResponse {
  success: boolean;
  message: string;
  isFavorite: boolean;
  searchServiceId: number;
}

async function toggleFavorite(
  searchServiceId: number,
  token: string
): Promise<ToggleFavoriteResponse> {
  const response = await fetch(`${API_URL}/api/Favorites/toggle`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${token}`
    },
    body: JSON.stringify({ searchServiceId })
  });

  if (!response.ok) {
    throw new Error('Error al actualizar favorito');
  }

  return response.json();
}

// Uso:
const handleFavoriteClick = async (serviceId: number) => {
  try {
    const result = await toggleFavorite(serviceId, userToken);
    setFavoriteState(result.isFavorite);
    showToast(result.message);
  } catch (error) {
    showError('Error al actualizar favorito');
  }
};
```

---

### Verificar Múltiples Servicios (Para Lista)

```typescript
interface CheckMultipleResponse {
  success: boolean;
  data: Record<number, boolean>; // { [serviceId]: isFavorite }
}

async function checkMultipleFavorites(
  serviceIds: number[],
  token: string
): Promise<CheckMultipleResponse> {
  const response = await fetch(`${API_URL}/api/Favorites/check-multiple`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${token}`
    },
    body: JSON.stringify(serviceIds)
  });

  return response.json();
}

// Uso al cargar lista de servicios:
const serviceIds = services.map(s => s.id);
const favoritesMap = await checkMultipleFavorites(serviceIds, userToken);

// Luego en el render:
services.map(service => (
  <ServiceCard
    key={service.id}
    service={service}
    isFavorite={favoritesMap.data[service.id] || false}
  />
));
```

---

### Obtener Favoritos del Usuario

```typescript
// ✅ IMPORTANTE: SearchServiceHomepageDto es el mismo DTO que se usa en la homepage
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
}

interface FavoriteWithService {
  id: number;
  createdAt: string;
  service: SearchServiceHomepageDto; // ✅ Mismo DTO que la homepage
}

interface GetFavoritesResponse {
  success: boolean;
  data: FavoriteWithService[];
  pagination: {
    currentPage: number;
    pageSize: number;
    totalCount: number;
    totalPages: number;
  };
}

async function getUserFavorites(
  page: number = 1,
  pageSize: number = 20,
  token: string
): Promise<GetFavoritesResponse> {
  const response = await fetch(
    `${API_URL}/api/Favorites?page=${page}&pageSize=${pageSize}`,
    {
      headers: {
        'Authorization': `Bearer ${token}`
      }
    }
  );

  return response.json();
}

// ✅ Ejemplo: Reutilizar el mismo componente de card de la homepage
function FavoritesPage() {
  const [favorites, setFavorites] = useState<FavoriteWithService[]>([]);
  const [pagination, setPagination] = useState({ currentPage: 1, totalPages: 1 });

  useEffect(() => {
    loadFavorites();
  }, []);

  async function loadFavorites(page: number = 1) {
    const response = await getUserFavorites(page, 20, userToken);
    setFavorites(response.data);
    setPagination(response.pagination);
  }

  return (
    <div>
      <h1>Mis Favoritos</h1>
      <div className="services-grid">
        {favorites.map(favorite => (
          // ✅ Reutilizar el mismo componente ServiceCard de la homepage
          <ServiceCard 
            key={favorite.id}
            service={favorite.service} // Pasar directamente el objeto service
            isFavorite={true} // Siempre es favorito en esta página
            onFavoriteToggle={handleToggleFavorite}
          />
        ))}
      </div>
    </div>
  );
}
```

---

## ⚠️ Errores Comunes

### 1. **No enviar el token de autenticación**
```json
// Response 401
{
  "success": false,
  "message": "Usuario no autenticado"
}
```
**Solución**: Asegúrate de incluir `Authorization: Bearer {token}` en todos los headers.

---

### 2. **Servicio no existe o no está activo**
```json
// Response 400
{
  "success": false,
  "message": "Servicio no encontrado o no está activo"
}
```
**Solución**: Verifica que el `searchServiceId` sea válido y que el servicio esté activo.

---

### 3. **Intentar agregar un favorito que ya existe**
```json
// Response 400 (solo en POST /api/Favorites, no en toggle)
{
  "success": false,
  "message": "El servicio ya está en favoritos"
}
```
**Solución**: Usa `POST /api/Favorites/toggle` en lugar de `POST /api/Favorites` para evitar este error.

---

## 📝 Notas Importantes

1. **Toggle es la mejor opción**: Usa `POST /api/Favorites/toggle` para agregar/eliminar favoritos. Es más eficiente y no genera errores si el favorito ya existe.

2. **Verificación en listas**: Cuando muestres una lista de servicios, usa `POST /api/Favorites/check-multiple` para verificar todos los favoritos de una vez (más eficiente que hacer múltiples llamadas).

3. **Paginación**: El endpoint `GET /api/Favorites` soporta paginación. El máximo de `pageSize` es 50.

4. **Servicios inactivos**: Solo se pueden agregar a favoritos servicios que estén activos (`IsActive = true`).

5. **Orden de favoritos**: Los favoritos se ordenan por fecha de creación descendente (más recientes primero).

6. **✅ Mismo DTO que Homepage**: El objeto `service` dentro de cada favorito es exactamente el mismo `SearchServiceHomepageDto` que se usa en las cards de la homepage. **Puedes reutilizar el mismo componente de card** sin modificaciones. La única diferencia es que `availability` viene como `null` en favoritos (no se carga la disponibilidad del experto).

---

## 🔄 Flujo Recomendado

### Al mostrar un servicio individual:
1. Cargar el servicio
2. Llamar a `GET /api/Favorites/check/{id}` para saber si es favorito
3. Mostrar el botón de favorito con el estado correcto
4. Al hacer clic, usar `POST /api/Favorites/toggle`
5. Actualizar el estado local con `isFavorite` de la respuesta

### Al mostrar una lista de servicios:
1. Cargar los servicios
2. Extraer todos los IDs: `const ids = services.map(s => s.id)`
3. Llamar a `POST /api/Favorites/check-multiple` con el array de IDs
4. Mapear los resultados a cada servicio
5. Mostrar el estado de favorito en cada tarjeta
6. Al hacer clic en favorito, usar `POST /api/Favorites/toggle` y actualizar el estado local

### Al mostrar la página de favoritos:
1. Llamar a `GET /api/Favorites?page=1&pageSize=20`
2. Extraer el array `data` de la respuesta
3. **Reutilizar el mismo componente de card de la homepage** pasando `favorite.service` como prop
4. El componente de card funcionará igual porque es el mismo DTO (`SearchServiceHomepageDto`)

---

## 🎯 Resumen de Endpoints

| Método | Endpoint | Auth | Descripción |
|--------|----------|------|-------------|
| `POST` | `/api/Favorites/toggle` | ✅ | Agregar/eliminar favorito (recomendado) |
| `POST` | `/api/Favorites` | ✅ | Agregar favorito |
| `DELETE` | `/api/Favorites/{id}` | ✅ | Eliminar favorito |
| `GET` | `/api/Favorites/check/{id}` | ✅ | Verificar si es favorito |
| `POST` | `/api/Favorites/check-multiple` | ✅ | Verificar múltiples servicios |
| `GET` | `/api/Favorites?page=1&pageSize=20` | ✅ | Obtener favoritos del usuario |
| `GET` | `/api/Favorites/service/{id}/count` | ❌ | Cantidad de favoritos (público) |

---

¿Tienes dudas? Revisa los ejemplos de código o consulta con el equipo de backend. 🚀
