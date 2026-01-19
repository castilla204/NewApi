# 📋 Cambios en el DTO `SearchListDto`

## 🎯 Resumen

Se han añadido **4 nuevos campos** al DTO `SearchListDto` para incluir información del servicio, del experto y de la categoría que permite mostrar cards más completas en el frontend.

---

## 📊 Estructura del DTO Antes y Después

### **ANTES** (DTO Original)

```csharp
public class SearchListDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public int Frequency { get; set; }
    public bool IsActive { get; set; }
    public bool IsRevised { get; set; }
    public DateTime LastExecution { get; set; }
    public DateTime NextExecution { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime StartDate { get; set; }
    public string? LocationName { get; set; }
    public int Category { get; set; }
    public UserDto User { get; set; }
    public SearchHireDto? SearchHire { get; set; }
    
    // Indicadores de notificaciones
    public int UnreadMessagesCount { get; set; }
    public bool HasPendingAppointment { get; set; }
    public string? PendingAppointmentStatus { get; set; }
}
```

### **DESPUÉS** (DTO Actualizado)

```csharp
public class SearchListDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public int Frequency { get; set; }
    public bool IsActive { get; set; }
    public bool IsRevised { get; set; }
    public DateTime LastExecution { get; set; }
    public DateTime NextExecution { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime StartDate { get; set; }
    public string? LocationName { get; set; }
    public int Category { get; set; }
    public UserDto User { get; set; }
    public SearchHireDto? SearchHire { get; set; }
    
    // Indicadores de notificaciones
    public int UnreadMessagesCount { get; set; }
    public bool HasPendingAppointment { get; set; }
    public string? PendingAppointmentStatus { get; set; }
    
    // ✅ NUEVO: Información del servicio y experto para cards
    public string? ServiceImageUrl { get; set; } // Primera imagen del servicio
    public HomepageExpertAvailabilityDto? ExpertAvailability { get; set; } // Horario del experto
    public string? ExpertCity { get; set; } // Ciudad del experto
    public string? CategoryName { get; set; } // ✅ NUEVO: Nombre de la categoría (ej: "Hogar", "Coches", "Motos")
}
```

---

## 🆕 Nuevos Campos Añadidos

### 1. **`ServiceImageUrl`** (string?)

**Descripción**: URL de la primera imagen del servicio asociado a la búsqueda.

**Origen de datos**:
- Se obtiene de `SearchHire.SearchService.Images` (primera imagen ordenada por ID)
- Si la imagen tiene `ImageObjectName`, se genera una URL firmada usando `SignedUrlService`
- Si no, se usa directamente `ImageUrl`

**Cuándo es `null`**:
- No hay `SearchHire` asociado
- El `SearchHire` no tiene `SearchService`
- El `SearchService` no tiene imágenes

**Ejemplo**:
```json
"serviceImageUrl": "https://storage.googleapis.com/atrapobucket/services/abc123.jpg?X-Goog-Signature=..."
```

---

### 2. **`ExpertAvailability`** (HomepageExpertAvailabilityDto?)

**Descripción**: Horario de disponibilidad del experto asociado a la búsqueda.

**Estructura del objeto**:
```csharp
public class HomepageExpertAvailabilityDto
{
    public List<string> DaysOfWeek { get; set; } // ["Monday", "Tuesday", "Wednesday", ...]
    public TimeSpan StartTime { get; set; }      // "09:00:00"
    public TimeSpan EndTime { get; set; }        // "18:00:00"
}
```

**Origen de datos**:
- Se obtiene de `ExpertAvailability` activa del experto (`IsActive = true` y `EffectiveTo = null`)
- Se carga de forma eficiente: una sola consulta para todos los expertos de la página
- Los días de la semana se deserializan desde JSON almacenado en la BD

**Cuándo es `null`**:
- No hay `SearchHire` asociado
- El `SearchHire` no tiene experto asignado
- El experto no tiene `ExpertProfile`
- El experto no tiene disponibilidad configurada

**Ejemplo**:
```json
"expertAvailability": {
  "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
  "startTime": "09:00:00",
  "endTime": "18:00:00"
}
```

---

### 3. **`ExpertCity`** (string?)

**Descripción**: Ciudad donde trabaja el experto (ej: "Madrid", "Barcelona", "Valencia").

