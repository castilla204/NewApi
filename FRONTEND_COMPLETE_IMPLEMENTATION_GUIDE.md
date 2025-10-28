# 🎨 **GUÍA FRONTEND ACTUALIZADA - SYSTEMSTATUS CON COLORES EN TODOS LOS ENDPOINTS**

## 📋 **RESUMEN DE CAMBIOS**

Se han actualizado **TODOS** los endpoints principales para incluir `SystemStatusDto` con colores y información completa:

- **`GET /api/SearchHire/expert`** - ✅ ACTUALIZADO
- **`GET /api/Search?page=1&pageSize=20&sortBy=createdAt&sortDirection=desc`** - ✅ ACTUALIZADO  
- **`GET /api/Search/{id}`** - ✅ ACTUALIZADO
- **`GET /api/Search/{id}/details-complete`** - ✅ YA FUNCIONABA

---

## 🔄 **ENDPOINTS ACTUALIZADOS**

### **1. SearchHire/expert**
```typescript
// ANTES
interface SearchHireResponseDto {
  id: number;
  status: string;
  statusTranslated: string;
  // ... otros campos
}

// DESPUÉS
interface SearchHireResponseDto {
  id: number;
  status: string;
  statusTranslated: string;
  statusInfo?: SystemStatusDto; // ✅ NUEVO CAMPO
  // ... otros campos
}
```

### **2. Search con paginación**
```typescript
// ANTES
interface SearchListDto {
  id: number;
  title: string;
  searchHire?: SearchHireDto;
  // ... otros campos
}

interface SearchHireDto {
  id: number;
  status: string;
  statusTranslated: string;
  // ... otros campos
}

// DESPUÉS
interface SearchHireDto {
  id: number;
  status: string;
  statusTranslated: string;
  statusInfo?: SystemStatusDto; // ✅ NUEVO CAMPO
  // ... otros campos
}
```

### **3. Search individual**
```typescript
// Misma estructura que SearchHireDto con StatusInfo
```

---

## 🎨 **COLORES DISPONIBLES**

| Estado | Color | Descripción |
|--------|-------|-------------|
| **SearchHireStatus** | | |
| `pending` | `#FFA500` | 🟠 Pendiente |
| `active` | `#17A2B8` | 🔵 Activo |
| `completed` | `#28A745` | 🟢 Completado |
| `cancelled` | `#DC3545` | 🔴 Cancelado |
| `dispute_resolved_client` | `#17A2B8` | 🔵 Disputa Resuelta (Cliente) |
| `dispute_resolved_expert` | `#6F42C1` | 🟣 Disputa Resuelta (Experto) |
| `awaiting_client_decision` | `#FFC107` | 🟡 Esperando Decisión del Cliente |
| `awaiting_expert_response` | `#20C997` | 🟢 Esperando Respuesta del Experto |
| **AppointmentStatus** | | |
| `awaiting_appointment` | `#FFC107` | 🟡 Esperando Cita |
| `appointment_proposed` | `#6F42C1` | 🟣 Cita Propuesta |
| `appointment_confirmed` | `#20C997` | 🟢 Cita Confirmada |
| `appointment_rejected` | `#FD7E14` | 🟠 Cita Rechazada |
| `appointment_completed` | `#28A745` | 🟢 Cita Completada |
| `appointment_cancelled` | `#DC3545` | 🔴 Cita Cancelada |
| `appointment_report_sent` | `#6610F2` | 🟣 Informe Enviado |
| `expert_report_timeout` | `#E83E8C` | 🩷 Timeout del Experto |

---

## 💻 **IMPLEMENTACIÓN FRONTEND**

### **1. Interfaces TypeScript Actualizadas**

```typescript
// types/search.ts
export interface SystemStatusDto {
  id: number;
  statusType: string;
  statusName: string;
  statusValue: string;
  displayName: string;
  description: string | null;
  color: string | null;
  isActive: boolean;
  isFinalizationStatus: boolean;
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
}

export interface SearchHireResponseDto {
  id: number;
  status: string;
  statusTranslated: string;
  statusInfo?: SystemStatusDto; // ✅ NUEVO CAMPO
  // ... otros campos existentes
}

export interface SearchHireDto {
  id: number;
  status: string;
  statusTranslated: string;
  statusInfo?: SystemStatusDto; // ✅ NUEVO CAMPO
  // ... otros campos existentes
}
```

### **2. Componente StatusBadge Universal**

