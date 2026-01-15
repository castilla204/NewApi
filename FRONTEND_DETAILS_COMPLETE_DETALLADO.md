# 📚 Guía Detallada: Endpoint Details-Complete para Frontend

**Fecha:** 15 de enero de 2026  
**Endpoint:** `GET /api/Search/{searchId}/details-complete`

---

## 🎯 Propósito del Endpoint

Este endpoint proporciona **TODA la información necesaria** para mostrar la pantalla de detalles de una búsqueda (`SearchDetails`) en una **sola llamada API**. Fue diseñado para optimizar el rendimiento y reducir el número de requests HTTP.

---

## 📡 Estructura de la Llamada

### **URL**
```
GET /api/Search/{searchId}/details-complete
```

### **Headers Requeridos**
```typescript
{
  "Authorization": "Bearer {token}",  // Token JWT del usuario autenticado
  "Content-Type": "application/json"
}
```

### **Parámetros**
- `searchId` (path parameter): ID de la búsqueda (Search) de la cual se quieren obtener los detalles

### **Autenticación y Autorización**
El endpoint verifica que el usuario tenga acceso a la búsqueda. Puede acceder si:
- Es el **cliente** que creó la búsqueda (`Search.UserId == userId`)
- Es el **experto** asignado a la contratación (`SearchHire.ExpertId == userId`)
- Es un **administrador** (`IsAdmin(User)`)

---

## 📦 Estructura de la Respuesta

### **Tipo de Respuesta**
```typescript
interface SearchDetailsCompleteResponseDto {
  search: SearchListDto | null;                    // Datos básicos de la búsqueda
  moneyDistribution: MoneyDistributionConfigDto | null;  // Configuración de distribución de dinero
  category: CategoryDto | null;                    // Categoría del servicio
  review: ReviewDto | null;                        // Reseña si existe
  appointment: AppointmentDto | null;              // Cita si existe
  deliverables: DeliverableDto[];                 // Archivos entregados
  disputes: DisputeDto[];                         // Disputas si existen
  requiredDeliverableTypes: DeliverableTypeDto[]; // Tipos de reportes requeridos
  expertProfile: ExpertProfileDto | null;         // Perfil completo del experto
}
```

---

## 📊 DTOs Detallados

### **1. SearchListDto** - Información Básica de la Búsqueda

```typescript
interface SearchListDto {
  id: number;                    // ID de la búsqueda
  userId: number;                // ID del cliente que creó la búsqueda
  title: string;                 // Título de la búsqueda
  description: string;            // Descripción de la búsqueda
  frequency: string;              // Frecuencia (ej: "OneTime", "Weekly")
  isActive: boolean;             // Si la búsqueda está activa
  isRevised: boolean;             // Si la búsqueda fue revisada
  createdAt: string;              // Fecha de creación (ISO 8601)
  
  // Usuario que creó la búsqueda (cliente)
  user: UserDto;
  
  // Contratación asociada (puede ser null si no hay contratación)
  searchHire: SearchHireDto | null;
}
```

### **2. SearchHireDto** - Información de la Contratación

```typescript
interface SearchHireDto {
  id: number;                     // ID de la contratación
  status: string;                // Estado actual (ej: "Pending", "InProgress", "Completed")
  createdAt: string;              // Fecha de creación
  
  // Montos (con IVA incluido)
  amount: number;                 // Monto total con IVA
  baseAmount: number;             // Base sin IVA
  taxAmount: number;              // IVA calculado
  
  // Información de internacionalización
  expertTimezone: string | null;  // Zona horaria del experto
  expertCountry: string | null;  // País del experto
  
  // Usuario experto asignado
  expert: UserDto | null;
  
  // Servicio contratado
  service: ServiceInfo | null;
  
  // Información completa del estado
  statusInfo: SystemStatusDto | null;
}

interface ServiceInfo {
  id: number;                     // ID del servicio
  serviceTypeId: number;          // ID del tipo de servicio
  serviceTypeName: string;        // Nombre del tipo de servicio
  serviceTypeCategoryId: number | null;  // ID de la categoría
  serviceTypeCategoryName: string | null; // Nombre de la categoría
  requiresAppointment: boolean;   // Si requiere cita (siempre false en este contexto)
  price: number;                  // Precio del servicio
  
  // Información de ubicación del experto
  expertLatitude: string | null;  // Latitud del experto
  expertLongitude: string | null; // Longitud del experto
  locationRange: number;           // Rango de ubicación en km (por defecto 50)
}

interface SystemStatusDto {
  id: number;
  statusType: string;             // Tipo de estado
  statusName: string;             // Nombre del estado
  statusValue: string;            // Valor del estado
  displayName: string;            // Nombre para mostrar
  description: string;             // Descripción del estado
  color: string;                   // Color del estado (hex)
  isActive: boolean;              // Si el estado está activo
  isFinalizationStatus: boolean; // Si es un estado de finalización
  sortOrder: number;              // Orden de visualización
  createdAt: string;
  updatedAt: string;
}
```

