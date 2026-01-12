# 📍 Guía Frontend: Campo `City` del Experto

## 🎯 Resumen

El campo `City` ahora está disponible en **todos los endpoints** que devuelven información del experto. Se obtiene **automáticamente** cuando el experto selecciona su ubicación en el mapa usando **Google Geocoding API**.

---

## ✅ Dónde está Disponible

El campo `City` se devuelve en los siguientes DTOs:

### 1. `HomepageExpertDto` (Homepage Wall)
### 2. `ExpertProfileDto` (Detalle de Servicio)
### 3. Todos los endpoints que devuelven información del experto

---

## 📋 Estructura de los DTOs

### `HomepageExpertDto`

```typescript
interface HomepageExpertDto {
  Id: number;
  Name: string;
  ProfilePictureUrl: string;
  Country: string | null;        // Código ISO (ej: "ES", "MX")
  City: string | null;            // ✅ NUEVO: Nombre de la ciudad (ej: "Madrid", "Barcelona")
  Availability?: HomepageExpertAvailabilityDto | null;
}
```

### `ExpertProfileDto`

```typescript
interface ExpertProfileDto {
  Id: number;
  ProfilePictureUrl: string;
  Description: string;
  Latitude: string;
  Longitude: string;
  Timezone: string | null;        // IANA timezone (ej: "Europe/Madrid")
  Country: string | null;         // Código ISO (ej: "ES", "MX")
  City: string | null;            // ✅ NUEVO: Nombre de la ciudad (ej: "Madrid", "Barcelona")
  User: UserDto;
  Reviews: ReviewDto[];
  // ... otros campos
}
```

---

## 🔌 Endpoints que Incluyen `City`

### 1. **Homepage Wall**

**Endpoint:** `GET /api/SearchService/homepage-wall?categoryId={id}`

**Respuesta:**
```json
[
  {
    "title": "Revisiones cerca de mí",
    "services": [
      {
        "Id": 2,
        "CategoryId": 1,
        "CategoryName": "Coches",
        "Price": 50.00,
        "Expert": {
          "Id": 123,
          "Name": "Juan Pérez",
          "ProfilePictureUrl": "https://...",
          "Country": "ES",
          "City": "Madrid"  ✅
        },
        "AverageRating": 4.5,
        "CompletedSearches": 25
      }
    ],
    "pagination": { ... }
  }
]
```

**Ejemplo de uso:**
```typescript
const response = await fetch(`/api/SearchService/homepage-wall?categoryId=1`);
const sections = await response.json();

sections.forEach(section => {
  section.services.forEach(service => {
    const expertCity = service.Expert.City; // "Madrid" | null
    const expertCountry = service.Expert.Country; // "ES" | null
    
    // Mostrar ubicación: "Madrid, España" o solo "Madrid"
    const location = expertCity 
      ? expertCountry 
        ? `${expertCity}, ${getCountryName(expertCountry)}`
        : expertCity
      : expertCountry 
        ? getCountryName(expertCountry)
        : "Ubicación no disponible";
    
    console.log(`Experto: ${service.Expert.Name} - ${location}`);
  });
});
```

---

### 2. **Detalle de Servicio Individual**

**Endpoint:** `GET /api/SearchService/{id}`

**Respuesta:**
```json
{
  "Id": 2,
  "CategoryId": 1,
  "Price": 50.00,
  "Conditions": "Revisión completa...",
  "Expert": {
    "Id": 123,
    "Name": "Juan Pérez",
    "ProfilePictureUrl": "https://...",
    "Description": "Experto en revisión de coches...",
    "Latitude": "40.4168",
    "Longitude": "-3.7038",
    "Timezone": "Europe/Madrid",
    "Country": "ES",
    "City": "Madrid",  ✅
    "Reviews": [
      {
        "Id": 1,
        "Score": 5,
        "Description": "Excelente servicio...",
        "Reviewer": { ... }
      }
    ]
  }
}
```

**Ejemplo de uso:**
```typescript
const response = await fetch(`/api/SearchService/${serviceId}`);
const service = await response.json();

// Mostrar ubicación del experto
const expertLocation = service.Expert.City 
  ? `${service.Expert.City}, ${getCountryName(service.Expert.Country)}`
  : getCountryName(service.Expert.Country) || "Ubicación no disponible";

// Renderizar en UI
<div className="expert-location">
  <LocationIcon />
  <span>{expertLocation}</span>
</div>
```

---

### 3. **Servicios del Mapa (Con Detalles)**

**Endpoint:** `GET /api/SearchService/map-experts-with-details?categoryId={id}&serviceTypeId={id}`

