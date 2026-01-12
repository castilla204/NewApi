# 📍 Instrucciones Frontend: Campo `City` del Experto

## ✅ Estado Actual

El campo `City` ya está disponible en **todos los endpoints** que devuelven información del experto. Se obtiene automáticamente cuando el experto selecciona su ubicación.

### 🔍 Endpoints que Incluyen `City`:

- ✅ `GET /api/SearchService/homepage-wall?categoryId={id}` → `HomepageExpertDto.City`
- ✅ **`GET /api/SearchService/{id}`** → `ExpertProfileDto.City` ⬅️ **IMPORTANTE: También aquí**
- ✅ `GET /api/SearchService/map-experts-with-details` → `ExpertProfileDto.City`
- ✅ `GET /api/SearchService/expert/{expertId}` → `ExpertProfileDto.City`
- ✅ `GET /api/Favorite/my-favorites` → `HomepageExpertDto.City`

**Ejemplo de respuesta del detalle:**
```json
GET http://localhost:7124/api/SearchService/2

{
  "Id": 2,
  "Expert": {
    "Id": 10,
    "Name": "Hans Müller",
    "Country": "DE",
    "City": "Berlin"  ✅ Disponible aquí
  }
}
```

---

## 🚀 Pasos para Implementar

### 1. Actualizar Tipos TypeScript

Actualiza tus interfaces TypeScript para incluir el campo `City`:

```typescript
// types/expert.ts o donde tengas tus tipos
interface HomepageExpertDto {
  Id: number;
  Name: string;
  ProfilePictureUrl: string;
  Country: string | null;
  City: string | null;  // ✅ AGREGAR ESTE CAMPO
  Availability?: HomepageExpertAvailabilityDto | null;
}

interface ExpertProfileDto {
  Id: number;
  ProfilePictureUrl: string;
  Description: string;
  Latitude: string;
  Longitude: string;
  Timezone: string | null;
  Country: string | null;
  City: string | null;  // ✅ AGREGAR ESTE CAMPO
  User: UserDto;
  Reviews: ReviewDto[];
  // ... otros campos
}
```

---

### 2. Helper para Formatear Ubicación

Crea un helper para formatear la ubicación (ciudad + país):

```typescript
// utils/location.ts
const countryNames: Record<string, string> = {
  "ES": "España",
  "MX": "México",
  "US": "Estados Unidos",
  "GB": "Reino Unido",
  "DE": "Alemania",
  "FR": "Francia",
  "IT": "Italia",
  "PT": "Portugal",
  "CA": "Canadá",
  "BR": "Brasil",
  "AR": "Argentina",
  "CL": "Chile",
  "JP": "Japón",
  "CN": "China",
  "IN": "India",
  "KR": "Corea del Sur",
  "AU": "Australia",
  "NZ": "Nueva Zelanda",
  "ZA": "Sudáfrica",
  "NG": "Nigeria",
  "NL": "Países Bajos",
  "SE": "Suecia",
  // Agrega más según necesites
};

export const getCountryName = (countryCode: string | null): string => {
  if (!countryCode) return "";
  return countryNames[countryCode] || countryCode;
};

export const formatExpertLocation = (expert: {
  City?: string | null;
  Country?: string | null;
}): string => {
  const parts: string[] = [];
  
  if (expert.City) {
    parts.push(expert.City);
  }
  
  if (expert.Country) {
    parts.push(getCountryName(expert.Country));
  }
  
  if (parts.length === 0) {
    return "Ubicación no disponible";
  }
  
  return parts.join(", ");
};
```

---

### 3. Actualizar Componente de Card de Servicio

Actualiza tu componente de card para mostrar la ciudad:

```typescript
// components/ServiceCard.tsx
import { formatExpertLocation } from '@/utils/location';
import { MapPin } from 'lucide-react'; // o el icono que uses

interface ServiceCardProps {
  service: SearchServiceHomepageDto;
}

export const ServiceCard: React.FC<ServiceCardProps> = ({ service }) => {
  const location = formatExpertLocation(service.Expert);
  
  return (
    <div className="service-card">
      <img 
        src={service.Expert.ProfilePictureUrl} 
        alt={service.Expert.Name}
        className="expert-avatar"
      />
      
      <h3 className="expert-name">{service.Expert.Name}</h3>
      
      {/* ✅ AGREGAR: Mostrar ubicación */}
      <div className="expert-location">
        <MapPin size={16} />
        <span>{location}</span>
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

**CSS sugerido:**
```css
.expert-location {
  display: flex;
  align-items: center;
  gap: 4px;
  color: #666;
  font-size: 14px;
  margin-top: 4px;
}
```

---

### 4. Actualizar Detalle del Servicio (GET /api/SearchService/{id})

**✅ IMPORTANTE:** El endpoint `GET /api/SearchService/{id}` también devuelve el campo `City`.

Si tienes una página de detalle del servicio:

```typescript
// pages/ServiceDetail.tsx o components/ServiceDetail.tsx
import { formatExpertLocation } from '@/utils/location';
import { MapPin, ExternalLink } from 'lucide-react';