### **3. UserDto** - Información del Usuario

```typescript
interface UserDto {
  id: number;
  name: string;
  email: string;
  profilePictureUrl: string | null;  // URL de la foto de perfil (puede ser null)
}
```

### **4. MoneyDistributionConfigDto** - Distribución de Dinero

```typescript
interface MoneyDistributionConfigDto {
  clientPercentage: number;       // Porcentaje para el cliente (ej: 0.85 = 85%)
  expertPercentage: number;        // Porcentaje para el experto (ej: 0.10 = 10%)
  platformPercentage: number;     // Porcentaje para la plataforma (ej: 0.05 = 5%)
  source: string;                 // Fuente de la configuración (ej: "SearchHire")
  status: string;                 // Estado (ej: "Active")
}
```

### **5. CategoryDto** - Categoría del Servicio

```typescript
interface CategoryDto {
  id: number;
  name: string;                    // Nombre de la categoría
  isActive: boolean;              // Si la categoría está activa
  createdAt: string;
  updatedAt: string;
}
```

### **6. ReviewDto** - Reseña del Servicio

```typescript
interface ReviewDto {
  id: number;
  score: number;                   // Puntuación de 1 a 5
  description: string;             // Descripción de la reseña
  createdAt: string;                // Fecha de creación
  
  // Usuario que hizo la reseña
  reviewer: UserDto;
  
  // URLs de las imágenes de la reseña
  imageUrls: string[];            // Array de URLs
  
  // País donde se realizó la contratación (internacionalización)
  country: string | null;
}
```

### **7. AppointmentDto** - Información de la Cita

```typescript
interface AppointmentDto {
  id: number;
  searchHireId: number;           // ID de la contratación asociada
  status: string;                  // Estado de la cita (ej: "Pending", "Confirmed", "Rejected")
  
  // Fecha y hora propuesta (pueden ser null si aún no se propone)
  proposedDate: string | null;     // Fecha propuesta (ISO 8601)
  proposedTime: string | null;     // Hora propuesta (formato HH:mm:ss)
  
  // Ubicación de la cita
  location: string | null;         // Dirección de la cita
  latitude: string | null;         // Latitud
  longitude: string | null;        // Longitud
  doorNumber: string | null;       // Número de puerta
  ownerPhone: string | null;       // Teléfono del propietario
  siteDetails: string | null;      // Detalles del sitio
  
  // Contadores
  rejectionCount: number;          // Número de rechazos
  clientCancellationCount: number; // Cancelaciones del cliente
  expertCancellationCount: number; // Cancelaciones del experto
  
  // Fechas de eventos
  lastRejectionAt: string | null;  // Última fecha de rechazo
  lastClientCancellationAt: string | null;
  lastExpertCancellationAt: string | null;
  lastProposalAt: string | null;   // Última fecha de propuesta
  lastResponseAt: string | null;   // Última fecha de respuesta
  
  // Información adicional
  clientName: string | null;       // Nombre del cliente
  expertName: string | null;       // Nombre del experto
  amount: number;                  // Monto de la contratación
  
  // Información de ubicación del experto
  expertLatitude: string | null;
  expertLongitude: string | null;
  locationRange: number;           // Rango de ubicación en km
  
  // Información completa del estado
  statusInfo: SystemStatusDto | null;
  
  // Timers asociados (para control de tiempos de respuesta)
  timers: AppointmentTimerDto[];
  
  createdAt: string;
  updatedAt: string;
}

interface AppointmentTimerDto {
  id: number;
  appointmentId: number;
  timerType: string;               // Tipo de timer (ej: "Response", "Confirmation")
  startTime: string;               // Hora de inicio
  endTime: string | null;          // Hora de fin (null si aún está activo)
  isExpired: boolean;              // Si el timer expiró
  expiredAt: string | null;        // Fecha de expiración
}
```

