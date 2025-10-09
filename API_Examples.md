# 📋 Ejemplos de Uso de la Nueva API de Búsquedas

## 🚀 Endpoints Mejorados

### 1. **Admin**: `GET /api/search/all`
### 2. **Usuario**: `GET /api/search`

### ✅ Características Implementadas:
- **Paginación**: Máximo 20 resultados por página (configurable hasta 50)
- **Filtros**: Por estado, usuario, categoría, fechas, etc.
- **Búsqueda**: Por texto en título y descripción
- **Ordenamiento**: Por cualquier campo (asc/desc)
- **Estados traducidos**: Incluye `statusTranslated` en español
- **Estadísticas**: Para usuarios (mensajes sin leer, citas pendientes, etc.)

---

## 📝 Parámetros de Query

### **Parámetros Comunes (Admin y Usuario)**

| Parámetro | Tipo | Descripción | Ejemplo |
|-----------|------|-------------|---------|
| `page` | int | Número de página (empezando en 1) | `?page=1` |
| `pageSize` | int | Tamaño de página (1-50, default: 20) | `?pageSize=10` |
| `searchTerm` | string | Búsqueda en título/descripción | `?searchTerm=coche` |
| `category` | int | Filtrar por categoría | `?category=1` |
| `isActive` | bool | Filtrar por estado activo | `?isActive=true` |
| `isRevised` | bool | Filtrar por estado revisado | `?isRevised=false` |
| `searchHireStatus` | string | Estado del SearchHire | `?searchHireStatus=pending` |
| `sortBy` | string | Campo de ordenamiento | `?sortBy=createdAt` |
| `sortDirection` | string | Dirección (asc/desc) | `?sortDirection=desc` |

---

## 🔍 Ejemplos de Uso

### **Para Administradores (`/api/search/all`)**

#### 1. **Búsqueda Básica (Primera Página)**
```http
GET /api/search/all?page=1&pageSize=20
```

#### 2. **Buscar por Texto**
```http
GET /api/search/all?searchTerm=coche&page=1
```

#### 3. **Filtrar por Categoría**
```http
GET /api/search/all?category=1&page=1
```

#### 4. **Filtrar por Estado Activo**
```http
GET /api/search/all?isActive=true&page=1
```

#### 5. **Filtrar por Estado de Contratación**
```http
GET /api/search/all?searchHireStatus=pending&page=1
```

#### 6. **Búsqueda Completa Admin**
```http
GET /api/search/all?searchTerm=moto&category=2&isActive=true&searchHireStatus=completed&sortBy=title&page=1&pageSize=10
```

### **Para Usuarios (`/api/search`)**

#### 1. **Búsqueda Básica del Usuario**
```http
GET /api/search?page=1&pageSize=20
```

#### 2. **Buscar por Texto en Mis Búsquedas**
```http
GET /api/search?searchTerm=coche&page=1
```

#### 3. **Filtrar por Categoría**
```http
GET /api/search?category=1&page=1
```

#### 4. **Filtrar por Estado Activo**
```http
GET /api/search?isActive=true&page=1
```

#### 5. **Filtrar por Estado de Contratación**
```http
GET /api/search?searchHireStatus=completed&page=1
```

#### 6. **Búsqueda Completa Usuario**
```http
GET /api/search?searchTerm=moto&category=2&isActive=true&searchHireStatus=pending&sortBy=createdAt&sortDirection=desc&page=1&pageSize=10
```

---

## 📊 Estructura de Respuesta

### **Respuesta para Administradores (`/api/search/all`)**

```json
{
  "searches": [
    {
      "id": 123,
      "userId": 456,
      "title": "Búsqueda de coche",
      "description": "Busco un coche económico",
      "frequency": 24,
      "isActive": true,
      "isRevised": false,
      "lastExecution": "2024-01-15T10:30:00Z",
      "createdAt": "2024-01-15T10:30:00Z",
      "startDate": "2024-01-15T10:30:00Z",
      "category": 1,
      "user": {
        "email": "usuario@example.com",
        "name": "Juan Pérez"
      },
      "searchHire": {
        "id": 789,
        "expertId": 101,
        "status": "pending",
        "statusTranslated": "Pendiente",
        "createdAt": "2024-01-15T10:30:00Z",
        "expert": {
          "name": "María García",
          "profilePictureUrl": "/avatars/maria.jpg"
        }
      }
    }
  ],
  "pagination": {
    "currentPage": 1,
    "pageSize": 20,
    "totalCount": 150,
    "totalPages": 8,
    "hasPrevious": false,
    "hasNext": true
  }
}
```