export const ServiceDetail: React.FC = () => {
  const { id } = useParams();
  const { data: service } = useQuery(['service', id], () =>
    fetch(`/api/SearchService/${id}`).then(res => res.json())
  );
  
  if (!service) return <div>Cargando...</div>;
  
  const location = formatExpertLocation(service.Expert);
  
  return (
    <div className="service-detail">
      <div className="service-header">
        <h1>Servicio #{service.Id}</h1>
        <div className="price">{service.Price}€</div>
      </div>
      
      {/* Información del Experto */}
      <div className="expert-section">
        <img 
          src={service.Expert.ProfilePictureUrl} 
          alt={service.Expert.User?.Name || 'Experto'}
          className="expert-avatar-large"
        />
        
        <div className="expert-info">
          <h2>{service.Expert.User?.Name || 'Experto'}</h2>
          
          {/* ✅ AGREGAR: Mostrar ubicación del experto */}
          <div className="expert-location">
            <MapPin size={18} />
            <span>{location}</span>
          </div>
          
          {/* Opcional: Link a Google Maps */}
          {service.Expert.Latitude && service.Expert.Longitude && (
            <a
              href={`https://www.google.com/maps?q=${service.Expert.Latitude},${service.Expert.Longitude}`}
              target="_blank"
              rel="noopener noreferrer"
              className="map-link"
            >
              <ExternalLink size={16} />
              Ver ubicación en Google Maps
            </a>
          )}
          
          <p className="expert-description">{service.Expert.Description}</p>
        </div>
      </div>
      
      {/* Reviews, etc. */}
      <div className="reviews-section">
        {service.Expert.Reviews?.map(review => (
          <div key={review.Id} className="review-card">
            {/* ... */}
          </div>
        ))}
      </div>
    </div>
  );
};
```

**CSS sugerido:**
```css
.expert-section {
  display: flex;
  gap: 20px;
  padding: 20px;
  background: #f9f9f9;
  border-radius: 8px;
  margin-bottom: 30px;
}

.expert-avatar-large {
  width: 120px;
  height: 120px;
  border-radius: 50%;
  object-fit: cover;
}

.expert-info {
  flex: 1;
}

.expert-location {
  display: flex;
  align-items: center;
  gap: 6px;
  color: #666;
  font-size: 16px;
  margin: 8px 0;
}

.map-link {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: #0066cc;
  text-decoration: none;
  margin-top: 8px;
  font-size: 14px;
}

.map-link:hover {
  text-decoration: underline;
}
```

---

### 5. Actualizar Lista de Servicios (Homepage Wall)

Si muestras servicios en la homepage:

```typescript
// pages/HomePage.tsx o components/ServiceList.tsx
import { ServiceCard } from '@/components/ServiceCard';

export const HomePage: React.FC = () => {
  const { data: sections } = useHomepageWall(categoryId);
  
  return (
    <div className="homepage">
      {sections?.map((section, index) => (
        <section key={index} className="service-section">
          <h2>{section.title}</h2>
          
          <div className="services-grid">
            {section.services.map(service => (
              <ServiceCard key={service.Id} service={service} />
            ))}
          </div>
        </section>
      ))}
    </div>
  );
};
```

---

## 📋 Ejemplos de Respuesta del API

### Homepage Wall
```json
GET /api/SearchService/homepage-wall?categoryId=1

[
  {
    "title": "Revisiones cerca de mí",
    "services": [
      {
        "Id": 64,
        "Expert": {
          "Id": 10,
          "Name": "Hans Müller",
          "Country": "DE",
          "City": "Berlin"  ✅ NUEVO
        }
      }
    ]
  }
]
```

### Detalle de Servicio Individual
```json
GET /api/SearchService/2

{
  "Id": 2,
  "CategoryId": 1,
  "Price": 50.00,
  "Conditions": "Revisión completa...",
  "Expert": {
    "Id": 10,
    "Name": "Hans Müller",
    "ProfilePictureUrl": "https://...",
    "Description": "Experto en revisión de coches...",
    "Latitude": "52.5200",
    "Longitude": "13.4050",
    "Timezone": "Europe/Berlin",
    "Country": "DE",
    "City": "Berlin",  ✅ NUEVO - Disponible aquí también
    "Reviews": [...]
  }
}
```

**✅ IMPORTANTE:** Este endpoint (`GET /api/SearchService/{id}`) también devuelve el campo `City` en el objeto `Expert`.

---

## ⚠️ Consideraciones Importantes

### 1. Campo Nullable
El campo `City` puede ser `null`, siempre verifica antes de usarlo:

```typescript
// ✅ Correcto
const city = expert.City || "Ciudad no disponible";