### **8. DeliverableDto** - Archivos Entregados

```typescript
interface DeliverableDto {
  id: number;
  type: string;                    // Tipo de archivo (ej: "pdf", "image", "video")
  url: string;                     // URL del archivo (signed URL de Google Cloud Storage)
  createdAt: string;               // Fecha de creación
}
```

### **9. DeliverableTypeDto** - Tipos de Reportes Requeridos

```typescript
interface DeliverableTypeDto {
  id: number;
  name: string;                    // Nombre técnico
  displayName: string;             // Nombre para mostrar
  description: string;             // Descripción
  isRequired: boolean;             // Si es obligatorio
  isActive: boolean;              // Si está activo
  sortOrder: number;              // Orden de visualización
}
```

### **10. DisputeDto** - Disputas

```typescript
interface DisputeDto {
  id: number;
  searchHireId: number;            // ID de la contratación
  reporterId: number;              // ID del usuario que reportó
  status: string;                  // Estado de la disputa (ej: "Open", "Resolved")
  reason: string;                  // Razón de la disputa
  expertResponse: string | null;   // Respuesta del experto
  expertResponseDeadline: string | null;  // Fecha límite para respuesta
  expertResponseAt: string | null; // Fecha de respuesta del experto
  canExpertRespond: boolean;       // Si el experto puede responder
  createdAt: string;               // Fecha de creación
}
```

### **11. ExpertProfileDto** - Perfil Completo del Experto

```typescript
interface ExpertProfileDto {
  id: number;
  profilePictureUrl: string;       // URL de la foto de perfil
  description: string;             // Descripción del experto
  stripeAccountId: string | null;  // ID de cuenta de Stripe
  createdAt: string;
  
  // Usuario del experto
  user: UserDto | null;
  
  // Reviews (generalmente vacío, se cargan por separado si es necesario)
  reviews: ReviewDto[];
  
  // Ubicación
  latitude: string;                // Latitud
  longitude: string;                // Longitud
  
  // Estado de Stripe
  stripeStatus: string;            // Estado de la cuenta Stripe
  stripeStatusDetails: string | null; // Detalles del estado
  onboardingCompleted: boolean;    // Si completó el onboarding
  isOnVacation: boolean;           // Si está de vacaciones
  
  // Requisitos futuros de Stripe
  stripeFutureRequirements: string | null;
  stripeFutureDueAt: string | null;
  
  // Horarios de disponibilidad actuales
  currentAvailability: CurrentExpertAvailabilityDto | null;
}

interface CurrentExpertAvailabilityDto {
  id: number;
  daysOfWeek: string[];           // Días de la semana (ej: ["Monday", "Wednesday"])
  startTime: string;               // Hora de inicio (formato HH:mm:ss)
  endTime: string;                 // Hora de fin (formato HH:mm:ss)
  effectiveFrom: string;           // Fecha desde la cual es efectiva
}
```

---

## 🔄 Flujo de Datos

### **1. Carga Inicial de la Página**

```typescript
// 1. Usuario navega a /search/{searchId}/details
// 2. Componente SearchDetails se monta
// 3. Hook useSearchDetailsComplete se ejecuta
// 4. Se hace la llamada GET /api/Search/{searchId}/details-complete
// 5. Backend carga todos los datos relacionados en una sola query optimizada
// 6. Se retorna SearchDetailsCompleteResponseDto
// 7. Frontend renderiza todos los componentes con los datos
```

### **2. Qué Carga el Backend**

El backend hace una **sola query optimizada** con múltiples `Include` para cargar:

1. ✅ **Search** (búsqueda base)
   - Usuario que creó la búsqueda
   - Parámetros de búsqueda (SearchParameters)
   
2. ✅ **SearchHire** (contratación)
   - Cliente
   - Estado (SystemStatus)
   - Experto asignado
   - Servicio (SearchService)
     - Perfil del experto (ExpertProfile)
     - Tipo de servicio (ServiceType)
     - Categoría del tipo de servicio (ServiceTypeCategory)
     - Tipos de reportes requeridos (SelectedDeliverableTypes → DeliverableType)
   - Cita (Appointment)
     - Estado de la cita
     - Timers de la cita
   - Archivos entregados (Deliverables)
   - Disputas (Disputes)