### **Respuesta para Usuarios (`/api/search`) - CON ESTADÍSTICAS**

```json
{
  "searches": [
    {
      "id": 123,
      "userId": 456,
      "title": "Búsqueda de coche",
      "description": "Busco un coche económico",
      "frequency": 24,
      "isActive": true,
      "isRevised": false,
      "lastExecution": "2024-01-15T10:30:00Z",
      "createdAt": "2024-01-15T10:30:00Z",
      "startDate": "2024-01-15T10:30:00Z",
      "locationName": "Madrid, España",
      "category": 1,
      "unreadMessagesCount": 3,
      "hasPendingAppointment": true,
      "pendingAppointmentStatus": "appointment_proposed",
      "user": {
        "email": "usuario@example.com",
        "name": "Juan Pérez"
      },
      "searchHire": {
        "id": 789,
        "expertId": 101,
        "status": "pending",
        "statusTranslated": "Pendiente",
        "createdAt": "2024-01-15T10:30:00Z",
        "expert": {
          "name": "María García",
          "profilePictureUrl": "/avatars/maria.jpg"
        }
      }
    }
  ],
  "pagination": {
    "currentPage": 1,
    "pageSize": 20,
    "totalCount": 25,
    "totalPages": 2,
    "hasPrevious": false,
    "hasNext": true
  },
  "stats": {
    "activeSearches": 15,
    "inactiveSearches": 10,
    "searchesWithHire": 8,
    "searchesWithoutHire": 17,
    "unreadMessages": 5,
    "pendingAppointments": 2
  }
}
```

---

## 🎯 Campos de Ordenamiento Disponibles

| Campo | Descripción |
|-------|-------------|
| `createdAt` | Fecha de creación (default) |
| `title` | Título de la búsqueda |
| `description` | Descripción |
| `frequency` | Frecuencia en horas |
| `isActive` | Estado activo |
| `isRevised` | Estado revisado |
| `lastExecution` | Última ejecución |
| `startDate` | Fecha de inicio |
| `userId` | ID del usuario |

---

## 🔧 Implementación en Frontend

### React/Vue Component Example:

```javascript
// Estado del componente
const [searches, setSearches] = useState([]);
const [pagination, setPagination] = useState({});
const [filters, setFilters] = useState({
  page: 1,
  pageSize: 20,
  searchTerm: '',
  isActive: null,
  searchHireStatus: '',
  sortBy: 'createdAt',
  sortDirection: 'desc'
});

// Función para cargar búsquedas
const loadSearches = async () => {
  const params = new URLSearchParams();
  Object.entries(filters).forEach(([key, value]) => {
    if (value !== null && value !== '') {
      params.append(key, value);
    }
  });

  const response = await fetch(`/api/search/all?${params}`);
  const data = await response.json();
  
  setSearches(data.searches);
  setPagination(data.pagination);
};

// Usar en el template
{searches.map(search => (
  <div key={search.id}>
    <h3>{search.title}</h3>
    <p>Estado: {search.searchHire?.statusTranslated}</p>
    <p>Usuario: {search.user.name}</p>
  </div>
))}

// Paginación
{pagination.hasPrevious && (
  <button onClick={() => setFilters({...filters, page: filters.page - 1})}>
    Anterior
  </button>
)}
{pagination.hasNext && (
  <button onClick={() => setFilters({...filters, page: filters.page + 1})}>
    Siguiente
  </button>
)}
```

---

## ⚡ Beneficios de Rendimiento

1. **Paginación**: Solo carga 20 registros por defecto
2. **Filtros en BD**: Los filtros se aplican en la base de datos, no en memoria
3. **Índices**: Se pueden crear índices en campos de filtro frecuentes
4. **Lazy Loading**: Carga solo los datos necesarios
5. **Búsqueda Eficiente**: Búsqueda por texto optimizada en BD

---

## 🚨 Breaking Changes

- **ANTES**: `GET /api/search/all` devolvía array directo
- **AHORA**: `GET /api/search/all` devuelve objeto con `searches` y `pagination`

### Migración en Frontend:
```javascript
// ❌ ANTES
const searches = await response.json();

// ✅ AHORA
const data = await response.json();
const searches = data.searches;
const pagination = data.pagination;
```