// ❌ Incorrecto (puede causar error)
const city = expert.City.toUpperCase();
```

### 2. Fallback
Si `City` es `null`, usa `Country` como fallback:

```typescript
const location = expert.City 
  ? `${expert.City}, ${getCountryName(expert.Country)}`
  : getCountryName(expert.Country) || "Ubicación no disponible";
```

### 3. Formato del Nombre
El nombre de la ciudad viene directamente de Google Maps API:
- Puede estar en diferentes idiomas
- No necesitas normalizarlo
- Úsalo tal cual viene

---

## 🎨 Ejemplos Visuales

### Card de Servicio
```
┌─────────────────────────────┐
│  [Foto del Experto]          │
│                              │
│  Hans Müller                 │
│  📍 Berlin, Alemania         │ ← City + Country
│  ⭐ 4.5 (25 revisiones)      │
│  💰 50€                      │
└─────────────────────────────┘
```

### Detalle del Experto
```
┌─────────────────────────────┐
│  [Foto Grande]               │
│                              │
│  Hans Müller                 │
│  📍 Berlin, Alemania         │ ← City + Country
│  🗺️ Ver en Google Maps       │
│                              │
│  Experto en revisión de...   │
└─────────────────────────────┘
```

---

## ✅ Checklist de Implementación

- [ ] Actualizar tipos TypeScript (`HomepageExpertDto`, `ExpertProfileDto`)
- [ ] Crear helper `formatExpertLocation()`
- [ ] Actualizar componente `ServiceCard` para mostrar ciudad
- [ ] Actualizar componente `ServiceDetail` (GET /api/SearchService/{id})
- [ ] Probar que el campo aparece en homepage wall
- [ ] Probar que el campo aparece en detalle de servicio (`/api/SearchService/2`)
- [ ] Manejar casos donde `City` es `null`
- [ ] Agregar estilos CSS para la ubicación

---

## 🧪 Testing

### Endpoints a Probar:

1. **Homepage Wall:**
   ```
   GET /api/SearchService/homepage-wall?categoryId=1
   ```
   Verificar que `services[].Expert.City` aparece

2. **Detalle de Servicio:**
   ```
   GET /api/SearchService/2
   ```
   Verificar que `Expert.City` aparece ✅ **IMPORTANTE**

3. **Otros endpoints:**
   - `GET /api/SearchService/map-experts-with-details`
   - `GET /api/SearchService/expert/{id}`
   - `GET /api/Favorite/my-favorites`

### Casos a Probar:

1. **Experto con ciudad y país:**
   ```json
   { "City": "Berlin", "Country": "DE" }
   ```
   Debe mostrar: "Berlin, Alemania"

2. **Experto solo con país:**
   ```json
   { "City": null, "Country": "DE" }
   ```
   Debe mostrar: "Alemania"

3. **Experto sin ubicación:**
   ```json
   { "City": null, "Country": null }
   ```
   Debe mostrar: "Ubicación no disponible"

---

## 📞 Soporte

Si tienes dudas o problemas:
1. Verifica que el campo `City` esté en la respuesta del API
   - Homepage Wall: `GET /api/SearchService/homepage-wall?categoryId=1`
   - Detalle: `GET /api/SearchService/2` ✅ **Verificar aquí también**
2. Revisa la consola del navegador para errores
3. Contacta al equipo de backend si el campo no aparece

## 🔍 Verificación Rápida

Para verificar que el campo `City` está disponible, abre la consola del navegador y ejecuta:

```javascript
// Verificar en Homepage Wall
fetch('/api/SearchService/homepage-wall?categoryId=1')
  .then(r => r.json())
  .then(data => console.log('City en homepage:', data[0]?.services[0]?.Expert?.City));

// Verificar en Detalle de Servicio
fetch('/api/SearchService/2')
  .then(r => r.json())
  .then(data => console.log('City en detalle:', data.Expert?.City));
```

Ambos deberían mostrar el nombre de la ciudad (ej: "Berlin", "Madrid", etc.)

---

## 🚀 Próximos Pasos (Opcional)

### Filtro por Ciudad
Si quieres agregar un filtro por ciudad:

```typescript
const [selectedCity, setSelectedCity] = useState<string | null>(null);

const filteredServices = services.filter(service => 
  !selectedCity || service.Expert.City === selectedCity
);

// Obtener ciudades únicas
const uniqueCities = [...new Set(
  services
    .map(s => s.Expert.City)
    .filter(city => city !== null)
)] as string[];
```

---

**Última actualización:** Enero 2025  
**Estado:** ✅ Campo `City` disponible en todos los endpoints