3. ✅ **Datos Adicionales Calculados**
   - Configuración de distribución de dinero (MoneyDistribution)
   - Reseña si existe (Review)
   - Disponibilidad actual del experto (ExpertAvailabilities)

---

## 💻 Implementación en Frontend

### **1. Hook Personalizado**

```typescript
// src/hooks/useSearchDetailsComplete.ts
import { useQuery } from '@tanstack/react-query';
import { fetchApi } from '../utils/api';

export interface SearchDetailsCompleteResponseDto {
  search: SearchListDto | null;
  moneyDistribution: MoneyDistributionConfigDto | null;
  category: CategoryDto | null;
  review: ReviewDto | null;
  appointment: AppointmentDto | null;
  deliverables: DeliverableDto[];
  disputes: DisputeDto[];
  requiredDeliverableTypes: DeliverableTypeDto[];
  expertProfile: ExpertProfileDto | null;
}

export const useSearchDetailsComplete = (searchId: number | null) => {
  return useQuery({
    queryKey: ['searchDetailsComplete', searchId],
    queryFn: async () => {
      if (!searchId) throw new Error('SearchId is required');
      return fetchApi<SearchDetailsCompleteResponseDto>(
        `/api/Search/${searchId}/details-complete`
      );
    },
    enabled: !!searchId,           // Solo ejecutar si hay searchId
    staleTime: 30000,              // 30 segundos de cache
    cacheTime: 300000,             // 5 minutos en cache
    retry: 2,                      // Reintentar 2 veces en caso de error
    retryDelay: 1000               // Esperar 1 segundo entre reintentos
  });
};
```

### **2. Uso en el Componente**

```typescript
// src/components/SearchDetails.tsx
import { useSearchDetailsComplete } from '../hooks/useSearchDetailsComplete';
import { useParams } from 'react-router-dom';

export const SearchDetails: React.FC = () => {
  const { searchId } = useParams<{ searchId: string }>();
  const { 
    data: searchDetails, 
    isLoading, 
    error,
    refetch 
  } = useSearchDetailsComplete(searchId ? parseInt(searchId) : null);

  // Estados de carga
  if (isLoading) {
    return <LoadingSpinner message="Cargando detalles de la búsqueda..." />;
  }

  if (error) {
    return (
      <ErrorMessage 
        message="Error al cargar los detalles"
        onRetry={() => refetch()}
      />
    );
  }

  if (!searchDetails || !searchDetails.search) {
    return <NotFound message="Búsqueda no encontrada" />;
  }

  // Desestructurar datos
  const {
    search,
    moneyDistribution,
    category,
    review,
    appointment,
    deliverables,
    disputes,
    requiredDeliverableTypes,
    expertProfile
  } = searchDetails;

  const searchHire = search.searchHire;

  return (
    <div className="search-details">
      {/* Header con información básica */}
      <SearchDetailsHeader 
        title={search.title}
        description={search.description}
        createdAt={search.createdAt}
        user={search.user}
      />

      {/* Información de la contratación */}
      {searchHire && (
        <>
          {/* Estado de la contratación */}
          <StatusBadge 
            status={searchHire.status}
            statusInfo={searchHire.statusInfo}
          />

          {/* Información del experto */}
          {searchHire.expert && (
            <ExpertCard 
              expert={searchHire.expert}
              expertProfile={expertProfile}
              service={searchHire.service}
            />
          )}

          {/* Información de la cita */}
          {appointment && (
            <AppointmentCard 
              appointment={appointment}
              canEdit={/* lógica de permisos */}
            />
          )}

          {/* Archivos entregados */}
          <DeliverablesSection 
            deliverables={deliverables}
            requiredTypes={requiredDeliverableTypes}
            searchHireId={searchHire.id}
          />

          {/* Reseña */}
          {review && (
            <ReviewCard review={review} />
          )}

          {/* Disputas */}
          {disputes.length > 0 && (
            <DisputesSection disputes={disputes} />
          )}

          {/* Distribución de dinero */}
          {moneyDistribution && (
            <MoneyDistributionCard 
              distribution={moneyDistribution}
              amount={searchHire.amount}
            />
          )}
        </>
      )}

      {/* Categoría */}
      {category && (
        <CategoryBadge category={category} />
      )}
    </div>
  );
};
```

### **3. Componentes de Ejemplo**

#### **AppointmentCard**