**Respuesta:**
```json
[
  {
    "Id": 2,
    "Price": 50.00,
    "Expert": {
      "Id": 123,
      "Country": "ES",
      "City": "Madrid",  ✅
      "Reviews": [ ... ]
    }
  }
]
```

---

### 4. **Servicios de un Experto**

**Endpoint:** `GET /api/SearchService/expert/{expertId}?page=1&pageSize=20`

**Respuesta:**
```json
{
  "services": [
    {
      "Id": 2,
      "Expert": {
        "Country": "ES",
        "City": "Madrid"  ✅
      }
    }
  ],
  "pagination": { ... }
}
```

---

### 5. **Favoritos**

**Endpoint:** `GET /api/Favorite/my-favorites`

**Respuesta:**
```json
{
  "favorites": [
    {
      "Id": 1,
      "Service": {
        "Id": 2,
        "Expert": {
          "Id": 123,
          "Name": "Juan Pérez",
          "Country": "ES",
          "City": "Madrid"  ✅
        }
      }
    }
  ],
  "totalCount": 5
}
```

---

## 💡 Ejemplos de Uso en el Frontend

### Ejemplo 1: Mostrar Ubicación en Card de Servicio

```typescript
// Componente: ServiceCard.tsx
interface ServiceCardProps {
  service: SearchServiceHomepageDto;
}

export const ServiceCard: React.FC<ServiceCardProps> = ({ service }) => {
  const formatLocation = (expert: HomepageExpertDto): string => {
    if (expert.City && expert.Country) {
      return `${expert.City}, ${getCountryName(expert.Country)}`;
    }
    if (expert.City) {
      return expert.City;
    }
    if (expert.Country) {
      return getCountryName(expert.Country);
    }
    return "Ubicación no disponible";
  };

  return (
    <div className="service-card">
      <img src={service.Expert.ProfilePictureUrl} alt={service.Expert.Name} />
      <h3>{service.Expert.Name}</h3>
      
      {/* ✅ Mostrar ciudad y país */}
      <div className="location">
        <LocationIcon />
        <span>{formatLocation(service.Expert)}</span>
      </div>
      
      <div className="rating">
        <StarIcon />
        <span>{service.AverageRating.toFixed(1)}</span>
        <span>({service.CompletedSearches} revisiones)</span>
      </div>
      
      <div className="price">
        <span>{service.Price}€</span>
      </div>
    </div>
  );
};
```

---

### Ejemplo 2: Filtro por Ciudad

```typescript
// Hook personalizado para filtrar servicios por ciudad
const useServicesByCity = (services: SearchServiceHomepageDto[], city: string | null) => {
  return useMemo(() => {
    if (!city) return services;
    
    return services.filter(service => 
      service.Expert.City?.toLowerCase() === city.toLowerCase()
    );
  }, [services, city]);
};

// Uso en componente
const ServiceList: React.FC = () => {
  const [selectedCity, setSelectedCity] = useState<string | null>(null);
  const { data: services } = useHomepageWall(categoryId);
  
  const filteredServices = useServicesByCity(services, selectedCity);
  
  return (
    <div>
      <select 
        value={selectedCity || ''} 
        onChange={(e) => setSelectedCity(e.target.value || null)}
      >
        <option value="">Todas las ciudades</option>
        {getUniqueCities(services).map(city => (
          <option key={city} value={city}>{city}</option>
        ))}
      </select>
      
      {filteredServices.map(service => (
        <ServiceCard key={service.Id} service={service} />
      ))}
    </div>
  );
};
```

---

### Ejemplo 3: Mostrar Ubicación en Detalle del Experto

```typescript
// Componente: ExpertDetail.tsx
interface ExpertDetailProps {
  expert: ExpertProfileDto;
}

export const ExpertDetail: React.FC<ExpertDetailProps> = ({ expert }) => {
  const locationParts = [];
  
  if (expert.City) {
    locationParts.push(expert.City);
  }
  
  if (expert.Country) {
    locationParts.push(getCountryName(expert.Country));
  }
  
  const fullLocation = locationParts.length > 0 
    ? locationParts.join(", ")
    : "Ubicación no disponible";

  return (
    <div className="expert-detail">
      <img src={expert.ProfilePictureUrl} alt={expert.User.Name} />
      <h1>{expert.User.Name}</h1>
      
      {/* ✅ Mostrar ubicación completa */}
      <div className="expert-location">
        <MapPinIcon />
        <span>{fullLocation}</span>
      </div>
      
      {/* Mostrar coordenadas si están disponibles */}
      {expert.Latitude && expert.Longitude && (
        <a 
          href={`https://www.google.com/maps?q=${expert.Latitude},${expert.Longitude}`}
          target="_blank"
          rel="noopener noreferrer"
        >
          Ver en Google Maps
        </a>
      )}
      
      <p>{expert.Description}</p>
      
      {/* Reviews, etc. */}
    </div>
  );
};
```

---

### Ejemplo 4: Helper para Obtener Nombres de Países

```typescript
// utils/countryNames.ts
const countryNames: Record<string, string> = {
  "ES": "España",
  "MX": "México",
  "US": "Estados Unidos",
  "GB": "Reino Unido",
  "DE": "Alemania",
  "FR": "Francia",
  "IT": "Italia",
  "PT": "Portugal",
  // ... más países
};