```tsx
// components/StatusBadge.tsx
import React from 'react';
import { SystemStatusDto } from '../types/search';

interface StatusBadgeProps {
  statusInfo: SystemStatusDto;
  size?: 'sm' | 'md' | 'lg';
  showDescription?: boolean;
  className?: string;
}

export const StatusBadge: React.FC<StatusBadgeProps> = ({ 
  statusInfo, 
  size = 'md', 
  showDescription = false,
  className = ''
}) => {
  const sizeClasses = {
    sm: 'px-2 py-1 text-xs',
    md: 'px-3 py-1.5 text-sm', 
    lg: 'px-4 py-2 text-base'
  };

  const getStatusStyle = () => {
    const baseColor = statusInfo.color || '#6C757D';
    return {
      backgroundColor: `${baseColor}20`,
      color: baseColor,
      border: `1px solid ${baseColor}40`
    };
  };

  return (
    <div className={`inline-flex flex-col ${className}`}>
      <span
        className={`inline-flex items-center rounded-full font-medium ${sizeClasses[size]}`}
        style={getStatusStyle()}
        title={statusInfo.description || statusInfo.displayName}
      >
        {statusInfo.displayName}
      </span>
      
      {showDescription && statusInfo.description && (
        <span className="text-xs text-gray-500 mt-1 max-w-xs">
          {statusInfo.description}
        </span>
      )}
    </div>
  );
};
```

### **3. Uso en Diferentes Componentes**

```tsx
// components/SearchHireCard.tsx
const SearchHireCard: React.FC<{ hire: SearchHireResponseDto }> = ({ hire }) => {
  return (
    <div className="search-hire-card">
      <h3>{hire.searchTitle}</h3>
      <p>{hire.searchDescription}</p>
      
      {/* Estado con color */}
      {hire.statusInfo ? (
        <StatusBadge statusInfo={hire.statusInfo} showDescription={true} />
      ) : (
        <span className="status-fallback">{hire.statusTranslated}</span>
      )}
      
      {/* Información del experto */}
      <div className="expert-info">
        <img src={hire.expert?.profilePictureUrl} alt={hire.expert?.name} />
        <span>{hire.expert?.name}</span>
      </div>
    </div>
  );
};

// components/SearchList.tsx
const SearchList: React.FC<{ searches: SearchListDto[] }> = ({ searches }) => {
  return (
    <div className="search-list">
      {searches.map(search => (
        <div key={search.id} className="search-item">
          <h3>{search.title}</h3>
          <p>{search.description}</p>
          
          {/* Estado de SearchHire con color */}
          {search.searchHire?.statusInfo ? (
            <StatusBadge statusInfo={search.searchHire.statusInfo} />
          ) : search.searchHire ? (
            <span className="status-fallback">{search.searchHire.statusTranslated}</span>
          ) : (
            <span className="no-hire">Sin contratación</span>
          )}
        </div>
      ))}
    </div>
  );
};
```

### **4. Utilidades Helper**

```typescript
// utils/statusUtils.ts
import { SystemStatusDto } from '../types/search';

export const getStatusColor = (statusInfo: SystemStatusDto): string => {
  return statusInfo.color || '#6C757D';
};

export const getStatusDisplayName = (statusInfo: SystemStatusDto): string => {
  return statusInfo.displayName || statusInfo.statusValue;
};

export const getStatusDescription = (statusInfo: SystemStatusDto): string => {
  return statusInfo.description || statusInfo.displayName || statusInfo.statusValue;
};

export const isFinalizationStatus = (statusInfo: SystemStatusDto): boolean => {
  return statusInfo.isFinalizationStatus;
};

export const getStatusPriority = (statusInfo: SystemStatusDto): 'low' | 'medium' | 'high' => {
  if (statusInfo.isFinalizationStatus) return 'high';
  if (statusInfo.statusValue.includes('pending') || statusInfo.statusValue.includes('awaiting')) return 'medium';
  return 'low';
};

// Función de compatibilidad para migración gradual
export const getStatusInfo = (item: any): SystemStatusDto | null => {
  return item.statusInfo || null;
};
```

### **5. Hooks para los Endpoints**