**Origen de datos**:
- Se obtiene directamente de `SearchHire.Expert.ExpertProfile.City`
- Es el campo que el experto configura en su perfil

**Cuándo es `null`**:
- No hay `SearchHire` asociado
- El `SearchHire` no tiene experto asignado
- El experto no tiene `ExpertProfile`
- El experto no ha configurado su ciudad

**Ejemplo**:
```json
"expertCity": "Madrid"
```

---

### 4. **`CategoryName`** (string?)

**Descripción**: Nombre de la categoría de la búsqueda (ej: "Hogar", "Coches", "Motos", "Informática").

**Origen de datos**:
- Se obtiene de la tabla `Categories` usando el `CategoryId` de `SearchParameters`
- Se carga de forma eficiente: una sola consulta para todas las categorías únicas de la página
- Solo se devuelven categorías activas (`IsActive = true`)

**Cuándo es `null`**:
- No hay `SearchParameters` asociados
- El `SearchParameter` no tiene `Category` configurado
- La categoría no existe o no está activa

**Ejemplo**:
```json
"categoryName": "Hogar"
```

---

## 📡 Ejemplo de Respuesta Completa

### **Request**:
```http
GET /api/Search/all?page=1&pageSize=20&isActive=true&sortBy=createdAt&sortDirection=desc
```

### **Response**:
```json
{
  "searches": [
    {
      "id": 123,
      "userId": 1,
      "title": "Revisión de instalación eléctrica",
      "description": "Necesito revisar la instalación eléctrica de mi casa...",
      "frequency": 24,
      "isActive": true,
      "isRevised": false,
      "lastExecution": "2026-01-16T10:00:00Z",
      "nextExecution": "2026-01-17T10:00:00Z",
      "createdAt": "2026-01-16T09:00:00Z",
      "startDate": "2026-01-16T09:00:00Z",
      "locationName": "Calle Mayor, 10, Madrid",
      "category": 1,
      "categoryName": "Hogar", // ✅ NUEVO
      "user": {
        "email": "cliente@example.com",
        "name": "Juan Pérez"
      },
      "searchHire": {
        "id": 44,
        "expertId": 13,
        "status": "pending",
        "statusTranslated": "Pendiente",
        "createdAt": "2026-01-16T09:30:00Z",
        "amount": 150.00,
        "expert": {
          "name": "Diego Castilla",
          "profilePictureUrl": "https://storage.googleapis.com/..."
        }
      },
      "unreadMessagesCount": 0,
      "hasPendingAppointment": false,
      "pendingAppointmentStatus": null,
      
      // ✅ NUEVOS CAMPOS
      "serviceImageUrl": "https://storage.googleapis.com/atrapobucket/services/abc123.jpg?X-Goog-Signature=...",
      "expertAvailability": {
        "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
        "startTime": "09:00:00",
        "endTime": "18:00:00"
      },
      "expertCity": "Madrid"
    }
  ],
  "pagination": {
    "currentPage": 1,
    "pageSize": 20,
    "totalCount": 45,
    "totalPages": 3,
    "hasPrevious": false,
    "hasNext": true
  }
}
```

---

## 🔄 Cambios en el Backend

### **Controlador** (`SearchController.cs` - método `GetAllSearches`)

#### 1. **Includes añadidos**:
```csharp
.Include(s => s.SearchHire)
    .ThenInclude(sh => sh.SearchService)
    .ThenInclude(ss => ss.Images) // ✅ NUEVO: Para obtener imágenes del servicio
```

#### 2. **Carga de disponibilidades** (optimizada):
```csharp
// ✅ Obtener IDs de expertos para cargar disponibilidades
var expertIds = searches
    .Where(s => s.SearchHire?.Expert?.ExpertProfile != null)
    .Select(s => s.SearchHire.Expert.ExpertProfile.Id)
    .Distinct()
    .ToList();

// ✅ Cargar disponibilidades de expertos (una sola consulta)
var availabilities = new Dictionary<int, ExpertAvailability>();
if (expertIds.Any())
{
    var expertAvailabilities = await _context.ExpertAvailabilities
        .Where(ea => expertIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)
        .OrderByDescending(ea => ea.EffectiveFrom)
        .GroupBy(ea => ea.ExpertId)
        .Select(g => g.First())
        .ToListAsync();

    foreach (var availability in expertAvailabilities)
    {
        availabilities[availability.ExpertId] = availability;
    }
}
```

