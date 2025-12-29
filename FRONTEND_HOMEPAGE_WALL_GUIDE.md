# 🏠 Guía de Implementación: Muro de Homepage

## 📋 Resumen

Nuevo endpoint para obtener servicios cercanos y populares para la homepage, con soporte para geolocalización opcional y paginación independiente para cada sección.

---

## 🔗 Endpoint

```
GET /api/SearchService/homepage-wall
```

---

## 📥 Parámetros de Query (Todos Opcionales)

| Parámetro | Tipo | Requerido | Default | Descripción |
|-----------|------|-----------|---------|-------------|
| `latitude` | string | No | Capital del país | Latitud del usuario |
| `longitude` | string | No | Capital del país | Longitud del usuario |
| `countryCode` | string | No | "ES" (Madrid) | Código ISO 3166-1 alpha-2 (ej: "ES", "MX", "US") |
| `locationRange` | number | No | 50 | Rango de búsqueda en km |
| `nearbyPage` | number | No | 1 | Página para servicios cercanos |
| `nearbyPageSize` | number | No | 20 | Tamaño de página cercanos (máx: 50) |
| `popularPage` | number | No | 1 | Página para servicios populares |
| `popularPageSize` | number | No | 20 | Tamaño de página populares (máx: 50) |

---

## 📤 Respuesta

```typescript
interface HomepageWallResponse {
  nearbyServices: {
    services: SearchServiceDetailDto[];
    pagination: {
      page: number;
      pageSize: number;
      totalCount: number;
      totalPages: number;
      hasNextPage: boolean;
      hasPreviousPage: boolean;
    };
  };
  popularServices: {
    services: SearchServiceDetailDto[];
    pagination: {
      page: number;
      pageSize: number;
      totalCount: number;
      totalPages: number;
      hasNextPage: boolean;
      hasPreviousPage: boolean;
    };
  };
}
```

---

## 🎯 Comportamiento

### Con Ubicación del Usuario
- Si el usuario permite la ubicación: muestra servicios cercanos a su posición
- Ordenados por distancia (más cercanos primero)

### Sin Ubicación del Usuario
- Usa automáticamente la capital del país especificado en `countryCode`
- Si no se especifica `countryCode`, usa Madrid (España) por defecto
- Muestra servicios cercanos a la capital

### Servicios Populares
- Siempre muestra los servicios más populares
- Ordenados por: `(rating * 0.6) + (contrataciones * 0.4)`
- No depende de la ubicación

---

## 💻 Implementación en TypeScript/React

### 1. Tipos TypeScript

```typescript
// types/service.ts
export interface SearchServiceDetailDto {
  id: number;
  categoryId: number;
  serviceTypeId: number;
  serviceTypeName: string;
  serviceTypeDescription: string;
  serviceTypeCategoryId?: number;
  requiresAppointment: boolean;
  price: number;
  conditions: string;
  durationInHours: number;
  createdAt: string;
  isActive: boolean;
  imageUrls: string[];
  categoryName: string;
  completedSearches: number;
  averageRating: number;
  expert?: ExpertProfileDto;
  selectedDeliverableTypes: DeliverableTypeDto[];
}

export interface ExpertProfileDto {
  id: number;
  profilePictureUrl: string;
  description: string;
  latitude: string;
  longitude: string;
  user: {
    id: number;
    name: string;
    email: string;
  };
  reviews: ReviewDto[];
  currentAvailability?: {
    id: number;
    daysOfWeek: string[];
    startTime: string;
    endTime: string;
    effectiveFrom: string;
  };
  timezone?: string;
  country?: string;
}

export interface HomepageWallResponse {
  nearbyServices: {
    services: SearchServiceDetailDto[];
    pagination: PaginationInfo;
  };
  popularServices: {
    services: SearchServiceDetailDto[];
    pagination: PaginationInfo;
  };
}

export interface PaginationInfo {
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasNextPage: boolean;
  hasPreviousPage: boolean;
}
```

### 2. Hook para Obtener Ubicación del Usuario