```typescript
interface AppointmentCardProps {
  appointment: AppointmentDto;
  canEdit: boolean;
}

export const AppointmentCard: React.FC<AppointmentCardProps> = ({ 
  appointment, 
  canEdit 
}) => {
  return (
    <div className="appointment-card">
      <h3>Cita Programada</h3>
      
      {/* Estado de la cita */}
      {appointment.statusInfo && (
        <StatusBadge 
          status={appointment.status}
          color={appointment.statusInfo.color}
          displayName={appointment.statusInfo.displayName}
        />
      )}

      {/* Fecha y hora propuesta */}
      {appointment.proposedDate && appointment.proposedTime ? (
        <div>
          <p>
            <strong>Fecha:</strong> {formatDate(appointment.proposedDate)}
          </p>
          <p>
            <strong>Hora:</strong> {formatTime(appointment.proposedTime)}
          </p>
        </div>
      ) : (
        <p className="text-muted">Fecha y hora pendientes de propuesta</p>
      )}

      {/* Ubicación */}
      {appointment.location && (
        <div>
          <p><strong>Ubicación:</strong> {appointment.location}</p>
          {appointment.latitude && appointment.longitude && (
            <MapView 
              lat={parseFloat(appointment.latitude)}
              lon={parseFloat(appointment.longitude)}
              zoom={15}
            />
          )}
        </div>
      )}

      {/* Timers activos */}
      {appointment.timers
        .filter(t => !t.isExpired && !t.endTime)
        .map(timer => (
          <TimerDisplay 
            key={timer.id}
            timer={timer}
          />
        ))}

      {/* Acciones */}
      {canEdit && (
        <AppointmentActions appointment={appointment} />
      )}
    </div>
  );
};
```

#### **DeliverablesSection**

```typescript
interface DeliverablesSectionProps {
  deliverables: DeliverableDto[];
  requiredTypes: DeliverableTypeDto[];
  searchHireId: number;
}

export const DeliverablesSection: React.FC<DeliverablesSectionProps> = ({
  deliverables,
  requiredTypes,
  searchHireId
}) => {
  // Agrupar entregados por tipo
  const deliveredByType = deliverables.reduce((acc, d) => {
    acc[d.type] = (acc[d.type] || []).concat(d);
    return acc;
  }, {} as Record<string, DeliverableDto[]>);

  return (
    <div className="deliverables-section">
      <h3>Archivos Entregados</h3>

      {/* Mostrar tipos requeridos */}
      {requiredTypes.map(type => {
        const delivered = deliveredByType[type.name] || [];
        const isComplete = delivered.length > 0;

        return (
          <div 
            key={type.id} 
            className={`deliverable-type ${isComplete ? 'complete' : 'pending'}`}
          >
            <div className="deliverable-type-header">
              <h4>{type.displayName}</h4>
              {type.isRequired && (
                <span className="badge badge-required">Requerido</span>
              )}
              {isComplete && (
                <span className="badge badge-complete">✓ Entregado</span>
              )}
            </div>
            
            {type.description && (
              <p className="text-muted">{type.description}</p>
            )}

            {/* Archivos entregados de este tipo */}
            {delivered.length > 0 && (
              <div className="deliverables-list">
                {delivered.map(d => (
                  <DeliverableItem 
                    key={d.id}
                    deliverable={d}
                  />
                ))}
              </div>
            )}

            {/* Botón para subir si no está completo */}
            {!isComplete && (
              <UploadDeliverableButton 
                deliverableType={type}
                searchHireId={searchHireId}
              />
            )}
          </div>
        );
      })}
    </div>
  );
};
```

---

## ⚠️ Casos Especiales y Validaciones

### **1. Búsqueda sin Contratación**

Si `search.searchHire` es `null`, significa que la búsqueda aún no tiene una contratación asociada. En este caso:
- No mostrar información de experto
- No mostrar cita
- No mostrar archivos entregados
- No mostrar disputas

### **2. Cita sin Fecha Propuesta**

Si `appointment.proposedDate` o `appointment.proposedTime` son `null`, significa que la cita aún no ha sido propuesta. Mostrar:
- Estado: "Pendiente de propuesta"
- Botón para proponer cita (si el usuario tiene permisos)

### **3. Experto sin Perfil Completo**

Si `expertProfile` es `null`, puede significar:
- El experto aún no ha completado su perfil
- No hay experto asignado aún

### **4. Archivos con URLs Firmadas**