#### 3. **Mapeo de nuevos campos**:
```csharp
// ✅ Obtener primera imagen del servicio
string? serviceImageUrl = null;
if (s.SearchHire?.SearchService?.Images != null && s.SearchHire.SearchService.Images.Any())
{
    var firstImage = s.SearchHire.SearchService.Images.OrderBy(img => img.Id).First();
    serviceImageUrl = !string.IsNullOrWhiteSpace(firstImage.ImageObjectName)
        ? _signedUrlService.GetSignedUrl(firstImage.ImageObjectName) ?? firstImage.ImageUrl
        : firstImage.ImageUrl;
}

// ✅ Obtener disponibilidad del experto
HomepageExpertAvailabilityDto? expertAvailability = null;
if (s.SearchHire?.Expert?.ExpertProfile != null && 
    availabilities.TryGetValue(s.SearchHire.Expert.ExpertProfile.Id, out var availability))
{
    var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(availability.DaysOfWeek) ?? new List<string>();
    expertAvailability = new HomepageExpertAvailabilityDto
    {
        DaysOfWeek = daysOfWeek,
        StartTime = availability.StartTime,
        EndTime = availability.EndTime
    };
}

// ✅ Obtener ciudad del experto
string? expertCity = null;
if (s.SearchHire?.Expert?.ExpertProfile != null)
{
    expertCity = s.SearchHire.Expert.ExpertProfile.City;
}
```

---

## 💡 Beneficios para el Frontend

### **Antes**:
- El frontend tenía que hacer llamadas adicionales para obtener:
  - Imagen del servicio
  - Horario del experto
  - Ciudad del experto

### **Después**:
- ✅ **Todo viene en una sola respuesta**
- ✅ **Menos llamadas HTTP** = mejor rendimiento
- ✅ **Cards más completas** sin esperar datos adicionales
- ✅ **Consistente** con otros endpoints (como `SearchServiceHomepageDto`)

---

## 🎨 Uso en el Frontend

### **Ejemplo React/TypeScript**:

```typescript
interface SearchListDto {
  id: number;
  title: string;
  description: string;
  // ... otros campos ...
  
  // ✅ NUEVOS CAMPOS
  serviceImageUrl?: string;
  expertAvailability?: {
    daysOfWeek: string[];
    startTime: string;
    endTime: string;
  };
  expertCity?: string;
}

function SearchCard({ search }: { search: SearchListDto }) {
  return (
    <div className="search-card">
      {/* Imagen del servicio */}
      {search.serviceImageUrl && (
        <img 
          src={search.serviceImageUrl} 
          alt={search.title}
          className="service-image"
        />
      )}
      
      <h3>{search.title}</h3>
      <p>{search.description}</p>
      
      {/* Ciudad del experto */}
      {search.expertCity && (
        <div className="expert-location">
          📍 {search.expertCity}
        </div>
      )}
      
      {/* Horario del experto */}
      {search.expertAvailability && (
        <div className="expert-availability">
          <span>🕐 Disponible:</span>
          <span>{search.expertAvailability.daysOfWeek.join(", ")}</span>
          <span>
            {search.expertAvailability.startTime} - {search.expertAvailability.endTime}
          </span>
        </div>
      )}
    </div>
  );
}
```

---

## ⚠️ Notas Importantes

1. **Todos los nuevos campos son opcionales** (`?`): Pueden ser `null` si no hay `SearchHire` o si falta información.

2. **URLs firmadas**: Las imágenes usan URLs firmadas de Google Cloud Storage si tienen `ImageObjectName`, con expiración automática.

3. **Optimización**: Las disponibilidades se cargan de forma eficiente (una sola consulta para todos los expertos de la página).

4. **Consistencia**: Los nuevos campos siguen el mismo patrón que `SearchServiceHomepageDto`, facilitando la reutilización de componentes.

---

## 📝 Resumen de Cambios

| Campo | Tipo | Descripción | Origen |
|-------|------|-------------|--------|
| `ServiceImageUrl` | `string?` | Primera imagen del servicio | `SearchHire.SearchService.Images[0]` |
| `ExpertAvailability` | `HomepageExpertAvailabilityDto?` | Horario del experto | `ExpertAvailability` activa |
| `ExpertCity` | `string?` | Ciudad del experto | `ExpertProfile.City` |

---

¿Tienes alguna pregunta sobre los cambios? 🚀