```typescript
// hooks/useGeolocation.ts
import { useState, useEffect } from 'react';

interface GeolocationState {
  latitude: string | null;
  longitude: string | null;
  error: string | null;
  loading: boolean;
}

export const useGeolocation = () => {
  const [state, setState] = useState<GeolocationState>({
    latitude: null,
    longitude: null,
    error: null,
    loading: true,
  });

  useEffect(() => {
    if (!navigator.geolocation) {
      setState(prev => ({
        ...prev,
        error: 'Geolocalización no soportada',
        loading: false,
      }));
      return;
    }

    navigator.geolocation.getCurrentPosition(
      (position) => {
        setState({
          latitude: position.coords.latitude.toString(),
          longitude: position.coords.longitude.toString(),
          error: null,
          loading: false,
        });
      },
      (error) => {
        setState({
          latitude: null,
          longitude: null,
          error: error.message,
          loading: false,
        });
      },
      {
        enableHighAccuracy: true,
        timeout: 10000,
        maximumAge: 300000, // Cache por 5 minutos
      }
    );
  }, []);

  return state;
};
```

### 3. Hook para Obtener el Muro de Homepage

```typescript
// hooks/useHomepageWall.ts
import { useState, useEffect } from 'react';
import { HomepageWallResponse } from '@/types/service';

interface UseHomepageWallParams {
  latitude?: string | null;
  longitude?: string | null;
  countryCode?: string;
  locationRange?: number;
  nearbyPage?: number;
  nearbyPageSize?: number;
  popularPage?: number;
  popularPageSize?: number;
}

export const useHomepageWall = (params: UseHomepageWallParams = {}) => {
  const [data, setData] = useState<HomepageWallResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchHomepageWall = async () => {
      setLoading(true);
      setError(null);

      try {
        const queryParams = new URLSearchParams();
        
        if (params.latitude && params.longitude) {
          queryParams.append('latitude', params.latitude);
          queryParams.append('longitude', params.longitude);
        }
        
        if (params.countryCode) {
          queryParams.append('countryCode', params.countryCode);
        }
        
        if (params.locationRange) {
          queryParams.append('locationRange', params.locationRange.toString());
        }
        
        if (params.nearbyPage) {
          queryParams.append('nearbyPage', params.nearbyPage.toString());
        }
        
        if (params.nearbyPageSize) {
          queryParams.append('nearbyPageSize', params.nearbyPageSize.toString());
        }
        
        if (params.popularPage) {
          queryParams.append('popularPage', params.popularPage.toString());
        }
        
        if (params.popularPageSize) {
          queryParams.append('popularPageSize', params.popularPageSize.toString());
        }

        const response = await fetch(
          `/api/SearchService/homepage-wall?${queryParams.toString()}`,
          {
            headers: {
              'Content-Type': 'application/json',
              // Agregar token de autenticación si es necesario
              // 'Authorization': `Bearer ${token}`
            },
          }
        );

        if (!response.ok) {
          throw new Error(`Error ${response.status}: ${response.statusText}`);
        }

        const result = await response.json();
        setData(result);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Error desconocido');
      } finally {
        setLoading(false);
      }
    };

    fetchHomepageWall();
  }, [
    params.latitude,
    params.longitude,
    params.countryCode,
    params.locationRange,
    params.nearbyPage,
    params.nearbyPageSize,
    params.popularPage,
    params.popularPageSize,
  ]);

  return { data, loading, error };
};
```

### 4. Componente de Homepage