Los `deliverable.url` son **signed URLs** de Google Cloud Storage que expiran después de un tiempo. Si una URL expira:
- Hacer una nueva llamada al endpoint para obtener URLs frescas
- O implementar un endpoint específico para renovar URLs

### **5. Timers Activos**

Los `appointment.timers` pueden tener timers activos (sin `endTime`). Mostrar:
- Contador regresivo si el timer aún no expiró
- Indicador de expirado si `isExpired === true`

---

## 🔄 Actualización de Datos

### **1. Refetch Manual**

```typescript
const { refetch } = useSearchDetailsComplete(searchId);

// Refrescar datos después de una acción
const handleAppointmentConfirmed = async () => {
  await confirmAppointment(appointmentId);
  await refetch(); // Actualizar datos
};
```

### **2. Invalidación de Cache**

```typescript
import { useQueryClient } from '@tanstack/react-query';

const queryClient = useQueryClient();

// Invalidar cache después de una mutación
const handleDeliverableUploaded = async () => {
  await uploadDeliverable(file);
  queryClient.invalidateQueries(['searchDetailsComplete', searchId]);
};
```

### **3. Polling (Opcional)**

Para actualizar automáticamente cada cierto tiempo:

```typescript
const { data } = useSearchDetailsComplete(searchId, {
  refetchInterval: 30000, // Refrescar cada 30 segundos
  refetchIntervalInBackground: false // Solo cuando la pestaña está activa
});
```

---

## 🎨 Ejemplo Completo de Renderizado

```typescript
export const SearchDetailsPage: React.FC = () => {
  const { searchId } = useParams<{ searchId: string }>();
  const { data, isLoading, error } = useSearchDetailsComplete(
    searchId ? parseInt(searchId) : null
  );

  if (isLoading) return <LoadingSpinner />;
  if (error) return <ErrorMessage error={error} />;
  if (!data?.search) return <NotFound />;

  const { search, searchHire, appointment, deliverables, expertProfile } = data;

  return (
    <div className="container">
      {/* Header */}
      <div className="header">
        <h1>{search.title}</h1>
        <p className="text-muted">Creada el {formatDate(search.createdAt)}</p>
        {data.category && (
          <CategoryBadge category={data.category} />
        )}
      </div>

      {/* Estado de la contratación */}
      {searchHire && (
        <div className="status-section">
          <StatusCard 
            status={searchHire.status}
            statusInfo={searchHire.statusInfo}
            amount={searchHire.amount}
          />
        </div>
      )}

      {/* Información del experto */}
      {searchHire?.expert && expertProfile && (
        <ExpertSection 
          expert={searchHire.expert}
          expertProfile={expertProfile}
          service={searchHire.service}
        />
      )}

      {/* Cita */}
      {appointment && (
        <AppointmentSection appointment={appointment} />
      )}

      {/* Archivos */}
      <DeliverablesSection 
        deliverables={deliverables}
        requiredTypes={data.requiredDeliverableTypes}
        searchHireId={searchHire?.id}
      />

      {/* Reseña */}
      {data.review && (
        <ReviewSection review={data.review} />
      )}

      {/* Disputas */}
      {data.disputes.length > 0 && (
        <DisputesSection disputes={data.disputes} />
      )}
    </div>
  );
};
```

---

## 📝 Notas Importantes

1. **Una sola llamada**: Este endpoint reemplaza múltiples llamadas anteriores. No necesitas llamar a endpoints adicionales para obtener categoría, reseña, cita, etc.

2. **Datos nullable**: Muchos campos pueden ser `null`. Siempre verifica antes de renderizar.

3. **Signed URLs**: Las URLs de archivos expiran. Implementa renovación si es necesario.

4. **Cache**: El endpoint tiene cache de 30 segundos. Los datos pueden no estar actualizados inmediatamente después de una acción.

5. **Permisos**: El backend valida permisos. Si el usuario no tiene acceso, recibirá 404 o 403.

6. **Internacionalización**: Los campos `expertTimezone` y `expertCountry` están disponibles para mostrar información localizada.

---

## 🚀 Optimizaciones Implementadas

- ✅ **Una sola query SQL** con múltiples `Include` optimizados
- ✅ **Proyección Select** en lugar de cargar todas las relaciones
- ✅ **AsNoTracking** para queries de solo lectura
- ✅ **Cache en frontend** con React Query
- ✅ **Datos completos** en una sola respuesta

---

**Última actualización:** 15 de enero de 2026  
**Versión del endpoint:** 1.0