export const getCountryName = (countryCode: string | null): string => {
  if (!countryCode) return "";
  return countryNames[countryCode] || countryCode;
};
```

---

## ⚠️ Consideraciones Importantes

### 1. **Campo Nullable**
El campo `City` es **opcional** (`string | null`), por lo que siempre debes verificar si existe antes de usarlo:

```typescript
// ✅ Correcto
const city = expert.City || "Ciudad no disponible";

// ❌ Incorrecto (puede causar error si City es null)
const city = expert.City.toUpperCase();
```

### 2. **Formato del Nombre**
El nombre de la ciudad viene directamente de Google Geocoding API y puede variar:
- "Madrid" (España)
- "México City" (México)
- "New York" (Estados Unidos)
- "São Paulo" (Brasil)

**No necesitas normalizar** el nombre, úsalo tal cual viene.

### 3. **Idioma**
El nombre de la ciudad viene en el idioma según la configuración de Google Maps API. Si necesitas un idioma específico, puedes configurarlo en el backend (pero por defecto viene en el idioma local).

### 4. **Fallback**
Si `City` es `null`, puedes usar `Country` como fallback:

```typescript
const displayLocation = expert.City 
  ? `${expert.City}, ${getCountryName(expert.Country)}`
  : getCountryName(expert.Country) || "Ubicación no disponible";
```

---

## 🎨 Ejemplos de UI

### Card de Servicio
```
┌─────────────────────────────┐
│  [Foto del Experto]          │
│                              │
│  Juan Pérez                  │
│  📍 Madrid, España           │ ← City + Country
│  ⭐ 4.5 (25 revisiones)      │
│  💰 50€                      │
└─────────────────────────────┘
```

### Detalle del Experto
```
┌─────────────────────────────┐
│  [Foto Grande]               │
│                              │
│  Juan Pérez                  │
│  📍 Madrid, España           │ ← City + Country
│  🗺️ Ver en Google Maps       │
│                              │
│  Experto en revisión de...   │
└─────────────────────────────┘
```

### Lista de Servicios con Filtro
```
┌─────────────────────────────┐
│  Filtrar por ciudad:         │
│  [Todas ▼]                   │
│    - Madrid                  │
│    - Barcelona               │
│    - Valencia                │
└─────────────────────────────┘
```

---

## 📝 Resumen de Cambios

### ✅ Lo que SÍ cambió:
- **Nuevo campo `City`** en `HomepageExpertDto` y `ExpertProfileDto`
- Disponible en **todos los endpoints** que devuelven información del experto
- Se obtiene **automáticamente** cuando el experto selecciona su ubicación

### ❌ Lo que NO cambió:
- Estructura de los endpoints (solo se agregó un campo)
- Formato de las respuestas (solo se agregó `City`)
- Endpoints existentes siguen funcionando igual

---

## 🚀 Próximos Pasos

1. **Actualizar tipos TypeScript** en el frontend para incluir `City: string | null` en los DTOs
2. **Actualizar componentes** que muestran información del experto para incluir la ciudad
3. **Implementar filtros** por ciudad si es necesario
4. **Agregar helper** para formatear ubicación (ciudad + país)

---

## ❓ Preguntas Frecuentes

**P: ¿Qué pasa si un experto no tiene ciudad?**
R: El campo `City` será `null`. Puedes usar `Country` como fallback.

**P: ¿Puedo filtrar servicios por ciudad?**
R: Sí, puedes filtrar en el frontend usando `service.Expert.City`.

**P: ¿El nombre de la ciudad está en español?**
R: Depende de la configuración de Google Maps API, pero generalmente viene en el idioma local del país.

**P: ¿Se actualiza automáticamente si el experto cambia su ubicación?**
R: Sí, cuando el experto actualiza su ubicación en el mapa, el backend detecta automáticamente la nueva ciudad y la guarda.

---

## 📞 Soporte

Si tienes dudas o problemas con la implementación, contacta al equipo de backend.

**Última actualización:** Enero 2025