```typescript
// components/HomepageWall.tsx
import React, { useState } from 'react';
import { useGeolocation } from '@/hooks/useGeolocation';
import { useHomepageWall } from '@/hooks/useHomepageWall';
import { SearchServiceDetailDto } from '@/types/service';

export const HomepageWall: React.FC = () => {
  const { latitude, longitude, error: geoError, loading: geoLoading } = useGeolocation();
  const [nearbyPage, setNearbyPage] = useState(1);
  const [popularPage, setPopularPage] = useState(1);
  
  // Detectar código de país (puedes usar una librería como i18n o detectarlo del navegador)
  const countryCode = 'ES'; // O detectarlo automáticamente

  const { data, loading, error } = useHomepageWall({
    latitude,
    longitude,
    countryCode,
    locationRange: 50,
    nearbyPage,
    nearbyPageSize: 20,
    popularPage,
    popularPageSize: 20,
  });

  if (loading || geoLoading) {
    return <div>Cargando servicios...</div>;
  }

  if (error) {
    return <div>Error: {error}</div>;
  }

  if (!data) {
    return <div>No hay datos disponibles</div>;
  }

  return (
    <div className="homepage-wall">
      {/* Sección: Servicios Cercanos */}
      <section className="nearby-services">
        <div className="section-header">
          <h2>
            {latitude && longitude 
              ? 'Servicios cerca de ti' 
              : `Servicios en ${countryCode === 'ES' ? 'Madrid' : 'tu ciudad'}`}
          </h2>
          {geoError && (
            <p className="geo-warning">
              No se pudo obtener tu ubicación. Mostrando servicios de la capital.
            </p>
          )}
        </div>

        <div className="services-grid">
          {data.nearbyServices.services.map((service) => (
            <ServiceCard key={service.id} service={service} />
          ))}
        </div>

        {/* Paginación Servicios Cercanos */}
        <Pagination
          currentPage={nearbyPage}
          totalPages={data.nearbyServices.pagination.totalPages}
          hasNextPage={data.nearbyServices.pagination.hasNextPage}
          hasPreviousPage={data.nearbyServices.pagination.hasPreviousPage}
          onPageChange={setNearbyPage}
        />
      </section>

      {/* Sección: Servicios Populares */}
      <section className="popular-services">
        <div className="section-header">
          <h2>Servicios Populares</h2>
          <p className="subtitle">
            Los servicios mejor valorados por nuestros usuarios
          </p>
        </div>

        <div className="services-grid">
          {data.popularServices.services.map((service) => (
            <ServiceCard key={service.id} service={service} />
          ))}
        </div>

        {/* Paginación Servicios Populares */}
        <Pagination
          currentPage={popularPage}
          totalPages={data.popularServices.pagination.totalPages}
          hasNextPage={data.popularServices.pagination.hasNextPage}
          hasPreviousPage={data.popularServices.pagination.hasPreviousPage}
          onPageChange={setPopularPage}
        />
      </section>
    </div>
  );
};

// Componente de Tarjeta de Servicio
const ServiceCard: React.FC<{ service: SearchServiceDetailDto }> = ({ service }) => {
  return (
    <div className="service-card">
      {service.imageUrls.length > 0 && (
        <img 
          src={service.imageUrls[0]} 
          alt={service.serviceTypeName}
          className="service-image"
        />
      )}
      
      <div className="service-info">
        <h3>{service.serviceTypeName}</h3>
        <p className="service-description">{service.conditions}</p>
        
        {service.expert && (
          <div className="expert-info">
            <img 
              src={service.expert.profilePictureUrl} 
              alt={service.expert.user.name}
              className="expert-avatar"
            />
            <span>{service.expert.user.name}</span>
          </div>
        )}
        
        <div className="service-stats">
          <span className="rating">★ {service.averageRating.toFixed(2)}</span>
          <span className="completed">
            {service.completedSearches} contrataciones
          </span>
        </div>
        
        <div className="service-price">
          <span className="price">{service.price}€</span>
          {service.durationInHours && (
            <span className="duration">{service.durationInHours}h</span>
          )}
        </div>
      </div>
    </div>
  );
};

// Componente de Paginación
const Pagination: React.FC<{
  currentPage: number;
  totalPages: number;
  hasNextPage: boolean;
  hasPreviousPage: boolean;
  onPageChange: (page: number) => void;
}> = ({ currentPage, totalPages, hasNextPage, hasPreviousPage, onPageChange }) => {
  return (
    <div className="pagination">
      <button
        onClick={() => onPageChange(currentPage - 1)}
        disabled={!hasPreviousPage}
      >
        Anterior
      </button>
      
      <span>
        Página {currentPage} de {totalPages}
      </span>
      
      <button
        onClick={() => onPageChange(currentPage + 1)}
        disabled={!hasNextPage}
      >
        Siguiente
      </button>
    </div>
  );
};
```