```typescript
// hooks/useSearchHires.ts
import { useState, useEffect } from 'react';
import { SearchHireResponseDto } from '../types/search';

export const useSearchHires = (type: 'client' | 'expert') => {
  const [hires, setHires] = useState<SearchHireResponseDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchHires = async () => {
      try {
        setLoading(true);
        const response = await fetch(`/api/SearchHire/${type}`, {
          headers: {
            'X-Development-Mode': 'true'
          }
        });
        
        if (!response.ok) {
          throw new Error('Error al cargar las contrataciones');
        }
        
        const data = await response.json();
        setHires(data);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Error desconocido');
      } finally {
        setLoading(false);
      }
    };

    fetchHires();
  }, [type]);

  return { hires, loading, error };
};

// hooks/useSearches.ts
import { useState, useEffect } from 'react';
import { SearchListDto } from '../types/search';

export const useSearches = (page: number = 1, pageSize: number = 20) => {
  const [searches, setSearches] = useState<SearchListDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [pagination, setPagination] = useState<any>(null);

  useEffect(() => {
    const fetchSearches = async () => {
      try {
        setLoading(true);
        const response = await fetch(`/api/Search?page=${page}&pageSize=${pageSize}&sortBy=createdAt&sortDirection=desc`, {
          headers: {
            'X-Development-Mode': 'true'
          }
        });
        
        if (!response.ok) {
          throw new Error('Error al cargar las búsquedas');
        }
        
        const data = await response.json();
        setSearches(data.searches);
        setPagination(data.pagination);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Error desconocido');
      } finally {
        setLoading(false);
      }
    };

    fetchSearches();
  }, [page, pageSize]);

  return { searches, pagination, loading, error };
};
```

---

## 🧪 **TESTING**

### **Casos de Prueba**

```typescript
// __tests__/endpoints.test.ts
import { testEndpoints } from '../utils/testEndpoints';

describe('Endpoints con SystemStatusDto', () => {
  it('should return StatusInfo in SearchHire/expert', async () => {
    const response = await fetch('/api/SearchHire/expert', {
      headers: { 'X-Development-Mode': 'true' }
    });
    const data = await response.json();
    
    expect(data[0].statusInfo).toBeDefined();
    expect(data[0].statusInfo.displayName).toBeDefined();
    expect(data[0].statusInfo.color).toBeDefined();
  });

  it('should return StatusInfo in Search list', async () => {
    const response = await fetch('/api/Search?page=1&pageSize=5', {
      headers: { 'X-Development-Mode': 'true' }
    });
    const data = await response.json();
    
    if (data.searches[0].searchHire) {
      expect(data.searches[0].searchHire.statusInfo).toBeDefined();
      expect(data.searches[0].searchHire.statusInfo.displayName).toBeDefined();
      expect(data.searches[0].searchHire.statusInfo.color).toBeDefined();
    }
  });
});
```

---

## 📝 **CHECKLIST DE IMPLEMENTACIÓN**

### **Preparación**
- [ ] **Actualizar interfaces TypeScript** con `SystemStatusDto`
- [ ] **Crear componente StatusBadge** universal
- [ ] **Crear utilidades helper** para manejo de estados
- [ ] **Crear hooks** para los endpoints actualizados

### **Implementación**
- [ ] **Migrar SearchHireCard** para usar `StatusInfo`
- [ ] **Migrar SearchList** para usar `StatusInfo`
- [ ] **Migrar SearchDetails** para usar `StatusInfo`
- [ ] **Actualizar todos los componentes** que muestran estados

### **Testing**
- [ ] **Tests unitarios** para StatusBadge
- [ ] **Tests de integración** para endpoints
- [ ] **Testing manual** en diferentes estados
- [ ] **Verificar colores** en todos los endpoints

### **Deployment**
- [ ] **Deploy con compatibilidad** (código nuevo + legacy)
- [ ] **Monitorear métricas** de uso
- [ ] **Deploy completo** (solo código nuevo)
- [ ] **Limpiar código legacy** (opcional)

---

## 🚀 **BENEFICIOS**

1. **Consistencia Total**: Todos los endpoints devuelven la misma información de estado
2. **Colores Centralizados**: No más hardcoding de colores en el frontend
3. **Información Rica**: Descripciones y nombres legibles en todos lados
4. **Mantenibilidad**: Fácil agregar nuevos estados y colores
5. **UX Mejorada**: Estados visuales consistentes en toda la aplicación

---

## ⚠️ **CONSIDERACIONES**

1. **Compatibilidad**: Los campos `status` y `statusTranslated` siguen existiendo
2. **Fallbacks**: Siempre tener valores por defecto si `statusInfo` es null
3. **Performance**: Los nuevos campos no afectan significativamente el rendimiento
4. **Caching**: Considerar cache de colores y nombres de estado

---

*Documentación actualizada: 2025-01-20*  
*Versión API: Todos los endpoints actualizados con SystemStatusDto*  
*Compatibilidad: Total hacia atrás*