---

## 📱 Ejemplo con React Query (Recomendado)

```typescript
// hooks/useHomepageWallQuery.ts
import { useQuery } from '@tanstack/react-query';
import { HomepageWallResponse } from '@/types/service';

interface HomepageWallParams {
  latitude?: string | null;
  longitude?: string | null;
  countryCode?: string;
  locationRange?: number;
  nearbyPage?: number;
  nearbyPageSize?: number;
  popularPage?: number;
  popularPageSize?: number;
}

export const useHomepageWallQuery = (params: HomepageWallParams = {}) => {
  return useQuery<HomepageWallResponse>({
    queryKey: ['homepage-wall', params],
    queryFn: async () => {
      const queryParams = new URLSearchParams();
      
      if (params.latitude && params.longitude) {
        queryParams.append('latitude', params.latitude);
        queryParams.append('longitude', params.longitude);
      }
      
      if (params.countryCode) {
        queryParams.append('countryCode', params.countryCode);
      }
      
      if (params.locationRange) {
        queryParams.append('locationRange', params.locationRange.toString());
      }
      
      if (params.nearbyPage) {
        queryParams.append('nearbyPage', params.nearbyPage.toString());
      }
      
      if (params.nearbyPageSize) {
        queryParams.append('nearbyPageSize', params.nearbyPageSize.toString());
      }
      
      if (params.popularPage) {
        queryParams.append('popularPage', params.popularPage.toString());
      }
      
      if (params.popularPageSize) {
        queryParams.append('popularPageSize', params.popularPageSize.toString());
      }

      const response = await fetch(
        `/api/SearchService/homepage-wall?${queryParams.toString()}`
      );

      if (!response.ok) {
        throw new Error('Error al cargar el muro de homepage');
      }

      return response.json();
    },
    staleTime: 5 * 60 * 1000, // Cache por 5 minutos
    refetchOnWindowFocus: false,
  });
};
```

---

## 🎨 Estilos CSS (Ejemplo)

```css
.homepage-wall {
  padding: 2rem;
  max-width: 1200px;
  margin: 0 auto;
}

.section-header {
  margin-bottom: 2rem;
}

.section-header h2 {
  font-size: 1.5rem;
  font-weight: bold;
  margin-bottom: 0.5rem;
}

.subtitle {
  color: #666;
  font-size: 0.9rem;
}

.geo-warning {
  color: #ff9800;
  font-size: 0.85rem;
  margin-top: 0.5rem;
}

.services-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 1.5rem;
  margin-bottom: 2rem;
}

.service-card {
  border: 1px solid #e0e0e0;
  border-radius: 8px;
  overflow: hidden;
  transition: transform 0.2s, box-shadow 0.2s;
}

.service-card:hover {
  transform: translateY(-4px);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.1);
}

.service-image {
  width: 100%;
  height: 200px;
  object-fit: cover;
}

.service-info {
  padding: 1rem;
}

.service-info h3 {
  font-size: 1.1rem;
  margin-bottom: 0.5rem;
}

.service-description {
  color: #666;
  font-size: 0.9rem;
  margin-bottom: 1rem;
}

.expert-info {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.5rem;
}

.expert-avatar {
  width: 32px;
  height: 32px;
  border-radius: 50%;
  object-fit: cover;
}

.service-stats {
  display: flex;
  gap: 1rem;
  margin-bottom: 0.5rem;
  font-size: 0.85rem;
  color: #666;
}

.rating {
  color: #ff9800;
  font-weight: 600;
}

.service-price {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 1rem;
  padding-top: 1rem;
  border-top: 1px solid #e0e0e0;
}

.price {
  font-size: 1.2rem;
  font-weight: bold;
  color: #2196f3;
}

.duration {
  font-size: 0.9rem;
  color: #666;
}

.pagination {
  display: flex;
  justify-content: center;
  align-items: center;
  gap: 1rem;
  margin-top: 2rem;
}

.pagination button {
  padding: 0.5rem 1rem;
  border: 1px solid #e0e0e0;
  background: white;
  border-radius: 4px;
  cursor: pointer;
  transition: background 0.2s;
}

.pagination button:hover:not(:disabled) {
  background: #f5f5f5;
}

.pagination button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
```

---

## 🔄 Flujo Completo

1. **Cargar Página**: El componente se monta
2. **Solicitar Ubicación**: `useGeolocation` intenta obtener la ubicación del usuario
3. **Cargar Datos**: `useHomepageWall` hace la petición al endpoint
   - Si hay ubicación: usa `latitude` y `longitude`
   - Si no hay ubicación: usa `countryCode` (o "ES" por defecto)
4. **Mostrar Servicios**: Renderiza las dos secciones con paginación
5. **Cambiar Página**: Al cambiar página, se actualiza el estado y se vuelve a cargar

---

## ⚠️ Consideraciones Importantes

### 1. Permisos de Geolocalización
- El navegador pedirá permiso al usuario
- Si el usuario rechaza, se usa la capital del país automáticamente
- Muestra un mensaje informativo si no se puede obtener la ubicación

### 2. Performance
- Usa React Query o similar para cachear las respuestas
- Considera usar `staleTime` para evitar peticiones innecesarias
- Implementa lazy loading de imágenes

### 3. Manejo de Errores
- Siempre maneja el caso cuando no hay servicios disponibles
- Muestra mensajes claros si hay errores de red
- Considera un estado de "cargando" mientras se obtiene la ubicación

### 4. Detección de País
- Puedes detectar el país del usuario usando:
  - `navigator.language` o `navigator.languages`
  - Una librería de i18n
  - IP geolocation (servicio externo)
  - Configuración del usuario en tu app

---

## 📝 Ejemplo de Uso Completo

```typescript
// pages/HomePage.tsx
import React, { useState } from 'react';
import { useGeolocation } from '@/hooks/useGeolocation';
import { useHomepageWallQuery } from '@/hooks/useHomepageWallQuery';
import { HomepageWall } from '@/components/HomepageWall';

export default function HomePage() {
  const { latitude, longitude, error: geoError } = useGeolocation();
  const [nearbyPage, setNearbyPage] = useState(1);
  const [popularPage, setPopularPage] = useState(1);
  
  // Detectar país (ejemplo simple)
  const countryCode = navigator.language?.split('-')[1]?.toUpperCase() || 'ES';

  const { data, isLoading, error } = useHomepageWallQuery({
    latitude,
    longitude,
    countryCode,
    locationRange: 50,
    nearbyPage,
    nearbyPageSize: 20,
    popularPage,
    popularPageSize: 20,
  });

  if (isLoading) {
    return <div>Cargando...</div>;
  }

  if (error) {
    return <div>Error al cargar servicios</div>;
  }

  return (
    <div>
      <HomepageWall 
        data={data}
        nearbyPage={nearbyPage}
        popularPage={popularPage}
        onNearbyPageChange={setNearbyPage}
        onPopularPageChange={setPopularPage}
        geoError={geoError}
      />
    </div>
  );
}
```

---

## ✅ Checklist de Implementación

- [ ] Crear tipos TypeScript para `HomepageWallResponse` y `SearchServiceDetailDto`
- [ ] Implementar hook `useGeolocation` para obtener ubicación del usuario
- [ ] Implementar hook `useHomepageWall` o `useHomepageWallQuery` para cargar datos
- [ ] Crear componente `HomepageWall` para renderizar las secciones
- [ ] Crear componente `ServiceCard` para mostrar cada servicio
- [ ] Implementar paginación para ambas secciones
- [ ] Manejar estados de carga y error
- [ ] Agregar estilos CSS
- [ ] Probar con y sin permisos de geolocalización
- [ ] Probar cambio de páginas
- [ ] Optimizar imágenes (lazy loading)
- [ ] Agregar tests si es necesario

---

## 🚀 Listo para Usar

El endpoint está listo y devuelve exactamente el mismo formato que los otros endpoints de servicios, así que puedes reutilizar los componentes y tipos que ya tengas implementados.


